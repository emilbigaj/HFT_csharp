using Data;
using System.Runtime.CompilerServices;
using Tools;
using Execution;
using System;

namespace Simulator;

public struct SimOrder(ulong orderId, int quantity) : IEquatable<SimOrder>
{
    public bool IsUserOrder
    {
        get
        {
            return OrderId > 0;
        }
    }
    public ulong OrderId = orderId;
    public int Quantity { get; private set; } = quantity;

    public void AmendTo(int quantity)
    {
        Quantity = quantity;
        if (Quantity < 0)
            throw new ArgumentOutOfRangeException();
    }

    public bool Equals(SimOrder other)
    {
        return other.OrderId == OrderId;
    }

    public override string ToString()
    {
        return $"[{(IsUserOrder ? "UserOrder" : "MarketOrder")}] OrderId: {OrderId}, Quantity: {Quantity}";
    }
}


public class QueueManager
{
    // this is the current total quantity of user orders
    public bool IsMarkedForRemoval { get; set; } = false;
    public int UserQuantity { get; private set; }
    public int MarketQuantity { get; private set; }
    public int Ticks { get; internal set; }
    protected internal NodeList<SimOrder> Orders { get; } = new NodeList<SimOrder>();
    
    // when a user order is infront of market order it prevents the market order from getting filled.
    // this means that the market quantity is now Ghost quantity larger than what the historical data shows
    // we keep this here until the orderbook turns over 100%
    public int Ghost { get; private set; }

    private ref readonly SideByPrice64 SideByPrice => ref _orderManager.SideByPrice64;
    public int Sign;

    //
    public int _traded = 0;
    private OrderManager _orderManager;
    public InstrumentSimulator InstrumentSimulator => _orderManager.InstrumentSimulator;
    public ServerSimulator ServerSimlator => InstrumentSimulator.ExchangeSimulator.ServerSimulator;
    public QueueManager(int ticks, OrderManager orderManager)
    {
        Ticks = ticks;
        Sign = (int)orderManager.Side;
        _orderManager = orderManager;
    }

    public void ReduceUserOrderTo(ulong orderId, int quantity)
    {
        foreach(ref NodeList<SimOrder>.Node node in Orders.Nodes)
        {
            ref SimOrder order = ref node.Item;
            if (order.OrderId == orderId)
            {
                if (order.Quantity < quantity)
                    throw new ArgumentOutOfRangeException();
                UserQuantity += quantity - order.Quantity;
                order.AmendTo(quantity);
                if (order.Quantity == 0)
                {
                    Orders.Remove(in node);
                }
                return;
            }
        }
        throw new ArgumentOutOfRangeException();
    }
    public void DeleteUserOrder(ulong orderId)
    {
        foreach (ref NodeList<SimOrder>.Node node in Orders.Nodes)
        {
            if (node.Item.OrderId == orderId)
            {
                UserQuantity -= node.Item.Quantity;
                Orders.Remove(in node);
                return;
            }
        }
        throw new ArgumentOutOfRangeException();
    }

    public int EnqueueUserOrder(ulong orderId, int quantity)
    {

        IsMarkedForRemoval = false;
        UserQuantity += quantity;
        int quantityAhead = 0;
        foreach (ref NodeList<SimOrder>.Node node in Orders.Nodes)
        {
            ref SimOrder order = ref node.Item;
            quantityAhead += order.Quantity;
        }
        Orders.AddLast(new SimOrder(orderId, quantity));
        return quantityAhead;
    }

