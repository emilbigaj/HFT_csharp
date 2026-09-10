using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;
using Data;


namespace Execution;

[RegisterJson]
public enum OrderType : byte
{
    OrderState = 10,
    OrderTarget = 11,
    OrderRejected = 12,
    Fill = 13,
    Position = 14,
    AheadOfOrder = 15,
    RiskLimit = 16,
}

[RegisterJson]
public enum TimeInForce : byte
{
    Day = 0,
    GoodTillCancel = 1,
    ImmediateOrCancel = 2,
    FillOrKill = 3,
    OpeningAuction = 4,
    ClosingAuction = 5,
}

[RegisterJson]
public enum OrderStateDoneReason : byte
{
    None = 0,
    Filled = 1,
    Canceled = 2,
    Rejected = 3,
}

[RegisterJson]
public enum OrderStateStatus : byte
{
    Done = 0,
    Active = 1,
}

[RegisterJson]
// why are we pushing a new orderstate?
public enum OrderStateReason : byte
{
    Unknown = 0,
    PendingNew = 1,
    Acked = 2,
    Fill = 3,       // partial vs complete lives in OrderStateStatus: Fill+Active / Fill+Done
    Canceled = 4,   // here onwards -> Done unconditionally
    Rejected = 5,   // create rejected, not amend/cancel rejected
    Eliminated = 6,
}

[RegisterJson]
public enum OrderRejectedReason : byte
{
    Unknown = 0,

    // ---- 00..09: Bad orderheader (Create-time validity) ----
    ClientIdNotValid       = 1,
    ClientIdNotAllocated   = 2,
    StrategyIdNotValid     = 3,
    StrategyIdNotAllocated = 4,
    InstrumentIdNotValid   = 5,
    InstrumentNotAllocated = 6,

    // ---- 10..19: Wrong Amend/Cancel orderheader (mismatch with existing) ----
    ClientIdIsWrong        = 10,
    StrategyIdIsWrong      = 11,
    InstrumentIdIsWrong    = 12,
    ClientOrderIdIsWrong   = 13,
    SeqIsWrong             = 14,

    // ---- 20..29: Bad orderprofile ----
    QuantityNotValid       = 20,
    PriceNotValid          = 21,
    SideNotValid           = 22,
    OrderTypeNotSupported  = 23, // order type/TIF itself rejected — change the order, don't resend

    // ---- 30..39: Sequencing / lifecycle: client misuse of the order slot ----
    ConnectionBroken          = 30,
    SeqOutOfOrder             = 31,
    ClientOrderIdOutOfOrder   = 32,
    CantAllocateClientOrderId = 33,
    OrderIndexIsBusy          = 34,
    OrderNotFound             = 35,
    DuplicateOrderId          = 36, // ClOrdID reuse / would overwrite a resting order

    // ---- 40..49: Discarded: intentional no-ops; system decided not to act, no alert ----
    StateIsDone            = 40,
    CreateIsActive         = 41,
    CancelIsActive         = 42,
    TargetIsActive         = 43,
    TargetIsStale          = 44,
    TooManyActiveTargets   = 45,
    AlgoIsPaused           = 46,

    // ---- 50..59: Risk and business limits ----
    NotInSession           = 50,
    PositionIsSuspended    = 51,
    QuantityExceedsRiskLimit       = 52,
    QuantityTooLarge           = 53,
    PositionExceedsRiskLimit       = 54,
    NotEnoughMargin        = 55,
    TooManyOrdersPerSecond = 56,
    TooManyOrdersPerSession    = 57,
    MessageEfficiencyViolated  = 58,
    TooManyActiveOrders = 59,
    NotAuthorizedToTrade = 60,

    // ---- 60..69: System ----
    ExceptionThrownByRiskLayer = 63,
}

