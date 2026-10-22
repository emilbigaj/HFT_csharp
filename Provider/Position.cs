using Data;
using Execution;
using Socket;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Tools;

namespace Provider;

[RegisterJson]
public record struct ActiveTarget(ulong ClientOrderId, int QuantityAhead, int QuantityBehind, int QuantityFilled, int Seq, Target Target)
{
    public override string ToString()
    {
        return Json.SerializeToLine(this);
    }
}

[RegisterJson]
public record struct Target(int Ticks, int WorkingQuantity, TimeInForce TimeInForce = TimeInForce.Day)
{
    public readonly static Target Cancel = new Target(0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetQuantity(int workingQuantity)
    {
        WorkingQuantity = workingQuantity;
    }

    public int Sign
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Math.Sign(WorkingQuantity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsThisMoreAggressive(int ticks)
    {
        return Sign > 0 ? Ticks > ticks : Ticks < ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsThisCrossing(int ticks)
    {
        return Sign > 0 ? Ticks >= ticks : Ticks <= ticks;
    }

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}


[RegisterJson]
public struct Profit(Timestamp timestamp, double total, double floating, double realized, int quantity, double avgPrice, double midPrice)
{
    public Timestamp Timestamp = timestamp;
    public double Total = total;
    public double Floating = floating;
    public double Realized = realized;
    public int Quantity = quantity;
    public double AvgPrice = avgPrice;
    public double MidPrice = midPrice;

    public override string ToString()
    {
        return Json.SerializeToLine(this);
    }
}



public sealed class Position
{
    public AlgoStatus AlgoStatus => _headerEntry.GetReadonlyRef().AlgoStatus;

    // True while this strategy's algo may trade the instrument: its own switch and the server's master both Live. Hot path: two byte reads.
    public bool IsAlgoLive => _context.IsAlgoLive(_clientId, Instrument.InstrumentId);

    // Context reference removed.
    public Instrument Instrument { get; }

    // Direct access to the specific header entry for this position
    private readonly SharedArrayEntry<PositionHeader> _headerEntry;
    public ref readonly SharedArrayEntry<PositionHeader> PositionHeader => ref _headerEntry;


    public Profit Profit
    {
        get
        {
            unsafe
            {
                // Access the header directly from the shared memory entry
                if (_headerEntry.IsEmpty())
                    return new Profit(Clock.Now, double.NaN, double.NaN, 0, 0, double.NaN, double.NaN);

                PositionHeader positionHeader = _headerEntry.Read();

                if (_context.TryGetQuote(Instrument.InstrumentId, out Quote quote))
                {
                    double floating = Instrument.GetProfit(positionHeader.AvgPrice, quote.MidPrice, positionHeader.Quantity);
                    return new Profit(Clock.Now, floating + positionHeader.RealizedProfit, floating, positionHeader.RealizedProfit, positionHeader.Quantity, positionHeader.AvgPrice, quote.MidPrice);
                }
                else
                {
                    return new Profit(Clock.Now, double.NaN, double.NaN, positionHeader.RealizedProfit, positionHeader.Quantity, positionHeader.AvgPrice, double.NaN);
                }
            }
        }
    }

    public event PositionHeaderHandler? PositionChanged;
    private PositionHeader _lastPositionHeader;

    // Phase 1 of a ReadSocket pass, per message: keep the latest header, raise nothing (see Spec.md).
    public void ApplyPositionHeader(in PositionHeader positionHeader)
    {
        _lastPositionHeader = positionHeader;
    }

    // Phase 2, once per pass.
    public void RaiseChanged()
    {
        PositionChanged?.Invoke(in _lastPositionHeader);
    }

    public event RefAction<Fill>? Fill;
    public void OnFill(in Fill fill)
    {
        Fill?.Invoke(in fill);
    }
    public void OnOrderDone(int localOrderIndex)
    {
        _isOrderActive.Clear(localOrderIndex);
    }

    public void OnOrderActive(int localOrderIndex)
    {
        _isOrderActive.Set(localOrderIndex);
    }

    // _isOrderActive holds LOCAL slots, but order rows are addressed globally. The owner comes from
    // the context, not from this row's header: the header is persisted and restamped by the server on
    // allocation (and AllocateInstrument can return early without writing it at all), so it is not a
    // sound source of identity. _ownerClientId is fixed for the life of the context.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderId GetOrderId(int localOrderIndex)
    {
        OrderId orderId = default;
        orderId.ClientId = _clientId;
        orderId.LocalIndex = localOrderIndex;
        return orderId;
    }

    public Bitset64 IsOrderActive => _isOrderActive;
    private Bitset64 _isOrderActive = new Bitset64();
    private readonly Context _context;
    private readonly int _clientId;

    public Position(Instrument instrument, Context context, int clientId)
    {
        Instrument = instrument;
        _context = context;
        _clientId = clientId;
        _headerEntry = context.GetPositionHeader(instrument.InstrumentId);
    }


    private Quote _reconciledQuote;           // the last quote from a book that showed all our orders' latest states
    private bool _isReconciledQuoteValid;     // false until the first such quote, and again at every close

    // Our quote with our own resting orders taken out of the book; while the book and our order states describe different
    // moments it holds the last quote they agreed on (see Spec.md). False while the instrument is not open or a side is empty.
    [SkipLocalsInit]
    public bool TryGetQuote(out Quote quote)
    {
        using Latency latency = new Latency(CallId.PositionTryGetQuote);

        // Step 1: no quote while the instrument is not open, and none held across a close
        ref readonly MarketByPrice64 marketByPrice = ref Instrument.MarketByPrice;
        if (Instrument.Header.TradingStatus != TradingStatus.Open)
        {
            _isReconciledQuoteValid = false;
            quote = default;
            return false;
        }

        // Step 2: our resting levels per side, while the book shows every order's latest state
        bool isBookCaughtUp = true;
        Bitset64 isOrderActive = _isOrderActive;
        StackList<Level> userBids = new StackList<Level>(stackalloc Level[isOrderActive.Count]);
        StackList<Level> userAsks = new StackList<Level>(stackalloc Level[isOrderActive.Count]);
        while (isBookCaughtUp && !isOrderActive.IsEmpty)
        {
            int localOrderIndex = isOrderActive.LowestSet;
            isOrderActive.Clear(localOrderIndex);
            isBookCaughtUp = TryGetActiveLevel(localOrderIndex, marketByPrice.ExchangeTimestamp, out Side side, out Level level);
            if (level.Quantity == 0)
                continue;
            if (side == Side.Buy)
                userBids.Add(level);
            else
                userAsks.Add(level);
        }

        // Step 3: a caught-up book gives a fresh quote; otherwise hold the last one
        if (isBookCaughtUp)
        {
            bool hasBestBid = marketByPrice.Bids.TryGetBestLevelExcluding(userBids.AsSpan(), out Level bestBid);
            bool hasBestAsk = marketByPrice.Asks.TryGetBestLevelExcluding(userAsks.AsSpan(), out Level bestAsk);
            _isReconciledQuoteValid = hasBestBid && hasBestAsk;
            _reconciledQuote = _isReconciledQuoteValid ? new Quote(bestBid, bestAsk, Instrument.TickSize) : default;
        }
        quote = _reconciledQuote;
        return _isReconciledQuoteValid;
    }

    // One order's resting level as the book shows it (quantity 0 when it rests nowhere: an IOC or a Done order); false while
    // the exchange has not answered its latest target or the book has not caught up with its state.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetActiveLevel(int localOrderIndex, Timestamp bookExchangeTimestamp, out Side side, out Level level)
    {
        // Step 1: an IOC never rests
        OrderId orderId = GetOrderId(localOrderIndex);
        ref readonly OrderTarget target = ref _context.GetOrderTarget(orderId).GetReadonlyRef();
        side = target.OrderProfile.Side;
        level = default;
        if (target.TimeInForce == TimeInForce.ImmediateOrCancel)
            return true;

        // Step 2: read the state under its seqlock and compare its exchange time with the book's
        SharedArrayEntry<OrderState> stateEntry = _context.GetOrderState(orderId);
        ref readonly OrderState state = ref stateEntry.GetReadonlyRef();
        while (true)
        {
            ulong seq0 = stateEntry.GetSeq();
            if (Protocol.IsWriteInProgress(seq0))
            {
                X86BaseWrapper.Pause();
                continue;
            }
            bool isStateInBook = target.IsConfirmedBy(in state) && state.OrderHeader.ExchangeTimestamp <= bookExchangeTimestamp;
            bool isDone = state.OrderStateStatus == OrderStateStatus.Done;
            level = isDone ? default : new Level(state.OrderProfile.Ticks, Math.Abs(state.WorkingQuantity));
            if (seq0 == stateEntry.GetSeq())
                return isStateInBook;
        }
    }

    public ActiveTargetsEnumerable ActiveTargets
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return new ActiveTargetsEnumerable(this); }
    }

    // --- Nested types ---

    public readonly struct ActiveTargetsEnumerable
    {
        private readonly Position _position;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ActiveTargetsEnumerable(Position position)
        {
            _position = position;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ActiveTargetsEnumerator GetEnumerator()
        {
            return new ActiveTargetsEnumerator(_position);
        }
    }

    public ref struct ActiveTargetsEnumerator
    {
        private Bitset64 _isOrderActive;
        private Context _context;
        private ActiveTarget _current;
        private Position _position;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ActiveTargetsEnumerator(Position position)
        {
            // We can access private members of Position because we are a nested type
            _position = position;
            _context = position._context;
            _isOrderActive = position._isOrderActive;
            _current = default;
        }

        public readonly ActiveTarget Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _current; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            //using Latency latency = new Latency(CallId.PositionGetActiveTargets);
        NextOrder:
            while (!_isOrderActive.IsEmpty)
            {
                int localOrderIndex = _isOrderActive.LowestSet;
                _isOrderActive.Clear(localOrderIndex);

                OrderId orderId = _position.GetOrderId(localOrderIndex);
                SharedArrayEntry<OrderState> stateEntry = _context.GetOrderState(orderId);

                // 1. Get Reference (Zero-Copy)
                ref readonly OrderState state = ref stateEntry.GetReadonlyRef();
                ref readonly OrderTarget target = ref _context.GetOrderTarget(orderId).GetReadonlyRef();

                // An IOC never rests, so it is never an active target; it stays reserved in the client's WorkingRisk until its Done.
                if (target.TimeInForce == TimeInForce.ImmediateOrCancel)
                    goto NextOrder;

                ulong seq0, seq1 = 0;
                while(true)
                {
                    seq0 = stateEntry.GetSeq();
                    if (Protocol.IsWriteInProgress(seq0))
                    {
                        X86BaseWrapper.Pause();
                        continue;
                    }

                    // if last target is rejected we assume OrderState is truth
                    bool targetRejected = target.OrderTargetStatus == OrderStateStatus.Done;

                    // might be false if New not set yet by server, OrderTargetAction.Create must be inflight
                    bool isSameOrder = state.OrderHeader.OrderId == target.OrderHeader.OrderId;

                    // Until the server reads the Create, the state row still holds the slot's previous order: its fills are not ours.
                    int quantityFilled = isSameOrder ? state.QuantityFilled : 0;

                    bool targetIsCancel = !targetRejected && (target.OrderTargetAction == OrderTargetAction.Cancel || target.OrderProfile.Sign * (target.OrderProfile.Quantity - quantityFilled) <= 0);

                    bool stateIsTruth = target.IsConfirmedBy(in state);

                    // Done once the state says so, or as soon as a cancel is sent, even before the server has read the Create.
                    bool isOrderDone = (isSameOrder && state.OrderStateStatus == OrderStateStatus.Done) || targetIsCancel;

                    if (isOrderDone)
                    {
                        seq1 = stateEntry.GetSeq();
                        if (seq0 != seq1)
                            continue; // Retry inner loop if torn read
                        // A cancel-pending order stays reserved in the client's WorkingRisk until its Done (see Spec.md).
                        goto NextOrder;
                    }

                    // An in-flight reduce keeps the order's place in the queue; a new price or more size does not.
                    bool reduceOnly = !targetRejected && isSameOrder && target.OrderTargetAction == OrderTargetAction.Reduce && target.OrderHeader.Seq == state.OrderHeader.Seq + 1;

                    OrderProfile orderProfile = stateIsTruth ? state.OrderProfile : target.OrderProfile;
                    int quantityAhead;
                    int quantityBehind;
                    if (stateIsTruth || reduceOnly)
                    {
                        long queuePosition = Unsafe.As<int, long>(ref Unsafe.AsRef(in state.QuantityAhead));   // one load: Server.OnQuantityAhead writes the pair with one store
                        quantityAhead = (int)queuePosition;
                        quantityBehind = (int)(queuePosition >> 32);
                    }
                    else
                    {
                        // Unconfirmed: it joins behind everything resting at its price, as Server's InFlight seed assumes.
                        ref readonly MarketByPrice64 mbp64 = ref _position.Instrument.MarketByPrice;
                        quantityAhead = orderProfile.Side == Side.Buy ? mbp64.Bids.GetQuantity(orderProfile.Ticks) : mbp64.Asks.GetQuantity(orderProfile.Ticks);
                        quantityBehind = 0;
                    }
                    seq1 = stateEntry.GetSeq();

                    int workingQuantity = orderProfile.Quantity - quantityFilled;
                    _current = new ActiveTarget(target.OrderHeader.OrderId, quantityAhead, quantityBehind, quantityFilled, target.OrderHeader.Seq, new Target(orderProfile.Ticks, workingQuantity, target.TimeInForce));

                    if (seq0 == seq1)
                        break;
                }

                return true;
            }

            return false;
        }
    }
}