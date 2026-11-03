using System;
using System.Buffers;
using System.Drawing;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tools;
using Data;
using ZstdSharp.Unsafe;

namespace Simulator;

public class DataSimulator
{
    public System.Collections.Generic.List<TickHistorySearch> Searches { get; } = new System.Collections.Generic.List<TickHistorySearch>();
    public void AddSearch(TickHistorySearch search)
    {
        foreach(TickHistorySearch existing in Searches)
        {
            if (existing.DirectoryPath == search.DirectoryPath)
            {
                if (existing.ToString() == search.ToString())
                    return;
            }
        }
        Searches.Add(search);
    }
    public LockedHashMap<string, int> Subscriptions { get; } = new LockedHashMap<string, int>();
    public int Capacity { get; set; } = 1024 * 1024;

    private readonly TickMerge _tickMerge = new();
    private readonly TickQueueManager _tickQueueManager = new TickQueueManager();
    private System.Collections.Generic.List<TickHistoryTickQueue>[] _tickQueuesByInstrumentId = [];   // each instrument's file queues, for TryPeekNext; sized at ClockStarted
    private readonly byte[] _eventBuffer;   // the event being processed, copied out of its file queue: sized for the largest frame a file queue holds

    public string Name { get; }

    private readonly ExchangeSimulator _exchangeSimulator;

    public DataSimulator(string name, ExchangeSimulator exchangeSimulator)
    {
        Name = name;
        _exchangeSimulator = exchangeSimulator;
        _eventBuffer = new byte[64 * Capacity];
        SubscribeClock();
    }

    private void SubscribeClock()
    {
        Clock.Started += ClockStarted;
    }

    private void ClockStarted(Timestamp timestamp)
    {

        using HashMap<string, TickHistory> matched = new HashMap<string, TickHistory>();
        // Build buffers for all searches that match active subscriptions
        foreach (TickHistorySearch search in Searches)
        {
            using ArrayList<TickHistory> found = TickHistorySearch.Search(search);
            foreach(TickHistory history in found)
            {
                if (Subscriptions.ContainsKey(history.Symbology.Symbol))
                    matched.TryAdd(history.FilePath, history);
            }
        }

        // MBO carries trades in-stream and supersedes MBP: skip MarketByPrice and Trade files for any symbol with MarketByOrder history
        System.Collections.Generic.HashSet<string> marketByOrderSymbols = new System.Collections.Generic.HashSet<string>();
        foreach (System.Collections.Generic.KeyValuePair<string, TickHistory> match in matched)
            if (match.Value.TickType == TickType.MarketByOrder)
                marketByOrderSymbols.Add(match.Value.Symbology.Symbol);

        foreach (System.Collections.Generic.KeyValuePair<string, TickHistory> match in matched)
        {
            TickHistory history = match.Value;
            if ((history.TickType == TickType.MarketByPrice || history.TickType == TickType.Trade) && marketByOrderSymbols.Contains(history.Symbology.Symbol))
                continue;

            if (Subscriptions.TryGetValue(history.Symbology.Symbol, out int instrumentId))
            {
                int capacity = history.TickType == TickType.MarketByOrder ? 64 * Capacity : Capacity;   // MBO midnight snapshots are MBs
                TickHistoryReader reader = new TickHistoryReader(history, begin: Clock.Begin, Clock.End);
                TickHistoryTickQueue tickQueue = new TickHistoryTickQueue(reader, instrumentId, capacity);
                _tickQueueManager.Add(tickQueue);
                if (instrumentId >= _tickQueuesByInstrumentId.Length)
                    Array.Resize(ref _tickQueuesByInstrumentId, instrumentId + 1);
                (_tickQueuesByInstrumentId[instrumentId] ??= new System.Collections.Generic.List<TickHistoryTickQueue>(4)).Add(tickQueue);
                _tickMerge.Add(tickQueue); // blocks until the file's first event is in its ring
            }
        }
    }

    // The instrument's next recorded event: the earliest front among its file queues (its book and its trading status); false once all are
    // at end of stream, or for an instrument with no files. Waits, as the merge does, for a file whose worker has not unpacked that far yet.
    public bool TryPeekNext(int instrumentId, out TickHeader tickHeader)
    {
        tickHeader = default;
        bool isFound = false;
        if (instrumentId >= _tickQueuesByInstrumentId.Length || _tickQueuesByInstrumentId[instrumentId] == null)
            return false;
        foreach (TickHistoryTickQueue tickQueue in _tickQueuesByInstrumentId[instrumentId])
        {
            if (!tickQueue.TryPeek(out ReadOnlySpan<byte> src))
                continue;
            ref readonly TickHeader front = ref MemoryMarshal.AsRef<TickHeader>(src);
            if (!isFound || front.ExchangeTimestamp < tickHeader.ExchangeTimestamp)
            {
                tickHeader = front;
                isFound = true;
            }
        }
        return isFound;
    }

    public bool TryPeek(out Timestamp timestamp)
    {
        if (_tickMerge.TryPeek(out ReadOnlySpan<byte> dst))
        {
            ref readonly TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(dst);
            timestamp = tickHeader.ExchangeTimestamp;
            return true;
        }
        timestamp = Timestamp.MaxValue;
        return false;
    }

    internal void OnInterject(Timestamp _)
    {
        if (TryPeek(out Timestamp timestamp))
        {
            Clock.OnInterject(timestamp);
        }
    }

    public void Subscribe(Symbology symbology, int instrumentId)
    {
        Subscriptions.TryAdd(symbology.Symbol, instrumentId);
    }

    private Timestamp _date = new Timestamp();
    internal void OnTickTock(Timestamp now)
    {
        // Drain all ticks with timestamp <= now
        while (_tickMerge.TryPeek(out ReadOnlySpan<byte> head))
        {
            if (MemoryMarshal.AsRef<TickHeader>(head).ExchangeTimestamp > now)
                break;

            // Why a copy, dequeued before it is processed: each file queue's front is then the event behind this one, which the engine's stamps look at (see spec.txt)
            Span<byte> src = _eventBuffer.AsSpan(0, head.Length);
            head.CopyTo(src);
            _tickMerge.Dequeue();
            ref readonly TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(src);

            if (tickHeader.ExchangeTimestamp.Date > _date)
            {
                _date = tickHeader.ExchangeTimestamp.Date;
                Console.WriteLine(_date);
            }

            try
            {
                if (tickHeader.TickType.IsTick())
                {
                    Tick tick = MemoryMarshal.AsRef<Tick>(src);
                    _exchangeSimulator.OnTick(ref tick);
                }
                else if (tickHeader.TickType == TickType.MarketByPriceUpdate || tickHeader.TickType == TickType.MarketByPriceSnapshot)
                {
                    ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);
                    _exchangeSimulator.OnMarketByPrice(in mbp, src);
                }
                else if (tickHeader.TickType == TickType.MarketByOrderUpdate || tickHeader.TickType == TickType.MarketByOrderSnapshot)
                {
                    ref readonly MarketByOrder mbo = ref MemoryMarshal.AsRef<MarketByOrder>(src);
                    _exchangeSimulator.OnMarketByOrder(in mbo, src);
                }
                else
                {
                    throw new NotImplementedException($"Unsupported TickType: {tickHeader.TickType}");
                }
            }
            catch
            {
                // Why end the run: a message that failed part-way leaves the book damaged, and a retry would apply it twice; nothing after it can be trusted (see spec.txt)
                Clock.Stop();
                throw;
            }
        }
    }
}