    public void OnTrade(bool isHistoricalTrade, in Trade trade, ref int marketQuantityFilled, ref int userQuantityFilled)
    {
        bool isAddGhost = trade.Level.Ticks == Ticks && isHistoricalTrade;
        int totalQuantityFilled = marketQuantityFilled + userQuantityFilled;
        int quantityUnfilled = trade.Level.Quantity - totalQuantityFilled;

        StackList<(ulong OrderId, int Quantity)> makerFills = new StackList<(ulong OrderId, int Quantity)>(stackalloc (ulong OrderId, int Quantity)[Orders.Count]);
        ref SimOrder order = ref Orders.FirstRef;
        while(quantityUnfilled > 0)
        {
            if (Unsafe.IsNullRef(in order))
                break;

            int fill = Math.Min(order.Quantity, quantityUnfilled);
            order.AmendTo(order.Quantity - fill);
            
            quantityUnfilled -= fill;
            if (isHistoricalTrade || order.IsUserOrder)
                ServerSimlator.FromExchangeToNicToClient_Trade(new Trade(trade.TickHeader.InstrumentId, trade.TickHeader.ExchangeTimestamp, trade.TickHeader.SendingTimestamp, trade.TickHeader.NicTimestamp, Ticks, fill, trade.Direction));
            
            if (order.IsUserOrder)
            {
                UserQuantity -= fill;
                userQuantityFilled += fill;
                makerFills.Add((order.OrderId, fill * Sign));
            }
            else
            {
                MarketQuantity -= fill;
                marketQuantityFilled += fill;
                if (!isAddGhost)
                    ReduceGhost(fill);
                if (!isHistoricalTrade)
                    _orderManager.CrossMask += fill;
            }

            if (order.Quantity == 0)
                Orders.TryDequeue(out _);
            order = ref Orders.FirstRef;
        }
        if (isAddGhost)
        {
            _traded += trade.Level.Quantity;
            AddGhost(); // this can be done at all times now and that is much safer
        }
        foreach ((ulong orderId, int quantity) in makerFills)
        {
            InstrumentSimulator.Make(orderId, Ticks, quantity);
        }
        PublishQuantityAhead();
    }

    private int _totalMarketQuantity = 0;
    private double _inverseTotalMarketQuantity = 0;

    private void AddGhost()
    {
        int newGhost = Math.Max(_traded + MarketQuantity - SideByPrice.GetQuantity(Ticks), 0);
        int ghostAdded = newGhost - Ghost;

        if (ghostAdded <= 0)
            return;

        int totalMarketQuantity = 0;
        foreach (Level level in SideByPrice)
            totalMarketQuantity += level.Quantity;

        _totalMarketQuantity = (int)((Ghost * _totalMarketQuantity + ghostAdded * totalMarketQuantity) * double.ReciprocalEstimate(Ghost + ghostAdded))+1;
        _inverseTotalMarketQuantity = double.ReciprocalEstimate(_totalMarketQuantity); //acceptable accuracy
        Ghost = newGhost;
    }

    private void ReduceGhost(int quantity)
    {
        Ghost = Math.Max(0, Ghost - quantity);
    }

    private double _accumulatedGhostDecay = 0;
    public void DecayGhost(int quantity)
    {
        if (Ghost <= 0)
            return;

        double ghostReduceExact = Ghost * (Math.Abs(quantity) * _inverseTotalMarketQuantity);
        _accumulatedGhostDecay += ghostReduceExact;
        int ghostReduce = Math.Min((int)_accumulatedGhostDecay, Ghost);
        _accumulatedGhostDecay -= ghostReduce; // this is the remainder that will be cancelled next time
        Ghost = Math.Max(Ghost - ghostReduce, 0);
        _orderManager.CrossMask = Math.Max(_orderManager.CrossMask - ghostReduce, 0);
        ReduceMarketBy(ghostReduce);
    }

    public void OnMarketByPriceDelta(int delta)
    {
        delta += _traded;
        _traded = 0;

        if (delta > 0)
        {
            EnqueueMarket(delta);
        }
        else if (delta < 0)
        {
            ReduceMarketBy(-delta);
        }
    }

