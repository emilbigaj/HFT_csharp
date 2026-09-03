using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;

namespace Data;

// Full-depth order book keyed by PriorityId. Queue position is structural (list order), never numeric:
// PriorityId is an opaque key (MDP3 priority or Databento order_id), so Add appends at tail. A Reduce
// only shrinks quantity in place (Level.Quantity = quantity reduced by) and keeps queue position —
// the converter emits increases and price changes as Cancel + Add — so any other Reduce throws.
// Trade events do not mutate the book — feeds deliver the resting-side reduction as explicit Reduce/Cancel.
public sealed class MarketByOrderBook : IDisposable
{
    // 24 bytes: ticks live on the price level (via PriceLevelIndex); side is implied by which map owns the level
    [StructLayout(LayoutKind.Sequential)]
    private struct OrderNode
    {
        public ulong PriorityId;
        public int Quantity;
        public int Prev;    // order node index within the level list, -1 = none; Next doubles as the free-list link
        public int Next;
        public int PriceLevelIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PriceLevel
    {
        public int Ticks;
        public int Head;    // order node index, -1 = empty; doubles as the free-list link
        public int Tail;
        public int Count;
        public long Quantity;
    }

    private OrderNode[] _orderNodes;
    private int _orderNodesUsed;
    private int _freeOrderNode = -1;
    private readonly HashMap<ulong, int> _byId;

    private PriceLevel[] _priceLevels;
    private int _priceLevelsUsed;
    private int _freePriceLevel = -1;
    private readonly HashMap<int, int> _bidLevels;   // ticks -> price level index
    private readonly HashMap<int, int> _askLevels;

    /// <summary>Resting bid/ask order counts (not level counts).</summary>
    public int BidsCount { get; private set; }
    public int AsksCount { get; private set; }
    public int BidLevelsCount => _bidLevels.Count;
    public int AskLevelsCount => _askLevels.Count;

    /// <summary>Timestamps of the last applied wire message.</summary>
    public Timestamp ExchangeTimestamp;
    public Timestamp SendingTimestamp;
    public Timestamp NicTimestamp;

    /// <summary>Optional aggregation hook: (side, ticks, level total after mutation; 0 = level gone). Feed this to a MarketByPrice64.</summary>
    public Action<Side, int, long>? PriceLevelChanged;

    public MarketByOrderBook(int initialOrders = 1 << 16, int initialLevels = 1 << 12)
    {
        _orderNodes = new OrderNode[Tools.Tools.NextPowerOfTwo(initialOrders)];
        _priceLevels = new PriceLevel[Tools.Tools.NextPowerOfTwo(initialLevels)];
        _byId = new HashMap<ulong, int>(initialOrders);
        _bidLevels = new HashMap<int, int>(initialLevels);
        _askLevels = new HashMap<int, int>(initialLevels);
    }

    public void Clear()
    {
        _orderNodesUsed = 0;
        _freeOrderNode = -1;
        _priceLevelsUsed = 0;
        _freePriceLevel = -1;
        _byId.Clear();
        _bidLevels.Clear();
        _askLevels.Clear();
        BidsCount = 0;
        AsksCount = 0;
    }

    // [ MarketByOrder | bidOrders[] | askOrders[] ] wire message; Snapshot resets the book first.
    public void Apply(ReadOnlySpan<byte> message)
    {
        ref readonly MarketByOrder mbo = ref MemoryMarshal.AsRef<MarketByOrder>(message);

        TickType tickType = mbo.TickHeader.TickType;
        if (tickType == TickType.MarketByOrderSnapshot)
            Clear();
        else if (tickType != TickType.MarketByOrderUpdate)
            throw new NotSupportedException($"MarketByOrderBook.Apply does not support TickType {tickType}");

        ExchangeTimestamp = mbo.TickHeader.ExchangeTimestamp;
        SendingTimestamp = mbo.TickHeader.SendingTimestamp;
        NicTimestamp = mbo.TickHeader.NicTimestamp;

        ReadOnlySpan<Order> bids = mbo.BidsAsSpan(message);
        for (int i = 0; i < bids.Length; i++)
            Apply(Side.Buy, in bids[i]);

        ReadOnlySpan<Order> asks = mbo.AsksAsSpan(message);
        for (int i = 0; i < asks.Length; i++)
            Apply(Side.Sell, in asks[i]);
    }

