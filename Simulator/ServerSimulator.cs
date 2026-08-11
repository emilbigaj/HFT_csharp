using Data;
using Provider;
using Socket;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;
using Execution;
using System.Buffers;
using ZstdSharp.Unsafe;
using System.IO;
using System.Threading;
using System.IO.Enumeration;
using System;

namespace Simulator;

public class InstrumentSimulator
{
    public ExchangeSimulator ExchangeSimulator { get; }
    protected OrderManager Buys { get; }
    protected OrderManager Sells { get; }

    public InstrumentDetails InstrumentDetails { get; }
    public SessionManager? SessionManager { get; }

    public bool IsInSession => SessionManager?.IsInSession ?? true;


    public int _bidMask = 0;
    public int _askMask = 0;
    private MarketByPrice64 _marketByPrice64 = new MarketByPrice64();

    public ref MarketByPrice64 MarketByPrice64 => ref _marketByPrice64;

    public int InstrumentId { get; }

    public InstrumentSimulator(ExchangeSimulator exchangeSimulator, InstrumentDetails instrumentDetails, int instrumentId)
    {
        ExchangeSimulator = exchangeSimulator;
        InstrumentDetails = instrumentDetails;
        InstrumentId = instrumentId;
        _orderStates = new OrderState[ExchangeSimulator.ServerSimulator.ServerHeader.OrdersCapacity];
        _minClientOrderId = new ulong[ExchangeSimulator.ServerSimulator.ServerHeader.ClientIds.Length];
        Buys = new OrderManager(this, Side.Buy);
        Sells = new OrderManager(this, Side.Sell);

        if (instrumentDetails.Sessions.Length > 0)
        {
            SessionManager = new SessionManager(instrumentDetails.Sessions[0]);
            SessionManager.Changed += instrument =>
            {
                if (!SessionManager.IsInSession)
                {
                    CancelAllOrders();
                    Buys.Clear();
                    Sells.Clear();
                    _bidMask = 0;
                    _askMask = 0;
                    _minMaskBid = int.MaxValue;
                    _maxMaskAsk = int.MinValue;
                }
            };
        }


    }

    protected ulong _fillId { get; set; } = 0;

    private int _minMaskBid = int.MaxValue;
    private int _maxMaskAsk = int.MinValue;

