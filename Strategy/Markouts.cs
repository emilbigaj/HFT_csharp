using System;
using System.Runtime.CompilerServices;
using Data;
using Execution;
using Provider;
using Tools;

namespace Strategy;

/// The last values of something that changes over exchange time, each kept with the moment it began to stand, so the one
/// in force at an earlier moment can be looked up. Feed thread only. Hot path: allocates at construction only.
public sealed class MarkoutHistory<TValue> where TValue : unmanaged
{
    private const int s_capacity = 4096;   // values remembered, a power of two; the oldest is overwritten
    private const int s_mask = s_capacity - 1;

    private readonly Timestamp[] _timestamps = new Timestamp[s_capacity];   // apart from the values: a look back walks eight of these to a cache line
    private readonly TValue[] _values = new TValue[s_capacity];
    private long _addedCount;                                                // values added so far; a long, since an int would wrap within months of book updates

    /// Remembers the value that stands from this moment on.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Timestamp timestamp, TValue value)
    {
        int index = (int)(_addedCount++ & s_mask);
        _timestamps[index] = timestamp;
        _values[index] = value;
    }

    /// The value that stood at this moment: the newest one added that began at or before it.
    /// Returns noValue when none did, or when the one that did has been overwritten.
    public TValue GetValueAt(Timestamp timestamp, TValue noValue)
    {
        long oldestAddedIndex = Math.Max(_addedCount - s_capacity, 0);
        for (long addedIndex = _addedCount - 1; addedIndex >= oldestAddedIndex; addedIndex--)
        {
            int index = (int)(addedIndex & s_mask);
            if (_timestamps[index] <= timestamp)
                return _values[index];
        }
        return noValue;
    }
}

/// Records the markouts of every algo fill on one position. It remembers each change of the instrument's quote and of the
/// strategy's fair value, so a markout duration before, at or after a fill is answered from what stood then: as the
/// book's own exchange time passes the fill's plus a duration (no timers), one Markout with that moment's quote and fair
/// value is written to the position's .markout file.
/// Feed thread only. Hot path: no allocation after construction (see Spec.md "Markouts").
public sealed class Markouts
{
    /// The best bid and ask at one moment.
    private readonly record struct MarkoutQuote(double BidPrice, double AskPrice)
    {
        /// No quote: the instrument was not open, a side was empty, or the moment is further back than the history reaches.
        public static MarkoutQuote None => new(double.NaN, double.NaN);
    }

    private const int s_pendingCapacity = 4096;   // fills still awaiting their longest markout duration, a power of two; a full ring drops its oldest fill's remaining durations
    private const int s_pendingMask = s_pendingCapacity - 1;

    private readonly Client _client;
    private readonly Instrument _instrument;         // the position's; held directly because every book update reads it, and through the position that is one more dependent load
    private readonly Series<Markout> _markoutSeries;
    private readonly Markout[] _pendingMarkouts = new Markout[s_pendingCapacity];
    private readonly Duration[] _markoutDurations;   // the strategy's markout durations, strictly ascending, so the last is the longest; a negative one is before the fill
    private readonly int[] _observedCounts;          // per markout duration: how many fills it has observed
    private readonly int _longestMarkoutDurationIndex;
    private readonly MarkoutHistory<MarkoutQuote> _quoteHistory = new();      // the quote after each change of its prices, at the book's exchange time
    private readonly MarkoutHistory<double> _fairValuePriceHistory = new();   // the strategy's fair value after each change
    private MarkoutQuote _lastMarkoutQuote = MarkoutQuote.None;               // the newest quote in the history: a book update that leaves it as it is adds nothing
    private int _filledCount;                        // fills taken into the ring so far
    private Timestamp _nextMarkoutExchangeTimestamp = Timestamp.MaxValue;   // the earliest moment a pending observation falls; MaxValue = none pending, MinValue = to be worked out on the next book update
    private double _fairValuePrice = double.NaN;     // the strategy's fair value now; NaN until it sets one

    public event MarkoutHandler? Markout;            // raised once per algo fill, before any observation: the strategy tags it here

    public Markouts(Client client, Position position, Series<Markout> markoutSeries, Duration[] markoutDurations)
    {
        ThrowIfNotAscending(markoutDurations);
        _client = client;
        _instrument = position.Instrument;
        _markoutSeries = markoutSeries;
        _markoutDurations = markoutDurations;
        _observedCounts = new int[markoutDurations.Length];
        _longestMarkoutDurationIndex = markoutDurations.Length - 1;
        position.Fill += OnFill;
        position.Instrument.MarketByPriceChanged += OnMarketByPriceChanged;
        position.Instrument.TradingStatusUpdateEvent += OnTradingStatusUpdate;
    }

    /// The strategy's fair value of this instrument, NaN until it sets one. The strategy sets it whenever it changes, and
    /// each markout carries the one that stood at its moment.
    public double FairValuePrice
    {
        get => _fairValuePrice;
        set
        {
            // Why Equals: unlike ==, it holds between two NaNs, so setting the same value again remembers nothing
            if (value.Equals(_fairValuePrice))
                return;
            _fairValuePrice = value;

            // Why the later of the two times: neither may date the value before the newest message it was worked out from (see Spec.md "Markouts")
            Timestamp bookExchangeTimestamp = _instrument.MarketByPrice.ExchangeTimestamp;
            Timestamp clientExchangeTimestamp = _client.ExchangeTimestamp;
            _fairValuePriceHistory.Add(clientExchangeTimestamp > bookExchangeTimestamp ? clientExchangeTimestamp : bookExchangeTimestamp, value);
        }
    }