[RegisterJson]
public enum OrderRejectedSource : byte
{
    Client = 0,
    Server = 1,
    Rival = 2,
    Exchange = 3,
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct OrderRejected()
{
    public Header<OrderType> Header = new(OrderType.OrderRejected);
    public OrderHeader OrderHeader;
    public OrderTargetAction OrderTargetAction;
    public OrderRejectedSource OrderRejectedSource;
    private unsafe fixed byte _reserved[2];
    public OrderProfile OrderProfile;
    public Bitset64 OrderRejectedReasons;

    public string OrderRejectedReasonsString
    {
        get
        {
            string[] reasons = new string[OrderRejectedReasons.Count];
            int i = 0;
            foreach (int orderRejectedCode in OrderRejectedReasons)
            {
                OrderRejectedReason orderRejectedReason = (OrderRejectedReason)orderRejectedCode;
                reasons[i++] = orderRejectedReason.ToString();
            }
            return string.Join("|", reasons);
        }
    }
    public override string ToString() => Json.Serialize(this);

    // Benign rejection: every reason set is a routine race, absorbed silently by all consumers.
    public bool IsDiscarded => !OrderRejectedReasons.IsEmpty && OrderRejectedReasons.IsSubsetOf(OrderDiscarded);

    public readonly static Bitset64 OrderDiscarded;

    static OrderRejected()
    {
        OrderDiscarded.Set((int)OrderRejectedReason.CreateIsActive);
        OrderDiscarded.Set((int)OrderRejectedReason.CancelIsActive);
        OrderDiscarded.Set((int)OrderRejectedReason.TargetIsActive);
        OrderDiscarded.Set((int)OrderRejectedReason.TargetIsStale);
        OrderDiscarded.Set((int)OrderRejectedReason.StateIsDone);
        OrderDiscarded.Set((int)OrderRejectedReason.AlgoIsPaused);
        OrderDiscarded.Set((int)OrderRejectedReason.TooManyOrdersPerSecond);
        OrderDiscarded.Set((int)OrderRejectedReason.TooManyActiveTargets);
    }

}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct RiskLimit(int instrumentId)
{
    public Header<OrderType> Header = new(OrderType.RiskLimit);
    public int InstrumentId = instrumentId;
    public Timestamp Timestamp = Timestamp.MinValue;
    public int StrategyId = -1;
    public int MaxOrderQuantity = 0;
    public int MaxPositionQuantity = 0;
    public int WorstLongWorkingQuantity = 0;
    public int WorstShortWorkingQuantity = 0;

    public int GetLongQuantityAllowance(int position)
    {
        return Math.Max(0, MaxPositionQuantity - position - WorstLongWorkingQuantity);
    }
     public int GetShortQuantityAllowance(int position)
    {
        return Math.Min(0, -MaxPositionQuantity - position - WorstShortWorkingQuantity);
    }

    public static RiskLimit GetMaxLimits(int instrumentId) => new RiskLimit(instrumentId)
    {
        MaxOrderQuantity = int.MaxValue,
        MaxPositionQuantity = int.MaxValue,
        Timestamp = Clock.Now,
    };
    public static RiskLimit GetMinLimits(int instrumentId) => new RiskLimit(instrumentId)
    {
        MaxOrderQuantity = 0,
        MaxPositionQuantity = 0,
        Timestamp = Clock.Now,
    };

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct OrderRisk
{
    public const int MaxOrderQuantity = ushort.MaxValue;
    public const int MaxActiveTargets = 30;

    private ushort _activeTargetsCount;                 // struct — must NOT be readonly
    private ushort _worstOrderQuantity;                 // max over the active targets, 0 when there are none
    private Array30<ushort> _absOrderQuantities;        // one abs quantity per active target, indices [0, _activeTargetsCount)

    /// <summary>Branchless abs. Returns int.MinValue for int.MinValue (no throw);
    /// callers must range-check with an unsigned compare.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int v)
    {
        uint m = (uint)(v >> 31);
        return (int)(((uint)v ^ m) - m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetAbsWorstOrderQuantity(int ackedOrderQuantity)
    {
        int absAckedOrderQuantity = Abs(ackedOrderQuantity);
        return Math.Max(absAckedOrderQuantity, _worstOrderQuantity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(int orderQuantity, out OrderRejectedReason reason)
    {
        int absOrderQuantity = Abs(orderQuantity);
        if ((uint)absOrderQuantity > MaxOrderQuantity || absOrderQuantity == 0)
        {
            reason = OrderRejectedReason.QuantityNotValid;
            return false;
        }

        int activeTargetsCount = _activeTargetsCount;
        if (activeTargetsCount == MaxActiveTargets)
        {
            reason = OrderRejectedReason.TooManyActiveTargets;
            return false;
        }

        _absOrderQuantities[activeTargetsCount] = (ushort)absOrderQuantity;
        _activeTargetsCount = (ushort)(activeTargetsCount + 1);
        _worstOrderQuantity = (ushort)Math.Max(_worstOrderQuantity, absOrderQuantity);

        reason = default;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Ack(int orderQuantity) => Remove(orderQuantity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reject(int orderQuantity) => Remove(orderQuantity);

    // AggressiveInlining: the JIT will not inline a loop-bearing method on its own (+5 ns per order lifecycle measured).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Remove(int orderQuantity)
    {
        int absOrderQuantity = Abs(orderQuantity);
        if ((uint)absOrderQuantity > MaxOrderQuantity)
            return;

        // Acks retire the oldest target, so the forward scan normally stops at index 0.
        int activeTargetsCount = _activeTargetsCount;
        int targetIndex = 0;
        while (targetIndex < activeTargetsCount && _absOrderQuantities[targetIndex] != absOrderQuantity)
            targetIndex++;
        if (targetIndex == activeTargetsCount)
            return;

        int lastTargetIndex = activeTargetsCount - 1;
        _absOrderQuantities[targetIndex] = _absOrderQuantities[lastTargetIndex];
        _absOrderQuantities[lastTargetIndex] = 0;
        _activeTargetsCount = (ushort)lastTargetIndex;

        if (absOrderQuantity != _worstOrderQuantity)
            return;

        int worstOrderQuantity = 0;
        for (int i = 0; i < lastTargetIndex; i++)
            worstOrderQuantity = Math.Max(worstOrderQuantity, _absOrderQuantities[i]);
        _worstOrderQuantity = (ushort)worstOrderQuantity;
    }
}

[RegisterJson]
public enum OrderTargetAction : byte
{
    Create = 0,
    Amend = 1,
    Cancel = 2,
}

[Flags]
[RegisterJson]
public enum OrderFlags : byte
{
    None = 0,
    PostOnly = 1 << 0,
    ReduceOnly = 1 << 1,
    Hidden = 1 << 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct OrderProfile(int ticks, int quantity)
{
    public int Ticks = ticks;       // 4
    public int Quantity = quantity;  // 4

    public Side Side
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Side)Sign; }
    }

    public int Sign
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Math.Sign(Quantity); }
    }

    // size = 8

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(OrderProfile left, OrderProfile right) => left.Ticks == right.Ticks && left.Quantity == right.Quantity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(OrderProfile left, OrderProfile right) => left.Ticks != right.Ticks || left.Quantity != right.Quantity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsThisMoreAggressive(int ticks)
    {
        return (Ticks - ticks) * Sign > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsThisCrossing(int ticks)
    {
        return (Ticks - ticks) * Sign >= 0;
    }

    public static OrderProfile Cancel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get{ return new OrderProfile(0, 0); }
    }

    public override string ToString() => Json.Serialize(this);

}

[StructLayout(LayoutKind.Sequential, Pack = 1)] // 28 bytes
[RegisterJson]
public struct OrderHeader
{
    public int Seq;
    public OrderId OrderId;
    public Timestamp ExchangeTimestamp;
    public Timestamp NicTimestamp;
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct Fill()
{
    // 64 bytes; Price sits at offset 40 (4+28+8), naturally 8-aligned. Price is a PRICE, not
    // ticks: spread leg fills are assigned at increments finer than the leg's trading grid (CME
    // leg pricing), so a fill is a terminal price fact — never quantize it back to a grid.
    public Header<OrderType> Header = new(OrderType.Fill);
    public OrderHeader OrderHeader;
    public ulong FillId;
    public double Price;
    public int Quantity;
    public FillType FillType;
    private unsafe fixed byte _reserved[11];

    public Side Side
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Side)Sign; }
    }

    public int Sign
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Math.Sign(Quantity); }
    }