    private void EnqueueMarket(int quantity)
    {
        ref SimOrder tail = ref Orders.LastRef;
        if (Unsafe.IsNullRef(in tail) || tail.IsUserOrder)
        {
            Orders.AddLast(new SimOrder(0, quantity));
        }
        else
        {
            tail.AmendTo(tail.Quantity + quantity);
        }
        MarketQuantity += quantity;
    }
    private void ReduceMarketBy(int quantity)
    {
        int quantityCopy = quantity;
        bool underflow = false;
        while (quantity > 0)
        {
            if (MarketQuantity == 0)
                return;
            else if (MarketQuantity < 0)
                throw new Exception();

            double inverseMarketQuantity = double.ReciprocalEstimate(MarketQuantity);

            int marketAhead = 0;
            int accumulatedCancelled = 0;
            int quantityLeft = quantity - accumulatedCancelled;

            foreach (ref NodeList<SimOrder>.Node node in Orders.Nodes)
            {
                ref SimOrder order = ref node.Item;
                if (!order.IsUserOrder)
                {
                    marketAhead += order.Quantity + (underflow ? 1 : 0);
                    double evenCancelRate = Math.Min(marketAhead * inverseMarketQuantity,1);
                    double behindHeavyCancelRate = evenCancelRate * evenCancelRate * evenCancelRate;
                    double accumulatedCancelExact = quantity * behindHeavyCancelRate;
                    int accumulatedCancel = (int)accumulatedCancelExact;
                    underflow = accumulatedCancel < accumulatedCancelExact;
                    int incrementalCancel = Math.Min(order.Quantity, accumulatedCancel - accumulatedCancelled);
                    if (incrementalCancel < 0)
                        throw new InvalidOperationException("If this is reached there is a bug");
                    accumulatedCancelled += incrementalCancel;
                    if (accumulatedCancelled > quantity)
                        throw new InvalidOperationException("If this is reached there is a bug");
                    order.AmendTo(order.Quantity - incrementalCancel);
                    MarketQuantity -= incrementalCancel;
                    if (order.Quantity == 0)
                        Orders.Remove(in node);
                    quantityLeft = quantity - accumulatedCancelled;
                    if (quantityLeft == 0)
                        break;
                }
            }
            if (accumulatedCancelled > quantityCopy)
            {
                throw new InvalidOperationException("If this is reached there is a bug");
            }
            quantity = quantityLeft;
        }
        PublishQuantityAhead();
    }

    private void PublishQuantityAhead()
    {
        int quantityAhead = 0;
        foreach (ref NodeList<SimOrder>.Node node in Orders.Nodes)
        {
            if (node.Item.Quantity == 0)
                throw new ArgumentOutOfRangeException();

            ref SimOrder order = ref node.Item;
            if (order.IsUserOrder)
                InstrumentSimulator.ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_AheadOfOrder(new AheadOfOrder(order.OrderId, quantityAhead));
            quantityAhead += order.Quantity;
        }
    }

    public override string ToString() => $"[QueueManager] {_orderManager.InstrumentSimulator.InstrumentDetails.Symbol} Ticks: {Ticks}, Orders: {Orders.Count}, Ghost: {Ghost}, Traded: {_traded}, MarketQuantity: {MarketQuantity}, UserQuantity: {UserQuantity}";

    public void Clear()
    {
        Orders.Clear();
        IsMarkedForRemoval = false;
        Ghost = 0;
        _accumulatedGhostDecay = 0;
        _traded = 0;
        _totalMarketQuantity = 0;
        _inverseTotalMarketQuantity = 1;
        UserQuantity = 0;
        MarketQuantity = 0;
    }
}


public class OrderManager
{
    private readonly ArrayList<QueueManager> _pool = new ArrayList<QueueManager>(16);
    private QueueManager RentQueueManager(int ticks)
    {
        if (_pool.Count > 0)
        {
            int end = _pool.Count - 1;
            QueueManager queueManager = _pool[end];
            _pool.RemoveAt(end);
            queueManager.Ticks = ticks;
            return queueManager;
        }
        else
        {
            QueueManager queueManager = new QueueManager(ticks, this);
            return queueManager;
        }
    }
    private void ReturnQueueManager(QueueManager queueManager)
    {
        queueManager.Clear();
        _pool.Add(queueManager);
    }

    public bool IsSubscribed { get; protected set; } = false;

    public readonly int Sign;
    public readonly Side Side;
    public int CrossMask { get; set;  } = 0;

    // Custom LinkedList class from Tools for better performance, avoids allocations, avoids pointer chasing, and allows delete during iteration
    protected internal NodeList<QueueManager> QueueManagers { get; } = new NodeList<QueueManager>();


    public ArrayList<int> TicksRemoved = new ArrayList<int>();

    internal ref readonly SideByPrice64 SideByPrice64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Access the ref MarketByPrice64 on the parent, then return the specific side
            if (Side == Side.Buy)
                return ref InstrumentSimulator.MarketByPrice64.Bids;

