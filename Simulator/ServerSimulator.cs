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
    private readonly MarketByPriceByOrder _marketByPriceByOrder = new MarketByPriceByOrder();
    private readonly ArrayList<Level> _marketByPriceByOrderBidsChanged = new ArrayList<Level>(64);
    private readonly ArrayList<Level> _marketByPriceByOrderAsksChanged = new ArrayList<Level>(64);
    public ref MarketByPrice64 MarketByPrice64 => ref _marketByPrice64;

    public Quote GetQuote()
    {
        if (_marketByPrice64.BidsCount < 0 || _marketByPrice64.AsksCount < 0)
            throw new InvalidOperationException("Invalid market state: negative bid or ask count.");
        Quote quote = new(_marketByPrice64.BestBid, _marketByPrice64.BestAsk, InstrumentDetails.TickSize);
        return quote;
    }

    // Ahead-blob seeding: full-depth totals when MBO data drives this instrument (aggregation non-empty), the windowed book otherwise
    internal int GetMarketQuantity(Side side, int ticks)
    {
        SideByPriceByOrder sideByPriceByOrder = _marketByPriceByOrder.GetSide(side);
        if (sideByPriceByOrder.OrdersCount > 0)
            return sideByPriceByOrder.GetQuantity(ticks);
        return side == Side.Buy ? _marketByPrice64.GetBidQuantity(ticks) : _marketByPrice64.GetAskQuantity(ticks);
    }

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

        _marketByPriceByOrder.Bids.Changed += OnBidChanged;
        _marketByPriceByOrder.Asks.Changed += OnAskChanged;

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

    private void OnBidChanged(Level level)
    {
        for (int i = 0; i < _marketByPriceByOrderBidsChanged.Count; i++)
        {
            if (_marketByPriceByOrderBidsChanged[i].Ticks == level.Ticks)
            {
                _marketByPriceByOrderBidsChanged[i] = level;   // aggregate: last total per level wins
                return;
            }
        }
        _marketByPriceByOrderBidsChanged.Add(level);
    }
    private void OnAskChanged(Level level)
    {
        for (int i = 0; i < _marketByPriceByOrderAsksChanged.Count; i++)
        {
            if (_marketByPriceByOrderAsksChanged[i].Ticks == level.Ticks)
            {
                _marketByPriceByOrderAsksChanged[i] = level;   // aggregate: last total per level wins
                return;
            }
        }
        _marketByPriceByOrderAsksChanged.Add(level);
    }
    protected ulong _fillId { get; set; } = 0;

    private int _minMaskBid = int.MaxValue;
    private int _maxMaskAsk = int.MinValue;

    private bool _inOnMarketByPrice = false;
    private bool _inOnMarketByOrder = false;

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

    public void OnMarketByOrder(in MarketByOrder mbo, ReadOnlySpan<byte> src)
    {
        _inOnMarketByOrder = true;
        if (mbo.TickHeader.TickType == TickType.MarketByOrderSnapshot)
        {
            // hooks stay silent (Clear does not report removals); the derived snapshot diffs through the MBP path instead
            _marketByPriceByOrder.ApplySnapshot(src);

            int size = _marketByPriceByOrder.MarketByPriceSizeOf(SideByPrice64.Capacity);
            byte[] rented = ExchangeSimulator.ByteArrayPool.Rent(size);
            ref MarketByPrice snapshot = ref _marketByPriceByOrder.CopyToMarketByPriceSnapshot(InstrumentId, rented.AsSpan(0, size), SideByPrice64.Capacity);
            OnMarketByPrice(in snapshot, rented.AsSpan(0, size));
            ExchangeSimulator.ByteArrayPool.Return(rented);
        }
        else
        {
            // events first: marker -> trades -> exact queue deltas; the level update publishes last so AddGhost reads pre-trade totals
            OnMarketByOrder(in mbo, mbo.BidsAsSpan(src), Buys);
            OnMarketByOrder(in mbo, mbo.AsksAsSpan(src), Sells);

            _marketByPriceByOrderBidsChanged.Clear();
            _marketByPriceByOrderAsksChanged.Clear();
            _marketByPriceByOrder.Apply(src);
            OnMarketByPriceByOrderChangedLevels(in mbo.TickHeader);
        }
        _inOnMarketByOrder = false;
    }

    // Synthesizes the historical level update and feeds it to OnMarketByPrice, so the changed levels run
    // the full MBP pipeline: TrySet deltas, crossing, mask overlay, TicksRemoved restore, user overlay, publish.
    private void OnMarketByPriceByOrderChangedLevels(in TickHeader header)
    {
        Span<byte> span = stackalloc byte[MarketByPrice.SizeOf(_marketByPriceByOrderBidsChanged.Count, _marketByPriceByOrderAsksChanged.Count)];
        ref MarketByPrice update = ref MemoryMarshal.AsRef<MarketByPrice>(span);
        update = new MarketByPrice(TickType.MarketByPriceUpdate, InstrumentId, header.ExchangeTimestamp, header.SendingTimestamp, header.NicTimestamp, _marketByPriceByOrderBidsChanged.Count, _marketByPriceByOrderAsksChanged.Count);
        Span<Level> bids = update.BidsAsSpan(span);
        for (int i = 0; i < _marketByPriceByOrderBidsChanged.Count; i++) bids[i] = _marketByPriceByOrderBidsChanged[i];
        Span<Level> asks = update.AsksAsSpan(span);
        for (int i = 0; i < _marketByPriceByOrderAsksChanged.Count; i++) asks[i] = _marketByPriceByOrderAsksChanged[i];
        OnMarketByPrice(in update, span);
    }

    private void OnMarketByOrder(in MarketByOrder mbo, ReadOnlySpan<Order> orders, OrderManager orderManager)
    {
        for (int i = 0; i < orders.Length; i++)
        {
            ref readonly Order order = ref orders[i];
            Buys.OnPriorityId(order.PriorityId);
            Sells.OnPriorityId(order.PriorityId);

            switch (order.OrderAction)
            {
                case MarketByOrderAction.Add:
                    orderManager.OnMarketByPriceByOrderDelta(order.PriorityId, order.Level.Ticks, +order.Level.Quantity);
                    break;
                case MarketByOrderAction.Reduce:
                case MarketByOrderAction.Cancel:
                    orderManager.OnMarketByPriceByOrderDelta(order.PriorityId, order.Level.Ticks, -order.Level.Quantity);
                    break;
                case MarketByOrderAction.Trade:
                    Trade trade = new Trade(InstrumentId, mbo.TickHeader.ExchangeTimestamp, mbo.TickHeader.SendingTimestamp, mbo.TickHeader.NicTimestamp, order.Level.Ticks, order.Level.Quantity, (sbyte)order.Side);
                    OnTrade(ref trade);   // masks + opposite-side queue fills + trade prints to clients, unchanged
                    break;
            }
        }
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
                if (!_inOnMarketByOrder)   // MBO queues were fed exact per-event deltas; level deltas would double-count
                    Sells.OnMarketByPriceDelta(ask.Ticks, delta);
            }
        }
        ReadOnlySpan<Level> marketBids = update.BidsAsSpan(src);
        foreach (Level bid in marketBids)
        {
            _bids.Add(bid);
            if (_marketByPrice64.TrySetBidQuantity(bid.Ticks, bid.Quantity, out int delta))
            {
                if (!_inOnMarketByOrder)   // MBO queues were fed exact per-event deltas; level deltas would double-count
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
        if (_inOnMarketByPrice || _inOnMarketByOrder)   // the end-of-message synthetic update republishes; a mid-walk republish can remove a queue node the trade path still holds a ref to
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

        // if market is cross its probably closed or in auction, so don't take any fills
        if (_marketByPrice64.IsCrossed)
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

                Update(ref orderState, orderProfile, signedFillQuantity, OrderStateReason.Fill);
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

        Update(ref orderState, orderState.OrderProfile, quantityFilled, OrderStateReason.Fill);
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

        // CME reports the acceptance before any trade it causes, new order and replace alike (see Spec.md).
        // A marketable order is at the front of whatever it sweeps, so nothing is ahead of it; a resting
        // one takes its place in the book first so the ack carries its real queue position.
        bool isMarketable = IsMarketable(orderProfile, workingQuantity);
        orderState.QuantityAhead = isMarketable ? 0 : orderManager.Enqeue(orderState.OrderHeader.OrderId, orderProfile.Ticks, workingQuantity);
        Update(ref orderState, orderProfile, 0, OrderStateReason.Acked);

        if (!isMarketable)
            return;

        Take(ref orderState, orderProfile, ref workingQuantity);
        if (workingQuantity != 0)
        {
            if (orderState.OrderHeader.OrderId == Debug.OrderId)
            {
                Console.WriteLine($"        ExecutionSimulator.Enqueue.Enqueue(WorkingQuantity: {workingQuantity})");
            }
            // Whatever survives the sweep rests at the limit. CME sends no second ack for it, so neither do we.
            orderState.QuantityAhead = orderManager.Enqeue(orderState.OrderHeader.OrderId, orderProfile.Ticks, workingQuantity);
        }
    }

    // Would Take trade at least one lot right now: the same guard Take's loop applies against the opposite best.
    // Probed, not enqueued: a crossing order placed in our own book first would make IsCrossed true and Take would never trade.
    private bool IsMarketable(OrderProfile orderProfile, int workingQuantity)
    {
        if (_marketByPrice64.IsCrossed)
            return false;
        if (workingQuantity > 0)
            return _marketByPrice64.AsksCount > 0 && orderProfile.Ticks >= _marketByPrice64.BestAsk.Ticks;
        return _marketByPrice64.BidsCount > 0 && orderProfile.Ticks <= _marketByPrice64.BestBid.Ticks;
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
        // Keep what the caller said: the reason is WHY this state is being published — a fill stays
        // Fill whether or not it completes the order (Done/Active carries that), a rest/amend stays
        // Acked, so RiskLayer's ack path never runs on fills.
        orderState.OrderStateReason = orderStateReason;

        // A cancel is terminal regardless of how much filled. Everything else is terminal only once
        // CumQty reaches OrderQty — which, now that a cancel no longer rewrites OrderQty, can only
        // mean a genuine complete fill.
        bool isDone = orderStateReason >= OrderStateReason.Canceled || orderState.OrderProfile.Quantity == orderState.QuantityFilled;
        orderState.OrderStateStatus = isDone ? OrderStateStatus.Done : OrderStateStatus.Active;

        if (orderState.OrderHeader.OrderId == Debug.OrderId)
        {
            Console.WriteLine($"        ExecutionSimulator.Update.{(isDone ? "Done" : "Active")}({orderStateReason})");
        }

        // A fill's state rides inside its Fill entry (see FromExchangeToNicToClient_Fill) so the
        // server applies the pair atomically; every other state change still travels alone.
        if (orderStateReason != OrderStateReason.Fill)
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

            // Just cancel and skip other checks. An amend downsizing total quantity to at or below
            // the filled quantity is a cancel of the remainder, never a reject — Globex in-flight
            // mitigation. The amend's quantity is never applied: the terminal state keeps the order's
            // own profile (see the FIX-shape comment in Delete), exactly as CME reports it.
            if (orderTarget.OrderTargetAction == OrderTargetAction.Cancel || targetProfile.Sign * (targetProfile.Quantity - orderState.QuantityFilled) <= 0)
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
    public InstrumentSimulator GetInstrument(int instrumentId)
    {
        InstrumentSimulator instrumentSimulator = _instrumentSimulators[instrumentId];
        if (instrumentSimulator == null)
            throw new ArgumentOutOfRangeException(nameof(instrumentId));
        return instrumentSimulator;
    }
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

    public void OnMarketByOrder(in MarketByOrder mbo, ReadOnlySpan<byte> src)
    {
        _instrumentSimulators[mbo.TickHeader.InstrumentId].OnMarketByOrder(in mbo, src);
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

        InstrumentDetails.GetLeg = leg => _instrumentDetailsBySymbol[leg.Symbol];

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

        int instrumentId = orderState.OrderHeader.OrderId.InstrumentId;
        Instrument instrument = ServerContext.GetInstrument(instrumentId);

        // The order's own fill plus one per leg for a legged instrument. The order's own fill is the
        // real fill for an outright, and the accounting/volume fill on the spread row for a spread.
        int fillCount = 1 + (instrument.IsLegged ? instrument.Legs.Length : 0);

        // One ER, one entry: [Timestamp][OrderState][Fill × fillCount]. OrderState leads so the
        // release site dispatches on it; its OrderStateReason.Fill says trailing fills follow, and
        // the entry length gives their count. Server.OnFill applies the whole set atomically.
        Timestamp exchangeTimestamp = Clock.Now;
        Span<byte> dst = _byClientTimestamp.Enqueue(Unsafe.SizeOf<Timestamp>() + Unsafe.SizeOf<OrderState>() + fillCount * Unsafe.SizeOf<Fill>());
        Timestamp nicTimestamp = Clock.Now.AddMicroseconds(FromExchangeToNicToClientLatency);
        MemoryMarshal.AsRef<Timestamp>(dst) = nicTimestamp;
        dst = dst.Slice(Unsafe.SizeOf<Timestamp>());

        ref OrderState pairedOrderState = ref MemoryMarshal.AsRef<OrderState>(dst);
        pairedOrderState = orderState;
        pairedOrderState.OrderHeader.NicTimestamp = nicTimestamp;

        Span<Fill> fills = MemoryMarshal.Cast<byte, Fill>(dst.Slice(Unsafe.SizeOf<OrderState>()));

        OrderId orderId = orderState.OrderHeader.OrderId;
        int orderSeq = orderState.OrderHeader.Seq;
        Timestamp fillNicTimestamp = nicTimestamp;

        void emplaceFill(ref Fill fill, int legInstrumentId, double legPrice, int legQuantity)
        {
            // In-place over raw ring memory: run the field initializers or the wire type byte
            // (Header) is recycled garbage and every downstream reader misdispatches.
            OrderId legOrderId = orderId;
            legOrderId.InstrumentId = legInstrumentId;
            fill = new Fill()
            {
                OrderHeader = new()
                {
                    OrderId = legOrderId,
                    ExchangeTimestamp = exchangeTimestamp,
                    NicTimestamp = fillNicTimestamp,
                    Seq = orderSeq,
                },
                FillId = fillId,
                FillType = fillType,
                Price = legPrice,
                Quantity = legQuantity,
            };
        }

        // fills[0]: the order's own fill (spread price/quantity for a spread — accounting only).
        emplaceFill(ref fills[0], instrumentId, instrument.TicksToPrice(ticks), quantity);

        // Leg fills — case by case, because each legged type prices its legs differently. Spread only.
        if (instrument.IsLegged)
        {
            ReadOnlySpan<InstrumentLeg> legs = instrument.Legs;

            // Anchor legs 1..N-1 at their own market mid (tick-aligned prices); derive leg 0 EXACTLY
            // so Σ wᵢ·legPriceᵢ reproduces the traded spread price. The derived price is often finer
            // than the leg's trading grid — that is why Fill carries a price, not ticks.
            Span<double> legPrices = stackalloc double[legs.Length];
            double residual = instrument.TicksToPrice(ticks);
            for (int i = 1; i < legs.Length; i++)
            {
                Instrument anchorLeg = ServerContext.GetInstrument(legs[i].InstrumentId);
                legPrices[i] = anchorLeg.RoundPrice(ExchangeSimulator.GetInstrument(legs[i].InstrumentId).GetQuote().MidPrice);
                residual -= legs[i].Weight * legPrices[i];
            }
            legPrices[0] = residual / legs[0].Weight;

            for (int i = 0; i < legs.Length; i++)
                emplaceFill(ref fills[1 + i], legs[i].InstrumentId, legPrices[i], quantity * legs[i].Weight);
        }
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
    private readonly HashMap<string, int> _instrumentHeaderIdBySymbol = new HashMap<string, int>();


    public void OnInstrumentDetails(InstrumentDetails instrumentDetails)
    {
        if (_instrumentDetailsBySymbol.TryAdd(instrumentDetails.Symbology.Symbol, instrumentDetails))
        {
            int instrumentHeaderId = _instrumentDetailsByInstrumentHeaderId.Count;
            _instrumentDetailsByInstrumentHeaderId.TryAdd(instrumentHeaderId, instrumentDetails);
            _instrumentHeaderIdBySymbol.TryAdd(instrumentDetails.Symbology.Symbol, instrumentHeaderId);

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
            }
            else if (instrumentDetails.InstrumentType == InstrumentType.Spread)
            {
                // Legs are the source of truth. They registered before this spread (the Symbology
                // build above already resolved them via GetLeg), so reference them by header id.
                ref LeggedHeader leggedHeader = ref Unsafe.As<InstrumentHeader128, LeggedHeader>(ref header128);
                leggedHeader.Multiplier = instrumentDetails.Multiplier;

                System.Collections.Generic.List<(Leg Leg, InstrumentDetails Details)> legDetailsList = instrumentDetails.GetLegDetails();
                if (legDetailsList.Count > 6)
                    throw new NotSupportedException($"{instrumentDetails.Symbol}: {legDetailsList.Count} legs exceeds LeggedHeader capacity of 6.");

                leggedHeader.LegCount = legDetailsList.Count;
                for (int legIndex = 0; legIndex < legDetailsList.Count; legIndex++)
                {
                    leggedHeader.Legs[legIndex] = new LegHeader
                    {
                        InstrumentHeaderId = _instrumentHeaderIdBySymbol[legDetailsList[legIndex].Details.Symbology.Symbol],
                        Weight = legDetailsList[legIndex].Leg.Weight,
                    };
                }
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
                case (byte)OrderType.AheadOfOrder:
                    ref readonly AheadOfOrder aheadOfOrder = ref MemoryMarshal.AsRef<AheadOfOrder>(src);
                    _server.OnQuantityAhead(aheadOfOrder.ClientOrderId, aheadOfOrder.Quantity);
                    break;
                case (byte)OrderType.OrderState:
                    OrderState orderState = MemoryMarshal.Read<OrderState>(src);
                    if (orderState.OrderStateReason == OrderStateReason.Fill)
                    {
                        // A Fill state carries its fills in the same entry: the order's own fill plus
                        // one per leg for a legged instrument. The entry length gives the count.
                        Span<Fill> fills = MemoryMarshal.Cast<byte, Fill>(src.Slice(Unsafe.SizeOf<OrderState>()));
                        _server.OnFill(ref orderState, fills);
                    }
                    else
                    {
                        _server.OnOrderState(ref orderState);
                    }
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