    /// <summary>Single event; side = maker side (which array the order rides in). Trade is informational and ignored.</summary>
    public void Apply(Side side, in Order order)
    {
        MarketByOrderAction action = order.OrderAction;
        if (action == MarketByOrderAction.Trade)
            return;

        if (order.Side != Side.Flat && order.Side != side)
            throw new InvalidOperationException($"MarketByOrderBook.Apply side mismatch: packed {order.Side}, applied as {side}, PriorityId {order.PriorityId}");

        switch (action)
        {
            case MarketByOrderAction.Add:
                AddOrder(side, in order);
                break;
            case MarketByOrderAction.Reduce:
                ReduceOrder(side, in order);
                break;
            case MarketByOrderAction.Cancel:
                CancelOrder(side, in order);
                break;
            default:
                throw new NotSupportedException($"MarketByOrderBook.Apply unknown action {action}");
        }
    }

    private void AddOrder(Side side, in Order order)
    {
        int quantity = order.Level.Quantity;
        if (quantity <= 0)
            throw new InvalidOperationException($"MarketByOrderBook.Add non-positive quantity {quantity}, PriorityId {order.PriorityId}");

        int orderNodeIndex = AllocOrderNode();
        if (!_byId.TryAdd(order.PriorityId, orderNodeIndex))
        {
            FreeOrderNode(orderNodeIndex);
            throw new InvalidOperationException($"MarketByOrderBook.Add duplicate PriorityId {order.PriorityId}");
        }

        int priceLevelIndex = GetOrCreatePriceLevel(side, order.Level.Ticks);
        _orderNodes[orderNodeIndex] = new OrderNode
        {
            PriorityId = order.PriorityId,
            Quantity = quantity,
            Prev = -1,
            Next = -1,
            PriceLevelIndex = priceLevelIndex,
        };
        AppendToTail(priceLevelIndex, orderNodeIndex);

        if (side == Side.Buy) BidsCount++; else AsksCount++;
        PriceLevelChanged?.Invoke(side, order.Level.Ticks, _priceLevels[priceLevelIndex].Quantity);
    }

    private void ReduceOrder(Side side, in Order order)
    {
        if (!_byId.TryGetValue(order.PriorityId, out int orderNodeIndex))
            throw new InvalidOperationException($"MarketByOrderBook.Reduce unknown PriorityId {order.PriorityId}");

        int quantityReduced = order.Level.Quantity;
        if (quantityReduced <= 0)
            throw new InvalidOperationException($"MarketByOrderBook.Reduce non-positive quantity {quantityReduced}, PriorityId {order.PriorityId}");

        ref OrderNode orderNode = ref _orderNodes[orderNodeIndex];
        ref PriceLevel priceLevel = ref _priceLevels[orderNode.PriceLevelIndex];
        if (order.Level.Ticks != priceLevel.Ticks)
            throw new InvalidOperationException($"MarketByOrderBook.Reduce price change must arrive as Cancel + Add: booked {priceLevel.Ticks}, event {order.Level.Ticks}, PriorityId {order.PriorityId}");
        if (quantityReduced >= orderNode.Quantity)
            throw new InvalidOperationException($"MarketByOrderBook.Reduce must leave quantity resting: booked {orderNode.Quantity}, reduced by {quantityReduced}, PriorityId {order.PriorityId}");

        // a reduce keeps queue position
        orderNode.Quantity -= quantityReduced;
        priceLevel.Quantity -= quantityReduced;
        PriceLevelChanged?.Invoke(side, priceLevel.Ticks, priceLevel.Quantity);
    }

