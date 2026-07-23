using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tools;

/// <summary>
/// Continuously streams lines from a text file.
/// Supports starting from a specific byte offset and draining on close.
/// </summary>
public sealed class FileStreamer : IDisposable
{
    private readonly string _filePath;
    private readonly TimeSpan _pollInterval;
    private readonly Encoding _encoding;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    // Explicit start position. If null, starts at 0.
    private readonly long? _startOffset;

    private Task? _worker;
    private int _disposed;

    // Single long-lived stream/reader.
    private FileStream? _stream;
    private StreamReader? _reader;
    private DateTime? _creationTimeUtc;

    // Buffer for reading raw characters.
    private readonly char[] _buffer = new char[4096];

    // Trailing partial line (no newline yet).
    private string? _pendingFragment;

    public event Action<List<string>>? Lines;
    public event Action<string>? Line;
    public event Action<Exception>? Error;

    public string FilePath
    {
        get
        {
            return _filePath;
        }
    }

    public Task Completion
    {
        get
        {
            return _worker ?? Task.CompletedTask;
        }
    }

    /// <param name="startOffset">
    /// If provided, the streamer will Seek to this position immediately upon opening the file.
    /// This allows handing off "Historic" data to another reader and picking up exactly where it left off.
    /// </param>
    public FileStreamer(string filePath, TimeSpan? pollInterval = null, Encoding? encoding = null, long? startOffset = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        _filePath = filePath;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _encoding = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _startOffset = startOffset;
    }

    public void Connect()
    {
        if (_worker != null)
        {
            return;
        }

        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Reads any remaining bytes in the file synchronously, emits them, and closes the stream.
    /// Used during log rotation to ensure the old file is fully consumed.
    /// </summary>
    public void DrainAndClose()
    {
        // Stop the async loop
        _cts.Cancel();

        // Wait briefly for the worker to acknowledge cancellation if it's running
        if (_worker != null && !_worker.IsCompleted)
        {
            try { _worker.Wait(200); } catch { }
        }

        try
        {
            // Perform one final read if stream is open
            if (_stream != null && _reader != null)
            {
                // Read to end
                string remainder = _reader.ReadToEnd();
                if (!string.IsNullOrEmpty(remainder))
                {
                    List<string> lines = new List<string>();
                    ExtractLinesFromChunk(remainder, lines);

                    // If there's a pending fragment that wasn't terminated by a newline even at EOF, 
                    // we usually treat it as a line now or discard it. 
                    // Standard log practice: if it ends without \n, emit it.
                    if (_pendingFragment != null)
                    {
                        lines.Add(_pendingFragment);
                        _pendingFragment = null;
                    }

                    if (lines.Count > 0)
                    {
                        DispatchLines(lines);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        if (_worker != null)
        {
            Task.WaitAny(new[] { _worker }, 500);
        }

        CloseStream();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            List<string>? newLines = null;

            try
            {
                newLines = await PollChangesAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (newLines != null && newLines.Count > 0)
            {
                DispatchLines(newLines);
            }

            await DelayNoExceptionAsync(_pollInterval, token).ConfigureAwait(false);
        }
    }

    private static async Task DelayNoExceptionAsync(TimeSpan delay, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        Task delayTask = Task.Delay(delay, CancellationToken.None);
        TaskCompletionSource<bool> cancelTcs = new TaskCompletionSource<bool>();

        using (token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelTcs))
        {
            await Task.WhenAny(delayTask, cancelTcs.Task).ConfigureAwait(false);
        }
    }

    private void DispatchLines(List<string> lines)
    {
        Action<List<string>>? batchHandler = Lines;
        Action<string>? singleHandler = Line;

        if (batchHandler != null)
        {
            batchHandler(lines);
        }

        if (singleHandler != null)
        {
            foreach (string line in lines)
            {
                singleHandler(line);
            }
        }
    }

    private async Task<List<string>?> PollChangesAsync(CancellationToken token)
    {
        try
        {
            bool wasClosed = _stream == null;
            EnsureStreamOpen(token);

            if (_reader == null)
            {
                return null;
            }

            // --- CRITICAL SYNCHRONIZATION LOGIC ---
            // If this is the FIRST time we opened the file, and a specific StartOffset was requested,
            // we jump there immediately.
            // Note: We check _stream.Position == 0 to ensure we only seek once at the very beginning.
            if (wasClosed && _startOffset.HasValue && _stream != null && _stream.Position == 0)
            {
                // Ensure we don't seek past the actual end of file (file might have shrunk)
                long target = Math.Min(_startOffset.Value, _stream.Length);
                _stream.Seek(target, SeekOrigin.Begin);
                _reader.DiscardBufferedData();
            }
            // ---------------------------------------

            int bytesRead = await _reader.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return null;
            }

            List<string> lines = new List<string>();
            string chunk = new string(_buffer, 0, bytesRead);

            ExtractLinesFromChunk(chunk, lines);

            // Drain remaining data if file is large
            while (bytesRead == _buffer.Length && !token.IsCancellationRequested)
            {
                bytesRead = await _reader.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);

                if (bytesRead > 0)
                {
                    chunk = new string(_buffer, 0, bytesRead);
                    ExtractLinesFromChunk(chunk, lines);
                }
                else
                {
                    break;
                }
            }

            return lines;
        }
        catch (IOException ex)
        {
            Error?.Invoke(ex);
            CloseStream();
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Error?.Invoke(ex);
            CloseStream();
            return null;
        }
    }

    private void EnsureStreamOpen(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (_stream == null)
        {
            TryOpenStream();
            return;
        }

        if (!File.Exists(_filePath))
        {
            CloseStream();
            return;
        }

        try
        {
            DateTime currentCreationTime = File.GetCreationTimeUtc(_filePath);

            if (_creationTimeUtc.HasValue && currentCreationTime != _creationTimeUtc.Value)
            {
                CloseStream();
                TryOpenStream();
                return;
            }

            // Truncation detection
            if (_stream.Position > _stream.Length)
            {
                _stream.Seek(0, SeekOrigin.Begin);
                if (_reader != null)
                {
                    _reader.DiscardBufferedData();
                }
                _pendingFragment = null;
            }
        }
        catch (IOException ex)
        {
            Error?.Invoke(ex);
            CloseStream();
        }
    }

    private void TryOpenStream()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            _stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            _reader = new StreamReader(_stream, _encoding, true, 4096, true);
            _creationTimeUtc = File.GetCreationTimeUtc(_filePath);
            _pendingFragment = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CloseStream();
        }
    }

