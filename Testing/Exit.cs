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

public sealed class Exit : Algo
{


    public Exit(Client client, Position position) : base(client, position)
    {
    }
    public void Execute()
    {
        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);
        int pos = GetPositionQuantity();

        if (Instrument.TryGetQuote(out Quote quote))
        {
            int buyTicks = quote.Bid.Ticks;
            int sellTicks = quote.Ask.Ticks;
            if (pos > 0)
            {
                targets.Add(new Target { Ticks = sellTicks, WorkingQuantity = -1 });
            }
            if (pos < 0)
            {
                targets.Add(new Target { Ticks = buyTicks, WorkingQuantity = 1 });
            }
        }

        Target(ref targets);
    }

}