    private void CancelOrder(Side side, in Order order)
    {
        if (!_byId.TryRemove(order.PriorityId, out int orderNodeIndex))
            throw new InvalidOperationException($"MarketByOrderBook.Cancel unknown PriorityId {order.PriorityId}");

        ref OrderNode orderNode = ref _orderNodes[orderNodeIndex];
        ref PriceLevel priceLevel = ref _priceLevels[orderNode.PriceLevelIndex];
        if (order.Level.Ticks != 0 && order.Level.Ticks != priceLevel.Ticks)
            throw new InvalidOperationException($"MarketByOrderBook.Cancel ticks mismatch: booked {priceLevel.Ticks}, event {order.Level.Ticks}, PriorityId {order.PriorityId}");

        int ticks = priceLevel.Ticks;
        long remaining = UnlinkFromPriceLevel(side, ref orderNode);
        FreeOrderNode(orderNodeIndex);

        if (side == Side.Buy) BidsCount--; else AsksCount--;
        PriceLevelChanged?.Invoke(side, ticks, remaining);
    }

    public int SnapshotSizeOf() => MarketByOrder.SizeOf(BidsCount, AsksCount);

    /// <summary>Emit the resting book as a MarketByOrderSnapshot: bids best-to-worst, asks best-to-worst, head-to-tail within a level, original PriorityIds preserved.</summary>
    public ref MarketByOrder CopyToSnapshot(int instrumentId, Span<byte> dst)
    {
        ref MarketByOrder mbo = ref MemoryMarshal.AsRef<MarketByOrder>(dst);
        mbo.TickHeader = new TickHeader
        {
            TickType = TickType.MarketByOrderSnapshot,
            InstrumentId = instrumentId,
            ExchangeTimestamp = ExchangeTimestamp,
            SendingTimestamp = SendingTimestamp,
            NicTimestamp = NicTimestamp,
        };
        mbo.BidsCount = BidsCount;
        mbo.AsksCount = AsksCount;

        CopySide(Side.Buy, _bidLevels, mbo.BidsAsSpan(dst));
        CopySide(Side.Sell, _askLevels, mbo.AsksAsSpan(dst));
        return ref mbo;
    }

    private void CopySide(Side side, HashMap<int, int> levels, Span<Order> dst)
    {
        int written = 0;
        if (levels.Count > 0)
        {
            using ArrayList<int> ticks = levels.CopyKeys();
            ticks.Sort((a, b) => a.CompareTo(b));

            // best first: bids descend from the highest price, asks ascend from the lowest
            for (int i = 0; i < ticks.Count; i++)
            {
                int priceLevelTicks = side == Side.Buy ? ticks[ticks.Count - 1 - i] : ticks[i];
                ref PriceLevel priceLevel = ref _priceLevels[levels[priceLevelTicks]];
                for (int orderNodeIndex = priceLevel.Head; orderNodeIndex >= 0; orderNodeIndex = _orderNodes[orderNodeIndex].Next)
                {
                    ref OrderNode orderNode = ref _orderNodes[orderNodeIndex];
                    dst[written++] = new Order(orderNode.PriorityId, MarketByOrderAction.Add, side, priceLevelTicks, orderNode.Quantity);
                }
            }
        }

        if (written != dst.Length)
            throw new InvalidOperationException($"MarketByOrderBook.CopySide wrote {written} orders, expected {dst.Length} ({side})");
    }

    public int MarketByPriceSizeOf(int maxLevelsPerSide) =>
        MarketByPrice.SizeOf(Math.Min(BidLevelsCount, maxLevelsPerSide), Math.Min(AskLevelsCount, maxLevelsPerSide));

