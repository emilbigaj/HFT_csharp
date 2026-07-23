using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;          // shared, byte-identical types: Level, TickType, Format, Frequency, Symbology, TickHistoryHeader, LimitedStream
using Tools;         // Timestamp, FileSystemPath
using ZstdSharp;

// ============================================================================
// OLD ("deprecated") tick-history format — READ PATH ONLY.
//
// The only structural difference from the current format is that the old
// TickHeader has NO SendingTimestamp: it is 24 bytes (TickType + reserved[3] +
// InstrumentId + ExchangeTimestamp + NicTimestamp) instead of 32. That cascades
// into Trade / Settlement / MarketByPrice layouts and the delta/restore math.
//
// Everything else (Level, TickType, Frequency, Format, Symbology, the per-day
// TickHistoryHeader and LimitedStream) is identical, so we reuse it from `Data`.
//
// We only need to READ old files here — they are re-written through the current
// Data.TickHistoryWriter — so the writer half is intentionally not ported.
// ============================================================================
namespace Deprecated;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TickHeader   // 24 bytes (no SendingTimestamp)
{
    public TickType TickType;
    private unsafe fixed byte _reserved[3];
    public int InstrumentId;
    public Timestamp ExchangeTimestamp;
    public Timestamp NicTimestamp;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct Tick
{
    public TickHeader TickHeader;

    public ref Settlement AsSettlement() => ref Unsafe.As<Tick, Settlement>(ref Unsafe.AsRef(in this));
    public ref Trade AsTrade() => ref Unsafe.As<Tick, Trade>(ref Unsafe.AsRef(in this));
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct Settlement
{
    public TickHeader TickHeader;
    public double Price;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct Trade
{
    public TickHeader TickHeader;
    public Level Level;
    public sbyte Direction;
}

// [ TickHeader | BidsCount:int | AsksCount:int | bids[] | asks[] ] — 32-byte header
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MarketByPrice
{
    public TickHeader TickHeader;
    public int BidsCount;
    public int AsksCount;

    public int SizeOf() => SizeOf(BidsCount, AsksCount);

    public int SizeOfLevels() => (BidsCount + AsksCount) * Unsafe.SizeOf<Level>();

    public static int SizeOf(int bidsCount, int asksCount)
        => Unsafe.SizeOf<MarketByPrice>() + (bidsCount + asksCount) * Unsafe.SizeOf<Level>();

    public Span<Level> BidsAsSpan(Span<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes, BidsCount * levelBytes));
    }

    public Span<Level> AsksAsSpan(Span<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        int bidsBytes = BidsCount * levelBytes;
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes + bidsBytes, AsksCount * levelBytes));
    }
}

// File format: [TickHistoryHeader][Zstd chunk][TickHistoryHeader][Zstd chunk]...[TickHistoryHeader]
public class TickHistory
{
    private byte[]? _dictionary;
    private Decompressor? _decompressor;

    public Decompressor Decompressor
    {
        get
        {
            if (_dictionary == null)
                InitDecompressor(DictionaryPath);
            return _decompressor!;
        }
    }

    public FileSystemPath DirectoryPath { get; init; } = @"Z:\TickHistory";
    public FileSystemPath DictionaryPath => Path.Combine(DirectoryPath, "TickHistory.zd");
    public Symbology Symbology { get; }
    public TickType TickType { get; }
    public Frequency Frequency { get; }
    public Format Format { get; }

    public string FileName => $"{Symbology.Symbol}.{TickType}.{Frequency}.{Format}";
    public FileSystemPath FilePath => Path.Combine(DirectoryPath, FileName);

    public TickHistory(Symbology symbology, TickType tickType, Frequency frequency, Format format)
    {
        Symbology = symbology;
        TickType = tickType;
        Frequency = frequency;
        Format = format;
    }

    public void Dispose() => _decompressor?.Dispose();

    public static TickHistory FromFilePath(FileSystemPath filePath)
    {
        string directoryPath = Path.GetDirectoryName(filePath)!;
        string fileName = Path.GetFileName(filePath);
        string[] tokens = fileName.Split('.');

        if (tokens.Length < 4)
            throw new ArgumentException("Filename must be \"{Symbology}.{TickType}.{Frequency}.{Format}[.ext]\".", nameof(filePath));

        Symbology symbology = Symbology.FromString(tokens[0]);
        TickType tickType = Enum.Parse<TickType>(tokens[1], true);
        Frequency frequency = Enum.Parse<Frequency>(tokens[2], true);
        Format format = Enum.Parse<Format>(tokens[3], true);

        return new TickHistory(symbology, tickType, frequency, format)
        {
            DirectoryPath = directoryPath,
        };
    }

