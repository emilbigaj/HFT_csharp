//BEGIN_FILE HFT/Execution/RiskManager.cs
using System;
using System.Runtime.CompilerServices;
using Tools;
using Data;
using Execution;

namespace Provider;

public struct Exposure
{
    public int Position;
    public int WorkingBuyQuantity;
    public int WorkingSellQuantity;
    public int WorstLongPosition => Position + WorkingBuyQuantity;
    public int WorstShortPosition => Position - WorkingSellQuantity;
   
}



// This class is not thread safe. Only one thread should ever use it.
public class RiskLayer
{
    private ServerContext _serverContext;
    // CLIENT-side only: the high-water mark of this client's own allocations. Valid there because
    // validation runs at send time on one thread, so validation order IS allocation order. The
    // server must NOT run this check: ids come from one per-client counter but travel on per-core-
    // group rings read by different threads, so two same-instant creates on different instruments
    // can legitimately arrive out of allocation order — the old per-client array rejected the
    // slower one (ClientOrderIdOutOfOrder) and paused the algo, nondeterministically.
    private OrderId _maxClientOrderId;
    private readonly OrderRejectedSource _orderRejectedSource;
    public RiskLayer(ServerContext serverContext, OrderRejectedSource orderRejectedSource)
    {
        _serverContext = serverContext;
        _orderRejectedSource = orderRejectedSource;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateClient(int clientId, int strategyId)
    {
        Bitset64 orderRejectedReasons = new Bitset64();
        ref readonly ServerHeader serverHeader = ref _serverContext.ServerHeader.GetReadonlyRef();

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
        ref readonly ServerHeader serverHeader = ref _serverContext.ServerHeader.GetReadonlyRef();

        bool isValidInstrumentId = instrumentId >= 0 && instrumentId < _serverContext.InstrumentIds.Length;
        if (!isValidInstrumentId)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentIdNotValid);
            return orderRejectedReasons;
        }

