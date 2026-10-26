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


public class Scenario
{
    public AlertManager AlertManager { get; private set; } = null!;
    public string Name { get; }
    public virtual FileSystemPath ServerName { get; }
    // The server names its CoreGroups (ServerContext.EnumerateCoreGroups); this picks the one the strategy thread runs on.
    public string CoreGroupName { get; set; } = "";
    public virtual FileSystemPath ClientName { get; }

    public Client Client { get; protected set; } = null!;
    public Timestamp SimulationBegin { get; set; } = Timestamp.MinValue;
    public Timestamp SimulationEnd { get; set; } = Timestamp.MaxValue;
    public ServerSimulator? ServerSimulator { get; private set; }
    private volatile bool _isServerAlive = true;   // cleared once by the clock thread when the server goes; the strategy thread's loop ends on it

    public virtual FileSystemPath DefaultTickHistoryDirectoryPath { get; } = $"Z:\\TickHistory\\Databento\\";
    public virtual FileSystemPath DefaultInstrumentDetailsDirectoryPath { get; } = $"Z:\\InstrumentDetails\\Databento\\";


    public Scenario(string name)
    {
        Name = name;
        ServerName = ServerContext.GetDirectoryPath(Name);
        ClientName = ClientContext.GetDirectoryPath(Name);
    }

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
        foreach (var header256 in context.EnumerateInstrumentHeaders())
        {
            Console.WriteLine(header256.AsInstrumentHeader().InstrumentType);
            if (header256.AsInstrumentHeader().InstrumentType != InstrumentType.Spread)
                continue;
            ref LeggedHeader leggedHeader = ref header256.AsLegged();
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
        foreach (var header256 in context.EnumerateInstrumentHeaders())
        {
            if (header256.AsInstrumentHeader().InstrumentType != InstrumentType.Future)
                continue;
            ref FutureHeader futureHeader = ref header256.AsFuture();
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
        foreach (var header256 in context.EnumerateInstrumentHeaders())
        {
            if (header256.AsInstrumentHeader().InstrumentType != InstrumentType.Future)
                continue;
            ref FutureHeader futureHeader = ref header256.AsFuture();
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
            BuildRealtime();

            // The server assigned the cores in the CoreGroup's file; pin to the strategy core of the one chosen. Unknown name or unset core throws.
            int coreGroupId = ContextManager.ServerContext.GetCoreGroupId(CoreGroupName);
            LowLatency.PinCurrentThreadToCore(ContextManager.ServerContext.GetCoreGroup(coreGroupId).GetReadonlyRef().StrategyCoreId);
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
        // Why before the strategies: at each second this check runs ahead of their timers, which would read a server that is gone
        if (Clock.Mode == ClockMode.Realtime)
            new TickTocker("Server", (int)Frequency.Second, PollServerProcessId);

        BuildStrategies();


        if (Clock.Mode == ClockMode.Realtime)
        {
            LowLatency.StartBackgroundThread("Clock", Clock.Start);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.TryStartNoGCRegion(64 * 1024 * 1024, true);

            Thread.CurrentThread.Name = Client.ClientName;
            while (!Application.IsExiting && _isServerAlive)
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

            // Why dispose first: it sends the queued alert (the server's end) before the exit actions tear the process down
            AlertManager.Dispose();
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

    // Once a second on the clock thread: when the server this strategy connected to has stopped or been replaced, one alert and the strategy ends (see Spec.md "A strategy ends when its server goes").
    private void PollServerProcessId(Timestamp timestamp)
    {
        if (!_isServerAlive || Client.IsServerAlive)
            return;
        AlertManager.OnException(new InvalidOperationException($"Scenario.PollServerProcessId: server {ServerName} has stopped or restarted; {Client.ClientName} ends: start it again once the server is up"));
        _isServerAlive = false;
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
            ServerSimulator!.OnInstrumentDetails(details);
        }
    }

    public virtual void BuildRealtime()
    {

    }

}

