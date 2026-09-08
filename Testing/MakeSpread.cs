using Data;
using Execution;
using Provider;
using Strategy;
using System;
using Tools;

namespace Testing;

public sealed class MakeSpread : Algo
{
    public Future Long { get; }
    public Future Short { get; }

    public MakeSpread(Position position, Client client, Future @long, Future @short) : base(client, position)
    {
        Long = @long;
        Short = @short;
    }

    public void Execute()
    {
        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);
        int pos = GetPositionQuantity();

        // Theoretical spread market implied by the legs: selling the spread via the legs collects
        // longBid - shortAsk (implied bid); buying it costs longAsk - shortBid (implied ask).
        // Bid floors and ask ceilings onto the spread grid, so the quote never crosses its theo.
        // Max position 1: quote each side only while a fill there keeps the position within +/-1.
        if (Long.TryGetQuote(out Quote longQuote) && Short.TryGetQuote(out Quote shortQuote))
        {
            int bidTicks = Instrument.FloorToTicks(longQuote.BidPrice - shortQuote.AskPrice);
            int askTicks = Instrument.CeilingToTicks(longQuote.AskPrice - shortQuote.BidPrice);

            if (pos < 1)
                targets.Add(new Target { Ticks = bidTicks, WorkingQuantity = 1 });
            if (pos > -1)
                targets.Add(new Target { Ticks = askTicks, WorkingQuantity = -1 });
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
