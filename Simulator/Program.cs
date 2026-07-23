using System;
using Data;
using Execution;
using Provider;
using Simulator;
using Tools;

namespace Simulator
{
    public sealed class OrderManagerTestProgram : InstrumentSimulator
    {
        public OrderManagerTestProgram(ExchangeSimulator exchangeSimulator, InstrumentDetails instrumentDetails, int instrumentId)
            : base(exchangeSimulator, instrumentDetails, instrumentId)
        {
        }

        private new int _askMask = 0;


        ulong orderid = 1002;
        public void RunTest()
        {
            void EnqueueUserOrder(int ticks, int quantity)
            {
                Console.WriteLine($"\n--- Placing User Order ({ticks} @ {quantity}) ---");
                ulong orderId = orderid++;
                Buys.Enqeue(orderId, ticks, quantity);
                Console.WriteLine(Buys.ToDebugString());

            }

            void UpdateAskMBP(int ticks, int newQuantity)
            {
                Console.WriteLine($"ASK Update: {ticks} -> {newQuantity}");
                MarketByPrice64.TrySetAskQuantity(ticks, newQuantity, out int delta);

                foreach (Level ask in MarketByPrice64.EnumerateAsks())
                    Console.WriteLine($"Ask Price: {ask.Ticks} Qty: {ask.Quantity}");
                foreach (Level bid in MarketByPrice64.EnumerateBids())
                    Console.WriteLine($"Bid Price: {bid.Ticks} Qty: {bid.Quantity}");

                Sells.OnMarketByPriceDelta(ticks, delta);

                int mask = Buys.CrossMask + _askMask;
                foreach (Level ask in MarketByPrice64.EnumerateAsks())
                {
                    int crossQuantity = ask.Quantity;
                    int maskedQuantity = Math.Min(crossQuantity, mask);
                    crossQuantity = crossQuantity - maskedQuantity;
                    mask = Math.Max(mask - maskedQuantity, 0);
                    if (crossQuantity <= 0)
                        continue;
                    Trade trade = new Trade(InstrumentId, Clock.Now, Clock.Now, Clock.Now, ticks, crossQuantity, -1);
                    _askMask += Buys.OnTrade(false, trade);
                }

                
                Console.WriteLine(Buys.ToDebugString());
            }

            void UpdateBidMBP(int ticks, int newQuantity)
            {
                Console.WriteLine($"BID Update: {ticks} -> {newQuantity}");
                MarketByPrice64.TrySetBidQuantity(ticks, newQuantity, out int delta);

                foreach (Level ask in MarketByPrice64.EnumerateAsks())
                    Console.WriteLine($"Ask Price: {ask.Ticks} Qty: {ask.Quantity}");
                foreach (Level bid in MarketByPrice64.EnumerateBids())
                    Console.WriteLine($"Bid Price: {bid.Ticks} Qty: {bid.Quantity}");

                Buys.OnMarketByPriceDelta(ticks, delta);
               

                Console.WriteLine(Buys.ToDebugString());
            }

            void ApplyHistoricalTrade(int ticks, int quantity)
            {
                if (ticks < MarketByPrice64.Bids.BestTicks)
                    throw new Exception("unrealistic historical data!");

                Console.WriteLine($"\n--- Historical trade @ {ticks} for {quantity} ---");

                int oldQuantity = MarketByPrice64.GetBidQuantity(ticks);
                int newQuantity = oldQuantity - quantity;

                if (newQuantity < 0)
                    throw new Exception("unrealistic historical data!");

                Trade trade = new Trade(InstrumentId, Clock.Now, Clock.Now, Clock.Now, ticks, quantity, -1);
                Buys.OnTrade(true, trade);

                UpdateBidMBP(ticks, newQuantity);
            }

            EnqueueUserOrder(100, 10);

            UpdateBidMBP(100, 10);
            UpdateBidMBP(99, 10);
            UpdateBidMBP(98, 10);

            ApplyHistoricalTrade(100, 5);

            UpdateBidMBP(100, 10);

            ApplyHistoricalTrade(100, 10);

            ApplyHistoricalTrade(99, 5);

            UpdateBidMBP(99, 0);

            EnqueueUserOrder(99, 5);



            UpdateAskMBP(99, 7);
            UpdateAskMBP(99, 6);
            UpdateAskMBP(99, 9);

        }

        public static void Main()
        {
            Clock.Mode = ClockMode.Simulation;
            Clock.Begin = new Timestamp(1000000000);
            ServerSimulator serverSim = new ServerSimulator("S:\\Servers\\Simulation\\TestServer", false);
            ContextManager.Initialize("S:\\Servers\\Simulation\\TestServer");

            InstrumentDetails details = new InstrumentDetails
            {
                InstrumentType = InstrumentType.Future,
                Exchange = "XCME",
                Root = "ES",
                TickSize = 0.25,
                ExpiryType = ExpiryType.Month,
                ExpiryDate = Timestamp.MaxValue
            };

            OrderManagerTestProgram test = new OrderManagerTestProgram(serverSim.ExchangeSimulator, details, 1);
            test.RunTest();
        }
    }
}