    /// Takes an algo fill into the ring and lets the strategy tag it.
    private void OnFill(in Fill fill)
    {
        // Step 1: a manual fill or a transfer between books has no markout
        bool isAlgoTrade = fill.OrderHeader.OrderId.IsAlgoOrder() && fill.FillType != FillType.Transfer;
        if (!isAlgoTrade)
            return;

        // Step 2: a full ring drops the markout durations its oldest fill has not reached
        if (_filledCount - _observedCounts[_longestMarkoutDurationIndex] == s_pendingCapacity)
            DropPendingBefore(_observedCounts[_longestMarkoutDurationIndex] + 1);

        // Step 3: the fill's name; its quotes and fair values come from the histories as each markout duration passes
        ref Markout markout = ref _pendingMarkouts[_filledCount++ & s_pendingMask];
        markout = new Markout();
        markout.MarkoutId = fill.FillId;
        markout.ExchangeTimestamp = fill.OrderHeader.ExchangeTimestamp;

        // Step 4: its first markout duration may now be the earliest observation pending
        Timestamp firstMarkoutExchangeTimestamp = markout.ExchangeTimestamp + _markoutDurations[0];
        if (firstMarkoutExchangeTimestamp < _nextMarkoutExchangeTimestamp)
            _nextMarkoutExchangeTimestamp = firstMarkoutExchangeTimestamp;

        // Step 5: the strategy edits it; every observation of this fill carries what it set
        Markout?.Invoke(in fill, ref markout);
    }

    /// Remembers the quote if this update changed it, and writes one Markout for every markout duration the book's
    /// exchange time has passed since a pending fill.
    private void OnMarketByPriceChanged()
    {
        // Step 1: a quote that differs from the last one remembered stands from this update on (none while the instrument is not open or a side is empty)
        Timestamp bookExchangeTimestamp = _instrument.MarketByPrice.ExchangeTimestamp;
        MarkoutQuote markoutQuote = _instrument.TryGetQuote(out Quote quote) ? new MarkoutQuote(quote.BidPrice, quote.AskPrice) : MarkoutQuote.None;
        if (markoutQuote != _lastMarkoutQuote)
        {
            _lastMarkoutQuote = markoutQuote;
            _quoteHistory.Add(bookExchangeTimestamp, markoutQuote);
        }

        // Step 2: no pending observation has fallen yet, or none is pending: one more compare
        if (bookExchangeTimestamp <= _nextMarkoutExchangeTimestamp)
            return;

        // Step 3: a markout duration that fell before this update takes the quote and fair value that stood when it fell
        Timestamp _earliestMarkoutExchangeTimestamp = Timestamp.MaxValue;
        for (int markoutDurationIndex = 0; markoutDurationIndex < _markoutDurations.Length; markoutDurationIndex++)
        {
            ref int observedCount = ref _observedCounts[markoutDurationIndex];
            while (observedCount < _filledCount)
            {
                ref Markout markout = ref _pendingMarkouts[observedCount & s_pendingMask];
                Timestamp markoutExchangeTimestamp = markout.ExchangeTimestamp + _markoutDurations[markoutDurationIndex];
                if (markoutExchangeTimestamp >= bookExchangeTimestamp)
                {
                    // Why kept: the earliest observation still to come, over the durations, is when to look again
                    if (markoutExchangeTimestamp < _earliestMarkoutExchangeTimestamp)
                        _earliestMarkoutExchangeTimestamp = markoutExchangeTimestamp;
                    break;
                }
                MarkoutQuote quoteAtMarkout = _quoteHistory.GetValueAt(markoutExchangeTimestamp, MarkoutQuote.None);
                markout.MarkoutDuration = _markoutDurations[markoutDurationIndex];
                markout.BidPrice = quoteAtMarkout.BidPrice;
                markout.AskPrice = quoteAtMarkout.AskPrice;
                markout.FairValuePrice = _fairValuePriceHistory.GetValueAt(markoutExchangeTimestamp, double.NaN);
                _markoutSeries.Append(in markout);
                observedCount++;
            }
        }
        _nextMarkoutExchangeTimestamp = _earliestMarkoutExchangeTimestamp;
    }

    /// At the close, drops every observation still pending: the next quote belongs to another session.
    private void OnTradingStatusUpdate(in TradingStatusUpdate tradingStatusUpdate)
    {
        if (tradingStatusUpdate.TradingStatus == TradingStatus.Closed)
            DropPendingBefore(_filledCount);
    }

    /// Moves every markout duration past the fills before this count, so their unreached observations are never written.
    private void DropPendingBefore(int filledCount)
    {
        for (int markoutDurationIndex = 0; markoutDurationIndex < _markoutDurations.Length; markoutDurationIndex++)
            _observedCounts[markoutDurationIndex] = Math.Max(_observedCounts[markoutDurationIndex], filledCount);
        _nextMarkoutExchangeTimestamp = Timestamp.MinValue;
    }

    /// Throws unless there is at least one markout duration and each is longer than the one before; a negative one is before the fill.
    private static void ThrowIfNotAscending(Duration[] markoutDurations)
    {
        if (markoutDurations.Length == 0)
            throw new ArgumentException("Markouts.ThrowIfNotAscending: no markout durations, must be at least one; set the strategy's MarkoutDurations");
        for (int markoutDurationIndex = 1; markoutDurationIndex < markoutDurations.Length; markoutDurationIndex++)
        {
            Duration markoutDuration = markoutDurations[markoutDurationIndex];
            Duration previousMarkoutDuration = markoutDurations[markoutDurationIndex - 1];
            if (markoutDuration <= previousMarkoutDuration)
                throw new ArgumentException($"Markouts.ThrowIfNotAscending: markout duration {markoutDuration} after {previousMarkoutDuration}, must be strictly ascending; reorder the strategy's MarkoutDurations");
        }
    }
}
