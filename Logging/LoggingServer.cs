//BEGIN_FILE HFT/Logging/LoggingServer.cs
using Data;
using Execution;
using Socket;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Tools;
using Strategy;
using Provider;
using System.Text.Json;
using System.Linq;
using System;
using System.Runtime.CompilerServices;

namespace Logging;

public readonly struct PooledBuffer
{
    public readonly byte[] Array;
    public readonly int Length;

    public PooledBuffer(byte[] array, int length)
    {
        Array = array;
        Length = length;
    }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            return new ReadOnlySpan<byte>(Array, 0, Length);
        }
    }
}

public enum ReaderStatus
{
    Sleeping,
    Queued,
    Busy,
}

public interface IReader : IEquatable<IReader>, IDisposable
{
    ReaderStatus Status { get; set; }
    bool IsDisposed { get; }
    string Name { get; }
    IObjectWriter Writer { get; }
    ReadStatus GetReadStatus();
    ReadStatus TryRead(List<PooledBuffer> results);
    bool IsPendingDispose { get; set; }
}

public class ClientSocketReader : IReader
{
    public ref readonly SocketHeader SocketHeader => ref _socketListener.SocketHeader;
    public ReaderStatus Status { get; set; } = ReaderStatus.Sleeping;
    public readonly SocketListener _socketListener;
    private readonly string _name;
    private readonly IObjectWriter _writer;
    private volatile bool _isPendingDispose;

    public bool IsDisposed => _socketListener.IsDisposed;
    public ClientSocketReader(SocketListener socketListener, string name, IObjectWriter writer)
    {
        _socketListener = socketListener;
        _name = name;
        _writer = writer;
    }

    public string Name => _name;
    public IObjectWriter Writer => _writer;

