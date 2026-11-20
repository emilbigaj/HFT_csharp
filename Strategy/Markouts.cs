using System;
using Data;
using Execution;
using Provider;
using Tools;

namespace Strategy;

/// Records the markouts of every algo fill on one position: as the book's own exchange time passes each markout duration
/// after a fill (no timers), the quote that stood just before is written to the position's .markout file as one Markout.
/// Feed thread only. Hot path: no allocation after construction (see Spec.md "Markouts").
public sealed class Markouts
{
    private const int s_pendingCapacity = 4096;   // fills still awaiting their longest markout duration, a power of two; a full ring drops its oldest fill's remaining durations
    private const int s_pendingMask = s_pendingCapacity - 1;

    private readonly Position _position;
    private readonly Series<Markout> _markoutSeries;
    private readonly Markout[] _pendingMarkouts = new Markout[s_pendingCapacity];
    private readonly Duration[] _markoutDurations;   // the strategy's markout durations, strictly ascending, so the last is the longest
    private readonly int[] _observedCounts;          // per markout duration: how many fills it has observed
    private readonly int _longestMarkoutDurationIndex;
    private int _filledCount;                        // fills taken into the ring so far
    private double _previousBidPrice = double.NaN;   // the quote as of the previous pass: what stood when a markout duration fell before this update
    private double _previousAskPrice = double.NaN;

    public event MarkoutHandler? Markout;            // raised once per algo fill, before any observation: the strategy tags it here

    public Markouts(Position position, Series<Markout> markoutSeries, Duration[] markoutDurations)
    {
        ThrowIfNotAscending(markoutDurations);
        _position = position;
        _markoutSeries = markoutSeries;
        _markoutDurations = markoutDurations;
        _observedCounts = new int[markoutDurations.Length];
        _longestMarkoutDurationIndex = markoutDurations.Length - 1;
        position.Fill += OnFill;
        position.Instrument.MarketByPriceChanged += OnMarketByPriceChanged;
        position.Instrument.TradingStatusUpdateEvent += OnTradingStatusUpdate;
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

        // Step 3: the fill, and the quote it filled into as the first "before"
        SetPreviousQuote();
        ref Markout markout = ref _pendingMarkouts[_filledCount++ & s_pendingMask];
        markout = new Markout();
        markout.FillId = fill.FillId;
        markout.FillExchangeTimestamp = fill.OrderHeader.ExchangeTimestamp;
        markout.InstrumentId = fill.OrderHeader.OrderId.InstrumentId;
        markout.FillPrice = fill.Price;
        markout.FillQuantity = fill.Quantity;
        markout.FillType = fill.FillType;

        // Step 4: the strategy edits it; every observation of this fill carries what it set
        Markout?.Invoke(in fill, ref markout);
    }

    /// Writes one Markout for every markout duration the book's exchange time has passed since a pending fill.
    private void OnMarketByPriceChanged()
    {
        // Step 1: nothing pending costs one compare
        if (_observedCounts[_longestMarkoutDurationIndex] == _filledCount)
            return;

        // Step 2: a markout duration that fell before this update takes the quote that stood before it
        Timestamp bookExchangeTimestamp = _position.Instrument.MarketByPrice.ExchangeTimestamp;
        for (int markoutDurationIndex = 0; markoutDurationIndex < _markoutDurations.Length; markoutDurationIndex++)
        {
            ref int observedCount = ref _observedCounts[markoutDurationIndex];
            while (observedCount < _filledCount)
            {
                ref Markout markout = ref _pendingMarkouts[observedCount & s_pendingMask];
                if (markout.FillExchangeTimestamp + _markoutDurations[markoutDurationIndex] >= bookExchangeTimestamp)
                    break;
                markout.MarkoutDuration = _markoutDurations[markoutDurationIndex];
                markout.BidPrice = _previousBidPrice;
                markout.AskPrice = _previousAskPrice;
                _markoutSeries.Append(in markout);
                observedCount++;
            }
        }

        // Step 3: this update's quote is the next one's "before"
        SetPreviousQuote();
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
    }

    /// Keeps the instrument's raw quote now, or NaN while it is not open or a side is empty.
    private void SetPreviousQuote()
    {
        bool hasQuote = _position.Instrument.TryGetQuote(out Quote quote);
        _previousBidPrice = hasQuote ? quote.BidPrice : double.NaN;
        _previousAskPrice = hasQuote ? quote.AskPrice : double.NaN;
    }

    /// Throws unless there is at least one markout duration and each is positive and longer than the one before.
    private static void ThrowIfNotAscending(Duration[] markoutDurations)
    {
        if (markoutDurations.Length == 0)
            throw new ArgumentException("Markouts.ThrowIfNotAscending: no markout durations, must be at least one; give NewMarkouts the strategy's markout durations");
        Duration _previousMarkoutDuration = Duration.Zero;
        foreach (Duration markoutDuration in markoutDurations)
        {
            if (markoutDuration <= _previousMarkoutDuration)
                throw new ArgumentException($"Markouts.ThrowIfNotAscending: markout duration {markoutDuration} after {_previousMarkoutDuration}, must be positive and strictly ascending; reorder the strategy's markout durations");
            _previousMarkoutDuration = markoutDuration;
        }
    }
}
