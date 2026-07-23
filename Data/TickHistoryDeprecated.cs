/*
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tools;
using ZstdSharp;


namespace Data;

[RegisterJson]
public enum Format : byte
{
    zstd = 0,
    csv = 1,
}

[RegisterJson]
public enum Frequency : int
{
    Tick = 0,
    MS = 1,
    MS10 = 10,
    MS100 = 100,
    Second = 1_000,
    Minute = 60_000,
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
[RegisterJson]
public struct TickHistoryHeader
{
    public long Position;
    public long PositionOfTomorrow;
    public long PositionOfYesterday;
    public long Length => Math.Max(0, PositionOfTomorrow - Position - Unsafe.SizeOf<TickHistoryHeader>());

    public Timestamp ExchangeTimestamp;
    public long Version;
    public int Ticks;
    public int Quantity;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[RegisterJson]
public class TickHistorySearch
{
    public InstrumentType? InstrumentType { get; set; }
    public string? Exchange { get; set; }
    public string? Root { get; set; }
    public string? Ticker { get; set; }
    public Frequency? Frequency { get; set; }
    public Format? Format { get; set; }
    public TickType? TickType { get; set; }

    public FileSystemPath DirectoryPath { get; set; } = @"Z:\TickHistory";


    public static ArrayList<TickHistory> Search(TickHistorySearch search)
    {
        if (search is null)
            throw new ArgumentNullException(nameof(search));

        Console.WriteLine($"TickHistorySearch::Search({Json.SerializeToLine(search)})");


        ArrayList<TickHistory> results = new ArrayList<TickHistory>(16);

        if (string.IsNullOrWhiteSpace(search.DirectoryPath) || !Directory.Exists(search.DirectoryPath))
            return results;


        // Bound concurrency: IO-bound, so > CPU count helps, but cap to avoid storage saturation
        ParallelOptions parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(16, LowLatency.HouseKeepingCores.Length)
        };

        string[] filePaths = Directory.GetFiles(search.DirectoryPath, "*.zstd");

        object gate = new object();

        // Thread-local aggregation → no shared writes inside the hot loop
        Parallel.ForEach<string, ArrayList<TickHistory>>(filePaths, parallelOptions, () => new ArrayList<TickHistory>(64), (string filePath, ParallelLoopState loopState, ArrayList<TickHistory> local) =>
        {
            TickHistory tickHistory;
            try
            {
                tickHistory = TickHistory.FromFilePath(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TickHistorySearch.Search() Failed to parse: {filePath}");
                Console.WriteLine(ex.ToString());
                throw;
            }

            // --- Apply filters (only when specified) ---
            // InstrumentType
            if (search.InstrumentType.HasValue && tickHistory.Symbology.InstrumentType != search.InstrumentType.Value)
                return local;

            if (search.TickType.HasValue && tickHistory.TickType != search.TickType.Value)
                return local;

            // Exchange (case-insensitive)
            if (!string.IsNullOrWhiteSpace(search.Ticker) &&
                !tickHistory.Symbology.Ticker.Equals(search.Ticker, StringComparison.OrdinalIgnoreCase))
                return local;

            // Exchange (case-insensitive)
            if (!string.IsNullOrWhiteSpace(search.Exchange) &&
                !tickHistory.Symbology.Exchange.Equals(search.Exchange, StringComparison.OrdinalIgnoreCase))
                return local;

            // RootSymbol (maps to Symbology.Root) (case-insensitive)
            if (!string.IsNullOrWhiteSpace(search.Root) &&
                !tickHistory.Symbology.Root.Equals(search.Root, StringComparison.OrdinalIgnoreCase))
                return local;

            // Frequency
            if (search.Frequency.HasValue && tickHistory.Frequency != search.Frequency.Value)
                return local;

            // Format
            if (search.Format.HasValue && tickHistory.Format != search.Format.Value)
                return local;

            local.Add(tickHistory);

            return local;
        },
        (ArrayList<TickHistory> local) =>
        {
            if (local.Count == 0) return;
            lock (gate)
            {
                foreach (TickHistory tickHistory in local)
                    results.Add(tickHistory);
            }
        });

        results.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));

        return results;
    }
}

// Data layout:
// [ TickHistoryHeader | Zstd Data | TickHistoryHeader ... ]
// All writes end with a write of TickHistoryHeader for tomorrow. Therefore GetLastTime stamp is a matter of reading the lastheader.Timestmap
public class TickHistoryWriter : IDisposable
{
    private readonly FastArrayPool<byte> _byteArrayPool = new FastArrayPool<byte>();

    private MarketByPrice64 _mpb64 = new MarketByPrice64();

    private bool _isDisposed = false;
    public override string ToString() => $"TickHistoryWriter {TickHistory.FilePath}";
    private readonly FileStream _fileStream;
    private CompressionStream _compressionStream = null!;
    public TickHistory TickHistory { get; }

    private TickHistoryHeader _tomorrow = default;
    private TickHistoryHeader _today = default;
    public long Count { get; private set; } = 0;

    public TickHistoryWriter(TickHistory tickHistory)
    {
        TickHistory = tickHistory;
        _ = tickHistory.Compressor; // init compressor
        Application.AddExitAction($"Dispose {this}", Dispose);
        Directory.CreateDirectory(Path.GetDirectoryName(tickHistory.FilePath)!);
        _fileStream = new FileStream(tickHistory.FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.SequentialScan);
        SetHeaders();
    }
    // Everytime a chunk
    private void SetHeaders()
    {
        if (_fileStream.Length > 0)
        {
            TickHistory.Navigate(_fileStream, ref _tomorrow, Timestamp.MaxValue);
            if (TickHistory.TickType == TickType.MarketByPrice)
            {
                LimitedStream limitedStream = new LimitedStream(_fileStream, _fileStream.Length - _fileStream.Position);
                DecompressionStream decompressionStream = new DecompressionStream(limitedStream, TickHistory.Decompressor);
                Span<byte> header = stackalloc byte[64];
                int bytesNeeded = TickHistory.MoveNext(_fileStream, ref limitedStream, ref decompressionStream, ref _tomorrow, header, out TickType tickType);
                Span<byte> dst = stackalloc byte[bytesNeeded];
                header[..Math.Min(header.Length, dst.Length)].CopyTo(dst);
                ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
                TickHistory.ReadExact(decompressionStream, dst.Slice(Unsafe.SizeOf<MarketByPrice>(), mbp.SizeOfLevels()));
                TickHistoryHeader tomorrowCopy = _tomorrow;
                TickHistory.RestoreMarketByPrice(ref tomorrowCopy, dst);
                _mpb64.TrySet(dst);
                limitedStream.Dispose();
                decompressionStream.Dispose();
            }
            _fileStream.Position = _tomorrow.PositionOfYesterday;
            _today = TickHistory.ReadHeader(_fileStream);
            _fileStream.Position = _tomorrow.Position;
            _compressionStream = new CompressionStream(_fileStream, TickHistory.Compressor);
        }
    }
    // Remember we always write the current Timestamp to Tomorrow not Today! so that when tomorrow is written it has the lastTimestmap
    public void WriteTick(in Tick tick)
    {
        Tick copy = tick;
        EnsureHeader(copy.TickHeader.ExchangeTimestamp);
        switch (copy.TickHeader.TickType)
        {
            case TickType.Trade:
                TickHistory.DeltaTrade(ref _tomorrow, ref Unsafe.As<Tick, Trade>(ref copy));
                break;
            case TickType.Settlement:
                TickHistory.DeltaSettlement(ref _tomorrow, ref Unsafe.As<Tick, Settlement>(ref copy));
                break;
            default:
                throw new NotSupportedException($"WriteTick does not support TickType {copy.TickHeader.TickType}");
        }
        _compressionStream.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)));
        Count++;
    }

    // [ MarketByPrice | bids[] | asks[] ] must be in this format
    public void WriteMarketByPrice(Span<byte> bytes)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(bytes);

        Span<byte> copy = stackalloc byte[mbp.SizeOf()];
        bytes.Slice(0, copy.Length).CopyTo(copy);

        EnsureHeader(mbp.TickHeader.ExchangeTimestamp);

        if (!_mpb64.TrySet(copy))
            return;

        TickHistory.DeltaMarketByPrice(ref _tomorrow, copy);
        _compressionStream.Write(copy);
        Count++;
    }

    private void WriteSnapshot(Timestamp timestamp)
    {
        if (TickHistory.TickType != TickType.MarketByPrice)
            return;

        byte[] bytes = _byteArrayPool.Rent(MarketByPrice.SizeOf(_mpb64.BidsCount, _mpb64.AsksCount));
        _mpb64.CopyToSnapshot(0, bytes);
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(bytes.AsSpan());

        Span<byte> src = bytes.AsSpan(0, mbp.SizeOf());
        mbp.TickHeader.ExchangeTimestamp = timestamp;
        mbp.TickHeader.NicTimestamp = timestamp;
        WriteMarketByPrice(src);
        _byteArrayPool.Return(bytes);
    }

    private void EnsureHeader(Timestamp timestamp)
    {
        if (_fileStream.Position == 0)
        {
            _today = new TickHistoryHeader
            {
                Position = 0,
                PositionOfYesterday = -1,
                Ticks = 0,
                Quantity = 0,
                ExchangeTimestamp = timestamp.Date,
                Version = 1,
                PositionOfTomorrow = -1,
            };
            TickHistory.WriteHeader(_fileStream, _today);
            _tomorrow = _today;
            _tomorrow.PositionOfYesterday = _today.Position;
            _tomorrow.Position = -1;
            _compressionStream = new CompressionStream(_fileStream, TickHistory.Compressor);
        }
        else if (timestamp.Date > _tomorrow.ExchangeTimestamp.Date)
        {
            WriteTomorrowHeader(timestamp.Date);
        }
    }

    private void WriteTomorrowHeader(Timestamp date)
    {
        // finalize yesterday block
        _compressionStream.Flush();
        _compressionStream.Dispose();

        _today.PositionOfTomorrow = _fileStream.Position;
        long fsPosition = _fileStream.Position;
        TickHistory.WriteHeader(_fileStream, in _today);
        _fileStream.Position = fsPosition;

        _tomorrow.ExchangeTimestamp = date;
        _tomorrow.Position = _fileStream.Position;
        _tomorrow.PositionOfYesterday = _today.Position;
        _tomorrow.PositionOfTomorrow = -1;
        TickHistory.WriteHeader(_fileStream, in _tomorrow);

        _today = _tomorrow;

        _compressionStream = new CompressionStream(_fileStream, TickHistory.Compressor);
        WriteSnapshot(date);
    }

    private object _lock = new object();
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                bool deleteFile = _fileStream.Length == 0;

                if (Count > 0)
                    WriteTomorrowHeader(_tomorrow.ExchangeTimestamp);

                _compressionStream?.Flush();
                _compressionStream?.Dispose();
                _fileStream?.Flush();
                _fileStream?.Dispose();

                if (deleteFile)
                    File.Delete(TickHistory.FilePath);
                else
                    File.SetCreationTimeUtc(TickHistory.FilePath, _tomorrow.ExchangeTimestamp.ToDateTime);
            }
        }
    }
}


public class TickHistoryReader : IDisposable
{
    private bool _isDisposed = false;
    public override string ToString() => $"TickHistoryReader {TickHistory.FilePath} {Begin} - {End}";
    public TickHistory TickHistory { get; }
    private FileStream _fileStream;
    private LimitedStream _limitedStream;

    private DecompressionStream _decompressionStream;
    public Timestamp Begin { get; }
    public Timestamp End { get; }

    private bool _endOfStream;
    public bool EndOfStream
    {
        get => _endOfStream;
        set => _endOfStream = value;
    }

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

    private object _lock = new object();
    public void Dispose()
    {
        lock(_lock)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                EndOfStream = true;
                _limitedStream?.Dispose();
                _fileStream?.Dispose();
                _decompressionStream?.Dispose();
            }
        }
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


// File Format
// [TickHistoryHeader][Compressed Data][TickHistoryHeader][Compressed Data][TickHistoryHeader][Compressed Data]...[TickHistoryHeader]
// At the end of writing 

[RegisterJson]
public class TickHistory
{
    public static readonly int SizeOfHeader = Unsafe.SizeOf<TickHistoryHeader>();
    private Decompressor? _decompressor = null;
    public Decompressor Decompressor
    {
        get
        {
            if (Dictionary == null)
                InitCompressors(DictionaryPath);
            return _decompressor!;
        }
    }

    private Compressor? _compressor = null;
    public Compressor Compressor
    {
        get
        {
            if (Dictionary == null)
                InitCompressors(DictionaryPath);
            return _compressor!;
        }
        
    }
    private byte[]? Dictionary { get; set; } = null;

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

    public void Dispose()
    {
        Compressor?.Dispose();
        Decompressor?.Dispose();
    }

    public static TickHistory FromFilePath(FileSystemPath filePath)
    {
        string directoryPath = Path.GetDirectoryName(filePath)!;
        string fileName = Path.GetFileName(filePath);
        string[] tokens = fileName.Split('.');

        if (tokens.Length < 4)
            throw new ArgumentException("Filename must be in the form \"{Symbology}.{TickType}.{Frequency}.{Format}[.ext]\".", nameof(filePath));

        Symbology symbology = Symbology.FromString(tokens[0]);
        TickType tickType = Enum.Parse<TickType>(tokens[1], true);
        Frequency frequency = Enum.Parse<Frequency>(tokens[2], true);
        Format format = Enum.Parse<Format>(tokens[3], true);

        TickHistory tickHistory = new TickHistory(symbology, tickType, frequency, format)
        {
            DirectoryPath = directoryPath,
        };

        return tickHistory;
    }

    private void InitCompressors(string filePath)
    {
        if (Dictionary == null)
        {
            Dictionary = File.ReadAllBytes(filePath);
            _decompressor = new Decompressor();
            _decompressor.LoadDictionary(Dictionary);
            _compressor = new Compressor();
            _compressor.LoadDictionary(Dictionary);
        }
    }

    internal static void RestoreTickHeader(ref TickHistoryHeader day, ref TickHeader tick)
    {
        tick.ExchangeTimestamp = new Timestamp(day.ExchangeTimestamp.NanosSinceEpoch + tick.ExchangeTimestamp.NanosSinceEpoch);
        tick.NicTimestamp = new Timestamp(tick.ExchangeTimestamp.NanosSinceEpoch + tick.NicTimestamp.NanosSinceEpoch);
        day.ExchangeTimestamp = tick.ExchangeTimestamp;
    }

    internal static void DeltaHeader(ref TickHistoryHeader day, ref TickHeader tick)
    {
        tick.NicTimestamp = tick.NicTimestamp.Max(tick.ExchangeTimestamp);

        Timestamp exchangeTimestamp = new Timestamp(tick.ExchangeTimestamp.NanosSinceEpoch - day.ExchangeTimestamp.NanosSinceEpoch);
        Timestamp nicTimestamp = new Timestamp(tick.NicTimestamp.NanosSinceEpoch - tick.ExchangeTimestamp.NanosSinceEpoch);

        day.ExchangeTimestamp = tick.ExchangeTimestamp;

        tick.ExchangeTimestamp = exchangeTimestamp;
        tick.NicTimestamp = nicTimestamp;
    }

    internal static void RestoreLevel(ref TickHistoryHeader tickHistoryHeader, ref Level level)
    {
        level.Ticks = level.Ticks + tickHistoryHeader.Ticks;
        level.Quantity = level.Quantity + tickHistoryHeader.Quantity;
        tickHistoryHeader.Ticks = level.Ticks;
        tickHistoryHeader.Quantity = level.Quantity;
    }

    internal static void DeltaLevel(ref TickHistoryHeader tickHistoryHeader, ref Level level)
    {
        // ✅ compute delta *before* advancing the running baseline
        int priceDelta = level.Ticks - tickHistoryHeader.Ticks;
        int qtyDelta = level.Quantity - tickHistoryHeader.Quantity;

        tickHistoryHeader.Ticks = level.Ticks;
        tickHistoryHeader.Quantity = level.Quantity;

        level.Ticks = priceDelta;
        level.Quantity = qtyDelta;
    }

    internal static void RestoreTrade(ref TickHistoryHeader tickHistoryHeader, ref Trade trade)
    {
        RestoreTickHeader(ref tickHistoryHeader, ref trade.TickHeader);
        RestoreLevel(ref tickHistoryHeader, ref trade.Level);
    }

    internal static void DeltaTrade(ref TickHistoryHeader tickHistoryHeader, ref Trade trade)
    {
        DeltaHeader(ref tickHistoryHeader, ref trade.TickHeader);
        DeltaLevel(ref tickHistoryHeader, ref trade.Level);
    }

    internal static void RestoreSettlement(ref TickHistoryHeader tickHistoryHeader, ref Settlement settlement)
    {
        RestoreTickHeader(ref tickHistoryHeader, ref settlement.TickHeader);
    }

    internal static void DeltaSettlement(ref TickHistoryHeader tickHistoryHeader, ref Settlement settlement)
    {
        DeltaHeader(ref tickHistoryHeader, ref settlement.TickHeader);
    }

    internal static void DeltaMarketByPrice(ref TickHistoryHeader tickHistoryHeader, Span<byte> bytes)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(bytes);

        // First, write the header deltas
        DeltaHeader(ref tickHistoryHeader, ref mbp.TickHeader);

        {
            // Bids
            Span<Level> bids = mbp.BidsAsSpan(bytes);
            for (int i = 0; i < bids.Length; i++)
            {
                DeltaLevel(ref tickHistoryHeader, ref bids[i]);
            }
        }

        {
            // Asks come after all bids
            Span<Level> asks = mbp.AsksAsSpan(bytes);
            for (int i = 0; i < asks.Length; i++)
            {
                DeltaLevel(ref tickHistoryHeader, ref asks[i]);
            }
        }
    }

    internal ref Tick ReadTick(ref TickHistoryHeader dayHeader, Span<byte> dst)
    {
        ref Tick tick = ref MemoryMarshal.AsRef<Tick>(dst);
        if (tick.TickHeader.TickType == TickType.Trade)
        {
            ref Trade trade = ref MemoryMarshal.AsRef<Trade>(dst);
            RestoreTrade(ref dayHeader, ref trade);
        }
        else if (tick.TickHeader.TickType == TickType.Settlement)
        {
            ref Settlement settlement = ref MemoryMarshal.AsRef<Settlement>(dst);
            RestoreSettlement(ref dayHeader, ref settlement);
        }
        else
        {
            throw new NotSupportedException($"ReadTick does not support TickType {tick.TickHeader.TickType}");
        }
        return ref tick;
    }
    internal ref MarketByPrice ReadMarketByPrice(DecompressionStream decompressionStream, ref TickHistoryHeader dayHeader, Span<byte> dst)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
        TickHistory.ReadExact(decompressionStream, dst.Slice(Unsafe.SizeOf<MarketByPrice>(), mbp.SizeOfLevels()));
        TickHistory.RestoreMarketByPrice(ref dayHeader, dst);
        return ref mbp;
    }

    internal static void RestoreMarketByPrice(ref TickHistoryHeader tickHistoryHeader, Span<byte> bytes)
    {
        ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(bytes);

        // First, expand the header deltas back to absolute timestamps
        RestoreTickHeader(ref tickHistoryHeader, ref mbp.TickHeader);

        Span<Level> bidsSpan = mbp.BidsAsSpan(bytes);
        for (int i = 0; i < bidsSpan.Length; i++)
        {
            RestoreLevel(ref tickHistoryHeader, ref bidsSpan[i]);
        }

        Span<Level> asksSpan = mbp.AsksAsSpan(bytes);
        for (int i = 0; i < asksSpan.Length; i++)
        {
            RestoreLevel(ref tickHistoryHeader, ref asksSpan[i]);
        }
    }

    internal void Navigate(FileStream fileStream, ref TickHistoryHeader dayHeader, Timestamp begin)
    {
        do
        {
            fileStream.Position = dayHeader.PositionOfTomorrow;
            dayHeader = ReadHeader(fileStream);
        }
        while (dayHeader.ExchangeTimestamp < begin && dayHeader.PositionOfTomorrow >= 0);

        //if (dayHeader.ExchangeTimestamp > begin && dayHeader.PositionOfYesterday >= 0)
        //{
         //   fileStream.Position = dayHeader.PositionOfYesterday;
        //    dayHeader = ReadHeader(fileStream);
        //}
    }

    internal int MoveNext(FileStream fileStream, ref LimitedStream limitedStream, ref DecompressionStream decompressionStream, ref TickHistoryHeader today, Span<byte> dst, out TickType tickType)
    {
        if (dst.Length < 64)
            throw new ArgumentException("dst must be at least 64 bytes");

        // read the next tickheader
        int sizeOfTickHeader = Unsafe.SizeOf<TickHeader>();
        int bytesRead = ReadExact(decompressionStream, dst.Slice(0, sizeOfTickHeader));
        while (bytesRead == 0)
        {
            limitedStream.Dispose();
            decompressionStream.Dispose();
            fileStream.Position = today.PositionOfTomorrow;
            today = ReadHeader(fileStream);
            //Console.WriteLine(Json.Serialize(today));
            if (today.Length <= 0) // EndOfStream
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
        // Trade, Settlement, and any other Tick-shaped (Size=64) types share one decode path.
        if (tickHeader.TickType == TickType.Trade || tickHeader.TickType == TickType.Settlement)
        {
            int sizeOfTick = Unsafe.SizeOf<Tick>();
            bytesRead = ReadExact(decompressionStream, dst.Slice(sizeOfTickHeader, sizeOfTick - sizeOfTickHeader));
            return sizeOfTick;
        }
        else if (tickHeader.TickType == TickType.MarketByPriceUpdate || tickHeader.TickType == TickType.MarketByPriceSnapshot)
        {
            int sizeOfMarketByPrice = Unsafe.SizeOf<MarketByPrice>();
            bytesRead = ReadExact(decompressionStream, dst.Slice(sizeOfTickHeader, sizeOfMarketByPrice - sizeOfTickHeader));
            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
            return mbp.SizeOf();
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    internal static TickHistoryHeader ReadHeader(FileStream fileStream)
    {
        int size = Unsafe.SizeOf<TickHistoryHeader>();
        Span<byte> buffer = stackalloc byte[size];
        fileStream.ReadExactly(buffer);
        TickHistoryHeader header = MemoryMarshal.Read<TickHistoryHeader>(buffer);
        return header;
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

    public void Delist(Timestamp delisted)
    {
        FileInfo fileInfo = new FileInfo(FilePath);
        FileAttributes fileAttributes = (fileInfo.Exists ? File.GetAttributes(FilePath) : FileAttributes.None) & FileAttributes.ReadOnly;
        if (fileAttributes == FileAttributes.ReadOnly)
            return;

        using FileStream fileStream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, SizeOfHeader, FileOptions.RandomAccess);
        TickHistoryHeader footer = default;
        
        if (fileStream.Length > 0)
            Navigate(fileStream, ref footer, Timestamp.MaxValue);

        delisted = footer.ExchangeTimestamp.RoundUpDay().Max(delisted);
        footer.ExchangeTimestamp = delisted;
        WriteHeader(fileStream, footer);
        fileStream.SetLength(fileStream.Position);
        File.SetCreationTimeUtc(FilePath, footer.ExchangeTimestamp.ToDateTime);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
    }

    internal void WriteHeader(FileStream fileStream, in TickHistoryHeader header)
    {
        fileStream.Position = header.Position;
        fileStream.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));
    }

    public bool IsDelisted
    {
        get
        {
            FileInfo fileInfo = new FileInfo(FilePath);
            return fileInfo.Exists && fileInfo.IsReadOnly;
        }
    }

    public TickHistoryHeader Footer
    {
        get
        {
            FileInfo fileInfo = new FileInfo(FilePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                return default;
            using FileStream fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, SizeOfHeader, FileOptions.SequentialScan);
            TickHistoryHeader footer = default;
            Navigate(fileStream, ref footer, Timestamp.MaxValue);
            return footer;
        }
    }
    

    public static byte[] ToDict(System.Collections.Generic.IEnumerable<byte[]> samples)
    {
        long bytes = 0;
        // 1) Transform + materialize
        System.Collections.Generic.List<byte[]> prepared = new System.Collections.Generic.List<byte[]>();
        TickHistoryHeader header = default;
        foreach (byte[] sample in samples)
        {
            if (sample == null || sample.Length == 0) continue;

            byte[] copy = (byte[])sample.Clone();
            DeltaMarketByPrice(ref header, copy);   // must not change copy.Length
            if (copy.Length > 0)
            {
                prepared.Add(copy);
                bytes += copy.Length;
            }
        }

        int dictLength = 256_000;

        if (bytes < dictLength * 100)
            throw new ArgumentException($"Not enough sample data. Need at least {dictLength * 100} bytes, got {bytes} bytes.");

        // 2) Higher-quality FastCover params for your use case
        ZstdSharp.Unsafe.ZDICT_fastCover_params_t p = new ZstdSharp.Unsafe.ZDICT_fastCover_params_t
        {
            d = 8,                 // fine segmenting for binary/tick data
            f = 20,                // 2^20 freq table
            steps = 8,             // more refinement passes
            k = 4000,              // segment size
            nbThreads = (uint)System.Environment.ProcessorCount,
        };

        // 3) Train
        return ZstdSharp.DictBuilder.TrainFromBufferFastCover(prepared, p, dictLength).ToArray();
    }

}

public sealed class LimitedStream(Stream s, long length, bool leaveOpen = true) : Stream
{
    private readonly Stream _s = s ?? throw new ArgumentNullException(nameof(s));
    private readonly bool _leaveOpen = leaveOpen;
    private readonly long _start = s.CanSeek ? s.Position : throw new NotSupportedException("Base must be seekable.");
    private readonly long _len = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));
    private long _pos;

    public override bool CanRead => _s.CanRead;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _len;
    public override long Position { get => _pos; set { if (value < 0 || value > _len) throw new ArgumentOutOfRangeException(); _pos = value; _s.Position = _start + _pos; } }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= _len) return 0;
        int toRead = (int)Math.Min((long)count, _len - _pos);
        _s.Position = _start + _pos;
        int n = _s.Read(buffer, offset, toRead);
        _pos += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _len) return 0;
        int toRead = (int)Math.Min((long)buffer.Length, _len - _pos);
        _s.Position = _start + _pos;
        int n = _s.Read(buffer[..toRead]);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long t = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, SeekOrigin.End => _len + offset, _ => throw new ArgumentOutOfRangeException() };
        return Position = t;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) { if (disposing && !_leaveOpen) _s.Dispose(); base.Dispose(disposing); }
}
*/