        if (!_serverContext.GetInstrumentIdsByClientId(strategyId).GetReadonlyRef()[instrumentId])
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentNotAllocated);
        }

        Instrument instrument = _serverContext.GetInstrument(instrumentId);

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

        foreach (InstrumentLeg leg in _serverContext.GetInstrument(orderId.InstrumentId).Legs)
        {
            int legSide = orderSideSign * Math.Sign(leg.Weight);
            int legMagnitudeDelta = magnitudeDelta * Math.Abs(leg.Weight);
            ref RiskLimit riskLimit = ref _serverContext.GetRiskLimit(leg.InstrumentId).GetRef();
            riskLimit.WorstLongWorkingQuantity += legSide > 0 ? legMagnitudeDelta : 0;
            riskLimit.WorstShortWorkingQuantity -= legSide < 0 ? legMagnitudeDelta : 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnOrderState(in OrderState orderState, int beforeAckedOrderQuantity)
    {
        if (_orderRejectedSource != OrderRejectedSource.Server)
            return;

        // Expects the exchange to acknowledge before it trades: a marketable create or amend arrives as Acked,
        // then its fills. The Acked branch releases the old-to-new quantity change, the Done branch releases
        // the rest measured from the acked quantity; a fill that carried an unacked quantity would leak the
        // difference for good. The simulator and CME both honour this (see Spec.md "Acceptance before trade").
        if (orderState.OrderStateReason == OrderStateReason.Acked)
        {
            ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderState.OrderHeader.OrderId).GetRef();
            Side side = orderState.OrderProfile.Side;

            int worstOrderQuantityBefore = orderRisk.GetAbsWorstOrderQuantity(beforeAckedOrderQuantity);
            orderRisk.Ack(orderState.OrderProfile.Quantity);
            int worstOrderQuantityAfter = orderRisk.GetAbsWorstOrderQuantity(orderState.OrderProfile.Quantity);
            int worstOrderQuantityDelta = (worstOrderQuantityAfter - worstOrderQuantityBefore);

            ApplyWorstWorkingQuantityDelta(orderState.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, worstOrderQuantityDelta);
        }
        else if (orderState.OrderStateStatus == OrderStateStatus.Done)
        {
            ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderState.OrderHeader.OrderId).GetRef();
            Side side = orderState.OrderProfile.Side;

            int worstOrderQuantity = orderRisk.GetAbsWorstOrderQuantity(orderState.OrderProfile.Quantity);
            int released = worstOrderQuantity - Math.Abs(orderState.QuantityFilled);

            orderRisk = default;

            ApplyWorstWorkingQuantityDelta(orderState.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, -released);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnFill(in Fill fill)
    {
        if (_orderRejectedSource != OrderRejectedSource.Server)
            return;

        ApplyWorstWorkingQuantityDelta(fill.OrderHeader.OrderId, fill.Sign, -Math.Abs(fill.Quantity));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnOrderRejected(in OrderRejected orderRejected)
    {
        if (_orderRejectedSource != OrderRejectedSource.Server)
            return;

        if (orderRejected.OrderRejectedSource == OrderRejectedSource.Server)
            return;

        ref OrderState orderState = ref _serverContext.GetOrderState(orderRejected.OrderHeader.OrderId).GetRef();
        ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderRejected.OrderHeader.OrderId).GetRef();
        Side side = orderRejected.OrderProfile.Side;

        int worstOrderQuantityBefore = orderRisk.GetAbsWorstOrderQuantity(orderState.OrderProfile.Quantity);
        orderRisk.Reject(orderRejected.OrderProfile.Quantity);
        int worstOrderQuantityAfter = orderRisk.GetAbsWorstOrderQuantity(orderState.OrderProfile.Quantity);
        int worstOrderQuantityDelta = worstOrderQuantityAfter - worstOrderQuantityBefore;

        ApplyWorstWorkingQuantityDelta(orderRejected.OrderHeader.OrderId, side == Side.Buy ? 1 : -1, worstOrderQuantityDelta);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateOrder(in OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        orderRejectedReasons = new Bitset64();
        try
        {

            // 1. Basic Bounds Check
            int instrumentId = orderTarget.OrderHeader.OrderId.InstrumentId;
            Instrument instrument = _serverContext.GetInstrument(instrumentId);
            int strategyId = orderTarget.OrderHeader.OrderId.StrategyId;
            int clientId = orderTarget.OrderHeader.OrderId.ClientId;

            ref readonly OrderTarget existingTarget = ref _serverContext.GetOrderTarget(orderTarget.OrderHeader.OrderId).GetReadonlyRef();
            ref readonly OrderState orderState = ref _serverContext.GetOrderState(orderTarget.OrderHeader.OrderId).GetReadonlyRef();


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

                bool isAmend = orderTarget.OrderTargetAction == OrderTargetAction.Amend;

                if (_orderRejectedSource == OrderRejectedSource.Server)
                {
                    if (!(orderRejectedReasons = ValidateOrderHeader(in orderState.OrderHeader, in orderTarget.OrderHeader)).IsEmpty)
                    {
                        return false;
                    }

                    if (orderState.OrderStateStatus == OrderStateStatus.Done)
                        orderRejectedReasons.Set((int)OrderRejectedReason.StateIsDone);

                    if (isAmend && orderState.OrderHeader.Seq + 1 == orderTarget.OrderHeader.Seq && orderState.OrderProfile == orderTarget.OrderProfile)
                        orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);

                    if (existingTarget.OrderHeader.Seq > orderTarget.OrderHeader.Seq)
                        orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsStale);

                    if (orderTarget.OrderTargetAction == OrderTargetAction.Amend && orderState.OrderProfile.Side != orderTarget.OrderProfile.Side)
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
                        if (isAmend && existingTarget.OrderTargetStatus == OrderStateStatus.Done && orderState.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                    }   

                    if (existingTarget.OrderHeader.Seq >= orderTarget.OrderHeader.Seq)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);

                    if (existingTarget.OrderTargetStatus == OrderStateStatus.Active) // existingTarget == newTarget ??
                    {
                        if (isAmend && existingTarget.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                        
                        bool existingTargetWillCancel = existingTarget.OrderHeader.OrderId == orderState.OrderHeader.OrderId && existingTarget.OrderProfile.Sign * (existingTarget.OrderProfile.Quantity - orderState.QuantityFilled) <= 0;
                        if (existingTarget.OrderTargetAction == OrderTargetAction.Cancel || existingTargetWillCancel)
                            orderRejectedReasons.Set((int)OrderRejectedReason.CancelIsActive);
                    }

                    if (orderTarget.OrderTargetAction == OrderTargetAction.Amend && existingTarget.OrderProfile.Side != orderTarget.OrderProfile.Side)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
                }   
            }

            ref readonly PositionHeader localPosition = ref _serverContext.GetPositionHeader(orderTarget.OrderHeader.OrderId.StrategyId, orderTarget.OrderHeader.OrderId.InstrumentId).GetReadonlyRef();



            bool isCancel = orderTarget.OrderTargetAction == OrderTargetAction.Cancel;
            if (!isCancel && orderTarget.OrderHeader.OrderId.IsAlgoOrder() && localPosition.AlgoStatus == AlgoStatus.Paused)
            {
                orderRejectedReasons.Set((int)OrderRejectedReason.AlgoIsPaused);
                return false;
            }

            //Risk limits are owned by server.
            if (_orderRejectedSource != OrderRejectedSource.Server)
                return orderRejectedReasons.IsEmpty;

            if (!orderRejectedReasons.IsEmpty)
                return false;

            if (_orderRejectedSource == OrderRejectedSource.Server)
            {
                ref RollingRateLimit rollingRateLimit = ref _serverContext.GetRateLimit(instrument.Header.CoreGroupId).GetRef();

                // A cancel is counted but never refused: it is the message that reduces risk.
                if (isCancel)
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
                    if (Math.Abs(workingQuantity * leg.Weight) > _serverContext.GetRiskLimit(leg.InstrumentId).GetReadonlyRef().MaxOrderQuantity)
                    {
                        orderRejectedReasons.Set((int)OrderRejectedReason.QuantityExceedsRiskLimit);
                        return false;
                    }
                }

                int ackedOrderQuantity = orderTarget.OrderTargetAction == OrderTargetAction.Create ? 0 : orderState.OrderProfile.Quantity;

                ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderTarget.OrderHeader.OrderId).GetRef();

                if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
                    orderRisk = new OrderRisk();

                // The ONE pre-verdict mutation, with its first-class inverse (Reject) on any breach.
                int sign = orderTarget.OrderProfile.Sign;
                int worstQuantityFilledBefore = orderRisk.GetAbsWorstOrderQuantity(ackedOrderQuantity);
                if (!orderRisk.TryAdd(orderTarget.OrderProfile.Quantity, out OrderRejectedReason reason))
                {
                    orderRejectedReasons.Set((int)reason);
                    return false;
                }
                int worstMagnitudeDelta = orderRisk.GetAbsWorstOrderQuantity(ackedOrderQuantity) - worstQuantityFilledBefore;

                // Phase 1 — pure: check every leg, write nothing. The magnitude delta is >= 0, so
                // legDelta's sign IS the leg's side — routing by the ORDER's sign corrupts every
                // negative-weight leg (buy calendar reserves the back leg SHORT, not long).
                foreach (InstrumentLeg leg in instrument.Legs)
                {
                    int legDelta = worstMagnitudeDelta * sign * leg.Weight;
                    ref readonly RiskLimit riskLimit = ref _serverContext.GetRiskLimit(leg.InstrumentId).GetReadonlyRef();
                    int quantity = _serverContext.GetPosition(leg.InstrumentId).PositionHeader.GetReadonlyRef().Quantity;

                    bool isRiskLimitExceeded = legDelta >= 0
                        ? quantity + riskLimit.WorstLongWorkingQuantity + legDelta > riskLimit.MaxPositionQuantity
                        : quantity + riskLimit.WorstShortWorkingQuantity + legDelta < -riskLimit.MaxPositionQuantity;

                    if (isRiskLimitExceeded)
                    {
                        orderRisk.Reject(orderTarget.OrderProfile.Quantity);
                        orderRejectedReasons.Set((int)OrderRejectedReason.PositionExceedsRiskLimit);
                        return false;
                    }
                }

                // Phase 2 — commit through the same arithmetic the release hooks use. Single-writer:
                // nothing can change between the phases, so check-then-apply is atomic by ownership.
                ApplyWorstWorkingQuantityDelta(orderTarget.OrderHeader.OrderId, sign, worstMagnitudeDelta);
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
