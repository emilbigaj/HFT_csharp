
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
    Dictionary<string, List<Series<Point>>> _profitByRoot = new Dictionary<string, List<Series<Point>>>();
    List<Series<Point>> _profitTotal = new List<Series<Point>>();


    public double GetTotal(List<Series<Point>> series)
    {
        double _profit = 0;
        foreach(Series<Point> profit in series)
            _profit += profit.Value.Value;
        return _profit;
    }

    private void OnPosition(Position position)
    {
        _positions.Add(position);
        if (!_profitByRoot.TryGetValue(position.Instrument.Root, out List<Series<Point>>? profits))
        {
            profits = new List<Series<Point>>();
            _profitByRoot[position.Instrument.Root] = profits;
            _profitTotal.Add(NewSeries($"Profit By {position.Instrument.Root}", ref TickTock!, () => GetTotal(profits)));
        }
        profits.Add(NewSeries($"Profit By {position.Instrument.Symbol}", ref BeforeTickTock!, () => position.Profit.Total));
    }
    public TestingStrategy(Scenario scenario) : base(scenario)
    {
        _latency = NewSeries<Point>("Latency");
        _profit = NewSeries("Profit", ref AfterTickTock!, () => GetTotal(_profitTotal));
        TickTocker tickTocker = new TickTocker(DirectoryPath, 1_000, OnTickTock);

     }

    protected Action<Timestamp>? BeforeTickTock;
    protected Action<Timestamp>? TickTock;
    protected Action<Timestamp>? AfterTickTock;
    private void OnTickTock(Timestamp timestamp)
    {
        BeforeTickTock?.Invoke(timestamp);
        TickTock?.Invoke(timestamp);
        AfterTickTock?.Invoke(timestamp);
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

        Make makeQuote = new Make(position, Client, lead, friend);


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
            makeQuote.Execute();
        };

        lead.QuoteChanged += () =>
        {
            using Latency latency = new Latency((int)CallId.InstrumentOnMarketByPrice);
            makeQuote.Execute();
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