    private bool _inOnMarketByPrice = false;
    public void OnMarketByPrice(in MarketByPrice mbp, ReadOnlySpan<byte> src)
    {
        _inOnMarketByPrice = true;
        if (mbp.TickHeader.TickType == TickType.MarketByPriceSnapshot)
        {
            Span<byte> past = stackalloc byte[MarketByPrice.SizeOf(_marketByPrice64.BidsCount, _marketByPrice64.AsksCount)];
            _marketByPrice64.CopyToSnapshot(InstrumentId, past);
            ReadOnlySpan<byte> future = src;
            int maxBidChanges = mbp.BidsCount + _marketByPrice64.BidsCount;
            int maxAskChanges = mbp.AsksCount + _marketByPrice64.AsksCount;
            int maxSize = MarketByPrice.SizeOf(maxBidChanges, maxAskChanges);
            Span<byte> dst = stackalloc byte[maxSize];
            ref readonly MarketByPrice update = ref MarketByPrice.SnapshotAsUpdate(past, future, dst);
            dst = dst.Slice(0, update.SizeOf());
            OnMarketByPriceUpdate(in update, dst);
        }
        else
        {
            OnMarketByPriceUpdate(in mbp, src);
        }
        _inOnMarketByPrice = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OverwriteBid(ref StackList<Level> bids, Level bid)
    {
        for(int i = 0; i < bids.Count; i++)
        {
            if (bids[i].Ticks == bid.Ticks)
            {
                bids[i] = bid;
                return;
            }
            else if (bids[i].Ticks < bid.Ticks) // larger bids go infront
            {
                bids.InsertAt(i, bid);
                return;
            }
        }
        bids.Add(bid);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OverwriteAsk(ref StackList<Level> asks, Level ask)
    {
        for (int i = 0; i < asks.Count; i++)
        {
            if (asks[i].Ticks == ask.Ticks)
            {
                asks[i] = ask;
                return;
            }
            else if (asks[i].Ticks > ask.Ticks) // smallers asks go infront
            {
                asks.InsertAt(i, ask);
                return;
            }
        }
        asks.Add(ask);
    }

    private void OnMarketByPriceUpdate(in MarketByPrice update, ReadOnlySpan<byte> src)
    {
        _marketByPrice64.ExchangeTimestamp = update.TickHeader.ExchangeTimestamp;

        StackList<Level> _bids = new StackList<Level>(stackalloc Level[128]);
        StackList<Level> _asks = new StackList<Level>(stackalloc Level[128]);

        ReadOnlySpan<Level> marketAsks = update.AsksAsSpan(src);
        foreach (Level ask in marketAsks)
        {
            _asks.Add(ask);
            if (_marketByPrice64.TrySetAskQuantity(ask.Ticks, ask.Quantity, out int delta))
            {
                Sells.OnMarketByPriceDelta(ask.Ticks, delta);
            }
        }
        ReadOnlySpan<Level> marketBids = update.BidsAsSpan(src);
        foreach (Level bid in marketBids)
        {
            _bids.Add(bid);
            if (_marketByPrice64.TrySetBidQuantity(bid.Ticks, bid.Quantity, out int delta))
            {
                Buys.OnMarketByPriceDelta(bid.Ticks, delta);
            }
        }

       

        if (_marketByPrice64.BidsCount > 10 || _marketByPrice64.AsksCount > 10)
        {
            //Console.WriteLine("Refiviniv data can not have more than 10 bids or asks!!");
            //throw new InvalidOperationException("Refiviniv data can not have more than 10 bids or asks!!");

        }

        /// EXECUTE QUEUED ORDERS IF ASK CROSSES BID
        /// loop through the book, and first mask off levels that have already been traded against. ie local mask = _mask
        /// after masking off levels, if there is quantity left, execute against the level, and update _mask with any additional masking from this trade
        /// Skip when the book is auction-locked/crossed (best bid >= best ask): the matching engine isn't running continuously,
        /// so user orders must not be passively filled and mask must not accumulate every tick.

        if (!_marketByPrice64.IsCrossed)
        {
            {
                ref QueueManager firstBuys = ref Buys.QueueManagers.FirstRef;
                int askMask = _askMask + Buys.CrossMask;
                SideByPrice64.Enumerator asks = _marketByPrice64.Asks.GetEnumerator();
                Level ask;
                while (!Unsafe.IsNullRef(in firstBuys) && asks.MoveNext() && (ask = asks.Current).Ticks <= firstBuys.Ticks)
                {
                    int crossedQuantity = ask.Quantity;
                    int askMasked = Math.Min(ask.Quantity, askMask);
                    crossedQuantity -= askMasked;
                    askMask = Math.Max(askMask - askMasked, 0);
                    if (crossedQuantity <= 0)
                        continue;
                    int crossed = Buys.OnTrade(false, new Trade(InstrumentId, update.TickHeader.ExchangeTimestamp, update.TickHeader.SendingTimestamp, update.TickHeader.NicTimestamp, ask.Ticks, crossedQuantity, -1));
                    if (ExchangeSimulator.MaskCrossed)
                    {
                        _askMask += crossed;
                    }

                    firstBuys = ref Buys.QueueManagers.FirstRef;
                }
            }
            {
                ref QueueManager firstSells = ref Sells.QueueManagers.FirstRef;
                int bidMask = _bidMask + Sells.CrossMask;
                var bids = _marketByPrice64.Bids.GetEnumerator();
                Level bid;

                while (!Unsafe.IsNullRef(in firstSells) && bids.MoveNext() && (bid = bids.Current).Ticks >= firstSells.Ticks)
                {
                    int crossedQuantity = bid.Quantity;
                    int bidMasked = Math.Min(crossedQuantity, bidMask);
                    crossedQuantity -= bidMasked;
                    bidMask = Math.Max(bidMask - bidMasked, 0);
                    if (crossedQuantity <= 0)
                        continue;
                    int crossed = Sells.OnTrade(false, new Trade(InstrumentId, update.TickHeader.ExchangeTimestamp, update.TickHeader.SendingTimestamp, update.TickHeader.NicTimestamp, bid.Ticks, crossedQuantity, +1));
                    if (ExchangeSimulator.MaskCrossed)
                    {
                        _bidMask += crossed;
                    }

                    firstSells = ref Sells.QueueManagers.FirstRef;
                }
            }
        }

        /// RESTORE DEPTH AT PRICES WHERE USER ORDERS HAVE BEEN REMOVED
        
        foreach (int ticks in Buys.TicksRemoved)
            OverwriteBid(ref _bids, new Level(ticks, _marketByPrice64.GetBidQuantity(ticks)));
        Buys.TicksRemoved.Clear();

        foreach (int ticks in Sells.TicksRemoved)
            OverwriteAsk(ref _asks, new Level(ticks, _marketByPrice64.GetAskQuantity(ticks)));
        Sells.TicksRemoved.Clear();

        /// FIRST REMOVE MARKET DEPTH WITH MASKS TO CORRECT FOR WHEN WE HIT MARKET OR BIDS CROSSED ASKS
        if (_bidMask > 0 || _minMaskBid < int.MaxValue)
        {
            int bidMask = _bidMask;
            int minMaskBid = _minMaskBid;
            var bids = _marketByPrice64.Bids.GetEnumerator();
            Level bid;
            while (bids.MoveNext() && ((bid = bids.Current).Ticks >= minMaskBid || bidMask > 0))
            {
                _minMaskBid = bidMask > 0 ? bid.Ticks : _minMaskBid;
                int bidQuantity = bid.Quantity;
                int bidMasked = Math.Min(bidQuantity, bidMask);
                bidQuantity -= bidMasked;
                bidMask -= bidMasked;
                OverwriteBid(ref _bids, new Level(bid.Ticks, bidQuantity));
            }
            _minMaskBid = _bidMask == 0 ? int.MaxValue : _minMaskBid; // reset min mask bid
        }

        if (_askMask > 0 || _maxMaskAsk > int.MinValue)
        {
            int askMask = _askMask;
            int maxMaskAsk = _maxMaskAsk;
            var asks = _marketByPrice64.Asks.GetEnumerator();
            Level ask;
            while (asks.MoveNext() && ((ask = asks.Current).Ticks <= maxMaskAsk || askMask > 0))
            {
                _maxMaskAsk = askMask > 0 ? ask.Ticks : _maxMaskAsk;
                int askQuantity = ask.Quantity;
                int askMasked = Math.Min(askQuantity, askMask);
                askQuantity -= askMasked;
                askMask -= askMasked;
                OverwriteAsk(ref _asks, new Level(ask.Ticks, askQuantity));
            }
            _maxMaskAsk = _askMask == 0 ? int.MinValue : _maxMaskAsk;
        }

        // --- BIDS ---
        foreach (QueueManager queueManager in Buys.QueueManagers)
        {
            if (queueManager.UserQuantity == 0)
                continue;

            int adjustedQty = _marketByPrice64.GetBidQuantity(queueManager.Ticks);

            // Find the mask-adjusted quantity from the list we just built
            for (int i = 0; i < _bids.Count; i++)
            {
                if (_bids[i].Ticks == queueManager.Ticks)
                {
                    adjustedQty = _bids[i].Quantity;
                    break;
                }
            }

            // Add our UserQuantity to the ADJUSTED quantity
            adjustedQty += queueManager.UserQuantity;
             OverwriteBid(ref _bids, new Level(queueManager.Ticks, adjustedQty));
        }

        // --- ASKS ---
        foreach (QueueManager queueManager in Sells.QueueManagers)
        {
            if (queueManager.UserQuantity == 0)
                continue;
            int adjustedQty = _marketByPrice64.GetAskQuantity(queueManager.Ticks);

            // Find the mask-adjusted quantity from the list we just built
            for (int i = 0; i < _asks.Count; i++)
            {
                if (_asks[i].Ticks == queueManager.Ticks)
                {
                    adjustedQty = _asks[i].Quantity;
                    break;
                }
            }
            // Add our UserQuantity to the Mask adjusted quantity
            adjustedQty += queueManager.UserQuantity;
            OverwriteAsk(ref _asks, new Level(queueManager.Ticks, adjustedQty));
        }

        FromExchangeToNic_MarketByPriceAggregatedUpdate(in update.TickHeader, _bids, _asks);

    }


    protected void FromExchangeToNic_MarketByPriceAggregatedUpdate(in TickHeader header, StackList<Level> bids, StackList<Level> asks)
    {
        Span<byte> span = stackalloc byte[MarketByPrice.SizeOf(bids.Count, asks.Count)];
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(span);
        mbp = new MarketByPrice(TickType.MarketByPriceUpdate, InstrumentId, header.ExchangeTimestamp, header.SendingTimestamp, header.NicTimestamp, bids.Count, asks.Count);
        bids.AsSpan().CopyTo(mbp.BidsAsSpan(span));
        asks.AsSpan().CopyTo(mbp.AsksAsSpan(span));
        ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_MarketByPrice(ref mbp,span);
    }

    private bool IsOrderEmpty(in OrderState orderState) => orderState.OrderStateStatus == OrderStateStatus.Done || orderState.OrderHeader.OrderId == 0;

    protected void CancelAllOrders()
    {
        foreach (ref OrderState orderState in _orderStates.AsSpan())
        {
            if (IsOrderEmpty(in orderState))
                continue;

            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"InstrumentExecutionSimulator.CancelAllOrders({orderState.OrderHeader.OrderId})");
            }
            OrderManager orderManager = orderState.OrderProfile.Side == Side.Buy ? Buys : Sells;
            Delete(ref orderState, orderManager);
        }
    }

