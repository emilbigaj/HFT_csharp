
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
    Dictionary<string, List<Position>> _positionsByRoot = new Dictionary<string, List<Position>>();
    Dictionary<string, Series<Point>> _profitByRoot = new Dictionary<string, Series<Point>>();

    private void OnPosition(Position position)
    {
        _positions.Add(position);
        if (!_positionsByRoot.TryGetValue(position.Instrument.Root, out List<Position>? instrumentIds))
        {
            _profitByRoot[position.Instrument.Root] = NewSeries<Point>("Profit By Root");
            instrumentIds = new List<Position>();
            _positionsByRoot[position.Instrument.Root] = instrumentIds;
        }
        instrumentIds.Add(position);
    }
    public TestingStrategy(Scenario scenario) : base(scenario)
    {
        _latency = NewSeries<Point>("Latency");
        _profit = NewSeries<Point>("Profit");
        TickTocker tickTocker = new TickTocker(DirectoryPath, 1_000, OnTickTock);
        TickTocker mstickTocker = new TickTocker(DirectoryPath, 1_00, OnMS100Timestamp);

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

            foreach (KeyValuePair<string, List<Position>> kvp in _positionsByRoot)
            {
                bool valid = false;
                double rootProfit = 0;
                foreach (Position position in kvp.Value)
                {
                    bool positionValid = double.IsFinite(position.Profit.Total);
                    rootProfit += positionValid ? position.Profit.Total : 0;
                    valid |= positionValid;
                }
                if (valid)
                    _profitByRoot[kvp.Key].Append(new Point(timestamp, rootProfit));
            }

        };
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

        OnPosition(position: position);

        Make executionAlgo = new Make(position, Client, lead, friend);

        
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


        lead.SettlementChanged += (in Settlement settlement) =>
        {
            Console.WriteLine(settlement);
        };

        position.PositionChanged += (in PositionHeader header) =>
        {
            using Latency latency = new Latency((int)CallId.InstrumentOnMarketByPrice);
            executionAlgo.Execute();
        };

        lead.QuoteChanged += () =>
        {
            using Latency latency = new Latency((int)CallId.InstrumentOnMarketByPrice);
            executionAlgo.Execute();
        };

        Series<Point> total = NewSeries(position.Instrument.Symbology + " Profit", ref TickTock!, ()=> position.Profit.Total);

        
           
    }


    public void OnSpread(Spread spread)
    {
        Future @long = (Future)spread.Long;
        Future @short = (Future)spread.Short;

        Position spreadPosition = GetPosition(spread);
        OnPosition(spreadPosition);
        Position longPosition = GetPosition(@long);
        OnPosition(longPosition);
        Position shortPosition = GetPosition(@short);
        OnPosition(shortPosition);

        MakeSpread make = new MakeSpread(spreadPosition, Client, @long, @short);
        Exit exitLong = new Exit(Client, longPosition);
        Exit exitShort = new Exit(Client, shortPosition);

        spread.MarketByPriceChanged += () =>
        {
            make.Execute();
        };
        @long.QuoteChanged += () =>
        {
            //exitLong.Execute();
        };
        @short.QuoteChanged += () =>
        {
            //exitShort.Execute();
        };
        
    }
}
