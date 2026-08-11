using Data;
using Execution;
using Provider;
using Strategy;
using System;
using Tools;
using Simulator;
using System.Collections.Generic;
using Socket;
using Testing;

namespace Testing;

public class TestingScenario : Scenario
{
    public override FileSystemPath ServerName { get; }
    public override FileSystemPath ClientName { get; }

    public TestingScenario(string name) : base(name)
    {
        Clock.Mode = Platform.IsCME ? ClockMode.Realtime : ClockMode.Simulation;
        if (Clock.Mode == ClockMode.Realtime)
        {

            Console.WriteLine("Enter Environment: NewRelease|Production");
            string? env = Console.ReadLine();
            while (env != "Production" && env != "NewRelease")
            {
                Console.WriteLine("Must be NewRelease|Production. Try again...");
                env = Console.ReadLine();
            }

            Console.WriteLine("Enter CoreGroupName: SandP500|Equity|Forex|Crypto");
            string? coreGroupName = Console.ReadLine();
            while (coreGroupName != "SandP500" && coreGroupName != "Equity" && coreGroupName != "Forex" && coreGroupName != "Crypto")
            {
                Console.WriteLine("Must be SandP500|Equity|Forex|Crypto. Try again...");
                coreGroupName = Console.ReadLine();
            }



            CoreGroupName = (CoreGroupId)Enum.Parse(typeof(CoreGroupId), coreGroupName);
            ServerName = ServerContext.GetDirectoryPath($"CME_{env}");
            ClientName = ClientContext.GetDirectoryPath($"{CoreGroupName}_Testing");

        }
        else
        {
            CoreGroupName = CoreGroupId.Equity;
            ServerName = ServerContext.GetDirectoryPath("ServerSimulation");
            ClientName = ClientContext.GetDirectoryPath($"{CoreGroupName}");
        }
        SimulationBegin = new Timestamp(2026, 7, 1);
        SimulationEnd = new Timestamp(2026, 7, 21, 0, 0, 0);
    }

    public override void BuildStrategies()
    {
        TestingStrategy strategy = new TestingStrategy(this);
        if (Clock.Mode == ClockMode.Simulation)
            strategy.OpenWorkspace("default");

        Dictionary<string, string> ProductGroups = new Dictionary<string, string>()
        {
            ["MNQ"] = "NQ",
            ["M2K"] = "RTY",
            ["MYM"] = "YM",
        };

        Client.Instrument += (Instrument instrument) =>
        {
            if (!ProductGroups.TryGetValue(instrument.Root, out string? productGroup))
                productGroup = instrument.Root;
            Client.Context.AllocateProductGroupId(instrument, productGroup);
        };

        int[] months = new int[] { 3, 6, 9, 12 };
        if (CoreGroupName == CoreGroupId.SandP500)
        {

            Future quote = GetFuture("XCME", "MES", Clock.Now, months);
            Future hedge = GetFuture("XCME", "ES", Clock.Now, months);
            Future friend = GetFuture("XCBT", "YM", Clock.Now, months);

            strategy.OnFuture(quote, hedge);
            strategy.OnFuture(hedge, hedge);

        }
        else if (CoreGroupName == CoreGroupId.Equity)
        {

            {
                Future quote = GetFuture("XCBT", "MYM", Clock.Now);
                Future hedge = GetFuture("XCBT", "YM", Clock.Now);
                Future friend = GetFuture(exchange: "XCME", "ES", Clock.Now);
                strategy.OnFuture(quote, hedge, friend);
            }
            {
                Future friend = GetFuture(exchange: "XCME", root: "ES", Clock.Now);
                Future quote = GetFuture("XCME", "M2K", Clock.Now);
                Future hedge = GetFuture("XCME", "RTY", Clock.Now);
                strategy.OnFuture(quote, hedge, friend);
            }

            
            

            {
                Future quote = GetFuture("XCME", "MNQ", Clock.Now);
                Future hedge = GetFuture("XCME", "NQ", Clock.Now);
                Future friend = GetFuture(exchange: "XCME", "ES", Clock.Now);
                strategy.OnFuture(quote, hedge, friend);
            }
            
            {
                Future friend = GetFuture(exchange: "XCME", "ES", maturity: Clock.Now);
                Future quote = GetFuture("XCME", "MNK", Clock.Now);
                Future hedge = GetFuture("XCME", "NKD", Clock.Now);
                strategy.OnFuture(quote, hedge, friend);
            }
            
        }
        else if (CoreGroupName == CoreGroupId.Forex)
        {
            {
                Future quote = GetFuture("XCME", "M6E", Clock.Now, months);
                Future hedge = GetFuture("XCME", "6E", Clock.Now, months);

                strategy.OnFuture(quote, hedge);
            }
            {
                Future quote = GetFuture("XCME", "M6B", Clock.Now, months);
                Future hedge = GetFuture("XCME", "6B", Clock.Now, months);

                strategy.OnFuture(quote, hedge);
            }
            {
                Future quote = GetFuture("XCME", "M6A", Clock.Now, months);
                Future hedge = GetFuture("XCME", "6A", Clock.Now, months);
                strategy.OnFuture(quote, hedge);
            }
            {
                Future quote = GetFuture("XCME", "MJY", Clock.Now, months);
                Future hedge = GetFuture("XCME", "6J", Clock.Now, months);
                strategy.OnFuture(quote, hedge);
            }
            {
                Future quote = GetFuture("XCME", "MCD", Clock.Now, months);
                Future hedge = GetFuture("XCME", "6C", Clock.Now, months);
                strategy.OnFuture(quote, hedge);
            }
            {
                //Future quote = GetFuture("XCME", "MSF", Clock.Now);
                //Future hedge = GetFuture("XCME", "6S", Clock.Now);
                //strategy.OnFuture(quote, hedge);
            }

        }
        else if (CoreGroupName == CoreGroupId.Crypto)
        {
            Timestamp thisMonth = new Timestamp(Clock.Now.Year, Clock.Now.Month, 1);
            Future quote = GetFuture("XCME", "MET", thisMonth);
            Future hedge = GetFuture("XCME", "ETH", thisMonth);
            strategy.OnFuture(quote, hedge);
        }
    }
    public override ServerSimulator BuildSimulation()
    {
        ServerSimulator server = new ServerSimulator(ServerName);
        ContextManager.Initialize(ServerName);

        server.OverrideNicTimestamp = false;
        server.FromExchangeToNicLatency = 250;
        server.FromNicToClientLatency = 0;
        server.ExchangeSimulator.DataSimulator.Searches.Add(new TickHistorySearch()
        {
            DirectoryPath = "Z:\\TickHistory\\Databento",
        });

        InstrumentDetailsSearch search = new InstrumentDetailsSearch
        {
            DirectoryPath = "Z:\\InstrumentDetails\\Databento",
        };

        using ArrayList<InstrumentDetails> found = InstrumentDetailsSearch.Search(search);
        foreach (InstrumentDetails details in found)
        {
            details.Sessions = new Session[] { Session.CME };
            server.OnInstrumentDetails(details);
        }


        server.Connect();


        Client = new AlgoClient(ClientName, ServerName);

        Clock.TickTock += timestamp => Client.ReadSocket();




        return server;

    }
    public override void BuildRealtime()
    {
        GCMonitor.Start();

        ContextManager.Initialize(ServerName);

        Client = new AlgoClient(ClientName, ServerName);
    }
}