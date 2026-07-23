using System;
using System.IO;
using System.Runtime.InteropServices;
using Data;
using Tools;

namespace Conversion;

// Self-check: convert a single old-format file and compare, record-by-record, the
// deprecated-format source against the current-format output.
//
//   - Trade/Settlement files: every record must match 1:1
//     (exchange ts, nic ts, payload), and sending == exchange on the new side.
//   - MarketByPrice files: every UPDATE record must match 1:1. Day-open SNAPSHOT
//     records are read (to keep the delta baseline) but excluded from the 1:1
//     comparison, because the current writer additionally injects one carried-over
//     snapshot at each day boundary — extra, redundant, and harmless.
//
// Usage: Data.exe --verify <sourceFile> [tempDir] [maxUpdates]
public static class RefinitivVerifier
{
    public static bool Verify(string srcPath, string tempDir, long maxUpdates = long.MaxValue)
    {
        Directory.CreateDirectory(tempDir);

        string srcDir = Path.GetDirectoryName(Path.GetFullPath(srcPath))!;
        RefinitivConverter.EnsureDictionary(srcDir, tempDir);

        (RefinitivConverter.ConvertResult _, string destPath) =
            RefinitivConverter.ConvertFile(srcPath, tempDir, skipExisting: false);

        Console.WriteLine($"Verifying:\n  old: {srcPath}\n  new: {destPath}");

        Deprecated.TickHistory oldHistory = Deprecated.TickHistory.FromFilePath(srcPath);
        Data.TickHistory newHistory = Data.TickHistory.FromFilePath(destPath);

        Deprecated.TickHistoryReader oldReader = new Deprecated.TickHistoryReader(oldHistory);
        Data.TickHistoryReader newReader = new Data.TickHistoryReader(newHistory);

        byte[] oldBuf = new byte[8192];
        byte[] newBuf = new byte[8192];

        long compared = 0;
        long oldSnapshots = 0, newSnapshots = 0;
        long sendingViolations = 0;
        bool ok = true;

        try
        {
            while (compared < maxUpdates)
            {
                bool haveOld = PullOldComparable(oldReader, oldBuf, out Record oldRec, ref oldSnapshots);
                bool haveNew = PullNewComparable(newReader, newBuf, out Record newRec, ref newSnapshots, ref sendingViolations);

                if (haveOld != haveNew)
                {
                    ok = false;
                    Console.Error.WriteLine($"MISMATCH: stream length differs (old has more = {haveOld}, new has more = {haveNew}) after {compared} compared records.");
                    break;
                }
                if (!haveOld)
                    break; // both ended

                if (!oldRec.Equals(newRec))
                {
                    ok = false;
                    Console.Error.WriteLine($"MISMATCH at comparable #{compared}:");
                    Console.Error.WriteLine($"  old: {oldRec}");
                    Console.Error.WriteLine($"  new: {newRec}");
                    break;
                }

                compared++;
            }
        }
        finally
        {
            oldReader.Dispose();
            newReader.Dispose();
        }

        if (sendingViolations > 0)
        {
            ok = false;
            Console.Error.WriteLine($"MISMATCH: {sendingViolations} new record(s) had SendingTimestamp != ExchangeTimestamp.");
        }

        Console.WriteLine($"Compared {compared} record(s). old snapshots={oldSnapshots}, new snapshots={newSnapshots} (new >= old expected for MBP).");
        Console.WriteLine(ok ? "VERIFY OK" : "VERIFY FAILED");
        return ok;
    }

    // A comparable record: type + exchange/nic ts + payload (in file order).
    private struct Record
    {
        public TickType Type;
        public long Exchange;
        public long Nic;
        public long[] Payload;

        public bool Equals(Record other)
        {
            if (Type != other.Type || Exchange != other.Exchange || Nic != other.Nic)
                return false;
            if (Payload.Length != other.Payload.Length)
                return false;
            for (int i = 0; i < Payload.Length; i++)
                if (Payload[i] != other.Payload[i])
                    return false;
            return true;
        }

        public override string ToString()
        {
            return $"{Type} ex={Exchange} nic={Nic} payload=[{string.Join(",", Payload)}]";
        }
    }

