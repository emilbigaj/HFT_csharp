
using Data;
using Provider;
using Tools;
using Strategy;
using System;

namespace Proxy;

public sealed class ProxyAlgo : Algo
{
    public ProxyAlgo(Position position, Client client) : base(client, position)
    {
    }

    public void Execute()
    {
        StackList<Target> targets = new StackList<Target>(stackalloc Target[64]);

        int offset = 8;
        if (Instrument.TryGetQuote(out Quote quote))
        {
            int qty = Position.Profit.Quantity;
            for (int i = 0; i < 1; i++)
            {
                targets.Add(new(quote.Ask.Ticks + i + offset, -1));
            }

            for (int i = 0; i < 1; i++)
            {
                targets.Add(new(quote.Bid.Ticks - i - offset, 1));
            }
        }

        Target(ref targets);
    }


}

public class ProxyStrategy : Strategy.Strategy
{
    public ProxyStrategy(Scenario scenario) : base(scenario)
    {
    }



    public void OnFuture(Future future)
    {

        Position position = GetPosition(future);

        ProxyAlgo proxyAlgo = new ProxyAlgo(position, Client);
            
        Series<Point> bestBid = NewSeries<Point>(position.Instrument.Symbol + " BestBid");
        Series<Point> bestAsk = NewSeries<Point>(position.Instrument.Symbol + " BestAsk");
        Series<Point> profit = NewSeries<Point>(position.Instrument.Symbol + " Profit");

        void OnTickTock(Timestamp timestamp)
        {
            if (future.TryGetQuote(out var quote))
            {
                bestBid.Append(new Point(timestamp, quote.Bid.Ticks));
                bestAsk.Append(new Point(timestamp, quote.Ask.Ticks));
                profit.Append(new Point(timestamp, position.Profit.Total));
            }
        }

        future.MarketByPriceDelta += (in MarketByPrice mbp, ReadOnlySpan<byte> rsrc) =>
        {
            proxyAlgo.Execute();
        };


        TickTocker tickTocker = new TickTocker(DirectoryPath, 1_000, OnTickTock);

    }
}

