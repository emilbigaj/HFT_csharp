//BEGIN_FILE HFT/Execution/RiskManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Tools;
using Data;
using Socket;
using Execution;

namespace Provider;


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
        _secondRateLimits = new RollingRateLimit[_serverContext.ServerHeader.GetReadonlyRef().InstrumentIds.Length];
        _sessionRateLimits = new SessionRateLimit[_serverContext.ServerHeader.GetReadonlyRef().InstrumentIds.Length];
    }

    private SessionRateLimit?[] _sessionRateLimits;
    private RollingRateLimit?[] _secondRateLimits;

    public void UpdateRiskLimit(int instrumentId)
    {
        Instrument instrument = _serverContext.GetInstrument(instrumentId);
        RiskLimit riskLimit = _serverContext.GetRiskLimit(instrumentId).GetReadonlyRef();
        _sessionRateLimits[instrumentId] = riskLimit.MaxOrdersPerSession.Limit == 0 ? null : new SessionRateLimit(riskLimit.MaxOrdersPerSession.Limit);
        _secondRateLimits[instrumentId] = riskLimit.MaxOrdersPerSecond.Limit == 0 ? null : new RollingRateLimit(riskLimit.MaxOrdersPerSecond);
    }

    public void OnInstrument(int instrumentId)
    {
        Instrument instrument = _serverContext.GetInstrument(instrumentId);
        UpdateRiskLimit(instrumentId);
        instrument.SessionManager.Changed += Timestamp =>
        {
            _sessionRateLimits[instrumentId]?.Reset();
        };
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
    public bool ValidateOrder(in OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        orderRejectedReasons = new Bitset64();
        try
        {

            // 1. Basic Bounds Check
            int instrumentId = orderTarget.OrderHeader.OrderId.InstrumentId;
            int strategyId = orderTarget.OrderHeader.OrderId.StrategyId;
            int clientId = orderTarget.OrderHeader.OrderId.ClientId;

            int globalOrderIndex = orderTarget.OrderHeader.OrderId.GlobalIndex;
            ref readonly OrderTarget existingTarget = ref _serverContext.GetOrderTarget(globalOrderIndex).GetReadonlyRef();
            ref readonly OrderState orderState = ref _serverContext.GetOrderState(globalOrderIndex).GetReadonlyRef();


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

            ref readonly RiskLimit riskLimit = ref _serverContext.GetRiskLimit(instrumentId).GetReadonlyRef();
            ref readonly PositionHeader localPosition = ref _serverContext.GetPositionHeader(orderTarget.OrderHeader.OrderId.StrategyId, orderTarget.OrderHeader.OrderId.InstrumentId).GetReadonlyRef();



            bool isCancel = orderTarget.OrderTargetAction == OrderTargetAction.Cancel;
            if (!isCancel && orderTarget.OrderHeader.OrderId.IsAlgoOrder() && localPosition.AlgoStatus == AlgoStatus.Paused)
            {
                orderRejectedReasons.Set((int)OrderRejectedReason.AlgoIsPaused);
                return false;
            }


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
                    orderRejectedReasons.Set((int)OrderRejectedReason.QuantityTooLarge);
                }

                Position serverPosition = _serverContext.GetPosition(instrumentId);
                int currentPosition = serverPosition.Header.Quantity;

                int projectedPosition = currentPosition + workingQuantity;

                if (Math.Abs(projectedPosition) > riskLimit.MaxPositionQuantity)
                {
                    bool isIncreasing = Math.Abs(projectedPosition) > Math.Abs(currentPosition);

                    if (isIncreasing)
                    {
                        orderRejectedReasons.Set((int)OrderRejectedReason.PositionTooLarge);
                    }
                }
            }
            if (orderRejectedReasons.IsEmpty && orderTarget.OrderHeader.OrderId.IsAlgoOrder())
            {
                Timestamp timestamp = orderTarget.OrderHeader.NicTimestamp;

                //SessionRateLimit? session = _sessionRateLimits[instrumentId];
                RollingRateLimit? second = _secondRateLimits[instrumentId];

                //bool canSendSession = session?.CanSendOrder(timestamp) ?? true;
                bool canSendSecond = second?.CanSendOrder(timestamp) ?? true;
                bool canSendOrder = isCancel || (/*canSendSession &&*/ canSendSecond);

                if (canSendOrder)
                {
                    //session?.TrySendOrder(timestamp);
                    second?.TrySendOrder(timestamp);
                }
                else if (!canSendSecond)
                {
                    orderRejectedReasons.Set((int)OrderRejectedReason.TooManyOrdersPerSecond);
                }
                //else if (!canSendSession)
                //{
                //    orderRejectedReasons.Set((int)OrderRejectedReason.TooManyOrdersPerSession);
                //}
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
