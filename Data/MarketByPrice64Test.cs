using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tools;

namespace Data
{
    internal class MarketByPrice64Test
    {
        private MarketByPrice64 _marketByPrice64 = new MarketByPrice64();
        private readonly int _bidsCount = 55;
        private readonly int _asksCount = 55;
        private readonly Random _random = new Random();

        public void Run()
        {
            Span<byte> past = stackalloc byte[MarketByPrice.SizeOf(64,64)];
            Span<byte> future = stackalloc byte[MarketByPrice.SizeOf(64, 64)];
            Span<byte> update = stackalloc byte[MarketByPrice.SizeOf(128, 128)];



            while (true)
            {
                CreateSnapshot(future);
                ref MarketByPrice future_ = ref MemoryMarshal.AsRef<MarketByPrice>(future);
                int bidsCount = 0;
                int maxTicks = future_.BidsAsSpan(future)[0].Ticks;
                foreach (Level bid in future_.BidsAsSpan(future))
                {
                    if (maxTicks - bid.Ticks < 64)
                        bidsCount++;
                }

                int asksCount = 0;
                int minTicks = future_.AsksAsSpan(future)[0].Ticks;
                foreach (Level ask in future_.AsksAsSpan(future))
                {
                    if (ask.Ticks - minTicks < 64)
                        asksCount++;
                }

                _marketByPrice64.CopyToSnapshot(0, past);
                ref MarketByPrice past_ = ref MemoryMarshal.AsRef<MarketByPrice>(past);


                MarketByPrice.SnapshotAsUpdate(past, future, update);

                ref MarketByPrice update_ = ref MemoryMarshal.AsRef<MarketByPrice>(update);

                Console.WriteLine(update_.ToString());

               
                _marketByPrice64.TrySet(update);


                if (_marketByPrice64.BidsCount != bidsCount || _marketByPrice64.AsksCount != asksCount)
                {
                    Console.WriteLine(_marketByPrice64.ToString());

                    throw new Exception("Counts do not match after update");
                }
                if (_marketByPrice64.BestBid.Ticks >= _marketByPrice64.BestAsk.Ticks)
                {
                    Console.WriteLine(_marketByPrice64.ToString());

                    throw new Exception("Bids - ask crossover.");
                }

            }
        }


        private void CreateSnapshot(Span<byte> dst)
        {
            int mid = _random.Next(1000, 2000); // upper bound is exclusive
            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
            mbp.TickHeader = new TickHeader
            {
                InstrumentId = 12345,
                TickType = TickType.MarketByPriceSnapshot,
                ExchangeTimestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            };
            mbp.BidsCount = _bidsCount;
            mbp.AsksCount = _asksCount;
            int lastTick = mid;
            foreach (ref Level bid in mbp.BidsAsSpan(dst))
            {
                bid.Ticks = lastTick - _random.Next(1, 5);
                lastTick = bid.Ticks;
                bid.Quantity = _random.Next(1, 101);
            }
            lastTick = mid;
            foreach (ref Level ask in mbp.AsksAsSpan(dst))
            {
                ask.Ticks = lastTick + _random.Next(1, 5);
                lastTick = ask.Ticks;
                ask.Quantity = _random.Next(1, 101);
            }
        }
    }
}
