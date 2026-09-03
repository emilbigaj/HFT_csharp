using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Data;

// Fuzzes MarketByOrderBook against a naive reference model (Dictionary + LINQ sort),
// then round-trips a snapshot through the wire Apply path and compares bytes.
public class MarketByOrderBookTest
{
    private sealed class RefOrder
    {
        public ulong Id;
        public Side Side;
        public int Ticks;
        public int Quantity;
        public long Seq;    // queue position: (ticks best-first, Seq ascending)
    }

    private readonly Random _random = new Random(1);
    private readonly Dictionary<ulong, RefOrder> _reference = new Dictionary<ulong, RefOrder>();
    private readonly List<ulong> _liveIds = new List<ulong>();
    private readonly Dictionary<ulong, int> _liveIndex = new Dictionary<ulong, int>();
    private ulong _nextId = 1;
    private long _seq;

    public void Run(int iterations = 100_000, int verifyEvery = 1_000)
    {
        using MarketByOrderBook book = new MarketByOrderBook(initialOrders: 16, initialLevels: 4); // tiny: force pool growth
        using MarketByPriceByOrder mbpByOrder = new MarketByPriceByOrder(initialLevels: 4);
        for (int i = 0; i < iterations; i++)
        {
            Step(book, mbpByOrder);
            if (i % verifyEvery == 0)
                Verify(book, mbpByOrder);
        }
        Verify(book, mbpByOrder);
        VerifyWireRoundTrip(book);
        Console.WriteLine($"MarketByOrderBookTest passed: {iterations} ops, final {book}");
    }

    // every event goes to the book and to the queue-free aggregation in lockstep
    private static void Apply(MarketByOrderBook book, MarketByPriceByOrder mbpByOrder, Side side, in Order order)
    {
        book.Apply(side, in order);
        mbpByOrder.Apply(side, in order);
    }

    private void Step(MarketByOrderBook book, MarketByPriceByOrder mbpByOrder)
    {
        double roll = _random.NextDouble();

        if (_liveIds.Count < 50 || roll < 0.45)
        {
            // Add
            ulong id = _nextId++;
            Side side = _random.NextDouble() < 0.5 ? Side.Buy : Side.Sell;
            int ticks = _random.Next(1000, 1101);
            int quantity = _random.Next(1, 1000);
            Apply(book, mbpByOrder, side, new Order(id, MarketByOrderAction.Add, side, ticks, quantity));
            _reference[id] = new RefOrder { Id = id, Side = side, Ticks = ticks, Quantity = quantity, Seq = ++_seq };
            _liveIndex[id] = _liveIds.Count;
            _liveIds.Add(id);
        }
        else if (roll < 0.70)
        {
            // Reduce shrinks in place and keeps position; increases and price changes arrive as Cancel + Add
            RefOrder reference = _reference[PickLiveId()];
            double kind = _random.NextDouble();
            if (kind < 0.5 && reference.Quantity > 1)
            {
                int quantityReduced = _random.Next(1, reference.Quantity);             // must leave quantity resting
                Apply(book, mbpByOrder, reference.Side, new Order(reference.Id, MarketByOrderAction.Reduce, reference.Side, reference.Ticks, quantityReduced));
                reference.Quantity -= quantityReduced;
            }
            else
            {
                int newTicks = kind < 0.75 ? reference.Ticks : _random.Next(1000, 1101);           // increase and/or price change: cancel + add
                int newQuantity = kind < 0.75 ? reference.Quantity + _random.Next(1, 500) : reference.Quantity;
                Apply(book, mbpByOrder, reference.Side, new Order(reference.Id, MarketByOrderAction.Cancel, reference.Side, reference.Ticks, reference.Quantity));
                Apply(book, mbpByOrder, reference.Side, new Order(reference.Id, MarketByOrderAction.Add, reference.Side, newTicks, newQuantity));
                reference.Ticks = newTicks;
                reference.Quantity = newQuantity;
                reference.Seq = ++_seq;
            }
        }
        else if (roll < 0.90)
        {
            // Cancel
            RefOrder reference = _reference[PickLiveId()];
            Apply(book, mbpByOrder, reference.Side, new Order(reference.Id, MarketByOrderAction.Cancel, reference.Side, reference.Ticks, reference.Quantity));
            RemoveLiveId(reference.Id);
            _reference.Remove(reference.Id);
        }
        else
        {
            // Trade: informational, must not mutate — random junk id on purpose
            Side side = _random.NextDouble() < 0.5 ? Side.Buy : Side.Sell;
            Apply(book, mbpByOrder, side, new Order((ulong)_random.NextInt64(), MarketByOrderAction.Trade, side, _random.Next(1000, 1101), _random.Next(1, 100)));
        }
    }