    private void InitDecompressor(string filePath)
    {
        if (_dictionary == null)
        {
            _dictionary = File.ReadAllBytes(filePath);
            _decompressor = new Decompressor();
            _decompressor.LoadDictionary(_dictionary);
        }
    }

    // ----- Delta -> absolute restore (must run for every record to keep the running baseline) -----

    internal static void RestoreTickHeader(ref TickHistoryHeader day, ref TickHeader tick)
    {
        tick.ExchangeTimestamp = new Timestamp(day.ExchangeTimestamp.NanosSinceEpoch + tick.ExchangeTimestamp.NanosSinceEpoch);
        tick.NicTimestamp = new Timestamp(tick.ExchangeTimestamp.NanosSinceEpoch + tick.NicTimestamp.NanosSinceEpoch);
        day.ExchangeTimestamp = tick.ExchangeTimestamp;
    }

    internal static void RestoreLevel(ref TickHistoryHeader header, ref Level level)
    {
        level.Ticks += header.Ticks;
        level.Quantity += header.Quantity;
        header.Ticks = level.Ticks;
        header.Quantity = level.Quantity;
    }

    internal static void RestoreTrade(ref TickHistoryHeader header, ref Trade trade)
    {
        RestoreTickHeader(ref header, ref trade.TickHeader);
        RestoreLevel(ref header, ref trade.Level);
    }

    internal static void RestoreSettlement(ref TickHistoryHeader header, ref Settlement settlement)
    {
        RestoreTickHeader(ref header, ref settlement.TickHeader);
    }

    internal static void RestoreMarketByPrice(ref TickHistoryHeader header, Span<byte> bytes)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(bytes);
        RestoreTickHeader(ref header, ref mbp.TickHeader);

        Span<Level> bids = mbp.BidsAsSpan(bytes);
        for (int i = 0; i < bids.Length; i++)
            RestoreLevel(ref header, ref bids[i]);

