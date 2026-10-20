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
    WorkingRisk = 17,
    MessageEfficiency = 18,   // a MessageEfficiency row the server posts on its audit channel at a session's close and at shutdown
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
    InFlight = 1,   // sent, not yet acked by the exchange (the server writes it; a walk shows it until the server answers)
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
    TooManyFillsPerSession     = 57,   // the session's fills could pass RiskLimit.MaxFillsPerSession (was TooManyOrdersPerSession, never raised)
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
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
public struct OrderRejected()
{
    public Header<OrderType> Header = new(OrderType.OrderRejected);
    public OrderHeader OrderHeader;
    public OrderTargetAction OrderTargetAction;
    public OrderRejectedSource OrderRejectedSource;
    // Why: plain bytes, not a fixed buffer: a fixed buffer puts a stack-cookie check in every frame holding the struct
    private byte _reserved0;
    private byte _reserved1;
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

    // Why: a constant, not a static constructor: that cost every OrderRejected constructor a class-init check and kept IsDiscarded from inlining
    private const ulong s_orderDiscardedMask = 1UL << (int)OrderRejectedReason.CreateIsActive | 1UL << (int)OrderRejectedReason.CancelIsActive
        | 1UL << (int)OrderRejectedReason.TargetIsActive | 1UL << (int)OrderRejectedReason.TargetIsStale | 1UL << (int)OrderRejectedReason.StateIsDone
        | 1UL << (int)OrderRejectedReason.AlgoIsPaused | 1UL << (int)OrderRejectedReason.TooManyOrdersPerSecond | 1UL << (int)OrderRejectedReason.TooManyActiveTargets;
    public static Bitset64 OrderDiscarded => new Bitset64(s_orderDiscardedMask);
}


[StructLayout(LayoutKind.Sequential, Pack = 1)] // 28 bytes: config only, written on edits
[RegisterJson]
public struct RiskLimit(int instrumentId)
{
    public Header<OrderType> Header = new(OrderType.RiskLimit);
    public int InstrumentId = instrumentId;
    public Timestamp Timestamp = Timestamp.MinValue;
    public int MaxOrderQuantity = 0;
    public int MaxPositionQuantity = 0;
    public int MaxFillsPerSession = 0;

    public readonly int GetLongQuantityAllowance(in WorkingRisk workingRisk)
    {
        return Math.Max(0, MaxPositionQuantity - workingRisk.Position - workingRisk.WorstWorkingLongQuantity);
    }
    public readonly int GetShortQuantityAllowance(in WorkingRisk workingRisk)
    {
        return Math.Min(0, -MaxPositionQuantity - workingRisk.Position - workingRisk.WorstWorkingShortQuantity);
    }

    public static RiskLimit GetMaxLimits(int instrumentId) => new RiskLimit(instrumentId)
    {
        MaxOrderQuantity = int.MaxValue,
        MaxPositionQuantity = int.MaxValue,
        MaxFillsPerSession = int.MaxValue,
        Timestamp = Clock.Now,
    };
    public static RiskLimit GetMinLimits(int instrumentId) => new RiskLimit(instrumentId)
    {
        MaxOrderQuantity = 0,
        MaxPositionQuantity = 0,
        MaxFillsPerSession = 0,
        Timestamp = Clock.Now,
    };