    private void Verify(MarketByOrderBook book, MarketByPriceByOrder mbpByOrder)
    {
        byte[] bytes = new byte[book.SnapshotSizeOf()];
        ref MarketByOrder snapshot = ref book.CopyToSnapshot(7, bytes);

        VerifySide(snapshot.BidsAsSpan(bytes.AsSpan()), Side.Buy, bidsBestFirst: true);
        VerifySide(snapshot.AsksAsSpan(bytes.AsSpan()), Side.Sell, bidsBestFirst: false);

        // the queue-free aggregation must produce a byte-identical MarketByPrice view
        byte[] mbpFromBook = new byte[book.MarketByPriceSizeOf(int.MaxValue)];
        book.CopyToMarketByPriceSnapshot(7, mbpFromBook, int.MaxValue);
        byte[] mbpFromOrders = new byte[mbpByOrder.MarketByPriceSizeOf(int.MaxValue)];
        mbpByOrder.CopyToMarketByPriceSnapshot(7, mbpFromOrders, int.MaxValue);
        if (!mbpFromBook.AsSpan().SequenceEqual(mbpFromOrders))
            throw new Exception($"MarketByPrice views diverged: book {book}, byOrder {mbpByOrder}");
    }

    private void VerifySide(ReadOnlySpan<Order> actual, Side side, bool bidsBestFirst)
    {
        RefOrder[] expected = _reference.Values
            .Where(o => o.Side == side)
            .OrderBy(o => bidsBestFirst ? -o.Ticks : o.Ticks)
            .ThenBy(o => o.Seq)
            .ToArray();

        if (actual.Length != expected.Length)
            throw new Exception($"{side} count mismatch: book {actual.Length}, reference {expected.Length}");

        for (int i = 0; i < expected.Length; i++)
        {
            ref readonly Order order = ref actual[i];
            RefOrder reference = expected[i];
            if (order.PriorityId != reference.Id || order.Level.Ticks != reference.Ticks || order.Level.Quantity != reference.Quantity ||
                order.OrderAction != MarketByOrderAction.Add || order.Side != side)
                throw new Exception($"{side}[{i}] mismatch: book (Id {order.PriorityId}, Ticks {order.Level.Ticks}, Qty {order.Level.Quantity}, {order.OrderAction}, {order.Side}), reference (Id {reference.Id}, Ticks {reference.Ticks}, Qty {reference.Quantity})");
        }
    }

    private void VerifyWireRoundTrip(MarketByOrderBook book)
    {
        byte[] bytes = new byte[book.SnapshotSizeOf()];
        book.CopyToSnapshot(7, bytes);

        using MarketByOrderBook copy = new MarketByOrderBook();
        copy.Apply(bytes);

        byte[] bytesFromCopy = new byte[copy.SnapshotSizeOf()];
        copy.CopyToSnapshot(7, bytesFromCopy);

        if (!bytes.AsSpan().SequenceEqual(bytesFromCopy))
            throw new Exception("Snapshot -> Apply -> Snapshot round trip is not byte-identical");

        // an MBO snapshot must also rebuild the queue-free aggregation to the same MarketByPrice view
        using MarketByPriceByOrder copyByOrder = new MarketByPriceByOrder();
        copyByOrder.Apply(bytes);
        byte[] mbpFromBook = new byte[book.MarketByPriceSizeOf(int.MaxValue)];
        book.CopyToMarketByPriceSnapshot(7, mbpFromBook, int.MaxValue);
        byte[] mbpFromCopy = new byte[copyByOrder.MarketByPriceSizeOf(int.MaxValue)];
        copyByOrder.CopyToMarketByPriceSnapshot(7, mbpFromCopy, int.MaxValue);
        if (!mbpFromBook.AsSpan().SequenceEqual(mbpFromCopy))
            throw new Exception("MBO snapshot -> MarketByPriceByOrder does not reproduce the MarketByPrice view");
    }

    private ulong PickLiveId() => _liveIds[_random.Next(_liveIds.Count)];

    private void RemoveLiveId(ulong id)
    {
        int index = _liveIndex[id];
        ulong last = _liveIds[^1];
        _liveIds[index] = last;
        _liveIndex[last] = index;
        _liveIds.RemoveAt(_liveIds.Count - 1);
        _liveIndex.Remove(id);
    }
}
