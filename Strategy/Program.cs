using Strategy;
using Execution;
using Data;
using Tools;
using Provider;
using Simulator;
using System;


TestScenario TestScenario = new TestScenario("Test");
TestScenario.Start();

public class TestScenario : Scenario
{
    public TestScenario(string name) : base(name)
    {
        SimulationBegin = new Timestamp(2023, 10, 1);
        SimulationEnd = new Timestamp(2024, 1, 10);


    }
    public override void BuildStrategies()
    {
        Strategy.Strategy strategy = new Strategy.Strategy(this);
        Future eur = GetFuture("XCME", "6E", SimulationEnd);
        Future aud = GetFuture("XCME", "6A", SimulationEnd);
        Future jpy = GetFuture("XCME", "6J", SimulationEnd);
        strategy.GetPosition(eur);
        strategy.GetPosition(aud);
        strategy.GetPosition(jpy);

        strategy.OpenWorkspace("default");
    }
    public override ServerSimulator BuildSimulation()
    {
        Console.WriteLine("Setting up simulation environment...");

        ServerSimulator server = new ServerSimulator("Simulator");

        server.FromNicToClientLatency = 20;
        server.ExchangeSimulator.DataSimulator.Searches.Add(new TickHistorySearch()
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
        });

        InstrumentDetailsSearch search = new InstrumentDetailsSearch
        {
            DirectoryPath = "Z:\\InstrumentDetails",
        };

        ArrayList<InstrumentDetails> found = InstrumentDetailsSearch.Search(search);
        foreach(InstrumentDetails details in found)
        {
            details.Sessions = new Session[] { Session.CME };
            server.OnInstrumentDetails(details);
        }


        server.Connect();


        Client = new AlgoClient("S:\\Strategies\\Simulation\\Strategy", "Simulator");
        Clock.TickTock += timestamp => Client.ReadSocket();

        return server;

    }
    public override void BuildRealtime()
    {
        Console.WriteLine("Setting up realtime environment...");
    }
}