    // The file's last saved limit, or with no file the mode's default: Max in a simulation, Min in realtime. Allocation only.
    public static RiskLimit FromFile(string riskLimitPath, int instrumentId)
    {
        string? riskLimitLine = Tools.Tools.ReadLastLine(riskLimitPath);
        RiskLimit riskLimit = riskLimitLine != null ? Json.Deserialize<RiskLimit>(riskLimitLine) : Clock.Mode == ClockMode.Simulation ? GetMaxLimits(instrumentId) : GetMinLimits(instrumentId);
        riskLimit.InstrumentId = instrumentId;
        return riskLimit;
    }

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

// What RiskLayer has applied for an instrument: its fills and worst-case working reservations. Never persisted.
[StructLayout(LayoutKind.Sequential, Pack = 1)] // 24 bytes
[RegisterJson]
public struct WorkingRisk()
{
    public Header<OrderType> Header = new(OrderType.WorkingRisk);   // the TCP mirror dispatches every row on its first byte
    public int Position = 0;                       // moves in the same RiskLayer.OnFill as the reservation release
    public int WorstWorkingLongQuantity = 0;       // >= 0: the most the working buys could still fill
    public int WorstWorkingShortQuantity = 0;      // <= 0: the most the working sells could still fill
    public int FillsThisSession = 0;               // >= 0: |fills| of algo orders, both sides, since the session opened (0 again at its Closed); manual fills never count
    public int WorstWorkingFills = 0;              // >= 0: the most the working algo orders, both sides, could still fill: the fills room's reservation

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct OrderRisk
{
    public const int MaxOrderQuantity = ushort.MaxValue;
    public const int MaxActiveTargets = 29;

    private ushort _activeTargetsCount;                 // struct — must NOT be readonly
    private ushort _worstOrderQuantity;                 // max over the active targets, 0 when there are none
    private ushort _absAckedOrderQuantity;              // the exchange's acked quantity, 0 until the first ack
    private Array29<ushort> _absOrderQuantities;        // one abs quantity per active target, indices [0, _activeTargetsCount)

    /// <summary>Branchless abs. Returns int.MinValue for int.MinValue (no throw);
    /// callers must range-check with an unsigned compare.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int v)
    {
        uint m = (uint)(v >> 31);
        return (int)(((uint)v ^ m) - m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetAbsWorstOrderQuantity()
    {
        return Math.Max(_absAckedOrderQuantity, _worstOrderQuantity);
    }

    // TryAdd refuses another target once this many are in flight.
    public readonly bool IsFull => _activeTargetsCount == MaxActiveTargets;

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
    public void Ack(int orderQuantity)
    {
        Remove(orderQuantity);
        _absAckedOrderQuantity = (ushort)Abs(orderQuantity);   // in range: every acked quantity passed TryAdd
    }

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
    Replace = 1,   // a new price or more quantity: loses queue priority (was Amend, same wire value)
    Cancel = 2,
    Reduce = 3,    // less quantity at the same price: keeps queue priority, never adds risk
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

    // Less quantity at the same price and side: keeps queue priority and can never add risk.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsReduceOf(in OrderProfile orderProfile)
    {
        return Ticks == orderProfile.Ticks && Sign == orderProfile.Sign && Math.Abs(Quantity) < Math.Abs(orderProfile.Quantity);
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


[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]   // Why: Size pads the tail, not a fixed buffer: that puts a stack-cookie check in every frame holding the struct
[RegisterJson]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
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

[StructLayout(LayoutKind.Sequential, Pack = 1)] // 64 bytes: no room left
[RegisterJson]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
public struct OrderState()
{
    public Header<OrderType> Header = new(OrderType.OrderState);
    public OrderHeader OrderHeader;
    public ulong ExchangeOrderId;
    public OrderProfile OrderProfile;
    public TimeInForce TimeInForce;
    public OrderStateStatus OrderStateStatus;
    public OrderStateReason OrderStateReason;
    private byte _reserved;   // Why: a plain byte, not a fixed buffer: that puts a stack-cookie check in every frame holding the struct
    public int QuantityFilled;
    public int QuantityAhead;
    public int QuantityBehind;   // same queue as QuantityAhead; only the simulator reports it

    public int WorkingQuantity => OrderProfile.Quantity - QuantityFilled;

    /// The state of a Create the exchange has not acked yet: Active, InFlight, nothing filled, Seq 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NewInFlight(in OrderTarget orderTarget, out OrderState orderState)
    {
        // Why: constructor, then field stores, into the caller's variable: new() { ... } builds a temporary and copies it (measured +7 ns)
        orderState = new();
        orderState.OrderHeader = orderTarget.OrderHeader;
        orderState.OrderHeader.Seq = 0;   // Why: Seq 0 marks a new order the exchange has not acked
        orderState.OrderProfile = orderTarget.OrderProfile;
        orderState.TimeInForce = orderTarget.TimeInForce;
        orderState.OrderStateStatus = OrderStateStatus.Active;
        orderState.OrderStateReason = OrderStateReason.InFlight;
    }

    /// This state ended by its Create's refusal: Done/Rejected, with the refused order's header and profile.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NewRejected(in OrderRejected orderRejected, out OrderState rejectedState)
    {
        // Why: out, not a return: returning by value costs an extra 64-byte copy (+4.7 ns) when the caller passes the result by ref
        rejectedState = this;
        rejectedState.OrderHeader = orderRejected.OrderHeader;
        rejectedState.OrderProfile = orderRejected.OrderProfile;
        rejectedState.OrderStateStatus = OrderStateStatus.Done;
        rejectedState.OrderStateReason = OrderStateReason.Rejected;
    }

    /// The target that cancels this order: its header and profile, action Cancel. Cold path (a GUI's cancel).
    public readonly OrderTarget NewCancel() => new()
    {
        OrderHeader = OrderHeader,
        OrderProfile = OrderProfile,
        OrderTargetAction = OrderTargetAction.Cancel,
    };

    public override string ToString() => Json.Serialize(this);
}

// 20 bytes
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct AheadOfOrder(ulong clientOrderid, int quantityAhead, int quantityBehind)
{
    public Header<OrderType> Header = new(OrderType.AheadOfOrder);
    public int QuantityAhead = quantityAhead;
    public ulong ClientOrderId = clientOrderid;
    public int QuantityBehind = quantityBehind;
}


[StructLayout(LayoutKind.Sequential, Pack = 1)] // 52 bytes
[RegisterJson]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
public struct OrderTarget()
{
    public const int s_foreignCancelSeqJump = 1_000_000;   // a cancel sent by someone other than the order's owner jumps past the owner's own seqs

    public Header<OrderType> Header = new(OrderType.OrderTarget);
    public OrderHeader OrderHeader;
    public Timestamp TriggerTimestamp; // NIC arrival of the message this target reacted to; OrderHeader.NicTimestamp is the send time
    public OrderProfile OrderProfile; // 8
    public TimeInForce TimeInForce;  // 1
    public OrderTargetAction OrderTargetAction;  // 1
    public OrderStateStatus OrderTargetStatus = OrderStateStatus.Active;
    private byte _reserved;   // Why: a plain byte, not a fixed buffer: that puts a stack-cookie check in every frame holding the struct

    /// The reject that refuses this target, for these reasons, from this source.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NewOrderRejected(Bitset64 orderRejectedReasons, OrderRejectedSource orderRejectedSource, out OrderRejected orderRejected)
    {
        // Why: constructor, then field stores, into the caller's variable: new() { ... } builds a temporary and copies it (measured +7 ns)
        orderRejected = new();
        orderRejected.OrderHeader = OrderHeader;
        orderRejected.OrderTargetAction = OrderTargetAction;
        orderRejected.OrderRejectedSource = orderRejectedSource;
        orderRejected.OrderProfile = OrderProfile;
        orderRejected.OrderRejectedReasons = orderRejectedReasons;
    }

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