    private void ExtractLinesFromChunk(string chunk, List<string> output)
    {
        string text = _pendingFragment == null ? chunk : _pendingFragment + chunk;
        _pendingFragment = null;
        int startIndex = 0;

        while (true)
        {
            int newlineIndex = text.IndexOf('\n', startIndex);
            if (newlineIndex < 0)
            {
                break;
            }

            int length = newlineIndex - startIndex;

            // Handle CRLF: strip trailing '\r' if present.
            if (length > 0 && text[newlineIndex - 1] == '\r')
            {
                length--;
            }

            output.Add(text.Substring(startIndex, length));
            startIndex = newlineIndex + 1;
        }

        if (startIndex < text.Length)
        {
            _pendingFragment = text.Substring(startIndex);
        }
    }

    private void CloseStream()
    {
        try { _reader?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }

        _reader = null;
        _stream = null;
        _creationTimeUtc = null;
        _pendingFragment = null;
    }
}

/// <summary>
/// Cross-platform directory monitor for file creation / deletion events.
/// </summary>
public sealed class DirectoryStreamer : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _filter;
    private readonly FileSystemWatcher _watcher;

    private int _started;
    private bool _disposed;

    public event Action<string>? FileCreated;
    public event Action<string>? FileDeleted;
    public event Action<string>? FileModified;

    public DirectoryStreamer(string directoryPath, string filter = "*.*")
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));
        }

        _directoryPath = directoryPath;
        _filter = filter;

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        _watcher = new FileSystemWatcher(directoryPath, filter)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamedEvent;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        // 1. Initial scan on a background thread.
        Task.Run(() => ScanExistingFiles());

        // 2. Start watching for real-time events.
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Renamed += OnRenamedEvent;
    }

    private void ScanExistingFiles()
    {
        try
        {
            string[] files = Directory.GetFiles(_directoryPath, _filter, SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                InvokeSafe(FileCreated, file);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DirectoryStreamer initial scan failed: {ex.Message}");
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Created:
                InvokeSafe(FileCreated, e.FullPath);
                break;
            case WatcherChangeTypes.Deleted:
                InvokeSafe(FileDeleted, e.FullPath);
                break;
            case WatcherChangeTypes.Changed:
                InvokeSafe(FileModified, e.FullPath);
                break;
        }
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // Treat rename as delete old -> create new
        InvokeSafe(FileDeleted, e.OldFullPath);
        InvokeSafe(FileCreated, e.FullPath);
    }

    private void InvokeSafe(Action<string>? handler, string path)
    {
        try
        {
            handler?.Invoke(path);
        }
        catch
        {
            /* Ignore listener exceptions to keep the watcher alive */
        }
    }
}