    public override string ToString() => Json.Serialize(this);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct OrderState()
{
    public Header<OrderType> Header = new(OrderType.OrderState);
    public OrderHeader OrderHeader;
    public ulong ExchangeOrderId;
    public OrderProfile OrderProfile;
    public TimeInForce TimeInForce;
    public OrderStateStatus OrderStateStatus;
    public OrderStateReason OrderStateReason;
    private unsafe fixed byte _reserved[1];
    public int QuantityFilled;
    public int QuantityAhead;

    public int WorkingQuantity => OrderProfile.Quantity - QuantityFilled;
    public override string ToString() => Json.Serialize(this);
}

// 56 bytes total (all 8-byte fields first, small field last; natural packing)
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct AheadOfOrder(ulong clientOrderid, int quantity)
{
    public Header<OrderType> Header = new(OrderType.AheadOfOrder);
    public int Quantity = quantity;
    public ulong ClientOrderId = clientOrderid;
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct OrderTarget()
{
    public Header<OrderType> Header = new(OrderType.OrderTarget);
    public OrderHeader OrderHeader;
    public OrderProfile OrderProfile; // 8
    public TimeInForce TimeInForce;  // 1
    public OrderTargetAction OrderTargetAction;  // 1
    public OrderStateStatus OrderTargetStatus = OrderStateStatus.Active;
    private unsafe fixed byte _reserved[1];
    public override string ToString() => Json.Serialize(this);
}

[RegisterJson]
public enum AlgoStatus : byte
{
    Paused,
    Live,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct PositionHeader()
{
    public readonly Header<OrderType> Header = new(OrderType.Position);
    public OrderHeader OrderHeader;                                    
    public int Quantity = 0;                                           
    public double AvgPrice = double.NaN;
    public double RealizedProfit = 0;
    public int QuantityTraded = 0;
    public AlgoStatus AlgoStatus = AlgoStatus.Paused;

    public override string ToString() => Json.Serialize(this);

    public void OnFill(in Fill fill, double multiplier)
    {
        int quantity = fill.Quantity;
        double price = fill.Price;
        OrderHeader = fill.OrderHeader;

        int oldQty = Quantity;
        int newQty = oldQty + quantity;
        int oldSide = Math.Sign(oldQty);
        int fillSide = Math.Sign(quantity);

        QuantityTraded += Math.Abs(quantity);
        if (oldQty == 0 || oldSide == fillSide)
        {
            // Opening or adding to the same side (sign-coded quantities)
            AvgPrice = (oldQty == 0) ? price : (AvgPrice * oldQty + price * quantity) / newQty;
        }
        else
        {
            // Opposite-side trade: close some or all of the existing position
            int closed = Math.Min(Math.Abs(oldQty), Math.Abs(quantity));
            double pnl = (price - AvgPrice) * closed * oldSide * multiplier;
            RealizedProfit += pnl;

            if (newQty == 0)
            {
                // Fully flat
                AvgPrice = double.NaN;
            }
            else if (Math.Sign(newQty) == oldSide)
            {
                // Partial close, still same side as before – keep old AvgPrice
                // (Quantity shrank; average entry doesn't change)
            }
            else
            {
                // Flipped side: remaining amount is net new position at this fill price
                AvgPrice = price;
            }
        }
        Quantity = newQty;
    }
}
