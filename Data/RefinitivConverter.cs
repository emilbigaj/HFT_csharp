using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Data;      // current format: Tick, Trade, Settlement, MarketByPrice, TickHeader, TickHistory, TickHistoryWriter
using Tools;

namespace Conversion;

// ============================================================================
// Converts the OLD Refinitiv tick-history format (no SendingTimestamp) into the
// current format.
//
//   read  ->  Deprecated.TickHistoryReader   (24-byte TickHeader, no sending)
//   write ->  Data.TickHistoryWriter         (32-byte TickHeader, with sending)
//
// The old data carries no SendingTimestamp, so we synthesise it as
// SendingTimestamp = ExchangeTimestamp (delta-encodes to 0, no fabricated
// latency). Records are read back to absolute values by the deprecated reader,
// re-projected into the current struct layout, and re-delta-encoded by the
// current writer — a faithful, lossless round-trip aside from the added field.
// ============================================================================
public static class RefinitivConverter
{
    private const string DictionaryName = "TickHistory.zd";

    public static void Run(string sourceDir, string destDir, int? maxParallelism = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        Directory.CreateDirectory(destDir);

        // The current writer needs a zstd dictionary in the destination directory.
        // Reuse the source dictionary so compression stays self-consistent.
        EnsureDictionary(sourceDir, destDir);

        // Top-level *.zstd only (skips RAW/Test subfolders and the .zd dictionary).
        string[] found = Directory.GetFiles(sourceDir, "*.zstd", SearchOption.TopDirectoryOnly);
        List<(string path, long length)> sized = new List<(string, long)>(found.Length);
        foreach (string f in found)
            if (f.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)) // defend against Windows 8.3 over-matching
                sized.Add((f, new FileInfo(f).Length));

        // Largest-first (longest-processing-time scheduling): start the multi-GB files
        // immediately so they don't strand cores at the tail; the many tiny files backfill.
        sized.Sort((x, y) => y.length.CompareTo(x.length));
        List<string> targets = sized.ConvertAll(t => t.path);

        int total = targets.Count;
        int parallelism = maxParallelism ?? Math.Max(1, Math.Min(8, Environment.ProcessorCount));

        Console.WriteLine($"RefinitivConverter: {total} file(s)");
        Console.WriteLine($"  source: {sourceDir}");
        Console.WriteLine($"  dest:   {destDir}");
        Console.WriteLine($"  parallelism: {parallelism}");

        long done = 0, converted = 0, skipped = 0, truncated = 0, failed = 0;
        object logGate = new object();
        Stopwatch stopwatch = Stopwatch.StartNew();

        ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
        Parallel.ForEach(targets, options, srcPath =>
        {
            string name = Path.GetFileName(srcPath);
            try
            {
                (ConvertResult result, string _) = ConvertFile(srcPath, destDir, skipExisting: true);
                string tag;
                switch (result)
                {
                    case ConvertResult.Skipped: Interlocked.Increment(ref skipped); tag = "skip "; break;
                    case ConvertResult.Truncated: Interlocked.Increment(ref truncated); tag = "trunc"; break;
                    default: Interlocked.Increment(ref converted); tag = "ok   "; break;
                }
                long d = Interlocked.Increment(ref done);
                lock (logGate)
                    Console.WriteLine($"[{d}/{total}] {tag} {name}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                long d = Interlocked.Increment(ref done);
                lock (logGate)
                {
                    Console.Error.WriteLine($"[{d}/{total}] FAIL {name}");
                    Console.Error.WriteLine(ex.ToString()); // full stack + inner exceptions
                }
            }
        });

        stopwatch.Stop();
        Console.WriteLine($"Done. converted={converted} skipped={skipped} truncated={truncated} failed={failed} elapsed={stopwatch.Elapsed}");
        if (truncated > 0)
            Console.WriteLine($"NOTE: {truncated} source file(s) had a truncated/corrupt tail; all readable records were still converted.");
    }

    internal enum ConvertResult { Converted, Skipped, Truncated }