    /// <summary>Emit the aggregated book as a MarketByPriceSnapshot: best maxLevelsPerSide levels per side, best first.</summary>
    public ref MarketByPrice CopyToMarketByPriceSnapshot(int instrumentId, Span<byte> dst, int maxLevelsPerSide)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
        mbp.TickHeader = new TickHeader
        {
            TickType = TickType.MarketByPriceSnapshot,
            InstrumentId = instrumentId,
            ExchangeTimestamp = ExchangeTimestamp,
            SendingTimestamp = SendingTimestamp,
            NicTimestamp = NicTimestamp,
        };
        mbp.BidsCount = Math.Min(BidLevelsCount, maxLevelsPerSide);
        mbp.AsksCount = Math.Min(AskLevelsCount, maxLevelsPerSide);

        CopySideLevels(Side.Buy, _bidLevels, mbp.BidsAsSpan(dst));
        CopySideLevels(Side.Sell, _askLevels, mbp.AsksAsSpan(dst));
        return ref mbp;
    }

    private void CopySideLevels(Side side, HashMap<int, int> levels, Span<Level> dst)
    {
        int written = 0;
        if (levels.Count > 0)
        {
            using ArrayList<int> ticks = levels.CopyKeys();
            ticks.Sort((a, b) => a.CompareTo(b));

            // best first: bids descend from the highest price, asks ascend from the lowest
            for (int i = 0; i < ticks.Count && written < dst.Length; i++)
            {
                int priceLevelTicks = side == Side.Buy ? ticks[ticks.Count - 1 - i] : ticks[i];
                ref PriceLevel priceLevel = ref _priceLevels[levels[priceLevelTicks]];
                dst[written++] = new Level(priceLevelTicks, (int)priceLevel.Quantity);
            }
        }

        if (written != dst.Length)
            throw new InvalidOperationException($"MarketByOrderBook.CopySideLevels wrote {written} levels, expected {dst.Length} ({side})");
    }

    public override string ToString() =>
        $"MarketByOrderBook Bids: {BidsCount} orders / {BidLevelsCount} levels, Asks: {AsksCount} orders / {AskLevelsCount} levels";

    // ── internals ────────────────────────────────────────────────────────

    private HashMap<int, int> GetLevels(Side side) => side switch
    {
        Side.Buy => _bidLevels,
        Side.Sell => _askLevels,
        _ => throw new ArgumentException($"MarketByOrderBook invalid side {side}"),
    };

    private int GetOrCreatePriceLevel(Side side, int ticks)
    {
        ref int slot = ref GetLevels(side).GetOrAddRef(ticks, out bool found);
        if (found)
            return slot;

        int priceLevelIndex = AllocPriceLevel();   // touches _priceLevels only; the map slot ref stays valid
        _priceLevels[priceLevelIndex] = new PriceLevel { Ticks = ticks, Head = -1, Tail = -1, Count = 0, Quantity = 0 };
        slot = priceLevelIndex;
        return priceLevelIndex;
    }

    private void AppendToTail(int priceLevelIndex, int orderNodeIndex)
    {
        ref PriceLevel priceLevel = ref _priceLevels[priceLevelIndex];
        ref OrderNode orderNode = ref _orderNodes[orderNodeIndex];
        orderNode.Prev = priceLevel.Tail;
        orderNode.Next = -1;
        if (priceLevel.Tail >= 0) _orderNodes[priceLevel.Tail].Next = orderNodeIndex;
        else priceLevel.Head = orderNodeIndex;
        priceLevel.Tail = orderNodeIndex;
        priceLevel.Count++;
        priceLevel.Quantity += orderNode.Quantity;
    }