/// <summary>
/// Orchestrates streaming for multiple files within a set of directories.
/// </summary>
public sealed class DirectoryFileStreamer : IDisposable
{
    private readonly List<DirectoryStreamer> _directoryStreamers;
    private readonly TimeSpan _pollInterval;
    private readonly Encoding _encoding;

    // Map: FilePath -> Active Subscription
    private readonly Dictionary<string, FileSubscription> _files = new Dictionary<string, FileSubscription>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    private int _started;
    private bool _disposed;

    public event Action<string, List<string>>? Lines;
    public event Action<string, Exception>? Error;
    public event Action<string>? FileStarted;
    public event Action<string>? FileStopped;

    /// <summary>
    /// Gets a snapshot of currently tracked file paths.
    /// </summary>
    public IReadOnlyCollection<string> ActiveFiles
    {
        get
        {
            lock (_lock)
            {
                return new List<string>(_files.Keys);
            }
        }
    }

    public DirectoryFileStreamer(
        IEnumerable<string> directoryPaths,
        string filter = "*.*",
        TimeSpan? pollInterval = null,
        Encoding? encoding = null)
    {
        if (directoryPaths == null)
        {
            throw new ArgumentNullException(nameof(directoryPaths));
        }

        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _encoding = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _directoryStreamers = new List<DirectoryStreamer>();

        foreach (string directoryPath in directoryPaths)
        {
            DirectoryStreamer streamer = new DirectoryStreamer(directoryPath, filter);
            streamer.FileCreated += OnFileCreated;
            streamer.FileDeleted += OnFileDeleted;
            _directoryStreamers.Add(streamer);
        }
    }

    public DirectoryFileStreamer(
        string directoryPath,
        string filter = "*.*",
        TimeSpan? pollInterval = null,
        Encoding? encoding = null)
        : this(new[] { directoryPath }, filter, pollInterval, encoding)
    {
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || _disposed)
        {
            return;
        }

        foreach (DirectoryStreamer streamer in _directoryStreamers)
        {
            streamer.Start();
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        foreach (DirectoryStreamer streamer in _directoryStreamers)
        {
            streamer.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (DirectoryStreamer streamer in _directoryStreamers)
        {
            streamer.FileCreated -= OnFileCreated;
            streamer.FileDeleted -= OnFileDeleted;
            streamer.Dispose();
        }

        lock (_lock)
        {
            foreach (FileSubscription sub in _files.Values)
            {
                sub.Dispose();
            }
            _files.Clear();
        }
    }

    private void OnFileCreated(string filePath)
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_files.ContainsKey(filePath))
            {
                return;
            }

            FileStreamer streamer = new FileStreamer(filePath, _pollInterval, _encoding);
            FileSubscription subscription = new FileSubscription(this, filePath, streamer);

            _files.Add(filePath, subscription);
            streamer.Connect();
        }

        FileStarted?.Invoke(filePath);
    }

    private void OnFileDeleted(string filePath)
    {
        if (_disposed)
        {
            return;
        }

        FileSubscription? subscription;

        lock (_lock)
        {
            if (!_files.TryGetValue(filePath, out subscription))
            {
                return;
            }

            _files.Remove(filePath);
        }

        subscription.Dispose();
        FileStopped?.Invoke(filePath);
    }

    private void DispatchLines(string filePath, List<string> batch)
    {
        Lines?.Invoke(filePath, batch);
    }

    private void DispatchError(string filePath, Exception exception)
    {
        Error?.Invoke(filePath, exception);
    }

    /// <summary>
    /// Helper class to bridge events from a single FileStreamer to the parent DirectoryFileStreamer.
    /// </summary>
    private sealed class FileSubscription : IDisposable
    {
        private readonly DirectoryFileStreamer _parent;
        private readonly string _path;
        private readonly FileStreamer _streamer;

        public FileSubscription(DirectoryFileStreamer parent, string path, FileStreamer streamer)
        {
            _parent = parent;
            _path = path;
            _streamer = streamer;

            _streamer.Lines += OnLines;
            _streamer.Error += OnError;
        }

        private void OnLines(List<string> batch)
        {
            _parent.DispatchLines(_path, batch);
        }

        private void OnError(Exception ex)
        {
            _parent.DispatchError(_path, ex);
        }

        public void Dispose()
        {
            _streamer.Lines -= OnLines;
            _streamer.Error -= OnError;
            _streamer.Dispose();
        }
    }
}