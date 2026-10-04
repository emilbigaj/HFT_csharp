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

public sealed class Test : Algo
{
    public Future Lead { get; }
    public Future Friend { get; }

    public Test(Position position, Client client, Future lead, Future friend) : base(client, position)
    {
        Lead = lead;
        Friend = friend;
    }

    
    public void Execute()
    {
        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);
        int maxPos = 10;
        int levelsDown = 10;
        int repriceTicks = 5;

        if (Position.TryGetQuote(out var quote))
        {
            int pos = GetPositionQuantity();

            // Flat: rest a single lot on the buy side, levelsDown ticks below the best bid. Not flat: quote nothing.
            if (pos == 0)
            {
                int buyTicks = quote.Bid.Ticks - levelsDown;

                // Once a bid is working, leave it where it is until it is repriceTicks or more away from where it would go now.
                foreach (ActiveTarget activeTarget in Position.ActiveTargets)
                {
                    if (activeTarget.Target.Sign > 0 && Math.Abs(activeTarget.Target.Ticks - buyTicks) < repriceTicks)
                    {
                        buyTicks = activeTarget.Target.Ticks;
                        break;
                    }
                }

                Target buy = new Target(buyTicks, 1);
                targets.Add(buy);
            }

            // Flat: rest a single lot on the buy side, levelsDown ticks below the best bid. Not flat: quote nothing.
            if (pos == 1)
            {
                int sellTicks = quote.Bid.Ticks - levelsDown;
                Target sell = new Target(sellTicks, -10);
                targets.Add(sell);
            }
        }

        // An empty target list cancels whatever is working.
        Target(ref targets);
    }

}