    public void CancelAllOrders(int clientId)
    {
        foreach (ref OrderState orderState in _orderStates.AsSpan())
        {
            if (IsOrderEmpty(in orderState) || orderState.OrderHeader.OrderId.ClientId != clientId)
                continue;

            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"InstrumentExecutionSimulator.CancelAllOrders({orderState.OrderHeader.OrderId}, ClientId: {clientId})");
            }
            OrderManager orderManager = orderState.OrderProfile.Side == Side.Buy ? Buys : Sells;
            Delete(ref orderState, orderManager);
        }
    }

    public void OnTrade(ref Trade trade)
    {
        if (!IsInSession)
            return;

        ref int mask = ref (trade.Direction > 0 ? ref _askMask : ref _bidMask);
        int oldMask = mask;
        mask = Math.Max(0, mask - trade.Level.Quantity);

        if (oldMask > 0)
        {
            UpdateMarketByPrice();
        }

        OrderManager orderManager = trade.Direction > 0 ? Sells : Buys;
        orderManager.OnTrade(true, trade);
    }



    protected void UpdateMarketByPrice()
    {
        if (_inOnMarketByPrice)
            return;

        Span<byte> src = stackalloc byte[MarketByPrice.SizeOf(0, 0)];
        ref MarketByPrice update = ref MemoryMarshal.AsRef<MarketByPrice>(src);
        update = new MarketByPrice(TickType.MarketByPriceUpdate, InstrumentId, Clock.Now, Clock.Now, Clock.Now, 0, 0);
        OnMarketByPrice(in update, src);
    }





    private void Take(ref OrderState orderState, OrderProfile orderProfile, ref int workingQuantity)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"        ExecutionSimulator.Take(ClientOrderId: {orderState.OrderHeader.OrderId}, TargetTicks: {orderProfile.Ticks}, TargetQuantity: {orderProfile.Quantity}, WorkingQuantity: {workingQuantity})");
        }
        if (!_marketByPrice64.IsCrossed)
            return;

        int quantityTaken = 0;
        int sign = Math.Sign(workingQuantity);
        int signedTicks = orderProfile.Ticks * sign;
        ref SideByPrice64 sideByPrice = ref _marketByPrice64.Asks;
        ref int _mask = ref _askMask;
        if (sign < 0)
        {
            _mask = ref _bidMask;
            sideByPrice = ref _marketByPrice64.Bids;
        }
        int masked = 0;
        SideByPrice64.Enumerator levels = sideByPrice.GetEnumerator();
        Level level;
        int maskCopy = _mask;
        while (workingQuantity != 0 && levels.MoveNext() && signedTicks >= (level = levels.Current).Ticks * sign)
        {
            int levelQuantity = level.Quantity;
            int maskedQuantity = Math.Min(level.Quantity, maskCopy - masked);
            levelQuantity -= maskedQuantity;
            masked += maskedQuantity;

            if (levelQuantity > 0)
            {
                int fillQuantity = Math.Min(levelQuantity, Math.Abs(workingQuantity));
                int signedFillQuantity = fillQuantity * sign;
                quantityTaken += signedFillQuantity;

                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"        ExecutionSimulator.Take.Fill({orderState.OrderHeader.OrderId}, {signedFillQuantity}, {level.Ticks})");
                }

                Update(ref orderState, orderProfile, signedFillQuantity, OrderStateReason.PartialFill);
                ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_Fill(in orderState, _fillId++, level.Ticks, signedFillQuantity, FillType.Taker);
                workingQuantity -= signedFillQuantity;
                if (ExchangeSimulator.MaskTaken)
                    _mask += fillQuantity;
            }
        }
    }


    public void Make(ulong clientOrderId, int ticks, int quantityFilled)
    {
        if (clientOrderId == Debug.OrderId)
        {
            Console.WriteLine($"            ExecutionSimulator.Make({clientOrderId}, {ticks}, {quantityFilled})");
        }

        ref OrderState orderState = ref TryGetOrderState(clientOrderId, out bool found);
        if (!found)
            throw new InvalidOperationException($"ExecutionSimulator({InstrumentDetails.Symbol}) can not Make Fill for ClientOrderId {clientOrderId}. GlobalOrderIndex is occupied by ClientOrderId {orderState.OrderHeader.OrderId}.");

        Update(ref orderState, orderState.OrderProfile, quantityFilled, OrderStateReason.PartialFill);
        ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_Fill(in orderState, _fillId++, ticks, quantityFilled, FillType.Maker);
    }

    private readonly OrderState[] _orderStates;



    // these need to update marketbyprice
    private void Delete(ref OrderState orderState, OrderManager orderManager)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"    ExecutionSimulator.Delete({orderState.OrderHeader.OrderId}, {orderState.OrderProfile.Ticks})");
        }

        orderManager.Delete(orderState.OrderHeader.OrderId, orderState.OrderProfile.Ticks);
        // FIX shape: a cancel leaves OrderQty alone and reports CumQty; LeavesQty goes to zero by
        // virtue of OrdStatus, not by rewriting the order. Overwriting Quantity with QuantityFilled
        // made every cancel indistinguishable from a complete fill, which is what let the risk layer
        // lose track of the reservation — and it destroyed the order's side when nothing had filled.
        Update(ref orderState, orderState.OrderProfile, 0, OrderStateReason.Canceled);
    }

    //try fill as taker
    private void Enqueue(ref OrderState orderState, OrderManager orderManager, OrderProfile orderProfile)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"    ExecutionSimulator.Enqueue(ClientOrderId: {orderState.OrderHeader.OrderId}, TargetTicks: {orderProfile.Ticks}, TargetQuantity: {orderProfile.Quantity})");
        }
        int workingQuantity = orderProfile.Quantity - orderState.QuantityFilled;
        Take(ref orderState, orderProfile, ref workingQuantity);
        if (workingQuantity != 0)
        {
            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"        ExecutionSimulator.Enqueue.Enqueue(WorkingQuantity: {workingQuantity})");
            }
            int quantityAhead = orderManager.Enqeue(orderState.OrderHeader.OrderId, orderProfile.Ticks, workingQuantity);
            orderState.QuantityAhead = quantityAhead;
            Update(ref orderState, orderProfile, 0, OrderStateReason.Acked);
        }
    }
    private void Reduce(ref OrderState orderState, OrderManager orderManager, OrderProfile orderProfile)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"    ExecutionSimulator.Reduce(ClientOrderId: {orderState.OrderHeader.OrderId}, StateQuantity: {orderState.OrderProfile.Quantity}, TargetQuantity: {orderProfile.Quantity})");
        }

        orderManager.Reduce(orderState.OrderHeader.OrderId, orderProfile.Ticks, orderProfile.Quantity - orderState.QuantityFilled);
        Update(ref orderState, orderProfile, 0, OrderStateReason.Acked);
    }

    private void Reprice(ref OrderState orderState, OrderManager orderManager, OrderProfile orderProfile)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"    ExecutionSimulator.Reprice(ClientOrderId: {orderState.OrderHeader.OrderId}, StateTicks: {orderState.OrderProfile.Ticks},  TargetTicks: {orderProfile.Ticks})");
        }

        orderManager.Delete(orderState.OrderHeader.OrderId, orderState.OrderProfile.Ticks);
        Enqueue(ref orderState, orderManager, orderProfile);
    }

    // orderStateReason is the TERMINAL reason this event implies — Canceled for a cancel, Filled for
    // anything that completes the order. While quantity is still outstanding the order stays Acked,
    // so the caller does not have to work out whether its own event finished the order.
    private void Update(ref OrderState orderState, OrderProfile orderProfile, int quantityFilled, OrderStateReason orderStateReason)
    {
        if (orderState.OrderStateStatus == OrderStateStatus.Done)
            throw new InvalidOperationException($"ExecutionSimulator({InstrumentDetails.Symbol}) can not update {orderState.OrderHeader.OrderId}. The order is alread done.");


        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"        ExecutionSimulator.Update(ClientOrderId: {orderState.OrderHeader.OrderId}, Ticks: {orderProfile.Ticks}, Quantity: {orderProfile.Quantity}, QuantityFilled: {quantityFilled})");
        }

        orderState.OrderProfile = orderProfile;
        orderState.QuantityFilled += quantityFilled;
        orderState.OrderHeader.ExchangeTimestamp = Clock.Now;

        // A cancel is terminal regardless of how much filled. Everything else is terminal only once
        // CumQty reaches OrderQty — which, now that a cancel no longer rewrites OrderQty, can only
        // mean a genuine complete fill.
        bool isDone = orderStateReason >= OrderStateReason.Canceled || orderState.OrderProfile.Quantity == orderState.QuantityFilled;

        if (isDone)
        {
            orderState.OrderStateStatus = OrderStateStatus.Done;
            orderState.OrderStateReason = orderStateReason == OrderStateReason.PartialFill ? OrderStateReason.Filled : orderStateReason;
            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"        ExecutionSimulator.Update.Done({orderStateReason})");
            }
        }
        else
        {
            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"        ExecutionSimulator.Update.Active");
            }
            orderState.OrderStateStatus = OrderStateStatus.Active;
            // Keep what the caller said: the reason is WHY this state is being published, so a fill
            // that leaves quantity working stays PartialFill and a rest/amend stays Acked.
            // Hardcoding Acked here made PartialFill unreachable, and made RiskLayer run its ack
            // path on every partial fill — releasing the order's reservation a second time on top
            // of OnFill.
            orderState.OrderStateReason = orderStateReason;
        }
        ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_OrderState(in orderState);
        UpdateMarketByPrice();
    }

    private ref OrderState TryGetOrderState(ulong clientOrderId, out bool found)
    {
        int globalOrderIndex = OrderIdAllocator.GetGlobalIndex(clientOrderId);
        ref OrderState orderState = ref _orderStates[globalOrderIndex];
        found = clientOrderId == orderState.OrderHeader.OrderId && orderState.OrderStateStatus == OrderStateStatus.Active;
        return ref orderState;
    }

    private ulong[] _minClientOrderId;
    public OrderState Target(in OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        if (orderTarget.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"ExecutionSimulator.Target(ClientOrderId: {orderTarget.OrderHeader.OrderId}, Client: {orderTarget.OrderHeader.OrderId.ClientId}, Action:{orderTarget.OrderTargetAction}, Seq: {orderTarget.OrderHeader.Seq}, Ticks: {orderTarget.OrderProfile.Ticks}, Quantity: {orderTarget.OrderProfile.Quantity})");
        }
        ref OrderState orderState = ref TryGetOrderState(orderTarget.OrderHeader.OrderId, out bool found);

        OrderProfile targetProfile = orderTarget.OrderProfile;
        orderRejectedReasons = new Bitset64();

        if (found)
        {
            if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
            {
                throw new Exception("Can not create a found order!");
            }

            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"ExecutionSimulator.Target.Found");
            }

            if (orderState.OrderHeader.Seq >= orderTarget.OrderHeader.Seq)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(InvalidSeq)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.SeqOutOfOrder);
            }


            orderState.OrderHeader.Seq = orderTarget.OrderHeader.Seq;

            if (!IsInSession)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(Closed)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.NotInSession);
            }

            if (orderState.OrderHeader.OrderId.StrategyId != orderTarget.OrderHeader.OrderId.StrategyId)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(InvalidStrategy)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.StrategyIdNotValid);
            }

            if (orderState.OrderHeader.OrderId.InstrumentId != InstrumentId)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(InstrumentMismatch)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.InstrumentIdNotValid);
            }

            OrderManager orderManager = orderState.OrderProfile.Side == Side.Sell ? Sells : Buys;
            OrderProfile stateProfile = orderState.OrderProfile;

            // Just cancel and skip other checks
            if (orderTarget.OrderTargetAction == OrderTargetAction.Cancel || targetProfile.Quantity == orderState.QuantityFilled)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(Delete)");
                }
                Delete(ref orderState, orderManager);
                return orderState;
            }

            //orderTargetAction == Amend


            if (stateProfile == targetProfile)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(TargetIsActive)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.TargetIsActive);
            }

            if (stateProfile.Side != targetProfile.Side)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(InvalidOrderProfile)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
            }

            if (targetProfile.Sign * (targetProfile.Quantity - orderState.QuantityFilled) < 0)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(InvalidOrderProfile)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.QuantityNotValid);
            }



            if (!orderRejectedReasons.IsEmpty)
                return orderState;



            int quantityDelta = targetProfile.Sign * (targetProfile.Quantity - stateProfile.Quantity);

            if (quantityDelta > 0 || targetProfile.Ticks != stateProfile.Ticks)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(Reprice)");
                }
                Reprice(ref orderState, orderManager, targetProfile);
                return orderState;
            }

            if (quantityDelta < 0)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Found(Reduce)");
                }
                Reduce(ref orderState, orderManager, targetProfile);
                return orderState;
            }

            throw new InvalidOperationException("Unreachable code");
        }
        else
        {
            if (!IsOrderEmpty(in orderState) && orderTarget.OrderTargetAction == OrderTargetAction.Create)
                throw new InvalidOperationException($"ExecutionSimulator({InstrumentDetails.Symbol}) can not Create New OrderState for ClientOrderId {orderTarget.OrderHeader.OrderId}. GlobalOrderIndex is occupied by ClientOrderId {orderState.OrderHeader.OrderId}.");

            if (orderTarget.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"ExecutionSimulator.Target.Missed");
            }

            if (!IsInSession)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Missed(ExchangeIsClosed)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.NotInSession);
            }

            if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
            {
                ref ulong minClientOrderId = ref _minClientOrderId[orderTarget.OrderHeader.OrderId.ClientId];
                if (orderTarget.OrderHeader.OrderId <= minClientOrderId)
                {
                    if (orderState.OrderHeader.OrderId == Debug.OrderId)
                    {
                        Console.WriteLine($"ExecutionSimulator.Target.Missed(DuplicateOrderId)");
                    }
                    orderRejectedReasons.Set((int)OrderRejectedReason.ClientOrderIdOutOfOrder);
                }
                minClientOrderId = Math.Max(minClientOrderId, orderTarget.OrderHeader.OrderId);

            }
            else
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Missed(OrderNotFound)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.OrderNotFound);
            }


            if (targetProfile.Quantity == 0)
            {
                if (orderState.OrderHeader.OrderId == Debug.OrderId)
                {
                    Console.WriteLine($"ExecutionSimulator.Target.Missed(InvalidOrderProfile)");
                }
                orderRejectedReasons.Set((int)OrderRejectedReason.QuantityNotValid);
                orderRejectedReasons.Set((int)OrderRejectedReason.SideNotValid);
            }

            if (!orderRejectedReasons.IsEmpty)
                return orderState;

            OrderManager orderManager = targetProfile.Side == Side.Sell ? Sells : Buys;
            // Init OrderState 
            orderState = new OrderState()
            {
                OrderHeader = orderTarget.OrderHeader,
                QuantityFilled = 0,
                OrderStateStatus = OrderStateStatus.Active,
                OrderStateReason = OrderStateReason.Acked,
                OrderProfile = targetProfile,
            };
            orderState.OrderHeader.ExchangeTimestamp = Clock.Now;
            orderState.OrderHeader.NicTimestamp = new Timestamp(0); // will be set when order is enqueued

            Enqueue(ref orderState, orderManager, targetProfile);
            return orderState;
        }
    }


}

