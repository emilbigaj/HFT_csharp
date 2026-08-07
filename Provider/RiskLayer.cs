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
    private ulong[] _maxClientOrderIds;
    private readonly OrderRejectedSource _orderRejectedSource;
    public RiskLayer(ServerContext serverContext, OrderRejectedSource orderRejectedSource)
    {
        _serverContext = serverContext;
        _orderRejectedSource = orderRejectedSource;
        _maxClientOrderIds = new ulong[_serverContext.ServerHeader.GetReadonlyRef().ClientIds.Length];
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

        if (!instrument.IsInSession)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.NotInSession);
        }

        return orderRejectedReasons;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64 ValidateCreate(in OrderTarget orderTarget, in OrderState orderState)
    {
        int clientId = orderTarget.OrderHeader.OrderId.ClientId;
        Bitset64 orderRejectedReasons = new Bitset64();
        if (orderTarget.OrderHeader.Seq != 1)
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);
        }

        if (orderTarget.OrderHeader.OrderId <= _maxClientOrderIds[clientId])
        {
            orderRejectedReasons.Set((int)OrderRejectedReason.ClientOrderIdOutOfOrder);
        }
        else
        {
            _maxClientOrderIds[clientId] = orderTarget.OrderHeader.OrderId;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnOrderState(in OrderState orderState)
    {
        if (_orderRejectedSource != OrderRejectedSource.Server)
            return;

        if (orderState.OrderStateReason == OrderStateReason.Acked)
        {
            ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderState.OrderHeader.OrderId).GetRef();
            Side side = orderState.OrderProfile.Side;

            int worstOrderQuantityBefore = orderRisk.GetWorstOrderQuantity(orderState.OrderProfile.Quantity);
            orderRisk.Ack(orderState.OrderProfile.Quantity);
            int worstOrderQuantityAfter = orderRisk.GetWorstOrderQuantity(orderState.OrderProfile.Quantity);
            int worstOrderQuantityDelta = worstOrderQuantityAfter - worstOrderQuantityBefore;
            
            if (worstOrderQuantityDelta == 0)
                return;   

            ref RiskLimit riskLimit = ref _serverContext.GetRiskLimit(orderState.OrderHeader.OrderId.InstrumentId).GetRef();
            riskLimit.WorstLongWorkingQuantity += worstOrderQuantityDelta * (side == Side.Buy ? 1 : 0);
            riskLimit.WorstShortWorkingQuantity += worstOrderQuantityDelta * (side == Side.Sell ? 1 : 0);
        }
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

        int worstOrderQuantityBefore = orderRisk.GetWorstOrderQuantity(orderState.OrderProfile.Quantity);
        orderRisk.Reject(orderRejected.OrderProfile.Quantity);
        int worstOrderQuantityAfter = orderRisk.GetWorstOrderQuantity(orderState.OrderProfile.Quantity);
        int worstOrderQuantityDelta = worstOrderQuantityAfter - worstOrderQuantityBefore;

        if (worstOrderQuantityDelta == 0)
            return;   

        ref RiskLimit riskLimit = ref _serverContext.GetRiskLimit(orderRejected.OrderHeader.OrderId.InstrumentId).GetRef();
        riskLimit.WorstLongWorkingQuantity += worstOrderQuantityDelta * (side == Side.Buy ? 1 : 0);
        riskLimit.WorstShortWorkingQuantity += worstOrderQuantityDelta * (side == Side.Sell ? 1 : 0);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateOrder(in OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        orderRejectedReasons = new Bitset64();
        try
        {

            // 1. Basic Bounds Check
            int instrumentId = orderTarget.OrderHeader.OrderId.InstrumentId;
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

                if (_orderRejectedSource == OrderRejectedSource.Server)
                {
                    if (!(orderRejectedReasons = ValidateOrderHeader(in orderState.OrderHeader, in orderTarget.OrderHeader)).IsEmpty)
                    {
                        return false;
                    }

                    if (orderState.OrderStateStatus == OrderStateStatus.Done)
                        orderRejectedReasons.Set((int)OrderRejectedReason.StateIsDone);

                    if (orderState.OrderHeader.Seq + 1 == orderTarget.OrderHeader.Seq && orderState.OrderProfile == orderTarget.OrderProfile)
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

                    bool isAmend = orderTarget.OrderTargetAction == OrderTargetAction.Amend;


                    if (orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId) //state == target ??
                    {
                        if (orderState.OrderStateStatus == OrderStateStatus.Done)
                            orderRejectedReasons.Set((int)OrderRejectedReason.StateIsDone);
                        if (isAmend && existingTarget.OrderTargetStatus == OrderStateStatus.Done && orderState.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                    }
                        

                    if (existingTarget.OrderHeader.Seq >= orderTarget.OrderHeader.Seq)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);

                    if (existingTarget.OrderTargetStatus == OrderStateStatus.Active) // lastTarget = newTarget ??
                    {
                        if (isAmend && existingTarget.OrderProfile == orderTarget.OrderProfile)
                            orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
                        if (existingTarget.OrderTargetAction == OrderTargetAction.Cancel)
                            orderRejectedReasons.Set((int)OrderRejectedReason.CancelIsActive);
                    }

                    if (orderTarget.OrderTargetAction == OrderTargetAction.Amend && existingTarget.OrderProfile.Side != orderTarget.OrderProfile.Side)
                        orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
                }   
            }

            ref RiskLimit riskLimit = ref _serverContext.GetRiskLimit(instrumentId).GetRef();
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


            // 10. RISK LIMITS
            // Only check risk on New or Amend (increasing size)
            if (!isCancel)
            {
                int quantityFilled = orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId ? orderState.QuantityFilled : 0;

                int workingQuantity = orderTarget.OrderProfile.Quantity - quantityFilled;
                int absWorkingQuantity = Math.Abs(workingQuantity);

                // Max Order Quantity
                if (absWorkingQuantity > riskLimit.MaxOrderQuantity)
                {
                    orderRejectedReasons.Set((int)OrderRejectedReason.QuantityExceedsRiskLimit);
                    return false;                   
                }



                int ackedOrderQuantity = orderTarget.OrderTargetAction == OrderTargetAction.Create ? 0 : orderState.OrderProfile.Quantity;
                
                ref OrderRisk orderRisk = ref _serverContext.GetOrderRisk(orderTarget.OrderHeader.OrderId).GetRef();
                
                int sign = orderTarget.OrderProfile.Sign;
                int worstQuantityFilledBefore = orderRisk.GetWorstOrderQuantity(ackedOrderQuantity) * sign;
                if (!orderRisk.TryAdd(orderTarget.OrderProfile.Quantity, out OrderRejectedReason reason))
                {
                    orderRejectedReasons.Set((int)reason);
                    return false;
                }
                        
                int worstQuantityFilledAfter = orderRisk.GetWorstOrderQuantity(ackedOrderQuantity) * sign;
                int worstWorkingQuantityDelta = worstQuantityFilledAfter - worstQuantityFilledBefore;

                //branchless
                int worstLongWorkingQuantity = riskLimit.WorstLongWorkingQuantity + worstWorkingQuantityDelta * (sign == 1 ? 1 : 0);
                int worstShortWorkingQuantity = riskLimit.WorstShortWorkingQuantity + worstWorkingQuantityDelta * (sign == -1 ? 1 : 0);


                Position serverPosition = _serverContext.GetPosition(instrumentId);
                int quantity = serverPosition.Header.Quantity;
                int worstLongQuantity = quantity + worstLongWorkingQuantity;
                int worstShortQuantity = quantity + worstShortWorkingQuantity;

                if (worstLongQuantity > riskLimit.MaxPositionQuantity || worstShortQuantity < -riskLimit.MaxPositionQuantity)
                {
                    orderRisk.Reject(orderTarget.OrderProfile.Quantity);
                    orderRejectedReasons.Set((int)OrderRejectedReason.PositionExceedsRiskLimit);
                }
                else
                {
                    riskLimit.WorstLongWorkingQuantity = worstLongWorkingQuantity;
                    riskLimit.WorstShortWorkingQuantity = worstShortWorkingQuantity;
                }
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
