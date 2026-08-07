using Data;
using Execution;
using Socket;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Tools;

namespace Provider;

[RegisterJson]
public record struct ActiveTarget(ulong ClientOrderId, int QuantityAhead, int QuantityFilled, int Seq, Target Target)
{
    public override string ToString()
    {
        return Json.SerializeToLine(this);
    }
}

[RegisterJson]
public record struct Target(int Ticks, int WorkingQuantity)
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
    public AlgoStatus AlgoStatus => Header.AlgoStatus;

    // Context reference removed.
    public Instrument Instrument { get; }

    // Direct access to the specific header entry for this position
    private readonly SharedArrayEntry<PositionHeader> _headerEntry;
    public ref readonly PositionHeader Header => ref _headerEntry.GetReadonlyRef();


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

                if (Instrument.TryGetQuote(out Quote quote))
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

    public event PositionHeaderHandler? PositionHeader;
    public void OnPositionHeader(in PositionHeader positionHeader)
    {
        PositionHeader?.Invoke(in positionHeader);
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


    public bool TryGetQuote(out Quote quote)
    {
        using Latency latency = new Latency(CallId.PositionTryGetQuote);

        MarketByPrice64 mbp = Instrument.MarketByPrice;
        Bitset64 isOrderActive = _isOrderActive; // Snapshot

    NextOrder:
        while (!isOrderActive.IsEmpty)
        {
            int localOrderIndex = isOrderActive.LowestSet;
            isOrderActive.Clear(localOrderIndex);

            // 1. Setup Pointers
            SharedArrayEntry<OrderState> stateEntry = _context.GetOrderState(GetOrderId(localOrderIndex));
            

            // Clear bit and move to next for next iteration

            ref readonly OrderState state = ref stateEntry.GetReadonlyRef();

            // Variables to extract safely
            int ticks = 0;
            int working = 0;
            Side side = Side.Buy;

            ulong seq0, seq1 = 0;
            while(true)
            {
                seq0 = stateEntry.GetSeq();
                if (Protocol.IsWriteInProgress(seq0))
                {
                    X86BaseWrapper.Pause();
                    continue;
                }

                if (state.OrderStateStatus == OrderStateStatus.Done)
                {
                    seq1 = stateEntry.GetSeq(); 
                    if (seq0 != seq1)
                        continue;
                    goto NextOrder;
                }

                ticks = state.OrderProfile.Ticks;
                side = state.OrderProfile.Side;
                working = Math.Abs(state.OrderProfile.Quantity - state.QuantityFilled);

                seq1 = stateEntry.GetSeq();

                if (seq0 == seq1)
                    break;
            }

            ref SideByPrice64 sbp = ref side == Side.Buy ? ref mbp.Bids : ref mbp.Asks;
            int total = sbp.GetQuantity(ticks);
            int quantity = Math.Max(total - working, 0);
            sbp.TrySetQuantity(ticks, quantity, out _);
        }

        if (!Instrument.IsInSession || mbp.BidsCount == 0 || mbp.AsksCount == 0)
        {
            quote = default;
            return false;
        }

        quote = new Quote(mbp.BestBid, mbp.BestAsk, Instrument.TickSize);
        return true;
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
                    bool sameOrder = state.OrderHeader.OrderId == target.OrderHeader.OrderId;

                    // fills already reported by the exchange overtook the inflight target's total quantity,
                    // so the exchange is guaranteed to reject it (QuantityNotValid) — same check as the server side
                    targetRejected |= sameOrder && target.OrderProfile.Sign * (target.OrderProfile.Quantity - state.QuantityFilled) < 0;

                    bool stateIsTruth = targetRejected || (sameOrder && state.OrderHeader.Seq >= target.OrderHeader.Seq);

                    // if its the same order and state says done (if its not the same order that suggest create still inflight)
                    bool isOrderDone = sameOrder && state.OrderStateStatus == OrderStateStatus.Done;

                    if (isOrderDone)
                    {
                        seq1 = stateEntry.GetSeq();
                        if (seq0 != seq1)
                            continue; // Retry inner loop if torn read
                        goto NextOrder;
                    }

                    // if the 
                    bool reduceOnly = !targetRejected && sameOrder && target.OrderHeader.Seq == state.OrderHeader.Seq + 1 && Math.Abs(target.OrderProfile.Quantity) <= Math.Abs(state.OrderProfile.Quantity);

                    int quantityFilled = sameOrder ? state.QuantityFilled : 0;
                    int quantityAhead = stateIsTruth || reduceOnly ? state.QuantityAhead : int.MaxValue;
                    OrderProfile orderProfile = stateIsTruth ? state.OrderProfile : target.OrderProfile;
                    seq1 = stateEntry.GetSeq();

                    int workingQuantity = orderProfile.Quantity - quantityFilled;
                    _current = new ActiveTarget(target.OrderHeader.OrderId, quantityAhead, quantityFilled, target.OrderHeader.Seq, new Target(orderProfile.Ticks, workingQuantity));

                    if (seq0 == seq1)
                        break;
                }

                return true;
            }

            return false;
        }
    }
}