public static class Debug
{
    public static ulong OrderId = 0;
}

public class ExchangeSimulator
{
    public bool MaskCrossed { get; set; } = true;
    public bool MaskTaken { get; set; } = true;
    internal FastArrayPool<byte> ByteArrayPool = new FastArrayPool<byte>();

    private readonly InstrumentSimulator[] _instrumentSimulators;
    private readonly ByteQueue _byExchangeTimestamp = new ByteQueue(64 * 4096);

    public DataSimulator DataSimulator { get; }
    public ServerSimulator ServerSimulator { get; }
    public ExchangeSimulator(ServerSimulator serverSimulator)
    {
        ServerSimulator = serverSimulator;
        DataSimulator = new DataSimulator("ExchangeSimulator" + "Data", this);
        _instrumentSimulators = new InstrumentSimulator[ServerSimulator.ServerHeader.InstrumentIds.Length];
        Clock.Interject += OnInterject;
        Clock.TickTock += OnTickTock;
    }

    public void OnMarketByPrice(in MarketByPrice mbp, ReadOnlySpan<byte> src)
    {
        _instrumentSimulators[mbp.TickHeader.InstrumentId].OnMarketByPrice(in mbp, src);
    }

    public void OnTick(ref Tick tick)
    {
        if (tick.TickHeader.TickType == TickType.Trade)
        {
            _instrumentSimulators[tick.TickHeader.InstrumentId].OnTrade(ref Unsafe.As<Tick, Trade>(ref tick));
        }
        else if (tick.TickHeader.TickType == TickType.Settlement)
        {
            ServerSimulator.FromExchangeToNicToClient_Tick(ref tick);
        }
        else
        {
            throw new NotSupportedException($"ExchangeSimulator.OnTick does not support TickType {tick.TickHeader.TickType}");
        }
    }

