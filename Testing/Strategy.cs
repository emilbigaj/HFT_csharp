
using Data;
using Provider;
using Tools;
using Strategy;
using Execution;
using System.Diagnostics;
using System;
using System.Collections.Generic;

namespace Testing;



public class TestingStrategy : Strategy.Strategy
{
    public TestingStrategy(Scenario scenario) : base(scenario)
    {
        _latency = NewSeries<Point>("Latency");
        _profit = NewSeries<Point>("Profit");
        TickTocker tickTocker = new TickTocker(DirectoryPath, 1_000, OnTickTock);
        TickTocker mstickTocker = new TickTocker(DirectoryPath, 1_00, OnMS100Timestamp);

    }
    protected Action<Timestamp>? MS100;
    private void OnMS100Timestamp(Timestamp timestamp)
    {
        MS100?.Invoke(timestamp);
    }


    protected Action<Timestamp>? TickTock;
    private void OnTickTock(Timestamp timestamp)
    {
        TickTock?.Invoke(timestamp);
    }

    private readonly Series<Point> _latency;
    private readonly Series<Point> _profit;

    private List<Position> _positions = new List<Position>();

    public void OnFuture(Future future, Future lead, Future? friend = null)
    {
        if (friend == null)
            friend = lead;
        if (future == null || lead == null || friend == null)
            return;
        Position position = GetPosition(future);
        _positions.Add(position);

        Mean ratio = NewMean(lead.Root + friend.Root + "Ratio", halfLife: 5 * 10, 1);

        TestingAlgo executionAlgo = new TestingAlgo(position, Client, lead, friend, ratio);

        
        // Hook up the flush handler. This fires automatically when ReadSocket() hits its Dispose().
        Latency.OnFlush += (ReadOnlySpan<LatencyRecord> records) =>
        {
            long anchorTicks = Stopwatch.GetTimestamp();
            Timestamp now = Clock.Now;
            foreach (ref readonly LatencyRecord record in records)
            {
                long ticksAgo = anchorTicks - record.StartTicks;
                long nanosAgo = (long)(ticksAgo * Latency.NanosPerTick);
                Timestamp startTimestamp = now.AddNanoseconds(-nanosAgo);

                if (record.CallId == (int)CallId.InstrumentOnMarketByPrice)
                {
                    _latency.Append(new Point(startTimestamp, record.TotalDuration.TotalMicroseconds));
                    return;
                }
            }
        };

        MS100 += (Timestamp ts) =>
        {
            if (friend.TryGetQuote(out Quote f) && lead.TryGetQuote(out Quote l))
            {
                ratio += l.MidPrice / f.MidPrice;
            }
            
        };

        lead.SettlementChanged += (in Settlement settlement) =>
        {
            Console.WriteLine(settlement);
        };

        position.PositionHeader += (in PositionHeader header) =>
        {
            using Latency latency = new Latency((int)CallId.InstrumentOnMarketByPrice);
            executionAlgo.Execute();
        };

        lead.QuoteChanged += () =>
        {
            using Latency latency = new Latency((int)CallId.InstrumentOnMarketByPrice);
            executionAlgo.Execute();
        };

        Series<Point> total = NewSeries(position.Instrument.Symbology.Root + " Profit", ref TickTock!, ()=> position.Profit.Total);

        TickTock += timestamp =>
        {
            double totalProfit = 0;
            bool _valid = false;
            foreach (Position position in _positions)
            {
                bool valid = double.IsFinite(position.Profit.Total);
                totalProfit += valid ? position.Profit.Total : 0;
                _valid |= valid;
            }
            if (_valid)
                _profit.Append(new Point(timestamp, totalProfit));

        };
        


        
    }
}
