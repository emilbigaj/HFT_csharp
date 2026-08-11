using Data;
using Execution;
using Provider;
using Simulator;
using Strategy;
using System;
using System.Reflection.Metadata.Ecma335;
using Tools;

namespace Proxy;

public static class GlobalVariables
{
    public static Timestamp SimulationBegin = new Timestamp(2024, 01, 03);
    public static Timestamp SimulationEnd = new Timestamp(2025, 01, 01);
    public static Timestamp Maturity = new Timestamp(2024, 03, 10);// 2024-03-18
}

public class ClientScenario : Scenario
{
    public ClientScenario(string name) : base(name)
    {
        Clock.Mode = ClockMode.Simulation;
        SimulationBegin = GlobalVariables.SimulationBegin;
        SimulationEnd = GlobalVariables.SimulationEnd;
    }

    public override ServerSimulator BuildSimulation()
    {
        Clock.SimulationSpeed = double.MaxValue;
        Client = new AlgoClient(ClientName, ServerName);
        Clock.TickTock += _ => Client.ReadSocket();
        return null!;
    }


    public override void BuildStrategies()
    {
        ProxyStrategy strategy = new ProxyStrategy(this);

        Future future = GetFuture("XCME", "6E", GlobalVariables.Maturity);
        strategy.OnFuture(future);

        strategy.OpenWorkspace("default");
    }

}

public class ServerScenario : Scenario
{
    public ServerScenario(string name) : base(name)
    {
        Clock.Mode = ClockMode.Simulation;
        SimulationBegin = GlobalVariables.SimulationBegin;
        SimulationEnd = GlobalVariables.SimulationEnd;
    }

    public override ServerSimulator BuildSimulation()
    {
        Clock.SimulationSpeed = 10;
        Clock.Exception += ex => Console.WriteLine(ex);

        ServerSimulator server = new ServerSimulator(ServerName);

        server.ExchangeSimulator.DataSimulator.Searches.Add(new TickHistorySearch()
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
        });

        InstrumentDetailsSearch search = new InstrumentDetailsSearch
        {
            DirectoryPath = "Z:\\InstrumentDetails",
        };

        using ArrayList<InstrumentDetails> found = InstrumentDetailsSearch.Search(search);
        foreach (InstrumentDetails details in found)
        {
            details.Sessions = new Session[] { Session.CME };
            server.OnInstrumentDetails(details);
        }


        server.Connect();

        ContextManager.Initialize(ServerName);
            
            
        using ArrayList<FutureHeader> futures = GetFutureHeaders("XCME", "6E");


        foreach (FutureHeader header in futures)
        {
            if (header.MaturityDate >= GlobalVariables.Maturity)
            {
                AllocateInstrument allocateInstrument = new()
                {
                    InstrumentHeaderId = header.InstrumentHeader.InstrumentHeaderId,
                    Symbol = "",
                };
                server.Server.OnAllocateInstrument(ref allocateInstrument);
                break;
            }
            
        }

        return server;

    }

}