    public void FromClientToExchange_OrderTarget(in OrderTarget orderTarget)
    {
        Span<byte> dst = _byExchangeTimestamp.Enqueue(Unsafe.SizeOf<OrderTarget>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp exchangeTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        MemoryMarshal.Write(dst, in orderTarget);

        ref OrderTarget orderTargetCopy = ref MemoryMarshal.AsRef<OrderTarget>(dst);
        exchangeTimestamp = orderTargetCopy.OrderHeader.NicTimestamp.AddMicroseconds(ServerSimulator.FromExchangeToNicToClientLatency);
        orderTargetCopy.OrderHeader.ExchangeTimestamp = exchangeTimestamp;
    }


    public void Allocate(InstrumentDetails details, int instrumentId)
    {
        if (_instrumentSimulators[instrumentId] != null)
            return;

        DataSimulator.Subscribe(details.Symbology, instrumentId);
        _instrumentSimulators[instrumentId] = new InstrumentSimulator(this, details, instrumentId);
    }

    private void OnInterject(Timestamp timestamp)
    {
        if (_byExchangeTimestamp.TryPeek(out Span<byte> src))
        {
            Clock.OnInterject(MemoryMarshal.Read<Timestamp>(src));
        }
        DataSimulator.OnInterject(timestamp);
    }
    private void OnTickTock(Timestamp timestamp)
    {
        DataSimulator.OnTickTock(timestamp);

        while (_byExchangeTimestamp.TryPeek(out Span<byte> src) && MemoryMarshal.AsRef<Timestamp>(src) <= timestamp)
        {
            /*
            if (DataSimulator.TryPeek(out Timestamp nextData))
            {
                ref Timestamp nextTarget = ref MemoryMarshal.AsRef<Timestamp>(src);
                Duration queue = nextData - nextTarget;
                if (queue < Duration.FromMicroseconds(ServerSimulator.ExchangeOrderQueueLatency))
                {
                    nextTarget = nextTarget.AddDuration(queue).AddMicroseconds(5);
                    break;
                }
            }
            */
            src = src.Slice(Unsafe.SizeOf<Timestamp>());
            ref readonly OrderTarget orderTarget = ref MemoryMarshal.AsRef<OrderTarget>(src);
            OnOrderTarget(in orderTarget);
            _byExchangeTimestamp.Dequeue();
        }
    }

    public void CancelAllOrders(int clientId)
    {
        foreach (InstrumentSimulator instrumentSimulator in _instrumentSimulators)
        {
            instrumentSimulator?.CancelAllOrders(clientId);
        }
    }

    private void OnOrderTarget(in OrderTarget orderTarget)
    {
        if (orderTarget.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine();
        }

        InstrumentSimulator instrumentExecutionSimulator = _instrumentSimulators[orderTarget.OrderHeader.OrderId.InstrumentId];
        OrderState orderState = instrumentExecutionSimulator.Target(in orderTarget, out Bitset64 orderRejectedReasons);

        if (orderTarget.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"ExecutionSimulator.Target.OrderState(ClientOrderId: {orderState.OrderHeader.OrderId}, Client: {orderState.OrderHeader.OrderId.ClientId}, Status:{orderState.OrderStateStatus}, Seq: {orderState.OrderHeader.Seq}, Ticks: {orderState.OrderProfile.Ticks}, WorkingQuantity: {orderState.OrderProfile.Quantity - orderState.QuantityFilled}, Quantity: {orderState.OrderProfile.Quantity}, QuantityFilled: {orderState.QuantityFilled})");
        }

        if (!orderRejectedReasons.IsEmpty)
        {
            OrderRejected orderRejected = new OrderRejected()
            {
                OrderHeader = orderTarget.OrderHeader,
                OrderProfile = orderTarget.OrderProfile,
                OrderTargetAction = orderTarget.OrderTargetAction,
                OrderRejectedReasons = orderRejectedReasons,
                OrderRejectedSource = OrderRejectedSource.Exchange,
            };

            ServerSimulator.FromExchangeToNicToClient_OrderRejected(in orderRejected);
        }
    }

}