    // Pull the next comparable record from the OLD (deprecated) reader.
    // MBP snapshots are read (baseline must advance) but not returned.
    private static bool PullOldComparable(Deprecated.TickHistoryReader reader, byte[] buf, out Record rec, ref long snapshots)
    {
        Span<byte> span = buf;
        while (true)
        {
            int n = reader.MoveNext(span, out TickType tickType);
            if (n == 0)
            {
                rec = default;
                return false;
            }

            if (tickType == TickType.Trade)
            {
                ref Deprecated.Trade t = ref reader.ReadTick(span).AsTrade();
                rec = new Record
                {
                    Type = TickType.Trade,
                    Exchange = t.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                    Nic = t.TickHeader.NicTimestamp.NanosSinceEpoch,
                    Payload = new long[] { t.Level.Ticks, t.Level.Quantity, t.Direction },
                };
                return true;
            }
            if (tickType == TickType.Settlement)
            {
                ref Deprecated.Settlement s = ref reader.ReadTick(span).AsSettlement();
                rec = new Record
                {
                    Type = TickType.Settlement,
                    Exchange = s.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                    Nic = s.TickHeader.NicTimestamp.NanosSinceEpoch,
                    Payload = new long[] { BitConverter.DoubleToInt64Bits(s.Price) },
                };
                return true;
            }

            // MarketByPrice snapshot/update — must read to advance the baseline.
            ref Deprecated.MarketByPrice mbp = ref reader.ReadMarketByPrice(span);
            if (tickType == TickType.MarketByPriceSnapshot)
            {
                snapshots++;
                continue; // excluded from 1:1 comparison
            }
            rec = new Record
            {
                Type = tickType,
                Exchange = mbp.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                Nic = mbp.TickHeader.NicTimestamp.NanosSinceEpoch,
                Payload = LevelsPayload(mbp.BidsAsSpan(span), mbp.AsksAsSpan(span)),
            };
            return true;
        }
    }

    // Pull the next comparable record from the NEW (current) reader; also validates sending == exchange.
    private static bool PullNewComparable(Data.TickHistoryReader reader, byte[] buf, out Record rec, ref long snapshots, ref long sendingViolations)
    {
        Span<byte> span = buf;
        while (true)
        {
            int n = reader.MoveNext(span, out TickType tickType);
            if (n == 0)
            {
                rec = default;
                return false;
            }

            if (tickType == TickType.Trade)
            {
                ref Data.Trade t = ref reader.ReadTick(span).AsTrade();
                if (t.TickHeader.SendingTimestamp != t.TickHeader.ExchangeTimestamp) sendingViolations++;
                rec = new Record
                {
                    Type = TickType.Trade,
                    Exchange = t.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                    Nic = t.TickHeader.NicTimestamp.NanosSinceEpoch,
                    Payload = new long[] { t.Level.Ticks, t.Level.Quantity, t.Direction },
                };
                return true;
            }
            if (tickType == TickType.Settlement)
            {
                ref Data.Settlement s = ref reader.ReadTick(span).AsSettlement();
                if (s.TickHeader.SendingTimestamp != s.TickHeader.ExchangeTimestamp) sendingViolations++;
                rec = new Record
                {
                    Type = TickType.Settlement,
                    Exchange = s.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                    Nic = s.TickHeader.NicTimestamp.NanosSinceEpoch,
                    Payload = new long[] { BitConverter.DoubleToInt64Bits(s.Price) },
                };
                return true;
            }

            ref Data.MarketByPrice mbp = ref reader.ReadMarketByPrice(span);
            if (mbp.TickHeader.SendingTimestamp != mbp.TickHeader.ExchangeTimestamp) sendingViolations++;
            if (tickType == TickType.MarketByPriceSnapshot)
            {
                snapshots++;
                continue;
            }
            rec = new Record
            {
                Type = tickType,
                Exchange = mbp.TickHeader.ExchangeTimestamp.NanosSinceEpoch,
                Nic = mbp.TickHeader.NicTimestamp.NanosSinceEpoch,
                Payload = LevelsPayload(mbp.BidsAsSpan(span), mbp.AsksAsSpan(span)),
            };
            return true;
        }
    }

    private static long[] LevelsPayload(ReadOnlySpan<Level> bids, ReadOnlySpan<Level> asks)
    {
        long[] payload = new long[2 + (bids.Length + asks.Length) * 2];
        int i = 0;
        payload[i++] = bids.Length;
        payload[i++] = asks.Length;
        for (int b = 0; b < bids.Length; b++) { payload[i++] = bids[b].Ticks; payload[i++] = bids[b].Quantity; }
        for (int a = 0; a < asks.Length; a++) { payload[i++] = asks[a].Ticks; payload[i++] = asks[a].Quantity; }
        return payload;
    }
}