        Span<Level> asks = mbp.AsksAsSpan(bytes);
        for (int i = 0; i < asks.Length; i++)
            RestoreLevel(ref header, ref asks[i]);
    }

    internal ref Tick ReadTick(ref TickHistoryHeader dayHeader, Span<byte> dst)
    {
        ref Tick tick = ref MemoryMarshal.AsRef<Tick>(dst);
        if (tick.TickHeader.TickType == TickType.Trade)
            RestoreTrade(ref dayHeader, ref MemoryMarshal.AsRef<Trade>(dst));
        else if (tick.TickHeader.TickType == TickType.Settlement)
            RestoreSettlement(ref dayHeader, ref MemoryMarshal.AsRef<Settlement>(dst));
        else
            throw new NotSupportedException($"ReadTick does not support TickType {tick.TickHeader.TickType}");
        return ref tick;
    }

    internal ref MarketByPrice ReadMarketByPrice(DecompressionStream decompressionStream, ref TickHistoryHeader dayHeader, Span<byte> dst)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
        ReadExact(decompressionStream, dst.Slice(Unsafe.SizeOf<MarketByPrice>(), mbp.SizeOfLevels()));
        RestoreMarketByPrice(ref dayHeader, dst);
        return ref mbp;
    }

    internal void Navigate(FileStream fileStream, ref TickHistoryHeader dayHeader, Timestamp begin)
    {
        do
        {
            fileStream.Position = dayHeader.PositionOfTomorrow;
            dayHeader = ReadHeader(fileStream);
        }
        while (dayHeader.ExchangeTimestamp < begin && dayHeader.PositionOfTomorrow >= 0);
    }

    internal int MoveNext(FileStream fileStream, ref LimitedStream limitedStream, ref DecompressionStream decompressionStream, ref TickHistoryHeader today, Span<byte> dst, out TickType tickType)
    {
        if (dst.Length < 64)
            throw new ArgumentException("dst must be at least 64 bytes");

        int sizeOfTickHeader = Unsafe.SizeOf<TickHeader>();
        int bytesRead = ReadExact(decompressionStream, dst.Slice(0, sizeOfTickHeader));
        while (bytesRead == 0)
        {
            // Chunk exhausted — advance to the next day's chunk.
            limitedStream.Dispose();
            decompressionStream.Dispose();
            fileStream.Position = today.PositionOfTomorrow;
            today = ReadHeader(fileStream);
            if (today.Length <= 0) // end of stream
            {
                tickType = default;
                return 0;
            }
            limitedStream = new LimitedStream(fileStream, today.Length);
            decompressionStream = new DecompressionStream(limitedStream, Decompressor);
            bytesRead = ReadExact(decompressionStream, dst.Slice(0, sizeOfTickHeader));
        }

        ref TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(dst);
        tickType = tickHeader.TickType;

        if (tickHeader.TickType == TickType.Trade || tickHeader.TickType == TickType.Settlement)
        {
            int sizeOfTick = Unsafe.SizeOf<Tick>();
            ReadExact(decompressionStream, dst.Slice(sizeOfTickHeader, sizeOfTick - sizeOfTickHeader));
            return sizeOfTick;
        }
        else if (tickHeader.TickType == TickType.MarketByPriceUpdate || tickHeader.TickType == TickType.MarketByPriceSnapshot)
        {
            int sizeOfMbp = Unsafe.SizeOf<MarketByPrice>();
            ReadExact(decompressionStream, dst.Slice(sizeOfTickHeader, sizeOfMbp - sizeOfTickHeader));
            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
            return mbp.SizeOf();
        }
        else
        {
            throw new NotSupportedException($"MoveNext does not support TickType {tickHeader.TickType}");
        }
    }

    internal static TickHistoryHeader ReadHeader(FileStream fileStream)
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<TickHistoryHeader>()];
        fileStream.ReadExactly(buffer);
        return MemoryMarshal.Read<TickHistoryHeader>(buffer);
    }

    internal static int ReadExact(DecompressionStream decompressionStream, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int got = decompressionStream.Read(dst.Slice(total));
            if (got == 0)
                break;
            total += got;
        }
        return total;
    }

    public TickHistoryHeader Footer
    {
        get
        {
            FileInfo fileInfo = new FileInfo(FilePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                return default;
            using FileStream fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64, FileOptions.SequentialScan);
            TickHistoryHeader footer = default;
            Navigate(fileStream, ref footer, Timestamp.MaxValue);
            return footer;
        }
    }
}

public class TickHistoryReader : IDisposable
{
    private bool _isDisposed;
    public TickHistory TickHistory { get; }
    private FileStream _fileStream;
    private LimitedStream _limitedStream;
    private DecompressionStream _decompressionStream;
    public Timestamp Begin { get; }
    public Timestamp End { get; }

    private TickHistoryHeader _today = default;

    public TickHistoryReader(TickHistory tickHistory, Timestamp? begin = null, Timestamp? end = null)
    {
        TickHistory = tickHistory;
        Begin = begin ?? Timestamp.MinValue;
        End = end ?? Timestamp.MaxValue;
        _fileStream = new FileStream(TickHistory.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        TickHistory.Navigate(_fileStream, ref _today, Begin);
        _limitedStream = new LimitedStream(_fileStream, _today.Length);
        _decompressionStream = new DecompressionStream(_limitedStream, TickHistory.Decompressor);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _limitedStream?.Dispose();
        _fileStream?.Dispose();
        _decompressionStream?.Dispose();
    }

    public int MoveNext(Span<byte> dst, out TickType tickType)
    {
        if (_today.ExchangeTimestamp >= End || _today.Length == 0)
        {
            tickType = default;
            return 0;
        }

        int bytes = TickHistory.MoveNext(_fileStream, ref _limitedStream, ref _decompressionStream, ref _today, dst, out tickType);

        if (_today.ExchangeTimestamp >= End)
            return 0;

        return bytes;
    }

    public ref Tick ReadTick(Span<byte> dst) => ref TickHistory.ReadTick(ref _today, dst);
    public ref MarketByPrice ReadMarketByPrice(Span<byte> dst) => ref TickHistory.ReadMarketByPrice(_decompressionStream, ref _today, dst);
}