public class ServerSimulator
{

    // Primary constructor parameters are *in scope* throughout the class.
    public FileSystemPath ServerName { get; }
    public ref readonly ServerHeader ServerHeader => ref _server.Context.ServerHeader.GetRef();

    private readonly ByteQueue _byClientTimestamp;

    // Everything the server does — sockets, context, risk, instrument rings, audit — lives in Server
    // and is shared with the realtime C++ build. This class only supplies the timing around it:
    //
    //   exchange -> _byClientTimestamp -> ServerSimulator -> Server -> socket -> client
    //   client   -> socket -> Server -> ServerSimulator -> _byExchangeTimestamp -> exchange
    //
    // so the client->server leg is instant (Server.ReadExecution/ReadAdmin are called directly) and
    // only the exchange->client leg is delayed.
    private readonly Server _server;

    public bool OverrideNicTimestamp { get; set; } = false;

    // from exchange event to nic, include SendingDelay + Wire
    public int FromExchangeToNicLatency { get; set; } = 100; 

    // Worst case How long it takes to parse a message
    public int FromNicToClientLatency { get; set; } = 100; 
    public int FromExchangeToNicToClientLatency => FromExchangeToNicLatency + FromNicToClientLatency;

    public Server Server => _server;
    public ServerContext ServerContext => _server.Context;

    private const int s_instrumentsCapacity = 4096;
    private const int s_ordersPerClient = 64;
    private const int ExecutionCoreGroupId = 1; // the single trading CoreGroup all sim instruments use

    public ExchangeSimulator ExchangeSimulator { get; }

    public ServerSimulator(FileSystemPath serverName, bool startLogginServer = false)
    {
        ServerName = serverName;
        Console.WriteLine($"Server Simulator Running PID: {Environment.ProcessId}");
        ServerHeader serverHeader = new ServerHeader()
        {
            ServerName = new String128(ServerName),
            InstrumentsCapacity = s_instrumentsCapacity,
            OrdersPerClient = s_ordersPerClient,
        };
        serverHeader.CoreGroupIds.Set(0); // admin / housekeeping channel
        serverHeader.CoreGroupIds.Set(ExecutionCoreGroupId); // single trading CoreGroup (all sim instruments)

        // Must be up before Server's constructor, which connects its .server and .audit sockets to it.
        if (startLogginServer)
        {
            StartLoggingServer(Provider.Context.GetLoggingServerDirectoryPath(ServerName));
        }

        // Server publishes the header, opens the context, sockets, audit and risk layer.
        _server = new Server(in serverHeader);

        ExchangeSimulator = new ExchangeSimulator(this);

        _byClientTimestamp = new ByteQueue(64 * 4096);

        // Outbound leg: Server validates, and only a target that passes risk reaches the exchange —
        // where ExchangeSimulator applies ExchangeOrderQueueLatency in its own queue.
        _server.OrderTarget += ExchangeSimulator.FromClientToExchange_OrderTarget;

        // Server cancels the dead client's book itself; the exchange has to drop its resting orders.
        _server.ClientClosed += ExchangeSimulator.CancelAllOrders;

        // Fires once per instrument, on first allocation, before any client is attached to it.
        _server.AllocateInstrument += OnServerAllocateInstrument;

        Clock.Interject += OnInterject;
        Clock.TickTock += OnTickTock;

        void ensureInterjectionForClockUpdate()
        {
            Clock.AddReminder(new Reminder(Clock.Now.AddMinutes(1), ts =>
            {
                _server.Context.ServerHeader.GetRef().Timestamp = Clock.Now;
                ensureInterjectionForClockUpdate();
            }));
        }

        ensureInterjectionForClockUpdate();

        Init();
    }
    public bool OpenConsoleForLogger { get; set; } = true;
    private void StartLoggingServer(string loggingName)
    {
        try
        {
            string[] args = new string[] { loggingName, Environment.ProcessId.ToString() };
            Console.WriteLine($"ServerSimulator launching LoggingServer...");
            Tools.Process.Start("Logging", args, OpenConsoleForLogger);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start LoggingServer: {ex.Message}");
        }
    }