    public bool IsPendingDispose
    {
        get => _isPendingDispose;
        set => _isPendingDispose = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadStatus Min(ReadStatus left, ReadStatus right)
    {
        return (ReadStatus)Math.Min((byte)left, (byte)right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetReadStatus()
    {
        // Channel 0 = admin; channels 1..N = per-CoreGroupId execution. Any channel with New means work.
        ReadStatus readStatus = ReadStatus.Closed;
        for (int channel = 0; channel < _socketListener.ClientToServerChannelCount; channel++)
        {
            readStatus = Min(_socketListener.GetClientToServerReadStatus(channel), readStatus);
            if (readStatus == ReadStatus.New)
                return readStatus;
        }
        for (int channel = 0; channel < _socketListener.ServerToClientChannelCount; channel++)
        {
            readStatus = Min(_socketListener.GetServerToClientReadStatus(channel), readStatus);
            if (readStatus == ReadStatus.New)
                return readStatus;
        }

        // Rings are empty. If we're still holding a tail and the source has gone idle for >= the margin,
        // signal New so the poller schedules a drain that flushes it (the "11th record never arrives" case).
        // Count check first so the clock read is skipped for idle readers with nothing held.
        if (_waterMarkBuffer.Count > 0 && (Timestamp.UtcNow - _lastDataTime) >= WatermarkMargin)
            return ReadStatus.New;

        return readStatus;
    }


    protected static Timestamp GetCreationTimestamp(ReadOnlySpan<byte> rsrc, Timestamp @default)
    {
        rsrc = rsrc.Slice(Unsafe.SizeOf<Header<OrderType>>());
        ref readonly OrderHeader orderHeader = ref MemoryMarshal.AsRef<OrderHeader>(rsrc);
        Timestamp exchangeTimestamp = orderHeader.ExchangeTimestamp == Timestamp.MinValue ? orderHeader.NicTimestamp : orderHeader.ExchangeTimestamp;
        Timestamp nicTimestamp = orderHeader.NicTimestamp == Timestamp.MinValue ? orderHeader.ExchangeTimestamp : orderHeader.NicTimestamp;
        Timestamp creationTimestamp = exchangeTimestamp.Min(nicTimestamp);
        creationTimestamp = creationTimestamp == Timestamp.MinValue ? @default : creationTimestamp;
        return creationTimestamp;
    }

    List<PooledBuffer> _waterMarkBuffer = new List<PooledBuffer>();
    Timestamp _maxSeen = Timestamp.MinValue;   // event-time high-water mark, persisted across passes
    Timestamp _lastDataTime = Timestamp.UtcNow; // wall-clock of the last real read; drives the idle-flush of the held tail

    // Order is guaranteed only while no active channel's reads lag the leading edge by more than this.
    // It must exceed the worst-case per-channel read lag (poll interval + scheduling/GC/visibility jitter),
    // NOT just the data period — a margin == period leaves no slack and lets rare 1-round lags slip through.
    static readonly Duration WatermarkMargin = Duration.FromMilliseconds(10);

    public ReadStatus TryRead(List<PooledBuffer> results)
    {
        Timestamp @default = _maxSeen;
        ReadStatus overallStatus = ReadStatus.Empty;
        ReadOnlySpan<byte> rsrc;
        ReadStatus readStatus;

        // Records held above last pass's watermark ride forward; admin (no timestamp) stays causal-first.
        List<PooledBuffer> executions = _waterMarkBuffer;
        _waterMarkBuffer = new List<PooledBuffer>();
        List<PooledBuffer> serverToClientAdmin = new List<PooledBuffer>();
        List<PooledBuffer> clientToServerAdmin = new List<PooledBuffer>();

        // Drain every channel once. Admin (channel 0) -> its own list; executions -> the buffer, advancing
        // maxSeen by each record's timestamp (the event-time "now", so this works live and in backtest).
        for (int channel = 0; channel < _socketListener.ServerToClientChannelCount; channel++)
        {
            bool isExec = channel != SocketChannel.Admin;
            List<PooledBuffer> dst = isExec ? executions : serverToClientAdmin;
            while ((readStatus = _socketListener.TryReadServerToClient(channel, out rsrc)) == ReadStatus.New)
            {
                if (isExec) _maxSeen = Timestamp.Max(GetCreationTimestamp(rsrc, @default), _maxSeen);
                byte[] buffer = ThreadArrayPool<byte>.Rent(rsrc.Length);
                rsrc.CopyTo(buffer);
                dst.Add(new PooledBuffer(buffer, rsrc.Length));
                overallStatus = ReadStatus.New;
            }
            overallStatus = Min(readStatus, overallStatus);
        }
        for (int channel = 0; channel < _socketListener.ClientToServerChannelCount; channel++)
        {
            bool isExec = channel != SocketChannel.Admin;
            List<PooledBuffer> dst = isExec ? executions : clientToServerAdmin;
            while ((readStatus = _socketListener.TryReadClientToServer(channel, out rsrc)) == ReadStatus.New)
            {
                if (isExec) _maxSeen = Timestamp.Max(GetCreationTimestamp(rsrc, @default), _maxSeen);
                byte[] buffer = ThreadArrayPool<byte>.Rent(rsrc.Length);
                rsrc.CopyTo(buffer);
                dst.Add(new PooledBuffer(buffer, rsrc.Length));
                overallStatus = ReadStatus.New;
            }
            overallStatus = Min(readStatus, overallStatus);
        }

        // Release everything older than (maxSeen - margin) in timestamp order; hold the newer records for
        // next pass (a lagging channel could still emit something earlier). Sorted -> the split is one index.
        Timestamp watermark = _maxSeen - WatermarkMargin;
        // OrderBy, not List.Sort: stability is the point — fill/state/position clusters share one
        // timestamp and must keep arrival order.
        executions = executions.OrderBy(pb => GetCreationTimestamp(pb.Span, @default)).ToList();        int hold = executions.FindIndex(pb => GetCreationTimestamp(pb.Span, @default) > watermark);
        if (hold < 0) hold = executions.Count;

        results.AddRange(clientToServerAdmin);
        results.AddRange(serverToClientAdmin);
        results.AddRange(executions.GetRange(0, hold));
        _waterMarkBuffer.AddRange(executions.GetRange(hold, executions.Count - hold));

        if (overallStatus == ReadStatus.New)
        {
            _lastDataTime = Timestamp.UtcNow;   // saw real data -> reset the idle clock (never on the carry-forward)
        }
        else if (_waterMarkBuffer.Count > 0 && (Timestamp.UtcNow - _lastDataTime) >= WatermarkMargin)
        {
            // Source idle >= margin with rings drained: flush the held tail (already sorted) so it isn't
            // stranded until Dispose. Signal New so DrainReader writes it.
            results.AddRange(_waterMarkBuffer);
            _waterMarkBuffer.Clear();
            overallStatus = ReadStatus.New;
        }
        return overallStatus;
    }

    public bool Equals(IReader? other)
    {
        return other is ClientSocketReader lr && _socketListener == lr._socketListener;
    }

    public override int GetHashCode()
    {
        return _socketListener.GetHashCode();
    }

    public void Dispose()
    {
        // Final drain + flush: pull anything still in the rings (one last TryRead), then write it plus
        // everything the watermark is still holding, in timestamp order — nothing is lost on teardown.
        List<PooledBuffer> tail = new List<PooledBuffer>();
        TryRead(tail);                       // drains the channels; releases <= watermark into `tail`
        tail.AddRange(_waterMarkBuffer);     // append the held remainder (already sorted, all newer)
        foreach (PooledBuffer pb in tail)
        {
            Writer.Write(pb.Span);
            ThreadArrayPool<byte>.Return(pb.Array);
        }
        Writer.Flush();
        _waterMarkBuffer.Clear();

        _socketListener.Dispose();
        Writer.Dispose();
    }
}

public class LoggingSocketReader : IReader
{
    public ReaderStatus Status { get; set; } = ReaderStatus.Sleeping;
    public readonly int ClientId;
    public readonly ServerSocket ServerSocket;
    private readonly int _channelCount;
    private readonly string _name;
    private readonly IObjectWriter _writer;
    private volatile bool _isPendingDispose;
    private volatile bool _isDisposed = false;

    public bool IsDisposed => _isDisposed;
    public LoggingSocketReader(ServerSocket serverSocket, int clientId, int channelCount, string name, IObjectWriter writer)
    {
        ServerSocket = serverSocket;
        ClientId = clientId;
        _channelCount = channelCount;
        _name = name;
        _writer = writer;
    }

    public string Name => _name;
    public IObjectWriter Writer => _writer;

    public bool IsPendingDispose
    {
        get => _isPendingDispose;
        set => _isPendingDispose = value;
    }

    public ReadStatus GetReadStatus()
    {
        // A direct socket may be multi-channel (e.g. .audit: channel index == CoreGroupId, 0 = admin).
        // New on any channel means work; otherwise propagate Closed so the poller can drop the client.
        ReadStatus status = ReadStatus.Empty;
        for (int channel = 0; channel < _channelCount; channel++)
        {
            ReadStatus s = ServerSocket.GetReadStatus(ClientId, channel);
            if (s == ReadStatus.New)
                return ReadStatus.New;
            if (s == ReadStatus.Closed)
                status = ReadStatus.Closed;
        }
        return status;
    }

    public ReadStatus TryRead(List<PooledBuffer> results)
    {
        bool hasData = false;
        ReadStatus status = ReadStatus.Empty;

        // Channel 0 = admin audit (drained first so AllocateInstrument symbol mappings precede the
        // fills that reference them); channels 1..N = per-CoreGroup audit.
        for (int channel = 0; channel < _channelCount; channel++)
        {
            while ((status = ServerSocket.TryRead(ClientId, channel, out ReadOnlySpan<byte> src)) == ReadStatus.New)
            {
                byte[] buffer = ThreadArrayPool<byte>.Rent(src.Length);
                src.CopyTo(buffer);
                results.Add(new PooledBuffer(buffer, src.Length));
                hasData = true;
            }
        }

        return hasData ? ReadStatus.New : status;
    }

    public bool Equals(IReader? other)
    {
        return other is LoggingSocketReader cr && ClientId == cr.ClientId && ServerSocket == cr.ServerSocket;
    }

    public override int GetHashCode()
    {
        return ClientId.GetHashCode();
    }

    public void Dispose()
    {
        _isDisposed = true;
        Writer.Dispose();
    }
}

public class LoggingServer : IDisposable
{
    public event Action<Exception>? Exception;
    private readonly int _maxWorkerThreads = Math.Min(Math.Min(LowLatency.HouseKeepingCores.Length, Environment.ProcessorCount), 8);
    private readonly ServerSocket _serverSocket;

    private readonly ConcurrentDictionary<string, IReader> _clients = new ConcurrentDictionary<string, IReader>();
    private readonly ConcurrentDictionary<IReader, byte> _sleeping = new ConcurrentDictionary<IReader, byte>();
    private readonly ConcurrentQueue<IReader> _busy = new ConcurrentQueue<IReader>();

    private readonly SemaphoreSlim _workAvailable = new SemaphoreSlim(0);
    private readonly object _lifecycleLock = new object();

    private Thread? _pollerThread;
    private readonly List<Thread> _workerThreads = new List<Thread>();
    private int _workerThreadCount;
    private volatile bool _disposed;
    private SocketHeader?[] _clientSocketHeaders;
    private readonly int Capacity = 1024;

    public Logger Logger { get; }
    public Telegram Alerts { get; }
    public FileSystemPath Name { get; }

    public LoggingServer(FileSystemPath name)
    {
        if (name.ToString().Contains("realtime", StringComparison.OrdinalIgnoreCase))
            Clock.Mode = ClockMode.Realtime;
        if (name.ToString().Contains("simulation", StringComparison.OrdinalIgnoreCase))
            Clock.Mode = ClockMode.Simulation;

        Name = name;
        Logger = new Logger(Name)
        {
            ToConsole = true,
            ToFile = true,
        };
        Alerts = new Telegram(Telegram.AlertChatId) { Logger = Logger };
        _clientSocketHeaders = new SocketHeader?[Capacity];
        _serverSocket = new ServerSocket(name, Capacity);
        _serverSocket.AllocateClientId = ClientIdAllocator;
        _serverSocket.DeallocateClient = ClientDeallocator;
        _serverSocket.ClientAllocated += OnDirectSubscribed;
        _serverSocket.ClientDeallocated += OnDirectUnsubscribed;
        Application.AddExitAction("Dispose LoggingServer", Dispose);
    }

    
    private int ClientIdAllocator(in SocketHeader socketHeader)
    {
        for (int i = 0; i < _clientSocketHeaders.Length; i++)
        {
            if (_clientSocketHeaders[i]?.ClientName == socketHeader.ClientName)
                return i;
        }
        int clientId = 0;
        while(clientId < _clientSocketHeaders.Length && _clientSocketHeaders[clientId] != null)
        {
            clientId++;
        }
        if (clientId >= _clientSocketHeaders.Length)
            return -1; // No available client ID

        SocketHeader socketHeaderCopy = socketHeader;
        socketHeaderCopy.ClientId = clientId;
        _clientSocketHeaders[socketHeaderCopy.ClientId] = socketHeaderCopy;
        return socketHeaderCopy.ClientId;
    }

    private SocketHeader ClientDeallocator(int clientId)
    {
        SocketHeader socketHeader = _clientSocketHeaders[clientId]!.Value;
        _clientSocketHeaders[clientId] = null;
        return socketHeader;
    }
    

    public void Log(params object[] items)
    {
        Logger.Log(Name, items);
    }

    public void Start()
    {
        _serverSocket.Listen();
    }

    private void OnDirectSubscribed(in SocketHeader socketHeader)
    {
        try
        {
            Log(new Dictionary<string, object>
            {
                ["Event"] = "OnDirectSubscribed",
                ["Name"] = socketHeader.Name,
                ["SocketHeader"] = socketHeader,
                ["ClientId"] = socketHeader.ClientId
            });

            string uniqueName = socketHeader.Name;

            if (_clients.TryGetValue(uniqueName, out IReader? existing))
            {
                // Same deferred-removal race as OnChildSubscribed, but here a resubscribe over a
                // reader still queued for disposal would throw instead of returning — losing the
                // .server/.audit tap via the exception handler. Retire it and rebuild.
                if (!existing.IsPendingDispose)
                    throw new ArgumentException($"Client {uniqueName} already subscribed.", nameof(socketHeader.ClientId));

                RemoveClient(uniqueName, true);
            }

            string clientName = socketHeader.ClientName.ToString();
            string ext = Path.GetExtension(clientName).TrimStart('.');

            IObjectWriter fileWriter;

            if (string.Equals(ext, "server", StringComparison.OrdinalIgnoreCase))
            {
                SocketHeaderWriter headerWriter = new SocketHeaderWriter(clientName);
                headerWriter.ChildSubscribed = OnChildSubscribed;
                headerWriter.ChildUnsubscribed = OnChildUnsubscribed;
                fileWriter = headerWriter;
            }
            else
            {
                if (!Enum.TryParse<FileType>(ext, true, out FileType fileType))
                    throw new ArgumentException($"Invalid FileType extension: {ext}");
                fileWriter = CreateWriterForType(fileType, clientName);
            }

            IReader reader = new LoggingSocketReader(_serverSocket, socketHeader.ClientId, socketHeader.ClientToServerChannelCount, uniqueName, fileWriter);
            AddClient(reader, false);
        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    private void OnDirectUnsubscribed(in SocketHeader socketHeader)
    {
        try
        {
            if (!_clients.TryGetValue(socketHeader.Name, out IReader? reader))
                return;

            Log(new Dictionary<string, object>
            {
                ["Event"] = "OnDirectUnsubscribed",
                ["Name"] = socketHeader.Name,
                ["SocketHeader"] = socketHeader,
                ["ClientId"] = socketHeader.ClientId
            });
            RemoveClient(socketHeader.Name);

        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    private void OnChildSubscribed(SocketHeader socketHeader)
    {
        try
        {
            Log(new Dictionary<string, object>
            {
                ["Event"] = "OnChildSubscribed",
                ["Name"] = socketHeader.Name,
                ["SocketHeader"] = socketHeader
            });

            string uniqueName = socketHeader.Name;
            if (_clients.TryGetValue(uniqueName, out IReader? existing))
            {
                if (!existing.IsPendingDispose)
                    return;

                RemoveClient(uniqueName, true);
            }

            string directoryPath = socketHeader.ClientName.ToString();

            SocketListener socketListener = new SocketListener(socketHeader);
            IObjectWriter fileWriter = new AuditWriter(directoryPath);

            IReader reader = new ClientSocketReader(socketListener, uniqueName, fileWriter);

            AddClient(reader, true);
        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    private void OnChildUnsubscribed(SocketHeader socketHeader)
    {
        try
        {
            Log(new Dictionary<string, object>
            {
                ["Event"] = "OnChildUnsubscribed",
                ["Name"] = socketHeader.Name,
                ["SocketHeader"] = socketHeader
            });
            RemoveClient(socketHeader.Name);
        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    private void OnException(Exception ex)
    {
        Log(ex);
        Exception?.Invoke(ex);
    }

    private IObjectWriter CreateWriterForType(FileType fileType, string clientName)
    {
        return fileType switch
        {
            FileType.Point => new ObjectWriter<Point>(clientName),
            FileType.Pair => new ObjectWriter<Pair>(clientName),
            FileType.Fill => new ObjectWriter<Fill>(clientName),
            FileType.Candle => new ObjectWriter<Candle>(clientName),
            FileType.Histogram => new ObjectWriter<Histogram>(clientName),
            FileType.Factor => new ObjectWriter<FactorPoint>(clientName),
            FileType.Mean => new ObjectWriter<MeanPoint>(clientName),
            FileType.StdDev => new ObjectWriter<StdDevPoint>(clientName),
            FileType.Alert => new AlertWriter(clientName, Alerts),
            FileType.Audit => new AuditWriter(clientName),
            _ => throw new ArgumentException($"Unsupported file type {fileType}")
        };
    }

    private void EnsurePollerAndWorkersStarted()
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                if (_pollerThread == null)
                {
                    // FIX: Use the escaper to keep this off the isolated core
                    _pollerThread = LowLatency.StartBackgroundThread("LoggingServer.Poller", PollerLoop);
                }

                while (_workerThreadCount < _maxWorkerThreads)
                {
                    // FIX: Use the escaper for logging workers
                    Thread worker = LowLatency.StartBackgroundThread($"LoggingServer.Worker#{_workerThreadCount + 1}", WorkerLoop);
                    _workerThreadCount++;
                    _workerThreads.Add(worker);
                }
            }
        }
        catch (Exception ex)
        {
            OnException(ex);
        }

    }

    private void PollerLoop()
    {
        SpinWait spinWait = new SpinWait();
        while (!_disposed)
        {
            try
            {
                if (_sleeping.IsEmpty)
                {
                    spinWait.SpinOnce();
                    continue;
                }

                bool anyWorkThisPass = false;

                foreach (var kvp in _sleeping)
                {
                    IReader reader = kvp.Key;
                    ReadStatus status;

                    try
                    {
                        status = reader.GetReadStatus();
                    }
                    catch (Exception ex)
                    {
                        OnException(ex);
                        continue;
                    }

                    if (status == ReadStatus.New || reader.IsPendingDispose)
                    {
                        if (_sleeping.TryRemove(reader, out _))
                        {
                            reader.Status = ReaderStatus.Queued;
                            _busy.Enqueue(reader);
                            _workAvailable.Release();
                            anyWorkThisPass = true;
                        }
                    }
                }

                if (!anyWorkThisPass)
                {
                    spinWait.SpinOnce();
                }
                else
                {
                    spinWait.Reset();
                }
            }
            catch (Exception ex)
            {
                OnException(ex);
            }
        }
    }

    private void AddClient(IReader reader, bool isChild)
    {
        if (_clients.TryAdd(reader.Name, reader))
        {
            Log(new Dictionary<string, object>
            {
                ["Event"] = "AddClient",
                ["Name"] = reader.Name,
                ["Source"] = isChild ? "Child" : "Direct"
            });
            _sleeping.TryAdd(reader, 0);
            EnsurePollerAndWorkersStarted();
        }
        else
        {
            reader.Dispose();
        }
    }

    private void RemoveClient(string name, bool disposeNow = false)
    {
        try
        {
            if (!_clients.TryGetValue(name, out IReader? reader))
                return;

            Log(new Dictionary<string, object>
            {
                ["Event"] = "RemoveClient",
                ["Name"] = reader.Name,
                ["DisposeNow"] = disposeNow
            });

            if (reader.Writer is SocketHeaderWriter socketHeaderWriter)
            {
                socketHeaderWriter.ChildSubscribed -= OnChildSubscribed;
                socketHeaderWriter.UnsubscribeChildren();
                socketHeaderWriter.ChildUnsubscribed -= OnChildUnsubscribed;
            }

            reader.IsPendingDispose = true;
            if (_sleeping.TryRemove(reader, out _))
            {
                _busy.Enqueue(reader);
                _workAvailable.Release();
            }

            if (disposeNow)
            {
                if (_clients.TryRemove(new KeyValuePair<string, IReader>(name, reader)))
                {
                    reader.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    private int _busyWorkers = 0;

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                _workAvailable.Wait();
                Interlocked.Increment(ref _busyWorkers);

                if (_busy.TryDequeue(out IReader? reader))
                {
                    ReadStatus readStatus = DrainReader(reader);

                    if (readStatus == ReadStatus.Closed || reader.IsPendingDispose)
                    {
                        RemoveClient(reader.Name, true);
                    }
                    else
                    {
                        _sleeping.TryAdd(reader, 0);
                    }
                }

                if (_disposed && _clients.IsEmpty)
                    return;
            }
            catch (Exception ex)
            {
                OnException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _busyWorkers);
            }
        }
    }

    private ReadStatus DrainReader(IReader reader)
    {
        reader.Status = ReaderStatus.Busy;
        try
        {
            IObjectWriter fileWriter = reader.Writer;
            lock (fileWriter.Lock)
            {
                if (reader.IsDisposed)
                    return ReadStatus.Closed;

                List<PooledBuffer> results = new List<PooledBuffer>();
                ReadStatus readStatus = reader.TryRead(results);
                if (readStatus == ReadStatus.New)
                {
                    foreach (PooledBuffer pooledBuffer in results)
                    {
                        fileWriter.Write(pooledBuffer.Span);
                        ThreadArrayPool<byte>.Return(pooledBuffer.Array);
                    }
                    results.Clear();
                    fileWriter.Flush();
                }
                return _disposed || readStatus == ReadStatus.Closed ? ReadStatus.Closed : ReadStatus.Empty;
            }
        }
        catch (Exception ex)
        {
            Log($"DrainReader({reader.Name}) Failed.");
            OnException(ex);
            return ReadStatus.Empty;
        }
        finally
        {
            reader.Status = ReaderStatus.Sleeping;
        }
    }

    private int _disposeCASLock = 0;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeCASLock, 1) == 1)
        {
            return;
        }
        _disposed = true;

        try
        {

            _serverSocket.ClientAllocated -= OnDirectSubscribed;
            _serverSocket.ClientDeallocated -= OnDirectUnsubscribed;

            Thread current = Thread.CurrentThread;
            if (_pollerThread != null && _pollerThread != current)
                try { _pollerThread.Join(); } catch { }

            while (_clients.Count > 0 || _busy.Count > 0 || _busyWorkers > 0)
            {
                foreach (IReader reader in _clients.Values)
                {
                    RemoveClient(reader.Name);
                }
                Thread.SpinWait(1000); // about 10 microseconds
            }

            if (_workerThreadCount > 0)
                _workAvailable.Release(_workerThreadCount * 2);

            foreach (Thread worker in _workerThreads)
                if (worker != current && worker.IsAlive)
                    try { worker.Join(); } catch { }

            _serverSocket.Dispose();
            _workAvailable.Dispose();
            Alerts.Dispose();
            Logger.Dispose();
        }
        catch (Exception ex)
        {
            OnException(ex);
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("LoggingServer Status");
        sb.AppendLine($" Worker threads : {_workerThreadCount}");
        sb.AppendLine($" Active clients : {_clients.Count}");

        if (_disposed) { sb.AppendLine(" (disposed)"); return sb.ToString(); }

        foreach (IReader reader in _clients.Values)
        {
            sb.AppendLine($" {reader.GetType().Name} | {reader.Status} | {reader.Writer.FilePath}");
        }
        return sb.ToString();
    }
}

public interface IObjectWriter : IDisposable
{
    void Write(ReadOnlySpan<byte> src);
    object Lock { get; }
    void Flush();
    FileSystemPath FilePath { get; }
}

public abstract class ObjectWriter : IObjectWriter
{
    public object Lock { get; } = new object();
    protected readonly FileSystemPath _filePath;
    protected FileStream _fileStream = null!;
    private static readonly byte[] s_newLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

    public FileSystemPath FilePath => _filePath;

    public ObjectWriter(FileSystemPath filePath)
    {
        _filePath = filePath;
        SetFileStream(filePath);
    }

    protected virtual void SetFileStream(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    public abstract string SerializeToLine(ReadOnlySpan<byte> src);

    public virtual void Write(ReadOnlySpan<byte> src)
    {
        string text = SerializeToLine(src);
        if (string.IsNullOrEmpty(text))
            return;
        int textBytes = Encoding.UTF8.GetByteCount(text);
        Span<byte> buffer = stackalloc byte[textBytes + s_newLineBytes.Length];
        Encoding.UTF8.GetBytes(text, buffer);
        s_newLineBytes.AsSpan().CopyTo(buffer[textBytes..]);
        _fileStream.Write(buffer);
    }

    public virtual void Write(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        int textBytes = Encoding.UTF8.GetByteCount(line);
        Span<byte> buffer = stackalloc byte[textBytes + s_newLineBytes.Length];
        Encoding.UTF8.GetBytes(line, buffer);
        s_newLineBytes.AsSpan().CopyTo(buffer[textBytes..]);
        _fileStream.Write(buffer);
    }

    public virtual void Flush() => _fileStream?.Flush(false);
    public virtual void Dispose()
    {
        lock (Lock)
        {
            _fileStream?.Dispose();
        }
    }
}
public class ObjectWriter<T> : ObjectWriter where T : unmanaged
{
    public ObjectWriter(FileSystemPath filePath) : base(filePath) { }
    public override string SerializeToLine(ReadOnlySpan<byte> src) => Json.SerializeToLine(MemoryMarshal.Read<T>(src));
}

public class AlertWriter : ObjectWriter
{
    private DateTime _lastDate;
    private readonly Telegram? _telegram;

    public AlertWriter(FileSystemPath filePath, Telegram? telegram = null) : base(filePath.GetPathWithoutExtension())
    {
        _telegram = telegram;
    }

    public override string SerializeToLine(ReadOnlySpan<byte> rsrc)
    {
        AlertType type = (AlertType)rsrc[0];
        ReadOnlySpan<byte> rsrcObj = rsrc[Unsafe.SizeOf<Header<AlertType>>()..];

        switch (type)
        {
            case AlertType.Exception:
            {
                string message = Encoding.ASCII.GetString(rsrcObj);
                return $"{type}{Environment.NewLine}{message}";
            }
            case AlertType.OrderRejected:
            {
                OrderRejected orderRejected = MemoryMarshal.Read<OrderRejected>(rsrcObj);
                string message = Encoding.ASCII.GetString(rsrcObj[Unsafe.SizeOf<OrderRejected>()..]);
                return new Alert(AlertType.OrderRejected, orderRejected, message).ToString();
            }
            default:
                return "";
        }
    }

    protected override void SetFileStream(string directoryPath)
    {

    }

    protected void SetFileStreamLazy(string directoryPath)
    {
        _lastDate = DateTime.UtcNow.Date;
        string alertsFilePath = Context.GetAlertsFilePath(directoryPath, _lastDate);
        base.SetFileStream(alertsFilePath);
    }
    public override void Write(ReadOnlySpan<byte> src)
    {
        if (DateTime.UtcNow.Date != _lastDate)
        {
            _fileStream?.Flush();
            _fileStream?.Dispose();
            SetFileStreamLazy(FilePath);
        }

        string text = SerializeToLine(src);
        if (string.IsNullOrEmpty(text)) return;

        base.Write(text);
        _telegram?.Send($"{_fileStream!.Name}{Environment.NewLine}{text}");

    }
}

public class AuditWriter : ObjectWriter
{
    private DateTime _lastDate;
    private readonly Dictionary<string, ObjectWriter> _fillWriters = new Dictionary<string, ObjectWriter>();
    private readonly Dictionary<string, ObjectWriter> _positionWriters = new Dictionary<string, ObjectWriter>();

    private readonly Dictionary<int, string> _symbols = new Dictionary<int, string>();

    public AuditWriter(FileSystemPath filePath) : base(filePath.GetPathWithoutExtension())
    {
    }

    protected void SetFileStreamLazy(string directoryPath)
    {
        _lastDate = DateTime.UtcNow.Date;
        string auditFilePath = Context.GetAuditFilePath(directoryPath, _lastDate);
        base.SetFileStream(auditFilePath);
    }
    protected override void SetFileStream(string directoryPath)
    {

    }

    public override void Write(ReadOnlySpan<byte> src)
    {
        if (DateTime.UtcNow.Date != _lastDate)
        {
            _fileStream?.Flush();
            _fileStream?.Dispose();
            SetFileStreamLazy(FilePath);
        }

        base.Write(src);
    }

    public override string SerializeToLine(ReadOnlySpan<byte> rsrc)
    {
        byte type = rsrc[0];

        switch (type)
        {
            case (byte)AllocateType.Client:
                {
                    ref readonly AllocateClient allocateClient = ref MemoryMarshal.AsRef<AllocateClient>(rsrc);
                    return Json.SerializeToLine(allocateClient);
                }
            case (byte)AllocateType.Instrument:
                {
                    ref readonly AllocateInstrument allocateInstrument = ref MemoryMarshal.AsRef<AllocateInstrument>(rsrc);
                    string symbol = allocateInstrument.Symbol.ToString();
                    if (!string.IsNullOrEmpty(symbol))
                    {
                        _symbols.TryAdd(allocateInstrument.InstrumentId, symbol);
                    }
                    return Json.SerializeToLine(allocateInstrument);
                }
            case (byte)ControlType.AlgoStatus:
                {
                    ref readonly ControlAlgoStatus controlAlgoStatus = ref MemoryMarshal.AsRef<ControlAlgoStatus>(rsrc);
                    string symbol = GetSymbol(controlAlgoStatus.InstrumentId);
                    string json = Json.SerializeToLine(controlAlgoStatus);
                    return json.Insert(1, $"\"Symbol\":\"{symbol}\",");
                }
            case (byte)OrderType.RiskLimit:
                {
                    ref readonly RiskLimit riskLimit = ref MemoryMarshal.AsRef<RiskLimit>(rsrc);
                    // Resolve by InstrumentId, not the span overload: that one reads an OrderHead
                    // (Header + OrderHeader) off the front, and RiskLimit has no OrderHeader — it
                    // would decode InstrumentId out of MaxOrderQuantity and friends.
                    string symbol = GetSymbol(riskLimit.InstrumentId);
                    string json = Json.SerializeToLine(riskLimit);
                    return json.Insert(1, $"\"Symbol\":\"{symbol}\",");
                }
            case (byte)OrderType.OrderState:
                {
                    string symbol = GetSymbol(rsrc);
                    ref readonly OrderState orderState = ref MemoryMarshal.AsRef<OrderState>(rsrc);
                    string json = Json.SerializeToLine(orderState);
                    return json.Replace("\"OrderHeader\":{", $"\"OrderHeader\":{{\"Symbol\":\"{symbol}\",");
                }

            case (byte)OrderType.OrderRejected:
                {
                    string symbol = GetSymbol(rsrc);
                    ref readonly OrderRejected orderRejected = ref MemoryMarshal.AsRef<OrderRejected>(rsrc);
                    string json = Json.SerializeToLine(orderRejected);
                    return json.Replace("\"OrderHeader\":{", $"\"OrderHeader\":{{\"Symbol\":\"{symbol}\",");
                }

            case (byte)OrderType.OrderTarget:
                {
                    string symbol = GetSymbol(rsrc);
                    ref readonly OrderTarget orderTarget = ref MemoryMarshal.AsRef<OrderTarget>(rsrc);
                    string json = Json.SerializeToLine(orderTarget);
                    return json.Replace("\"OrderHeader\":{", $"\"OrderHeader\":{{\"Symbol\":\"{symbol}\",");
                }

            case (byte)OrderType.Fill:
                {
                    string symbol = GetSymbol(rsrc);
                    ref readonly Fill fill = ref MemoryMarshal.AsRef<Fill>(rsrc);
                    if (!_fillWriters.TryGetValue(symbol, out ObjectWriter? writer))
                    {
                        string filePath = Context.GetFillsFilePath(_filePath, symbol);
                        writer = new ObjectWriter<Fill>(filePath);
                        _fillWriters[symbol] = writer;
                    }
                    string line = Json.SerializeToLine(fill);
                    line = line.Replace("\"OrderHeader\":{", $"\"OrderHeader\":{{\"Symbol\":\"{symbol}\",");
                    writer.Write(line);
                    return line;
                }

            case (byte)OrderType.Position:
                {
                    string symbol = GetSymbol(rsrc);
                    ref readonly PositionHeader position = ref MemoryMarshal.AsRef<PositionHeader>(rsrc);
                    if (!_positionWriters.TryGetValue(symbol, out ObjectWriter? writer))
                    {
                        string filePath = Context.GetPositionFilePath(_filePath, symbol);
                        writer = new ObjectWriter<PositionHeader>(filePath);
                        _positionWriters[symbol] = writer;
                    }
                    string line = Json.SerializeToLine(position);
                    line = line.Replace("\"OrderHeader\":{", $"\"OrderHeader\":{{\"Symbol\":\"{symbol}\",");
                    writer.Write(line);
                    return line;
                }

            default:
                return "";
        }
    }

    struct OrderHead
    {
        public Header<OrderType> Header;
        public OrderHeader OrderHeader;
    }

    protected int GetInstrumentId(ReadOnlySpan<byte> rsrc)
    {
        ref readonly OrderHead orderHead = ref MemoryMarshal.AsRef<OrderHead>(rsrc);
        return orderHead.OrderHeader.OrderId.InstrumentId;
    }

    protected string GetSymbol(int instrumentId)
    {
        if (_symbols.TryGetValue(instrumentId, out string? symbol))
            return symbol;
        else
            return $"UnknownSymbol_{instrumentId}";
    }

    protected string GetSymbol(ReadOnlySpan<byte> rsrc)
    {
        int instrumentId = GetInstrumentId(rsrc);
        return GetSymbol(instrumentId);

    }

    public override void Flush()
    {
        base.Flush();
        foreach (ObjectWriter writer in _fillWriters.Values) writer.Flush();
        foreach (ObjectWriter writer in _positionWriters.Values) writer.Flush();
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (ObjectWriter writer in _fillWriters.Values) writer.Dispose();
        foreach (ObjectWriter writer in _positionWriters.Values) writer.Dispose();
    }
}

public sealed class SocketHeaderWriter : IObjectWriter
{
    public object Lock { get; } = new object();
    public FileSystemPath FilePath { get; }

    public Action<SocketHeader>? ChildSubscribed;
    public Action<SocketHeader>? ChildUnsubscribed;
    private ConcurrentDictionary<string, SocketHeader> _headerNames = new();

    public SocketHeaderWriter(FileSystemPath filePath)
    {
        FilePath = filePath;
    }

    public void UnsubscribeChildren()
    {
        List<string> names = _headerNames.Keys.ToList();
        foreach (string name in names)
        {
            if (_headerNames.TryRemove(name, out SocketHeader socketHeader))
            {
                ChildUnsubscribed?.Invoke(socketHeader);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> src)
    {
        SocketHeader socketHeader = MemoryMarshal.Read<SocketHeader>(src);

        if (socketHeader.ClientToServerChannelCount == 0 && socketHeader.ServerToClientChannelCount == 0 && _headerNames.TryRemove(socketHeader.Name.ToString(), out _))
        {
            ChildUnsubscribed?.Invoke(socketHeader);
        }
        if ((socketHeader.ClientToServerChannelCount > 0 || socketHeader.ServerToClientChannelCount > 0) && _headerNames.TryAdd(socketHeader.Name.ToString(), socketHeader))
        {
            ChildSubscribed?.Invoke(socketHeader);
        }
    }

    public void Flush() { }

    public void Dispose() { }
}
//END_FILE HFT/Logging/LoggingServer.cs