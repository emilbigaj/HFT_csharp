using Tools;
using Data;
using Provider;
using System.Threading;
using Strategy;
using System;
using System.Linq;
using System.Runtime.Versioning;
using Execution;
using Simulator;
using System.Collections.Generic;

namespace Strategy;

[RegisterJson]
public enum CoreGroupId
{
    OS = 0,
    Reserved = 1,
    SandP500 = 2,
    Equity = 3,
    Forex = 4,
    Crypto = 5,
}


public class Scenario
{
    public AlertManager AlertManager { get; private set; } = null!;
    public string Name { get; }
    public virtual FileSystemPath ServerName { get; }
    public CoreGroupId CoreGroupName { get; set; } = CoreGroupId.OS;
    public virtual FileSystemPath ClientName { get; }

    public Client Client { get; protected set; } = null!;
    public Timestamp SimulationBegin { get; set; } = Timestamp.MinValue;
    public Timestamp SimulationEnd { get; set; } = Timestamp.MaxValue;
    public ServerSimulator? ServerSimulator { get; private set; }

    public virtual FileSystemPath DefaultTickHistoryDirectoryPath { get; } = $"Z:\\TickHistory\\Databento\\";
    public virtual FileSystemPath DefaultInstrumentDetailsDirectoryPath { get; } = $"Z:\\InstrumentDetails\\Databento\\";


    public Scenario(string name)
    {
        Name = name;
        ServerName = ServerContext.GetDirectoryPath(Name);
        ClientName = ClientContext.GetDirectoryPath(Name);
    }

    private static int GetMarketDataCore(int coreGroupId) => coreGroupId * 4;
    private static int GetExchangeRecvCore(int coreGroupId) => coreGroupId * 4 + 1;
    private static int GetExchangeSendCore(int coreGroupId) => coreGroupId * 4 + 2;
    private static int GetStrategyCore(int coreGroupId) => coreGroupId * 4 + 3;

    // Simulation-only: provision an instrument's quantity limits so a backtest can actually exercise
    // them. The default is GetMaxLimits (int.MaxValue), under which no order is ever refused.
    // No RiskLayer refresh needed — ValidateOrder reads both limits live from shared memory on every
    // order, so there is no cached copy to invalidate.
    public void SetRiskLimit(Instrument instrument, int maxOrderQuantity, int maxPositionQuantity)
    {
        RiskLimit riskLimit = ServerSimulator!.ServerContext.GetRiskLimit(instrument.InstrumentId).GetReadonlyRef();
        riskLimit.MaxOrderQuantity = maxOrderQuantity;
        riskLimit.MaxPositionQuantity = maxPositionQuantity;
        riskLimit.Timestamp = Clock.Now;
        ServerSimulator!.ServerContext.GetRiskLimit(instrument.InstrumentId).Write(in riskLimit);
    }

    public Spread GetSpread(string exchange, string root, Timestamp longMaturity, Timestamp shortMaturity)
    {
        AddProductSearch(exchange, root);

        Context context = ContextManager.ServerContext;

        String8 _exchange = new String8(exchange);
        String8 _root = new String8(root);
        foreach (var header128 in context.EnumerateInstrumentHeaders())
        {
            Console.WriteLine(header128.AsInstrumentHeader().InstrumentType);
            if (header128.AsInstrumentHeader().InstrumentType != InstrumentType.Spread)
                continue;
            ref LeggedHeader leggedHeader = ref header128.AsLegged();
            if (leggedHeader.InstrumentHeader.Exchange == _exchange && leggedHeader.InstrumentHeader.Root == _root)
            {
                LegHeader longLeg = default, shortLeg = default;
                foreach (LegHeader leg in leggedHeader.Legs)
                {
                    if (leg.Weight > 0)
                        longLeg = leg;
                    else if (leg.Weight < 0)
                        shortLeg = leg;
                }

                ref readonly FutureHeader @longLegHeader = ref context.GetInstrumentHeader(longLeg.InstrumentHeaderId).GetReadonlyRef().AsFuture();
                ref readonly FutureHeader @shortLegHeader = ref context.GetInstrumentHeader(shortLeg.InstrumentHeaderId).GetReadonlyRef().AsFuture();
                if (longLegHeader.MaturityDate >= longMaturity && shortLegHeader.MaturityDate >= shortMaturity)
                {
                    return (Client.GetInstrument(leggedHeader.InstrumentHeader.InstrumentHeaderId) as Spread)!;
                }
            }
        }
        return default!;
    }

