//BEGIN_FILE HFT/Execution/RiskManager.cs
using System;
using System.Runtime.CompilerServices;
using Tools;
using Data;
using Execution;
using Socket;

namespace Provider;

// This class is not thread safe. Only one thread should ever use it.
public class RiskLayer
{
    // The server's context on the server, the client's own on a client: OrderRisks and WorkingRisks are per context.
    private readonly Context _context;
    // CLIENT-side only: the high-water mark of this client's own allocations. Valid there because
    // validation runs at send time on one thread, so validation order IS allocation order. The
    // server must NOT run this check: ids come from one per-client counter but travel on per-core-
    // group rings read by different threads, so two same-instant creates on different instruments
    // can legitimately arrive out of allocation order — the old per-client array rejected the
    // slower one (ClientOrderIdOutOfOrder) and paused the algo, nondeterministically.
    private OrderId _maxClientOrderId;
    private readonly OrderRejectedSource _orderRejectedSource;
    public RiskLayer(Context context, OrderRejectedSource orderRejectedSource)
    {
        _context = context;
        _orderRejectedSource = orderRejectedSource;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateClient(int clientId, int strategyId)
    {
        Bitset64 orderRejectedReasons = new Bitset64();
        ref readonly ServerHeader serverHeader = ref _context.ServerHeader.GetReadonlyRef();

        bool isClientIdValid = clientId >= 0 && clientId < serverHeader.ClientIds.Length;
        if (!isClientIdValid)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ClientIdNotValid);
            return orderRejectedReasons;

        }
        if (!serverHeader.ClientIds[clientId])
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ClientIdNotAllocated);
        }

        bool isStrategyIdValid = strategyId >= 0 && strategyId < serverHeader.ClientIds.Length;
        if (!isStrategyIdValid)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.StrategyIdNotValid);
            return orderRejectedReasons;
        }
        if (!serverHeader.ClientIds[strategyId])
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.StrategyIdNotAllocated);
        }
        return orderRejectedReasons;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateInstrument(int strategyId, int instrumentId)
    {
        Bitset64 orderRejectedReasons = new Bitset64();
        ref readonly ServerHeader serverHeader = ref _context.ServerHeader.GetReadonlyRef();

        bool isValidInstrumentId = instrumentId >= 0 && instrumentId < _context.InstrumentIds.Length;
        if (!isValidInstrumentId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentIdNotValid);
            return orderRejectedReasons;
        }

        if (!_context.GetInstrumentIdsByClientId(strategyId).GetReadonlyRef()[instrumentId])
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentNotAllocated);
        }

        Instrument instrument = _context.GetInstrument(instrumentId);

        if (instrument.Header.TradingStatus != TradingStatus.Open)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.NotInSession);
        }

        return orderRejectedReasons;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateCreate(in OrderTarget orderTarget, in OrderState orderState)
    {
        Bitset64 orderRejectedReasons = new Bitset64();
        if (orderTarget.OrderHeader.Seq != 1)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);
        }

        // Client side only — see _maxClientOrderId. The server keeps OrderIndexIsBusy and the
        // amend/cancel header checks; a duplicate create cannot reach it anyway (the ring is
        // read-once, Recover() skips the backlog, a restarted client seeds higher generations).
        if (_orderRejectedSource == OrderRejectedSource.Client)
        {
            if (orderTarget.OrderHeader.OrderId <= _maxClientOrderId)
            {
                orderRejectedReasons.Set((int)OrderRejectedReason.ClientOrderIdOutOfOrder);
            }
            else
            {
                _maxClientOrderId = orderTarget.OrderHeader.OrderId;
            }
        }
        if (orderState.OrderStateStatus == OrderStateStatus.Active)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.OrderIndexIsBusy);
        }
        return orderRejectedReasons;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateOrderHeader(in OrderHeader stateOrderHeader, in OrderHeader targetOrderHeader)
    {
        Bitset64 orderRejectedReasons = new Bitset64();
        if (targetOrderHeader.OrderId.IsAlgoOrder() && stateOrderHeader.OrderId.ClientId != targetOrderHeader.OrderId.ClientId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ClientIdIsWrong);
        }
        if (stateOrderHeader.OrderId.StrategyId != targetOrderHeader.OrderId.StrategyId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.StrategyIdIsWrong);
        }
        if (stateOrderHeader.OrderId != targetOrderHeader.OrderId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ClientOrderIdIsWrong);
        }
        if (stateOrderHeader.OrderId.InstrumentId != targetOrderHeader.OrderId.InstrumentId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentIdIsWrong);
        }
        return orderRejectedReasons;
    }

    // Aggregates are per LEG (an outright is its own single leg, weight +1). Applies an ORDER-unit
    // magnitude delta (negative = release) to each leg's side of exposure. legSide = orderSide ×
    // sign(weight) — the sign is applied exactly ONCE, here; signing anywhere else squares it away
    // and drives the short aggregate positive (see Spec.md).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyWorstWorkingQuantityDelta(OrderId orderId, int orderSideSign, int magnitudeDelta)
    {
        if (magnitudeDelta == 0)
            return;

        foreach (InstrumentLeg leg in _context.GetInstrument(orderId.InstrumentId).Legs)
        {
            int legSide = orderSideSign * Math.Sign(leg.Weight);
            int legMagnitudeDelta = magnitudeDelta * Math.Abs(leg.Weight);
            // Seq-bumped (single writer: two volatile stores) so Read() sees an untorn row and the TCP mirror ships the change.
            ref SharedArrayEntry<WorkingRisk> workingRiskEntry = ref _context.GetWorkingRisk(leg.InstrumentId);
            ref WorkingRisk workingRisk = ref workingRiskEntry.GetRef();
            workingRiskEntry.AcquireLock();
            workingRisk.WorstLongWorkingQuantity += legSide > 0 ? legMagnitudeDelta : 0;
            workingRisk.WorstShortWorkingQuantity -= legSide < 0 ? legMagnitudeDelta : 0;
            workingRiskEntry.ReleaseLock();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnOrderState(in OrderState orderState)
    {
        // Expects the exchange to acknowledge before it trades: a marketable create or amend arrives as Acked,
        // then its fills. The Acked branch releases the old-to-new quantity change, the Done branch releases
        // the rest of what OrderRisk holds. An ack that rides inside a Fill or Done message is not seen here:
        // its quantity stays reserved until Done, which releases exactly what the order holds (see Spec.md
        // "Acceptance before trade").
        if (orderState.OrderStateReason == OrderStateReason.Acked)
        {
            ref OrderRisk orderRisk = ref _context.GetOrderRisk(orderState.OrderHeader.OrderId).GetRef();
            Side side = orderState.OrderProfile.Side;

            int worstOrderQuantityBefore = orderRisk.GetAbsWorstOrderQuantity();
            orderRisk.Ack(orderState.OrderProfile.Quantity);
            int worstOrderQuantityAfter = orderRisk.GetAbsWorstOrderQuantity();
            int worstOrderQuantityDelta = (worstOrderQuantityAfter - worstOrderQuantityBefore);

            ApplyWorstWorkingQuantityDelta(orderState.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, worstOrderQuantityDelta);
        }
        else if (orderState.OrderStateStatus == OrderStateStatus.Done)
        {
            ref OrderRisk orderRisk = ref _context.GetOrderRisk(orderState.OrderHeader.OrderId).GetRef();
            Side side = orderState.OrderProfile.Side;

            int worstOrderQuantity = orderRisk.GetAbsWorstOrderQuantity();
            int released = worstOrderQuantity - Math.Abs(orderState.QuantityFilled);

            orderRisk = default;

            ApplyWorstWorkingQuantityDelta(orderState.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, -released);
        }
    }


    // Every fill moves Position; only an outright fill of an order this RiskLayer reserved releases one.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnFill(in Fill fill, bool isReserved = true)
    {
        int instrumentId = fill.OrderHeader.OrderId.InstrumentId;
        ref SharedArrayEntry<WorkingRisk> workingRiskEntry = ref _context.GetWorkingRisk(instrumentId);
        workingRiskEntry.AcquireLock();
        workingRiskEntry.GetRef().Position += fill.Quantity;
        workingRiskEntry.ReleaseLock();

        // A legged instrument's own fill is accounting only (volume/position view on the spread row); risk lives on the
        // legs, so releasing it here would double-release the legs the leg fills already covered. Risk is an outright concept.
        if (!isReserved || _context.GetInstrument(instrumentId).IsLegged)
            return;

        ApplyWorstWorkingQuantityDelta(fill.OrderHeader.OrderId, fill.Sign, -Math.Abs(fill.Quantity));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnOrderRejected(in OrderRejected orderRejected)
    {
        // Nothing to release: a reject from this side never reserved anything here, and a cancel never reserves.
        if (orderRejected.OrderRejectedSource == _orderRejectedSource || orderRejected.OrderTargetAction == OrderTargetAction.Cancel)
            return;

        ref OrderRisk orderRisk = ref _context.GetOrderRisk(orderRejected.OrderHeader.OrderId).GetRef();
        Side side = orderRejected.OrderProfile.Side;

        int worstOrderQuantityBefore = orderRisk.GetAbsWorstOrderQuantity();
        orderRisk.Reject(orderRejected.OrderProfile.Quantity);
        int worstOrderQuantityAfter = orderRisk.GetAbsWorstOrderQuantity();
        int worstOrderQuantityDelta = worstOrderQuantityAfter - worstOrderQuantityBefore;

        ApplyWorstWorkingQuantityDelta(orderRejected.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, worstOrderQuantityDelta);
    }


    // The largest |order quantity| (filled included) this order may carry and still pass the position check: its current
    // worst plus the room left on every leg. Past the limit ValidateOrder lets it keep its worst (a cut always passes);
    // isWithinLimit cuts it back to what fits the limit instead, which the server always accepts (see Spec.md).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAbsAllowedOrderQuantity(in OrderTarget orderTarget, bool isWithinLimit = false)
    {
        // A Create's row still holds the previous order's values until ValidateOrder resets it.
        int worstOrderQuantity = orderTarget.OrderTargetAction == OrderTargetAction.Create ? 0 : _context.GetOrderRisk(orderTarget.OrderHeader.OrderId).GetReadonlyRef().GetAbsWorstOrderQuantity();
        int sign = orderTarget.OrderProfile.Sign;
        long absAllowedOrderQuantity = int.MaxValue;

        foreach (InstrumentLeg leg in _context.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId).Legs)
        {
            // legSide = orderSide × sign(weight), as in ApplyWorstWorkingQuantityDelta: a buy calendar reserves the back leg SHORT.
            int legSide = sign * Math.Sign(leg.Weight);
            ref readonly RiskLimit riskLimit = ref _context.GetRiskLimit(leg.InstrumentId).GetReadonlyRef();
            ref readonly WorkingRisk workingRisk = ref _context.GetWorkingRisk(leg.InstrumentId).GetReadonlyRef();
            long room = legSide > 0
                ? (long)riskLimit.MaxPositionQuantity - workingRisk.Position - workingRisk.WorstLongWorkingQuantity
                : (long)riskLimit.MaxPositionQuantity + workingRisk.Position + workingRisk.WorstShortWorkingQuantity;
            // Past the limit nothing may grow: keep the worst, or within the limit cut by the overshoot rounded up to whole orders.
            int absWeight = Math.Abs(leg.Weight);
            long roomOrderQuantity = room >= 0 ? room / absWeight : isWithinLimit ? -((absWeight - 1 - room) / absWeight) : 0;
            absAllowedOrderQuantity = Math.Min(absAllowedOrderQuantity, worstOrderQuantity + roomOrderQuantity);
        }
        return (int)absAllowedOrderQuantity;
    }

    // ValidateOrder's position check: what TryAdd would add must fit the room on every leg.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsWithinRiskLimit(in OrderTarget orderTarget)
    {
        return Math.Abs(orderTarget.OrderProfile.Quantity) <= GetAbsAllowedOrderQuantity(in orderTarget);
    }

    // Lowers a create or amend to the largest quantity within the limits, which ValidateOrder accepts here and on the server
    // whatever the server has read since; false when nothing workable fits.
    public bool TryClipToRiskLimit(ref OrderTarget orderTarget)
    {
        OrderId orderId = orderTarget.OrderHeader.OrderId;
        // An amend with 29 targets already in flight would be refused (TooManyActiveTargets): nothing fits until an ack frees one.
        if (orderTarget.OrderTargetAction != OrderTargetAction.Create && _context.GetOrderRisk(orderId).GetReadonlyRef().IsFull)
            return false;
        ref readonly OrderState orderState = ref _context.GetOrderState(orderId).GetReadonlyRef();
        int absQuantityFilled = orderTarget.OrderTargetAction != OrderTargetAction.Create && orderState.OrderHeader.OrderId == orderId ? Math.Abs(orderState.QuantityFilled) : 0;

        // Position room, TryAdd's cap, and the per-order limit ValidateOrder applies to the working quantity on each leg.
        long absAllowedOrderQuantity = Math.Min(GetAbsAllowedOrderQuantity(in orderTarget, isWithinLimit: true), OrderRisk.MaxOrderQuantity);
        foreach (InstrumentLeg leg in _context.GetInstrument(orderId.InstrumentId).Legs)
            absAllowedOrderQuantity = Math.Min(absAllowedOrderQuantity, absQuantityFilled + (long)_context.GetRiskLimit(leg.InstrumentId).GetReadonlyRef().MaxOrderQuantity / Math.Abs(leg.Weight));

        // Nothing left to work: an amend down to the filled quantity would be a cancel.
        if (absAllowedOrderQuantity <= absQuantityFilled)
            return false;
        if (Math.Abs(orderTarget.OrderProfile.Quantity) > absAllowedOrderQuantity)
            orderTarget.OrderProfile.Quantity = orderTarget.OrderProfile.Sign * (int)absAllowedOrderQuantity;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateOrder(in OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        orderRejectedReasons = new Bitset64();
        try
        {

            // 1. Basic Bounds Check
            int instrumentId = orderTarget.OrderHeader.OrderId.InstrumentId;
            Instrument instrument = _context.GetInstrument(instrumentId);
            int strategyId = orderTarget.OrderHeader.OrderId.StrategyId;
            int clientId = orderTarget.OrderHeader.OrderId.ClientId;

            ref readonly OrderTarget existingTarget = ref _context.GetOrderTarget(orderTarget.OrderHeader.OrderId).GetReadonlyRef();
            ref readonly OrderState orderState = ref _context.GetOrderState(orderTarget.OrderHeader.OrderId).GetReadonlyRef();


            // 3. Validate Creation Logic
            if (orderTarget.OrderTargetAction == OrderTargetAction.Create) // check slot is vacant
            {
                if (!(orderRejectedReasons = ValidateInstrument(strategyId, instrumentId)).IsEmpty)
                {
                    return false;
                }

                if (!(orderRejectedReasons = ValidateClient(clientId, strategyId)).IsEmpty)
                {
                    return false;
                }

                if (!(orderRejectedReasons = ValidateCreate(in orderTarget, in orderState)).IsEmpty)
                {
                    return false;
                }
            }
            else
            {

                bool isReduceOrReplace = orderTarget.OrderTargetAction == OrderTargetAction.Replace || orderTarget.OrderTargetAction == OrderTargetAction.Reduce;

                if (_orderRejectedSource == OrderRejectedSource.Server)
                {
                    if (!(orderRejectedReasons = ValidateOrderHeader(in orderState.OrderHeader, in orderTarget.OrderHeader)).IsEmpty)
                    {
                        return false;
                    }

                    if (orderState.OrderStateStatus == OrderStateStatus.Done)
                        orderRejectedReasons.Set((int)OrderRejectedReason.StateIsDone);

                    if (isReduceOrReplace && orderState.OrderHeader.Seq + 1 == orderTarget.OrderHeader.Seq && orderState.OrderProfile == orderTarget.OrderProfile)
                        orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);

                    if (existingTarget.OrderHeader.Seq > orderTarget.OrderHeader.Seq)
                        orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsStale);

                    if (isReduceOrReplace && orderState.OrderProfile.Side != orderTarget.OrderProfile.Side)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
                }

                if (_orderRejectedSource == OrderRejectedSource.Client)
                {

                    if (!(orderRejectedReasons = ValidateOrderHeader(in existingTarget.OrderHeader, in orderTarget.OrderHeader)).IsEmpty)
                    {
                        return false;
                    }

                    if (orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId) //state == target ??
                    {
                        if (orderState.OrderStateStatus == OrderStateStatus.Done)
                            orderRejectedReasons.Set((int)OrderRejectedReason.StateIsDone);
                        if (isReduceOrReplace && existingTarget.OrderTargetStatus == OrderStateStatus.Done && orderState.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                    }   

                    if (existingTarget.OrderHeader.Seq >= orderTarget.OrderHeader.Seq)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);

                    if (existingTarget.OrderTargetStatus == OrderStateStatus.Active) // existingTarget == newTarget ??
                    {
                        if (isReduceOrReplace && existingTarget.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                        
                        bool existingTargetWillCancel = existingTarget.OrderHeader.OrderId == orderState.OrderHeader.OrderId && existingTarget.OrderProfile.Sign * (existingTarget.OrderProfile.Quantity - orderState.QuantityFilled) <= 0;
                        if (existingTarget.OrderTargetAction == OrderTargetAction.Cancel || existingTargetWillCancel)
                            orderRejectedReasons.Set((int)OrderRejectedReason.CancelIsActive);
                    }

                    if (isReduceOrReplace && existingTarget.OrderProfile.Side != orderTarget.OrderProfile.Side)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
                }   
            }

            ref readonly PositionHeader localPosition = ref _context.GetPositionHeader(orderTarget.OrderHeader.OrderId.StrategyId, orderTarget.OrderHeader.OrderId.InstrumentId).GetReadonlyRef();



            bool isCancel = orderTarget.OrderTargetAction == OrderTargetAction.Cancel;
            if (!isCancel && orderTarget.OrderHeader.OrderId.IsAlgoOrder() && localPosition.AlgoStatus == AlgoStatus.Paused)
            {
                orderRejectedReasons.Set((int)OrderRejectedReason.AlgoIsPaused);
                return false;
            }

            // The client checks only its own algo orders: a manual order's position sits on another strategy's row.
            if (_orderRejectedSource == OrderRejectedSource.Client && !orderTarget.OrderHeader.OrderId.IsAlgoOrder())
                return orderRejectedReasons.IsEmpty;

            if (!orderRejectedReasons.IsEmpty)
                return false;

            if (_orderRejectedSource == OrderRejectedSource.Server)
            {
                ref RollingRateLimit rollingRateLimit = ref _context.GetRateLimit(instrument.Header.CoreGroupId).GetRef();

                // A cancel, or a reduce that really is one against the order's current state, is counted but never refused:
                // both only take risk off. A reduce that can't be verified (one behind an unacked replace) is throttled like any amend.
                bool isReduce = orderTarget.OrderTargetAction == OrderTargetAction.Reduce && orderTarget.OrderProfile.IsReduceOf(in orderState.OrderProfile);
                if (isCancel || isReduce)
                {
                    rollingRateLimit.SendOrder(Clock.Now);
                }
                else if (!rollingRateLimit.TrySendOrder(Clock.Now))
                {
                    orderRejectedReasons.Set((int)OrderRejectedReason.TooManyOrdersPerSecond);
                    return false;
                }
            }
            
            // 10. RISK LIMITS
            // Only check risk on New or Amend (increasing size)
            if (!isCancel)
            {
                int quantityFilled = orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId ? orderState.QuantityFilled : 0;
                int workingQuantity = orderTarget.OrderProfile.Quantity - quantityFilled;

                // Max order quantity per leg, in LEG units — before TryAdd, so rejects need no back-out.
                foreach (InstrumentLeg leg in instrument.Legs)
                {
                    if (Math.Abs(workingQuantity * leg.Weight) > _context.GetRiskLimit(leg.InstrumentId).GetReadonlyRef().MaxOrderQuantity)
                    {
                        orderRejectedReasons.Set((int)OrderRejectedReason.QuantityExceedsRiskLimit);
                        return false;
                    }
                }

                // Phase 1 — pure: check every leg, write nothing.
                if (!IsWithinRiskLimit(in orderTarget))
                {
                    orderRejectedReasons.Set((int)OrderRejectedReason.PositionExceedsRiskLimit);
                    return false;
                }

                ref OrderRisk orderRisk = ref _context.GetOrderRisk(orderTarget.OrderHeader.OrderId).GetRef();

                if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
                    orderRisk = new OrderRisk();

                // Phase 2 — commit through the same arithmetic the release hooks use. Single-writer:
                // nothing can change between the phases, so check-then-apply is atomic by ownership.
                int worstOrderQuantityBefore = orderRisk.GetAbsWorstOrderQuantity();
                if (!orderRisk.TryAdd(orderTarget.OrderProfile.Quantity, out OrderRejectedReason reason))
                {
                    orderRejectedReasons.Set((int)reason);
                    return false;
                }
                int worstMagnitudeDelta = orderRisk.GetAbsWorstOrderQuantity() - worstOrderQuantityBefore;
                ApplyWorstWorkingQuantityDelta(orderTarget.OrderHeader.OrderId, orderTarget.OrderProfile.Sign, worstMagnitudeDelta);
            }
        }
        catch(Exception ex)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ExceptionThrownByRiskLayer);
            Console.WriteLine($"Exception in RiskLayer: {ex}");
        }

        return orderRejectedReasons.IsEmpty;

    }
}
