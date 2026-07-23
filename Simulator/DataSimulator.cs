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
    public LockedHashMap<string, int> Subscriptions { get; } = new LockedHashMap<string, int>();
    public int Capacity { get; set; } = 1024 * 1024;

    private PriorityTickQueue _priorityTickQueue;
    private TickQueueManager _tickQueueManager = new TickQueueManager();

    public string Name { get; }

    private readonly ExchangeSimulator _exchangeSimulator;

    public DataSimulator(string name, ExchangeSimulator exchangeSimulator)
    {
        Name = name;
        _exchangeSimulator = exchangeSimulator;
        _priorityTickQueue = new PriorityTickQueue(64*Capacity);
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

        foreach (System.Collections.Generic.KeyValuePair<string, TickHistory> match in matched)
        {
            TickHistory history = match.Value;
            if (Subscriptions.TryGetValue(history.Symbology.Symbol, out int instrumentId))
            {
                TickHistoryReader reader = new TickHistoryReader(history, begin: Clock.Begin, Clock.End);
                TickHistoryTickQueue tickQueue = new TickHistoryTickQueue(reader, instrumentId, Capacity);
                _tickQueueManager.Add(tickQueue);
                _priorityTickQueue.Enqueue(tickQueue); // blocks
            }
        }
        _tickQueueManager.Add(_priorityTickQueue);
    }

    public bool TryPeek(out Timestamp timestamp)
    {
        if (_priorityTickQueue.TryPeek(out ReadOnlySpan<byte> dst))
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
        while (_priorityTickQueue.TryPeek(out ReadOnlySpan<byte> src))
        {
            ref readonly TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(src);
            if (tickHeader.ExchangeTimestamp > now)
                break;

            if (tickHeader.ExchangeTimestamp.Date > _date)
            {
                _date = tickHeader.ExchangeTimestamp.Date;
                Console.WriteLine(_date);
            }

            if (tickHeader.TickType == TickType.Trade || tickHeader.TickType == TickType.Settlement)
            {
                Tick tick = MemoryMarshal.AsRef<Tick>(src);
                _exchangeSimulator.OnTick(ref tick);
            }
            else if (tickHeader.TickType == TickType.MarketByPriceUpdate || tickHeader.TickType == TickType.MarketByPriceSnapshot)
            {
                ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);
                _exchangeSimulator.OnMarketByPrice(in mbp, src);
            }
            else
            {
                throw new NotImplementedException($"Unsupported TickType: {tickHeader.TickType}");
            }

            _priorityTickQueue.TryDequeue();

        }
    }
}
