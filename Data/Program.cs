using Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Tools;

public static class Program
{
    public static void Main(string[] args)
    {
        // Refinitiv conversion tooling has been removed. The dev utilities below
        // (Read/Verify/Merge/Pipe/...) remain and can be invoked manually as needed.
    }

    public static void Pipe(string outputPath, string ticker, TickType tickType, Timestamp begin, Timestamp end)
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Ticker = ticker,
            Frequency = Frequency.Tick,
            TickType = tickType,
        };

        Span<byte> dst = stackalloc byte[512];




        foreach (TickHistory tickHistory in TickHistorySearch.Search(search))
        {
            using FileStream stream = File.OpenWrite(outputPath);
            using BinaryWriter writer = new BinaryWriter(stream);

            TickHistoryReader reader = new TickHistoryReader(tickHistory);
            int bytesNeeded = 0;
            while ((bytesNeeded = reader.MoveNext(dst, out TickType _tickType)) > 0)
            {
                if (_tickType == TickType.MarketByPriceSnapshot || _tickType == TickType.MarketByPriceUpdate)
                {
                    ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
                    writer.Write(mbp.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                    writer.Write((byte)mbp.TickHeader.TickType);
                    writer.Write(mbp.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                    writer.Write(mbp.TickHeader.NicTimestamp.NanosSinceEpoch);
                    writer.Write(mbp.BidsCount);
                    writer.Write(mbp.AsksCount);
                    foreach (Level bid in mbp.BidsAsSpan(dst))
                    {
                        writer.Write(bid.Ticks);
                        writer.Write(bid.Quantity);
                    }
                    foreach (Level ask in mbp.AsksAsSpan(dst))
                    {
                        writer.Write(ask.Ticks);
                        writer.Write(ask.Quantity);
                    }
                }
                else if (_tickType == TickType.Trade)
                {
                    Trade trade = reader.ReadTick(dst).AsTrade();
                    writer.Write(trade.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                    writer.Write((byte)trade.TickHeader.TickType);
                    writer.Write(trade.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                    writer.Write(trade.TickHeader.NicTimestamp.NanosSinceEpoch);
                    writer.Write(trade.Level.Ticks);
                    writer.Write(trade.Level.Quantity);
                }

            }
        }
    }


    public static void Write()
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "ES",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.MarketByPrice,
        };

        Span<byte> dst = stackalloc byte[512];




        foreach (TickHistory tickHistory in TickHistorySearch.Search(search))
        {
            if (!tickHistory.Symbology.Symbol.Contains("2025"))
                continue;

            string filePath = $"Z:\\TickHistory\\Binary\\{tickHistory.Symbology.Symbol}.MarketByPrice.bin";
            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using BinaryWriter writer = new BinaryWriter(fileStream);

            TickHistoryReader reader = new TickHistoryReader(tickHistory);
            int bytesNeeded = 0;
            MarketByPrice64 marketByPrice64 = new MarketByPrice64();


            while ((bytesNeeded = reader.MoveNext(dst, out TickType tickType)) > 0)
            {
                reader.ReadMarketByPrice(dst);
                marketByPrice64.TrySet(dst);
                ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
                writer.Write((byte)mbp.TickHeader.TickType);
                writer.Write(mbp.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                writer.Write(mbp.TickHeader.NicTimestamp.NanosSinceEpoch);
                writer.Write(mbp.BidsCount);
                writer.Write(mbp.AsksCount);
                foreach (Level bid in mbp.BidsAsSpan(dst))
                {
                    writer.Write(bid.Ticks);
                    writer.Write(bid.Quantity);
                }
                foreach (Level ask in mbp.AsksAsSpan(dst))
                {
                    writer.Write(ask.Ticks);
                    writer.Write(ask.Quantity);
                }


            }

            break;
        }

        TickHistorySearch searchTrades = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "ES",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.Trade,
        };

        foreach (TickHistory tickHistory in TickHistorySearch.Search(searchTrades))
        {
            if (!tickHistory.Symbology.Symbol.Contains("2025"))
                continue;

            string filePath = $"Z:\\TickHistory\\Binary\\{tickHistory.Symbology.Symbol}.Trade.bin";
            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using BinaryWriter writer = new BinaryWriter(fileStream);

            TickHistoryReader reader = new TickHistoryReader(tickHistory);
            int bytesNeeded = 0;


            while ((bytesNeeded = reader.MoveNext(dst, out TickType tickType)) > 0)
            {
                Trade trade = reader.ReadTick(dst).AsTrade();
                writer.Write((byte)trade.TickHeader.TickType);
                writer.Write(trade.TickHeader.ExchangeTimestamp.NanosSinceEpoch);
                writer.Write(trade.TickHeader.NicTimestamp.NanosSinceEpoch);
                writer.Write(trade.Level.Ticks);
                writer.Write(trade.Level.Quantity);
            }

            break;
        }


    }

    public static void ReadME()
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "M6E",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.MarketByPrice,
        };

        Span<byte> dst = stackalloc byte[512];


        foreach (TickHistory tickHistory in TickHistorySearch.Search(search))
        {
            if (!tickHistory.Symbology.Symbol.Contains("2025"))
                continue;

            TickHistoryReader reader = new TickHistoryReader(tickHistory, tickHistory.Footer.ExchangeTimestamp);
            int bytesNeeded = 0;
            MarketByPrice64 marketByPrice64 = new MarketByPrice64();


            while ((bytesNeeded = reader.MoveNext(dst, out TickType tickType)) > 0)
            {
                reader.ReadMarketByPrice(dst);
                marketByPrice64.TrySet(dst);
                ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
                Console.WriteLine(mbp.ToString());
            }
        }

        
    }

    public static void ReadTrades()
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "QBTC",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.Trade,
        };

        Span<byte> dst = stackalloc byte[512];
        Dictionary<Timestamp, int> days = new();

        foreach (TickHistory tickHistory in TickHistorySearch.Search(search))
        {
            TickHistoryReader reader = new TickHistoryReader(tickHistory);
            int bytesNeeded = 0;
            while ((bytesNeeded = reader.MoveNext(dst, out TickType tickType)) > 0)
            {
                ref Trade trade = ref reader.ReadTick(dst).AsTrade();
                Timestamp date = trade.TickHeader.ExchangeTimestamp.Date;
                days.TryAdd(date, 0);
                days[date] += trade.Level.Quantity;
            }

            foreach(var item in days.Keys.OrderBy(d => d))
            {
                Console.WriteLine($"{item}: {days[item]}");
            }
        }


    }

    public static void Copy()
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "6E",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.MarketByPrice,
        };

        ArrayList<TickHistory> found = TickHistorySearch.Search(search);
        TickHistory source = found[0];

        Symbology symbology = Symbology.FromString(source.Symbology.Symbol.Replace("6E", "6ETest"));
        TickHistory destination = new TickHistory(symbology, TickType.MarketByPrice, Frequency.Tick, Format.zstd)
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv\\New",
        };
        var c = destination.Compressor;

        Span<byte> dst = stackalloc byte[512];

        TickHistoryReader reader = new TickHistoryReader(source);
        int bytesNeeded = 0;

        MarketByPrice64 marketByPrice64_0 = new MarketByPrice64();
        MarketByPrice64 marketByPrice64_1 = new MarketByPrice64();


        for (int i = 0; i < 10_000; i++)
        {
            using (TickHistoryWriter writer = new TickHistoryWriter(destination))
            {
                bytesNeeded = reader.MoveNext(dst, out TickType tickType);
                {
                    reader.ReadMarketByPrice(dst);
                    marketByPrice64_0.TrySet(dst);
                    ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
                    writer.WriteMarketByPrice(dst.Slice(0, bytesNeeded));
                }
            }
        }

        Console.WriteLine("marketByPrice64_0:");
        Console.WriteLine(marketByPrice64_0.ToString());


        TickHistoryReader copyReader = new TickHistoryReader(destination);
        while ((bytesNeeded = copyReader.MoveNext(dst, out TickType tickType)) > 0)
        {
            copyReader.ReadMarketByPrice(dst);
            marketByPrice64_1.TrySet(dst);
            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
        }

        Console.WriteLine("marketByPrice64_1:");
        Console.WriteLine(marketByPrice64_1.ToString());
    }


    public static void Verify()
    {
        HashMap<Timestamp, int> daily = new HashMap<Timestamp, int>();


        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv\\New",
            Root = "MNK",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.MarketByPrice,
        };

        ArrayList<TickHistory> found = TickHistorySearch.Search(search);

        int _count = 20;
        Span<byte> dst = stackalloc byte[512];

        foreach (TickHistory history in found)
        {
            Timestamp month = new Timestamp(0);

            Console.WriteLine(history.FilePath);
            Console.WriteLine(history.Footer);

            //if ((history.Symbology as FutureSymbology).ExpiryDate < new Timestamp(2024, 1, 1))
            //    continue;

            MarketByPrice64 marketByPrice64 = new MarketByPrice64();

            TickHistoryReader reader = new TickHistoryReader(history);

            int bytesNeeded = 0;
            ulong length = 0;
            while ((bytesNeeded = reader.MoveNext(dst, out TickType tickType)) > 0)
            {
                length++;
                reader.ReadMarketByPrice(dst);
                ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst.Slice(0, bytesNeeded));
                foreach (Level bid in mbp.BidsAsSpan(dst))
                {
                    if (bid.Quantity < 0)
                        throw new Exception("Invalid bid level");
                }
                foreach (Level ask in mbp.AsksAsSpan(dst))
                {
                    if (ask.Quantity < 0)
                        throw new Exception("Invalid ask level");
                }

                int d = daily.GetOrAdd(mbp.TickHeader.ExchangeTimestamp.Date, 0);
                d++;
                daily[mbp.TickHeader.ExchangeTimestamp.Date] = d;


                if (mbp.TickHeader.ExchangeTimestamp.EndOfMonth > month.EndOfMonth)
                {
                    month = mbp.TickHeader.ExchangeTimestamp;
                    Console.WriteLine($"{month.ToDateString()} {length}");
                }
                //Console.WriteLine(mbp.Header.ExchangeTimestamp.ToString());
                marketByPrice64.TrySet(dst);

                int count = marketByPrice64.BidsCount + marketByPrice64.AsksCount;
                if (count > _count)
                {
                    _count = count;
                    Console.WriteLine(marketByPrice64.ToString());
                }
            }

            ArrayList<Timestamp> keys = daily.CopyKeys();
            keys.AsSpan().Sort();
            foreach(Timestamp key in keys)
            {
                Console.WriteLine($"{key.ToDateString()} : {daily[key]}");
            }




        }
    }

    public static void Merge()
    {
        TickHistorySearch news = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv\\New",
            Frequency = Frequency.Tick,
            TickType = TickType.Trade,
        };

        TickHistorySearch adj = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv\\New\\Adj",
            Frequency = Frequency.Tick,
            TickType = TickType.Trade,
        };

        TickHistorySearch olds = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Frequency = Frequency.Tick,
            TickType = TickType.Trade,
        };

        Dictionary<string, TickHistory> newD = new Dictionary<string, TickHistory>();
        foreach(TickHistory history in TickHistorySearch.Search(news))
        {
            newD[history.Symbology.Symbol] = history;
        }
        Dictionary<string, TickHistory> adjD = new Dictionary<string, TickHistory>();
        foreach (TickHistory history in TickHistorySearch.Search(adj))
        {
            adjD[history.Symbology.Symbol] = history;
        }
        Dictionary<string, TickHistory> oldD = new Dictionary<string, TickHistory>();
        foreach (TickHistory history in TickHistorySearch.Search(olds))
        {
            oldD[history.Symbology.Symbol] = history;
        }

        List<string> keys = newD.Keys.ToList().Concat(adjD.Keys.ToList()).Concat(oldD.Keys.ToList()).Distinct().ToList();

        Dictionary<string, TickHistory> final = new Dictionary<string, TickHistory>();


        foreach (var key in keys)
        {
            long oldL = oldD.ContainsKey(key) ? new FileInfo(oldD[key].FilePath).Length : 0;
            long adjL = adjD.ContainsKey(key) ? new FileInfo(adjD[key].FilePath).Length : 0;
            long newL = newD.ContainsKey(key) ? new FileInfo(newD[key].FilePath).Length : 0;
            final[key] = oldL >= adjL ? (oldL >= newL ? oldD[key] : newD[key]) : (adjL >= newL ? adjD[key] : newD[key]);
            if (oldD.ContainsKey(key) && final[key] == oldD[key])
                continue;
            else
            {
                string filepath = Path.Combine("Z:\\TickHistory\\Refinitiv", final[key].FileName);
                Console.WriteLine($"Copying {final[key].FilePath} to {filepath}");
                if (File.Exists(filepath))
                {
                    File.SetAttributes(filepath, FileAttributes.None);
                    File.Delete(filepath);
                }
                
                File.Move(final[key].FilePath, filepath, true);

            }

        }



    }


    public static void Read()
    {
        TickHistorySearch search = new TickHistorySearch
        {
            DirectoryPath = "Z:\\TickHistory\\Refinitiv",
            Root = "MNK",
            Exchange = "XCME",
            Frequency = Frequency.Tick,
            TickType = TickType.MarketByPrice,
        };

        ArrayList<TickHistory> found = TickHistorySearch.Search(search);

        Span<byte> buf = stackalloc byte[512];

        foreach (TickHistory history in found)
        {
            Console.WriteLine(history.FilePath);
            Console.WriteLine(history.Footer);

            MarketByPrice64 marketByPrice64 = new MarketByPrice64();

            TickHistoryReader reader = new TickHistoryReader(history);

            int bytesNeeded = 0;
            ulong length = 0;
            while ((bytesNeeded = reader.MoveNext(buf, out TickType tickType)) > 0)
            {
                Span<byte> dst = buf.Slice(0, bytesNeeded);
                length++;
                reader.ReadMarketByPrice(buf);
                ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);

                marketByPrice64.TrySet(dst);

                Console.WriteLine(mbp.ToString());

            }

        }
    }


}