    public Future GetFuture(string exchange, string root, Timestamp maturity, int[]? months = null)
    {
        if (Clock.Mode == ClockMode.Simulation)
            AddProductSearch(exchange, root);

        Context context = ContextManager.ServerContext;

        String8 _exchange = new String8(exchange);
        String8 _root = new String8(root);
        foreach (var header128 in context.EnumerateInstrumentHeaders())
        {
            if (header128.AsInstrumentHeader().InstrumentType != InstrumentType.Future)
                continue;
            ref FutureHeader futureHeader = ref header128.AsFuture();
            if (futureHeader.InstrumentHeader.Exchange == _exchange && futureHeader.InstrumentHeader.Root == _root)
            {
                if (months != null && !months.Contains(futureHeader.MaturityDate.Month))
                    continue;

                if (futureHeader.MaturityDate >= maturity)
                {
                    return (Client.GetInstrument(futureHeader.InstrumentHeader.InstrumentHeaderId) as Future)!;
                }
            }
        }
        return default!;
    }

    public FutureChain GetFutureChain(string exchange, string root, Timestamp maturity, int[]? months = null)
    {
        AddProductSearch(exchange, root);
        List<Future> futures = new List<Future>();

        Context context = ContextManager.ServerContext;

        String8 _exchange = new String8(exchange);
        String8 _root = new String8(root);
        foreach (var header128 in context.EnumerateInstrumentHeaders())
        {
            if (header128.AsInstrumentHeader().InstrumentType != InstrumentType.Future)
                continue;
            ref FutureHeader futureHeader = ref header128.AsFuture();
            if (futureHeader.InstrumentHeader.Exchange == _exchange && futureHeader.InstrumentHeader.Root == _root)
            {
                if (months != null && !months.Contains(futureHeader.MaturityDate.Month))
                    continue;

                if (futureHeader.MaturityDate >= maturity)
                {
                    futures.Add((Client.GetInstrument(futureHeader.InstrumentHeader.InstrumentHeaderId) as Future)!);
                }
            }
        }
        return new FutureChain(futures);
    }


    public void Start()
    {
        if (Clock.Mode == ClockMode.Realtime)
        {
            Thread.CurrentThread.Name = Name;
            if (CoreGroupName > 0)
            {
                int strategyCore = GetStrategyCore((int)CoreGroupName);
                LowLatency.PinCurrentThreadToCore(strategyCore);
            }
            BuildRealtime();
        }
        else if (Clock.Mode == ClockMode.Simulation)
        {
            Clock.Begin = SimulationBegin;
            Clock.End = SimulationEnd;
            ServerSimulator = BuildSimulation();
        }
        AlertManager = new AlertManager(Client.Context);
        Clock.Exception += AlertManager.OnException;
        Client.OrderRejected += (in orderRejected) => AlertManager.OnOrderRejected(orderRejected, "");

        BuildStrategies();


        if (Clock.Mode == ClockMode.Realtime)
        {
            LowLatency.StartBackgroundThread("Clock", Clock.Start);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.TryStartNoGCRegion(64 * 1024 * 1024, true);

            Thread.CurrentThread.Name = Client.ClientName;
            while (!Application.IsExiting)
            {
                try
                {
                    Client.ReadSocket();
                }
                catch(Exception exception)
                {
                    AlertManager.OnException(exception);
                }
                X86BaseWrapper.Pause();
            }

            // IsExiting means the exit chain has STARTED on another thread (Ctrl+C handler); join it
            // so Main doesn't return and let CLR shutdown kill that thread mid-action.
            Application.OnExit(null, null);
        }
        else
        {
            Thread.CurrentThread.Name = "Clock";
            Clock.Start();
        }
    }

    public virtual void BuildStrategies()
    {

    }

    public virtual ServerSimulator BuildSimulation()
    {
        return null!;

    }
    
    public void AddProductSearch(string exchange, string root)
    {
        if (Clock.Mode == ClockMode.Realtime)
            throw new InvalidOperationException("Cannot add product in realtime mode");

        string product = $"{exchange} {root}";
        ServerSimulator!.ExchangeSimulator.DataSimulator.AddSearch(new TickHistorySearch()
        {
            DirectoryPath = $"{DefaultTickHistoryDirectoryPath}\\{product}",
        });

        InstrumentDetailsSearch search = new InstrumentDetailsSearch
        {
            DirectoryPath = $"{DefaultInstrumentDetailsDirectoryPath}\\{product}",
        };

        using ArrayList<InstrumentDetails> found = InstrumentDetailsSearch.Search(search);
        foreach (InstrumentDetails details in found)
        {
            details.Sessions = new Session[] { Session.CME };
            ServerSimulator!.OnInstrumentDetails(details);
        }
    }

    public virtual void BuildRealtime()
    {

    }

}