    // Server has already allocated the instrument and opened its broadcast ring; all that is left is
    // to give the exchange sim a matching book to match against.
    private void OnServerAllocateInstrument(AllocateInstrument allocateInstrument)
    {
        if (_instrumentDetailsByInstrumentHeaderId.TryGetValue(allocateInstrument.InstrumentHeaderId, out InstrumentDetails details))
        {
            ExchangeSimulator.Allocate(details, allocateInstrument.InstrumentId);
        }
    }

    


    public void FromExchangeToNicToClient_Fill(in OrderState orderState, ulong fillId, int ticks, int quantity, FillType fillType)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"                ExecutionSimulator.FromExchangeToNic_Fill(ClientOrderId: {orderState.OrderHeader.OrderId}, FillId: {fillId}, Ticks: {ticks}, Quantity: {quantity}, FillType: {fillType})");
        }

        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<Fill>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp nicTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        nicTimestamp = Clock.Now.AddMicroseconds(FromExchangeToNicToClientLatency);
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        ref Fill fill = ref MemoryMarshal.AsRef<Fill>(dst);

        Timestamp exchangeTimestamp = Clock.Now;
        fill = new Fill()
        {
            OrderHeader = new()
            {
                OrderId = orderState.OrderHeader.OrderId,
                ExchangeTimestamp = exchangeTimestamp,
                NicTimestamp = exchangeTimestamp.AddMicroseconds(FromExchangeToNicToClientLatency),
                Seq = orderState.OrderHeader.Seq,
            },
            FillId = fillId,
            FillType = fillType,
            OrderProfile = new(ticks, quantity),
        };
    }
    public void FromExchangeToNicToClient_OrderState(in OrderState orderState)
    {
        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"                ExecutionSimulator.FromExchangeToNic_OrderState(ClientOrderId: {orderState.OrderHeader.OrderId}, Client: {orderState.OrderHeader.OrderId.ClientId}, Status:{orderState.OrderStateStatus}, Seq: {orderState.OrderHeader.Seq}, Ticks: {orderState.OrderProfile.Ticks}, WorkingQuantity: {orderState.OrderProfile.Quantity - orderState.QuantityFilled}, Quantity: {orderState.OrderProfile.Quantity}, QuantityFilled: {orderState.QuantityFilled})");
        }

        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in orderState, 1));
        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<OrderState>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp nicTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        nicTimestamp = Clock.Now.AddMicroseconds(FromExchangeToNicToClientLatency);
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        src.CopyTo(dst);
        MemoryMarshal.AsRef<OrderState>(dst).OrderHeader.NicTimestamp = nicTimestamp;
    }

    public void FromExchangeToNicToClient_OrderRejected(in OrderRejected orderRejected)
    {
        if (orderRejected.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"                ExecutionSimulator.FromExchangeToNic_OrderRejected(ClientOrderId: {orderRejected.OrderHeader.OrderId}, Client: {orderRejected.OrderHeader.OrderId.ClientId}, Reasons:{orderRejected.OrderRejectedReasonsString}, Seq: {orderRejected.OrderHeader.Seq}, Ticks: {orderRejected.OrderProfile.Ticks}, Quantity: {orderRejected.OrderProfile.Quantity}");
        }

        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in orderRejected, 1));
        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<OrderRejected>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp nicTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        nicTimestamp = Clock.Now.AddMicroseconds(FromExchangeToNicToClientLatency);
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        src.CopyTo(dst);
        MemoryMarshal.AsRef<OrderRejected>(dst).OrderHeader.NicTimestamp = nicTimestamp;
    }



    public void FromExchangeToNicToClient_AheadOfOrder(AheadOfOrder aheadOfOrder)
    {
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in aheadOfOrder, 1));
        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<AheadOfOrder>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp nicTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        nicTimestamp = Clock.Now.AddMicroseconds(FromExchangeToNicToClientLatency);
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        src.CopyTo(dst);
    }

    public void FromExchangeToNicToClient_MarketByPrice(ref MarketByPrice mbp, ReadOnlySpan<byte> src)
    {
        Timestamp nicTimestamp = OverrideNicTimestamp ? mbp.TickHeader.ExchangeTimestamp.AddMicroseconds(FromExchangeToNicToClientLatency) : mbp.TickHeader.NicTimestamp.AddMicroseconds(FromNicToClientLatency);
        mbp.TickHeader.NicTimestamp = nicTimestamp;

        Span<byte> dst = _byClientTimestamp.Enqueue(src.Length + Unsafe.SizeOf<Timestamp>());
        ref Timestamp queueTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        queueTimestamp = mbp.TickHeader.NicTimestamp;

        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        src.CopyTo(dst);
    }
    public void FromExchangeToNicToClient_Tick(ref Tick tick)
    {
        Timestamp nicTimestamp = OverrideNicTimestamp ? tick.TickHeader.ExchangeTimestamp.AddMicroseconds(FromExchangeToNicToClientLatency) : tick.TickHeader.NicTimestamp.AddMicroseconds(FromNicToClientLatency);
        tick.TickHeader.NicTimestamp = nicTimestamp;

        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<Tick>() + Unsafe.SizeOf<Timestamp>());
        ref Timestamp queueTimestamp = ref MemoryMarshal.AsRef<Timestamp>(dst);
        queueTimestamp = tick.TickHeader.NicTimestamp;

        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in tick, 1));
        src.CopyTo(dst);

    }

    public void FromExchangeToNicToClient_Trade(in Trade trade)
        => FromExchangeToNicToClient_Tick(ref Unsafe.As<Trade, Tick>(ref Unsafe.AsRef(in trade)));


    private readonly HashMap<string, InstrumentDetails> _instrumentDetailsBySymbol = new HashMap<string, InstrumentDetails>();
    private readonly HashMap<int, InstrumentDetails> _instrumentDetailsByInstrumentHeaderId = new HashMap<int, InstrumentDetails>();


    public void OnInstrumentDetails(InstrumentDetails instrumentDetails)
    {
        if (_instrumentDetailsBySymbol.TryAdd(instrumentDetails.Symbology.Symbol, instrumentDetails))
        {
            int instrumentHeaderId = _instrumentDetailsByInstrumentHeaderId.Count;
            _instrumentDetailsByInstrumentHeaderId.TryAdd(instrumentHeaderId, instrumentDetails);

            InstrumentHeader128 header128 = default;
            ref InstrumentHeader header = ref Unsafe.As<InstrumentHeader128, InstrumentHeader>(ref header128);

            header = new InstrumentHeader()
            {
                InstrumentType = instrumentDetails.InstrumentType,
                CoreGroupId = ExecutionCoreGroupId,
                InstrumentId = -1,
                InstrumentHeaderId = instrumentHeaderId,
                Exchange = new String8(instrumentDetails.Exchange),
                Root = new String8(instrumentDetails.Root),
                InverseTickSize = instrumentDetails.InverseTickSize,
                TickSize = instrumentDetails.TickSize,
            };
            if (instrumentDetails.InstrumentType == InstrumentType.Future)
            {
                ref FutureHeader future = ref Unsafe.As<InstrumentHeader128, FutureHeader>(ref header128);
                future.Multiplier = instrumentDetails.Multiplier;
                future.MaturityDate = instrumentDetails.MaturityDate!.Value;
                future.MaturityType = instrumentDetails.MaturityType!.Value;
            }
            else if (instrumentDetails.InstrumentType == InstrumentType.Spread)
            {
                // Legs are the source of truth; InstrumentDetails.SpreadHeader resolves them to the
                // header's fixed long/short pair so that interpretation lives in exactly one place.
                SpreadHeader fromDetails = instrumentDetails.SpreadHeader;
                ref SpreadHeader spread = ref Unsafe.As<InstrumentHeader128, SpreadHeader>(ref header128);
                spread.Multiplier = instrumentDetails.Multiplier;
                spread.ShortMaturityDate = fromDetails.ShortMaturityDate;
                spread.ShortMaturityType = fromDetails.ShortMaturityType;
                spread.LongMaturityDate = fromDetails.LongMaturityDate;
                spread.LongMaturityType = fromDetails.LongMaturityType;
                spread.ShortInstrumentId = -1;
                spread.LongInstrumentId = -1;
            }
            _server.OnInstrumentHeader(in header128);
            foreach(InstrumentDetail instrumentDetail in instrumentDetails.Schedule)
            {
                Clock.AddReminder(new Reminder(instrumentDetail.Timestamp, ts =>
                {
                    throw new NotImplementedException();
                }));
            }
        }
    }

    public void Init()
    {
        if (Clock.IsRunning)
            return;

        bool simReady = false;
        Clock.Started += begin =>
        {
            while (!simReady)
                X86BaseWrapper.Pause();
        };
        Thread thread = new Thread(() =>
        {
            Thread.CurrentThread.Name = $"{ServerName}.Init()";
            while (!Clock.IsRunning)
            {
                // Clients allocate their instruments before the clock starts, so this is the same
                // admin drain the run loop uses — unthrottled, because nothing else is happening yet.
                _server.ReadAdmin();
                X86BaseWrapper.Pause();
            }
            OnInterject(Timestamp.MinValue);
            simReady = true;
        });
        thread.Start();
    }

    public void Connect()
    {
        _server.Connect();
    }

    // interrupt the clock
    private Timestamp _lastAdminRead = Timestamp.MinValue;
    protected void OnInterject(Timestamp timestamp)
    {
        // Client -> socket -> Server, with no delay on this leg. Server validates each target and
        // fires OrderTarget, which is bound to ExchangeSimulator's own latency queue, so the only
        // delay on the way out is the exchange's.
        _server.ReadExecution(ExecutionCoreGroupId);

        // Admin is polled at most once a second: allocations happen in Init() before the clock runs,
        // and scanning every client's admin channel on each interject is pure cost during a backtest.
        if (_lastAdminRead.AddSeconds(1) <= timestamp)
        {
            _lastAdminRead = timestamp;
            _server.ReadAdmin();
        }

        if (_byClientTimestamp.TryPeek(out Span<byte> nicSrc))
        {
            Clock.OnInterject(MemoryMarshal.Read<Timestamp>(nicSrc));
        }
    }



    protected void OnTickTock(Timestamp now)
    {
        SharedArrayEntry<ServerHeader> serverHeaderEntry = _server.Context.ServerHeader;
        serverHeaderEntry.AcquireLock();
        _server.Context.ServerHeader.GetRef().Timestamp = now;
        serverHeaderEntry.ReleaseLock();
        Timestamp timestamp = Timestamp.MinValue;
        // Release everything the exchange sent whose NIC timestamp has now arrived. This is the only
        // delayed leg: from here on it is plain Server work, identical to what the realtime build does.
        while (_byClientTimestamp.TryPeek(out Span<byte> src) && (timestamp = MemoryMarshal.AsRef<Timestamp>(src)) <= now)
        {
            src = src.Slice(Unsafe.SizeOf<Timestamp>());
            byte type = src[0];
            switch (type)
            {
                case (byte)TickType.MarketByPriceUpdate:
                    ref readonly MarketByPrice update = ref MemoryMarshal.AsRef<MarketByPrice>(src);
                    _server.OnMarketByPrice(in update, src);
                    break;
                case (byte)TickType.Trade:
                case (byte)TickType.Settlement:
                    ref readonly Tick tick = ref MemoryMarshal.AsRef<Tick>(src);
                    _server.WriteToInstrumentData(in tick);
                    break;
                case (byte)OrderType.Fill:
                    Fill fill = MemoryMarshal.Read<Fill>(src);
                    _server.OnFill(ref fill);
                    break;
                case (byte)OrderType.AheadOfOrder:
                    ref readonly AheadOfOrder aheadOfOrder = ref MemoryMarshal.AsRef<AheadOfOrder>(src);
                    _server.OnQuantityAhead(aheadOfOrder.ClientOrderId, aheadOfOrder.Quantity);
                    break;
                case (byte)OrderType.OrderState:
                    OrderState orderState = MemoryMarshal.Read<OrderState>(src);
                    _server.OnOrderState(ref orderState);
                    break;
                case (byte)OrderType.OrderRejected:
                    OrderRejected orderRejected = MemoryMarshal.Read<OrderRejected>(src);
                    OnExchangeOrderRejected(ref orderRejected);
                    break;
                default: // unknown
                         // handle/skip
                    break;
            }
            _byClientTimestamp.Dequeue();
        }
    }

    // The exchange refusing a Create leaves the slot with no terminal state — in production the
    // vendor session delivers that separately, so synthesise it here before routing the reject.
    private void OnExchangeOrderRejected(ref OrderRejected orderRejected)
    {
        if (orderRejected.OrderTargetAction == OrderTargetAction.Create)
        {
            OrderState orderState = new OrderState()
            {
                OrderHeader = orderRejected.OrderHeader,
                OrderProfile = orderRejected.OrderProfile,
                QuantityFilled = 0,
                QuantityAhead = 0,
                OrderStateStatus = OrderStateStatus.Done,
                OrderStateReason = OrderStateReason.Rejected,
            };
            _server.OnOrderState(ref orderState);
        }
        _server.OnOrderRejected(ref orderRejected, "Rejected by Exchange");
    }

}
