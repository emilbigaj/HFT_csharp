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

public sealed class Make : Algo
{
    public Future Lead { get; }
    public Future Friend { get; }

    public Make(Position position, Client client, Future lead, Future friend) : base(client, position)
    {
        Lead = lead;
        Friend = friend;
    }

    public static bool s_exit = false;
    static Make()
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
        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);
        int maxPos = 10;

        if (Position.TryGetQuote(out var quote))
        {
            int pos = GetPositionQuantity();
            Console.WriteLine("-----------------------------");
            Console.WriteLine($"Current position: {pos}, max position: {maxPos}");
            int buyNeeded = maxPos - pos;
            int sellNeeded = -maxPos - pos;


            int buyTicks = quote.Bid.Ticks;
            int sellTicks = quote.Ask.Ticks;


            while (buyNeeded > 0)
            {
                int buyQty = Random.Shared.Next(1, buyNeeded + 1);
                buyNeeded -= buyQty;
                Target buy = new Target(buyTicks--, buyQty);
                Console.WriteLine($"Adding buy target: {buyTicks} ticks, quantity: {buyQty}");
                targets.Add(buy);

            }
            while (sellNeeded < 0)
            {
                int sellQty = -Random.Shared.Next(1, -sellNeeded + 1);
                sellNeeded -= sellQty;
                Target sell = new Target(sellTicks++, sellQty);
                Console.WriteLine($"Adding sell target: {sellTicks} ticks, quantity: {sellQty}");
                targets.Add(sell);
            }
        }
        

        Target(ref targets);
    }

}