    internal static (ConvertResult result, string destPath) ConvertFile(string srcPath, string destDir, bool skipExisting)
    {
        Deprecated.TickHistory src = Deprecated.TickHistory.FromFilePath(srcPath);
        Data.TickHistory dst = new Data.TickHistory(src.Symbology, src.TickType, src.Frequency, src.Format)
        {
            DirectoryPath = destDir,
        };

        string destPath = dst.FilePath;
        string markerPath = destPath + ".partial";

        // Kill-safe resume: a destination counts as complete only if it is non-empty AND
        // has no ".partial" marker. The marker exists only while a conversion is in flight,
        // so a file left behind by a killed process is redone (not silently skipped).
        FileInfo destInfo = new FileInfo(destPath);
        if (skipExisting && destInfo.Exists && destInfo.Length > 0 && !File.Exists(markerPath))
            return (ConvertResult.Skipped, destPath);

        // Fresh attempt: clear any leftover output/marker, then claim the marker.
        TryDelete(destPath);
        TryDelete(markerPath);
        using (File.Create(markerPath)) { }

        Span<byte> readBuffer = stackalloc byte[8192];
        Span<byte> writeBuffer = stackalloc byte[8192];

        Deprecated.TickHistoryReader reader = new Deprecated.TickHistoryReader(src);
        bool completed = false;
        bool truncated = false;
        try
        {
            using Data.TickHistoryWriter writer = new Data.TickHistoryWriter(dst);

            try
            {
            int bytes;
            while ((bytes = reader.MoveNext(readBuffer, out TickType tickType)) > 0)
            {
                switch (tickType)
                {
                    case TickType.Trade:
                    {
                        ref Deprecated.Trade old = ref reader.ReadTick(readBuffer).AsTrade();
                        Data.Trade trade = default;
                        trade.TickHeader = new TickHeader(
                            TickType.Trade,
                            old.TickHeader.InstrumentId,
                            old.TickHeader.ExchangeTimestamp,
                            old.TickHeader.ExchangeTimestamp,   // sending := exchange
                            old.TickHeader.NicTimestamp);
                        trade.Level = new Level(old.Level.Ticks, old.Level.Quantity);
                        trade.Direction = old.Direction;

                        Tick tick = Unsafe.As<Data.Trade, Tick>(ref trade);
                        writer.WriteTick(in tick);
                        break;
                    }
                    case TickType.Settlement:
                    {
                        ref Deprecated.Settlement old = ref reader.ReadTick(readBuffer).AsSettlement();
                        Data.Settlement settlement = default;
                        settlement.TickHeader = new TickHeader(
                            TickType.Settlement,
                            old.TickHeader.InstrumentId,
                            old.TickHeader.ExchangeTimestamp,
                            old.TickHeader.ExchangeTimestamp,   // sending := exchange
                            old.TickHeader.NicTimestamp);
                        settlement.Price = old.Price;

                        Tick tick = Unsafe.As<Data.Settlement, Tick>(ref settlement);
                        writer.WriteTick(in tick);
                        break;
                    }
                    case TickType.MarketByPriceSnapshot:
                    case TickType.MarketByPriceUpdate:
                    {
                        ref Deprecated.MarketByPrice old = ref reader.ReadMarketByPrice(readBuffer);
                        int bidsCount = old.BidsCount;
                        int asksCount = old.AsksCount;
                        int size = MarketByPrice.SizeOf(bidsCount, asksCount);

                        Span<byte> outSpan = writeBuffer.Slice(0, size);
                        outSpan.Clear();

                        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(outSpan);
                        mbp.TickHeader = new TickHeader(
                            old.TickHeader.TickType,
                            old.TickHeader.InstrumentId,
                            old.TickHeader.ExchangeTimestamp,
                            old.TickHeader.ExchangeTimestamp,   // sending := exchange
                            old.TickHeader.NicTimestamp);
                        mbp.BidsCount = bidsCount;
                        mbp.AsksCount = asksCount;

                        old.BidsAsSpan(readBuffer).CopyTo(mbp.BidsAsSpan(outSpan));
                        old.AsksAsSpan(readBuffer).CopyTo(mbp.AsksAsSpan(outSpan));

                        writer.WriteMarketByPrice(outSpan);
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Unsupported TickType {tickType} in {srcPath}");
                }
            }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex.GetType().Name == "ZstdException")
            {
                // The source file's tail is truncated/corrupt. Every record read before this
                // point is already written; finalize the output with what we salvaged.
                truncated = true;
            }

            completed = true;
        }
        finally
        {
            reader.Dispose();
            if (completed)
            {
                TryDelete(markerPath); // success: clearing the marker publishes the file as complete
            }
            else
            {
                TryDelete(destPath);   // failure: drop the partial output...
                TryDelete(markerPath); // ...and its marker, so a re-run redoes it cleanly
            }
        }

        return (truncated ? ConvertResult.Truncated : ConvertResult.Converted, destPath);
    }

    internal static void EnsureDictionary(string sourceDir, string destDir)
    {
        string destDict = Path.Combine(destDir, DictionaryName);
        if (File.Exists(destDict))
            return;

        string sourceDict = Path.Combine(sourceDir, DictionaryName);
        if (!File.Exists(sourceDict))
            throw new FileNotFoundException($"Missing zstd dictionary required for conversion: {sourceDict}");

        File.Copy(sourceDict, destDict, overwrite: false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