    // Returns the level's remaining quantity (0 when the level empties and is freed).
    // Side comes from the event; a wrong side throws here (map mismatch) or at the next snapshot (count drift).
    private long UnlinkFromPriceLevel(Side side, ref OrderNode orderNode)
    {
        ref PriceLevel priceLevel = ref _priceLevels[orderNode.PriceLevelIndex];
        if (orderNode.Prev >= 0) _orderNodes[orderNode.Prev].Next = orderNode.Next;
        else priceLevel.Head = orderNode.Next;
        if (orderNode.Next >= 0) _orderNodes[orderNode.Next].Prev = orderNode.Prev;
        else priceLevel.Tail = orderNode.Prev;

        priceLevel.Count--;
        priceLevel.Quantity -= orderNode.Quantity;

        if (priceLevel.Count == 0)
        {
            if (!GetLevels(side).TryRemove(priceLevel.Ticks, out int removedIndex) || removedIndex != orderNode.PriceLevelIndex)
                throw new InvalidOperationException($"MarketByOrderBook corrupt: {side} does not own level {priceLevel.Ticks}, PriorityId {orderNode.PriorityId}");
            FreePriceLevel(orderNode.PriceLevelIndex);
            return 0;
        }
        return priceLevel.Quantity;
    }

    private int AllocOrderNode()
    {
        if (_freeOrderNode >= 0)
        {
            int index = _freeOrderNode;
            _freeOrderNode = _orderNodes[index].Next;
            return index;
        }
        if (_orderNodesUsed == _orderNodes.Length)
            Array.Resize(ref _orderNodes, _orderNodes.Length << 1);
        return _orderNodesUsed++;
    }

    private void FreeOrderNode(int index)
    {
        _orderNodes[index].Next = _freeOrderNode;
        _freeOrderNode = index;
    }

    private int AllocPriceLevel()
    {
        if (_freePriceLevel >= 0)
        {
            int index = _freePriceLevel;
            _freePriceLevel = _priceLevels[index].Head;
            return index;
        }
        if (_priceLevelsUsed == _priceLevels.Length)
            Array.Resize(ref _priceLevels, _priceLevels.Length << 1);
        return _priceLevelsUsed++;
    }

    private void FreePriceLevel(int index)
    {
        _priceLevels[index].Head = _freePriceLevel;
        _priceLevels[index].Count = -1;
        _freePriceLevel = index;
    }

    public void Dispose()
    {
        _byId.Dispose();
        _bidLevels.Dispose();
        _askLevels.Dispose();
    }
}

// Per-side stateless MBO -> MBP aggregation: ticks -> total quantity, the side baked in at construction
// so the hot path never branches on it. The contract makes every event a pure level delta:
// Add(+quantity), Reduce(-quantity reduced by), Cancel(-full resting quantity); increases and price
// changes arrive as Cancel + Add, and Trade events do not mutate — feeds deliver the resting-side
// reduction as explicit Reduce/Cancel.
public sealed class SideByPriceByOrder : IDisposable
{
    public readonly Side Side;
    private readonly HashMap<int, int> _levels;   // ticks -> total quantity

    /// <summary>Level total after each mutation (Quantity 0 = level gone). Feed this to a changed-levels collector.</summary>
    public Action<Level>? Changed;

    /// <summary>Resting order count (not level count).</summary>
    public int OrdersCount { get; private set; }
    public int LevelsCount => _levels.Count;

    public SideByPriceByOrder(Side side, int initialLevels = 1 << 12)
    {
        Side = side;
        _levels = new HashMap<int, int>(initialLevels);
    }

    public void Clear()
    {
        _levels.Clear();
        OrdersCount = 0;
    }

    /// <summary>Single event; Trade is informational and ignored.</summary>
    public void Apply(in Order order)
    {
        MarketByOrderAction action = order.OrderAction;
        if (action == MarketByOrderAction.Trade)
            return;

        if (order.Side != Side.Flat && order.Side != Side)
            throw new InvalidOperationException($"SideByPriceByOrder.Apply side mismatch: packed {order.Side}, applied as {Side}, PriorityId {order.PriorityId}");

        switch (action)
        {
            case MarketByOrderAction.Add:
                AddOrder(in order);
                break;
            case MarketByOrderAction.Reduce:
                ReduceOrder(in order);
                break;
            case MarketByOrderAction.Cancel:
                CancelOrder(in order);
                break;
            default:
                throw new NotSupportedException($"SideByPriceByOrder.Apply unknown action {action}");
        }
    }

