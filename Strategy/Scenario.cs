using Tools;
using Data;
using Provider;
using System.Threading;
using Strategy;
using System;
using System.Runtime.Versioning;
using Execution;
using Simulator;

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

    public Future GetFuture(string exchange, string root, Timestamp maturity, int[]? months = null)
    {
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

    public virtual void BuildRealtime()
    {

    }

}

