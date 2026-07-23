using Avalonia.Controls.Platform;
using Data;
using Execution;
using Provider;
using Strategy;
using System;
using System.Collections.Generic;
using System.Text;
using Tools;

namespace Testing;

public sealed class TestingAlgo : Algo
{
    public Future Lead { get; }
    public Future Friend { get; }
    public Mean Ratio { get; }

    public TestingAlgo(Position position, Client client, Future lead, Future friend, Mean ratio) : base(client, position)
    {
        Lead = lead;
        Friend = friend;
        Ratio = ratio;
    }

    public static bool s_exit = false;
    static TestingAlgo()
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            // Set to true to keep the process alive, or false (default) to kill it
            e.Cancel = false;
            Environment.Exit(0);
        };

        LowLatency.StartBackgroundThread("TestingAlgo::Commands", () =>
        {
            while(true)
            {
                string? cmd = Console.ReadLine();
                if (cmd == "exit")
                    s_exit = true;
            }
        });
    }

    int _bidTicks = 0;
    int _askTicks = 0;

    
    public void Execute()
    {
        //using Latency latency = new Latency(CallId.AlgoExecute);


        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);

        if (Lead.TryGetQuote(out Quote quote) && Friend.TryGetQuote(out Quote friend) && Instrument.TryGetQuote(out Quote inst))
        {
            double pc = quote.MidPrice * 0.00005;
            int spread = Instrument.RoundToTicks(pc);
            int half = Math.Max(spread / 2, 2);

            int bidTicks = Instrument.RoundToTicks( Math.Min(inst.BidPrice, Math.Min(quote.BidPrice, friend.BidPrice * Ratio)));
            if (Math.Abs(bidTicks - _bidTicks) >= half)
                _bidTicks = bidTicks;
            int askTicks = Instrument.RoundToTicks( Math.Max(inst.AskPrice, Math.Max(quote.AskPrice, friend.AskPrice * Ratio)));
            if (Math.Abs(askTicks - _askTicks) >= half)
                _askTicks = askTicks;

            if (s_exit)
            {
                int buyTicks = _bidTicks;
                int sellTicks = _askTicks;
                int pos = Position.Header.Quantity;
                if (pos > 0)
                {
                    targets.Add(new Target { Ticks = sellTicks, WorkingQuantity = -1 });
                }
                if (pos < 0)
                {
                    targets.Add(new Target { Ticks = buyTicks, WorkingQuantity = 1 });
                }
            }
            else
            {

                int pos = Position.Header.Quantity;
                if (pos == 0)
                {
                    int buyTicks = _bidTicks - spread;
                    int sellTicks = _askTicks + spread;
                    targets.Add(new Target { Ticks = sellTicks, WorkingQuantity = -1 });
                    targets.Add(new Target { Ticks = buyTicks, WorkingQuantity = 1 });

                }
                if (pos > 0)
                {
                    int sellTicks = _askTicks + 1;
                    targets.Add(new Target { Ticks = sellTicks, WorkingQuantity = -1 });
                }
                if (pos < 0)
                {
                    int buyTicks = _bidTicks - 1;
                    targets.Add(new Target { Ticks = buyTicks, WorkingQuantity = 1 });
                }
            }
            
        }

        int maxBuyTicks = int.MinValue;
        int minSellTicks = int.MaxValue;

        foreach (Target target in targets)
        {
            if (target.Sign > 0)
                maxBuyTicks = Math.Max(maxBuyTicks, target.Ticks);
            else
                minSellTicks = Math.Min(minSellTicks, target.Ticks);
        }

        if (maxBuyTicks >= minSellTicks)
            return;

            Target(ref targets);
    }

}