    private void AddOrder(in Order order)
    {
        int quantity = order.Level.Quantity;
        if (quantity <= 0)
            throw new InvalidOperationException($"SideByPriceByOrder.Add non-positive quantity {quantity}, PriorityId {order.PriorityId}");

        ref int total = ref _levels.GetOrAddRef(order.Level.Ticks, out _);   // a new slot starts at 0
        total += quantity;

        OrdersCount++;
        Changed?.Invoke(new Level(order.Level.Ticks, total));
    }

    private void ReduceOrder(in Order order)
    {
        int quantityReduced = order.Level.Quantity;
        if (quantityReduced <= 0)
            throw new InvalidOperationException($"SideByPriceByOrder.Reduce non-positive quantity {quantityReduced}, PriorityId {order.PriorityId}");

        ref int total = ref _levels.TryGetValueRef(order.Level.Ticks, out bool found);
        if (!found)
            throw new InvalidOperationException($"SideByPriceByOrder corrupt: {Side} does not own level {order.Level.Ticks}, PriorityId {order.PriorityId}");

        total -= quantityReduced;
        if (total <= 0)
            throw new InvalidOperationException($"SideByPriceByOrder corrupt: level {order.Level.Ticks} emptied by Reduce, PriorityId {order.PriorityId}");
        Changed?.Invoke(new Level(order.Level.Ticks, total));
    }

    private void CancelOrder(in Order order)
    {
        int quantity = order.Level.Quantity;
        if (quantity <= 0)
            throw new InvalidOperationException($"SideByPriceByOrder.Cancel non-positive quantity {quantity}, PriorityId {order.PriorityId}");

        ref int total = ref _levels.TryGetValueRef(order.Level.Ticks, out bool found);
        if (!found)
            throw new InvalidOperationException($"SideByPriceByOrder corrupt: {Side} does not own level {order.Level.Ticks}, PriorityId {order.PriorityId}");

        total -= quantity;
        int remaining = total;
        if (total < 0)
            throw new InvalidOperationException($"SideByPriceByOrder corrupt: level {order.Level.Ticks} went negative, PriorityId {order.PriorityId}");
        if (total == 0)
            _levels.TryRemove(order.Level.Ticks, out _);

        OrdersCount--;
        Changed?.Invoke(new Level(order.Level.Ticks, remaining));
    }

    /// <summary>Level total at ticks, full depth (0 when the level does not exist).</summary>
    public int GetQuantity(int ticks) => _levels.TryGetValue(ticks, out int total) ? total : 0;

    /// <summary>Copy the best dst.Length levels, best first: bids descend from the highest price, asks ascend from the lowest.</summary>
    public void CopyToLevels(Span<Level> dst)
    {
        int written = 0;
        if (_levels.Count > 0)
        {
            using ArrayList<int> ticks = _levels.CopyKeys();
            ticks.Sort((a, b) => a.CompareTo(b));

            for (int i = 0; i < ticks.Count && written < dst.Length; i++)
            {
                int priceLevelTicks = Side == Side.Buy ? ticks[ticks.Count - 1 - i] : ticks[i];
                dst[written++] = new Level(priceLevelTicks, _levels[priceLevelTicks]);
            }
        }

        if (written != dst.Length)
            throw new InvalidOperationException($"SideByPriceByOrder.CopyToLevels wrote {written} levels, expected {dst.Length} ({Side})");
    }

    public override string ToString() => $"SideByPriceByOrder {Side}: {OrdersCount} orders / {LevelsCount} levels";

    public void Dispose()
    {
        _levels.Dispose();
    }
}

// Stateless MBO -> MBP aggregation composed of two sides, mirroring MarketByPrice64 { Bids, Asks }.
public sealed class MarketByPriceByOrder : IDisposable
{
    public readonly SideByPriceByOrder Bids;
    public readonly SideByPriceByOrder Asks;

    /// <summary>Timestamps of the last applied wire message.</summary>
    public Timestamp ExchangeTimestamp;
    public Timestamp SendingTimestamp;
    public Timestamp NicTimestamp;