            return ref InstrumentSimulator.MarketByPrice64.Asks;
        }
    }
    public InstrumentSimulator InstrumentSimulator { get; }

    public OrderManager(InstrumentSimulator instrumentExecutionSimulator, Side side)
    {
        InstrumentSimulator = instrumentExecutionSimulator;
        Sign = (int)side;
        Side = side;
    }

    private bool IsFill(in Level aggressor, int ticks, int quantityFilled)
    {
        bool isQuantityUnfilled = aggressor.Quantity - quantityFilled > 0;

        bool isHitOrTake = Sign * (aggressor.Ticks - ticks) <= 0;

        return isQuantityUnfilled && isHitOrTake;
    }
    private int GetQuantity(int ticks)
    {
        return SideByPrice64.GetQuantity(ticks);
    }

    private bool TryGetWorstTicks(out int ticks)
    {
        if (SideByPrice64.Count > 0)
        {
            ticks = SideByPrice64.WorstTicks;
            return true;
        }
        ticks = 0;
        return false;
    }

    public void Clear()
    {
        CrossMask = 0;
        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
        {
            RemoveQueueManager(in node, true);
        }
    }

    protected void RemoveQueueManager(in NodeList<QueueManager>.Node node, bool forceRemove)
    {
        QueueManager queueManager = node.Item;
        bool remove = forceRemove || (queueManager.UserQuantity == 0 && queueManager.Ghost == 0);

        if ((forceRemove || queueManager.UserQuantity == 0) && !queueManager.IsMarkedForRemoval)
        {
            TicksRemoved.Add(node.Item.Ticks);
            queueManager.IsMarkedForRemoval = true;
        }

        if (remove)
        {
            QueueManagers.Remove(in node);
            ReturnQueueManager(queueManager);
        }
    }

    // Get or insert in sorted order (most aggressive first)
    private QueueManager GetOrAddQueueManager(int ticks)
    {
        int sidedTicks = Sign * ticks;

        ref NodeList<QueueManager>.Node insertBefore = ref Unsafe.NullRef<NodeList<QueueManager>.Node>();

        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
        {
            QueueManager existing = node.Item;

            if (ticks == existing.Ticks)
                return existing;

            int existingSidedTicks = existing.Ticks * Sign;

            // If new is more aggressive than this existing level, we insert BEFORE this node.
            if (sidedTicks > existingSidedTicks)
            {
                insertBefore = ref node;
                break;
            }
        }

        QueueManager queueManager = RentQueueManager(ticks);
        int quantity = GetQuantity(ticks);
        if (quantity == 0 && TryGetWorstTicks(out int worstTicks)) // pretend the 
        {
            if ((worstTicks - ticks) * Sign > 0)
              quantity = GetQuantity(worstTicks);
        }
        
        queueManager.OnMarketByPriceDelta(quantity);

        if (Unsafe.IsNullRef(in insertBefore)) // No more-aggressive position found: append at end
            QueueManagers.AddLast(queueManager);
        else // Insert before the first less-aggressive node
            QueueManagers.EmplaceBefore(in insertBefore) = queueManager;

        return queueManager;
    }

    private ref NodeList<QueueManager>.Node TryGetQueueManager(int ticks, out bool found)
    {
        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
        {
            if (ticks == node.Item.Ticks)
            {
                found = true;
                return ref node;
            }
        }
        found = false;
        return ref Unsafe.NullRef<NodeList<QueueManager>.Node>();
    }



    private ref NodeList<QueueManager>.Node GetQueueManager(int ticks)
    {
        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
            if (node.Item.Ticks == ticks)
                return ref node;
        throw new ArgumentOutOfRangeException();
    }

    public int Enqeue(ulong orderId, int ticks, int quantity)
    {
        quantity = Math.Abs(quantity);
        QueueManager queueManager = GetOrAddQueueManager(ticks);
        int quantityAhead = queueManager.EnqueueUserOrder(orderId, quantity);
        return quantityAhead;
    }
    public void Delete(ulong orderId, int ticks)
    {
        ref NodeList<QueueManager>.Node node = ref GetQueueManager(ticks);
        QueueManager queueManager = node.Item;
        queueManager.DeleteUserOrder(orderId);
        RemoveQueueManager(in node, false);
    }
    public void Reduce(ulong orderId, int ticks, int quantity)
    {
        quantity = Math.Abs(quantity);
        ref NodeList<QueueManager>.Node node = ref GetQueueManager(ticks); // Need the node ref
        QueueManager queueManager = node.Item;
        queueManager.ReduceUserOrderTo(orderId, quantity);

        RemoveQueueManager(in node, false); // if reduce to 0?

    }

    private void OnTrade(in NodeList<QueueManager>.Node node, bool isHistoricalTrade, in Trade trade, ref int marketQuantityFilled, ref int userQuantityFilled)
    {
        QueueManager queueManager = node.Item;
        node.Item.OnTrade(isHistoricalTrade, trade, ref marketQuantityFilled, ref userQuantityFilled);
        RemoveQueueManager(in node, false);
    }


    public int OnTrade(bool isHistoricalTrade, Trade trade)
    {
        int marketQuantityFilled = 0;
        int userQuantityFilled = 0;
        // Fill all orders infront of trade price
        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
        {
            QueueManager queueManager = node.Item;
            if (IsFill(in trade.Level, queueManager.Ticks, marketQuantityFilled + userQuantityFilled) && queueManager.Ticks != trade.Level.Ticks)
                OnTrade(node, isHistoricalTrade, trade, ref marketQuantityFilled, ref userQuantityFilled);
            else
                break;
        }

        // fill orders at trade price
        if (marketQuantityFilled + userQuantityFilled > 0)
        {
            ref NodeList<QueueManager>.Node node = ref TryGetQueueManager(trade.Level.Ticks, out bool found);
            if (found)
            {
                OnTrade(node, isHistoricalTrade, trade, ref marketQuantityFilled, ref userQuantityFilled);
            }
            else if (isHistoricalTrade)
            {
                QueueManager queueManagerAtTradePrice = GetOrAddQueueManager(trade.Level.Ticks);
                queueManagerAtTradePrice.OnTrade(isHistoricalTrade, trade, ref marketQuantityFilled, ref userQuantityFilled);
                // dont need to check for remove here because Ghost != 0, since isMarketTrade is true and tradeIsAtQueuePrice
            }
        }
        else
        {
            ref NodeList<QueueManager>.Node node = ref TryGetQueueManager(trade.Level.Ticks, out bool found);
            if (found)
                OnTrade(node, isHistoricalTrade, trade, ref marketQuantityFilled, ref userQuantityFilled);
        }
        int quantityUnfilled = trade.Level.Quantity - marketQuantityFilled - userQuantityFilled;
        if (isHistoricalTrade && quantityUnfilled > 0)
        {
            InstrumentSimulator.ExchangeSimulator.ServerSimulator.FromExchangeToNicToClient_Trade(new Trade(trade.TickHeader.InstrumentId, trade.TickHeader.ExchangeTimestamp, trade.TickHeader.SendingTimestamp, trade.TickHeader.NicTimestamp, trade.Level.Ticks, trade.Level.Quantity - marketQuantityFilled, trade.Direction));
        }
        return userQuantityFilled;
    }

    public void OnMarketByPriceDelta(int ticks, int quantity)
    {
        foreach (ref NodeList<QueueManager>.Node node in QueueManagers.Nodes)
        {
            QueueManager queueManager = node.Item;
            if (queueManager.Ticks == ticks)
                queueManager.OnMarketByPriceDelta(quantity);
            if (queueManager.Ghost > 0)
                queueManager.DecayGhost(quantity);
            RemoveQueueManager(in node, false);
        }
    }

    public override string ToString() => $"[OrderManager] {InstrumentSimulator.InstrumentDetails.Symbol} Side: {Side}, Queues: {QueueManagers.Count}";
}

public static class Extensions
{
    public static string ToDebugString(this OrderManager orderManager)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(orderManager.ToString());

        foreach (ref NodeList<QueueManager>.Node node in orderManager.QueueManagers.Nodes)
        {
            QueueManager queue = node.Item;
            sb.AppendLine($"   └── {queue}");

            foreach (ref NodeList<SimOrder>.Node orderNode in queue.Orders.Nodes)
            {
                ref SimOrder order = ref orderNode.Item;
                sb.AppendLine($"         └── {order}");

                /*
                if (order is SimOrder sim)
                {
                    foreach (var fill in sim.Fills)
                        sb.AppendLine($"               └── {fill}");
                }
                */
            }
        }

        return sb.ToString();
    }

}