    public MarketByPriceByOrder(int initialLevels = 1 << 12)
    {
        Bids = new SideByPriceByOrder(Side.Buy, initialLevels);
        Asks = new SideByPriceByOrder(Side.Sell, initialLevels);
    }

    public SideByPriceByOrder GetSide(Side side) => side == Side.Buy ? Bids : Asks;

    public void Clear()
    {
        Bids.Clear();
        Asks.Clear();
    }

    // [ MarketByOrder | bidOrders[] | askOrders[] ] wire message; Snapshot resets the aggregation first.
    public void Apply(ReadOnlySpan<byte> message)
    {
        ref readonly MarketByOrder mbo = ref MemoryMarshal.AsRef<MarketByOrder>(message);

        TickType tickType = mbo.TickHeader.TickType;
        if (tickType == TickType.MarketByOrderSnapshot)
            Clear();
        else if (tickType != TickType.MarketByOrderUpdate)
            throw new NotSupportedException($"MarketByPriceByOrder.Apply does not support TickType {tickType}");

        ExchangeTimestamp = mbo.TickHeader.ExchangeTimestamp;
        SendingTimestamp = mbo.TickHeader.SendingTimestamp;
        NicTimestamp = mbo.TickHeader.NicTimestamp;

        // the side resolves once per array; the loops are branch-free
        ReadOnlySpan<Order> bids = mbo.BidsAsSpan(message);
        for (int i = 0; i < bids.Length; i++)
            Bids.Apply(in bids[i]);

        ReadOnlySpan<Order> asks = mbo.AsksAsSpan(message);
        for (int i = 0; i < asks.Length; i++)
            Asks.Apply(in asks[i]);
    }

    /// <summary>Single event; side = maker side (which array the order rides in).</summary>
    public void Apply(Side side, in Order order) => GetSide(side).Apply(in order);

    /// <summary>Apply a MarketByOrderSnapshot without firing Changed hooks — Clear() does not report removals, so consumers resync from the derived snapshot instead.</summary>
    public void ApplySnapshot(ReadOnlySpan<byte> message)
    {
        Action<Level>? bidChanged = Bids.Changed;
        Action<Level>? askChanged = Asks.Changed;
        Bids.Changed = null;
        Asks.Changed = null;
        Apply(message);
        Bids.Changed = bidChanged;
        Asks.Changed = askChanged;
    }

    public int MarketByPriceSizeOf(int maxLevelsPerSide) =>
        MarketByPrice.SizeOf(Math.Min(Bids.LevelsCount, maxLevelsPerSide), Math.Min(Asks.LevelsCount, maxLevelsPerSide));

    /// <summary>Emit the aggregation as a MarketByPriceSnapshot: best maxLevelsPerSide levels per side, best first.</summary>
    public ref MarketByPrice CopyToMarketByPriceSnapshot(int instrumentId, Span<byte> dst, int maxLevelsPerSide)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
        mbp.TickHeader = new TickHeader
        {
            TickType = TickType.MarketByPriceSnapshot,
            InstrumentId = instrumentId,
            ExchangeTimestamp = ExchangeTimestamp,
            SendingTimestamp = SendingTimestamp,
            NicTimestamp = NicTimestamp,
        };
        mbp.BidsCount = Math.Min(Bids.LevelsCount, maxLevelsPerSide);
        mbp.AsksCount = Math.Min(Asks.LevelsCount, maxLevelsPerSide);

        Bids.CopyToLevels(mbp.BidsAsSpan(dst));
        Asks.CopyToLevels(mbp.AsksAsSpan(dst));
        return ref mbp;
    }

    public override string ToString() =>
        $"MarketByPriceByOrder Bids: {Bids.OrdersCount} orders / {Bids.LevelsCount} levels, Asks: {Asks.OrdersCount} orders / {Asks.LevelsCount} levels";

    public void Dispose()
    {
        Bids.Dispose();
        Asks.Dispose();
    }
}
