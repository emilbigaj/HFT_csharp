using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tools;

/// <summary>
/// Reads a SINGLE file backwards from a specified position.
/// </summary>
public sealed class ReverseFileReader : IDisposable
{
    private FileStream? _fileStream;
    private readonly byte[] _buffer = new byte[4096];
    private long _position;
    private readonly StringBuilder _sb = new StringBuilder();

    public string FilePath { get; }

    public bool IsExhausted => _position <= 0 && _sb.Length == 0;

    public ReverseFileReader(string filePath, long? startPosition = null)
    {
        FilePath = filePath;
        try
        {
            _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (startPosition.HasValue)
            {
                _position = Math.Min(startPosition.Value, _fileStream.Length);
            }
            else
            {
                _position = _fileStream.Length;
            }
        }
        catch
        {
            _position = 0;
            _fileStream = null;
        }
    }

    public bool TryReadLine(out string line)
    {
        line = null!;
        if (_fileStream == null) return false;

        while (true)
        {
            // If at start of file, flush pending buffer
            if (_position <= 0)
            {
                if (_sb.Length > 0)
                {
                    line = FlushStringBuilder();
                    return true;
                }
                return false;
            }

            int toRead = (int)Math.Min(_buffer.Length, _position);
            long chunkStart = _position - toRead;

            _fileStream.Seek(chunkStart, SeekOrigin.Begin);
            int read = _fileStream.Read(_buffer, 0, toRead);

            // Scan backwards
            for (int i = read - 1; i >= 0; i--)
            {
                byte b = _buffer[i];
                if (b == '\n')
                {
                    if (_sb.Length > 0)
                    {
                        line = FlushStringBuilder();
                        _position = chunkStart + i; // Set pos to just before \n
                        return true;
                    }
                    // Else: we found a newline but buffer was empty (empty line or consecutive newlines), 
                    // just skip it and continue.
                }
                else if (b != '\r')
                {
                    _sb.Append((char)b);
                }
            }

            _position = chunkStart;
        }
    }

    public List<string> ReadLines(int count)
    {
        List<string> results = new List<string>(count);
        while (results.Count < count && TryReadLine(out string line))
        {
            results.Add(line);
        }
        return results;
    }

    private string FlushStringBuilder()
    {
        if (_sb.Length == 0) return string.Empty;
        char[] chars = new char[_sb.Length];
        _sb.CopyTo(0, chars, 0, _sb.Length);
        Array.Reverse(chars);
        _sb.Clear();
        return new string(chars);
    }

    public void Dispose()
    {
        _fileStream?.Dispose();
        _fileStream = null;
    }
}

/// <summary>
/// Manages a collection of log files in a directory, reading them newest-to-oldest.
/// </summary>
public sealed class ReverseDirectoryReader : IDisposable
{
    private readonly List<string> _files;
    private int _currentFileIndex;
    private ReverseFileReader? _currentReader;
    private readonly long? _newestFileLimit;

    public ReverseDirectoryReader(string directoryPath, string searchPattern = "*", long? newestFileLimit = null)
    {
        _newestFileLimit = newestFileLimit;

        if (!Directory.Exists(directoryPath))
        {
            _files = new List<string>();
        }
        else
        {
            // 2. Sort files from most recent to oldest date
            _files = Directory.GetFiles(directoryPath, searchPattern)
                              .OrderByDescending(f => f)
                              .ToList();
        }
        _currentFileIndex = 0;
    }

    public List<string> ReadHistory(int count)
    {
        List<string> results = new List<string>(count);

        while (results.Count < count)
        {
            if (_currentReader == null)
            {
                if (_currentFileIndex >= _files.Count) break;

                string path = _files[_currentFileIndex];

                // 4. Read in reverse... limit only applies to the first (newest) file
                long? limit = (_currentFileIndex == 0) ? _newestFileLimit : null;

                _currentReader = new ReverseFileReader(path, limit);
            }

            // 5. If a file is exhausted then read the next previous dated file
            List<string> batch = _currentReader.ReadLines(count - results.Count);
            results.AddRange(batch);

            if (_currentReader.IsExhausted)
            {
                _currentReader.Dispose();
                _currentReader = null;
                _currentFileIndex++;
            }
        }
        return results;
    }

    public void Dispose()
    {
        _currentReader?.Dispose();
        _currentReader = null;
    }
}

/// <summary>
/// Facade for Live Streaming + Historical Paging.
/// </summary>
public sealed class LogReader : IDisposable
{
    private readonly string _directory;
    private readonly string _pattern;

    private DirectoryStreamer? _directoryStreamer;
    private FileStreamer? _liveStreamer;
    private ReverseDirectoryReader? _historyReader;

    private readonly object _lock = new object();
    private string? _currentLiveFilePath;

    public event Action<List<string>>? LiveLines;

    public LogReader(string directory, string pattern)
    {
        _directory = directory;
        _pattern = pattern;
    }

    public void Start()
    {
        // 1. Gather all files... 2. sort...
        string? newestFile = null;
        if (Directory.Exists(_directory))
        {
            newestFile = Directory.GetFiles(_directory, _pattern)
                                  .OrderByDescending(f => f)
                                  .FirstOrDefault();
        }

        long splitPoint = -1;
        long boundary = 0;

        if (newestFile != null)
        {
            _currentLiveFilePath = newestFile;
            // 3. Take the most recent file - find the position of the last newline
            splitPoint = GetLastNewlinePosition(newestFile);
            boundary = splitPoint == -1 ? 0 : splitPoint + 1;
        }

        // 4. History reader setup (starts at boundary of newest file)
        lock (_lock)
        {
            _historyReader = new ReverseDirectoryReader(_directory, _pattern, newestFileLimit: boundary);
        }

        // 6. Stream from the separator
        if (newestFile != null)
        {
            StartLiveStream(newestFile, startOffset: boundary);
        }

        // 7. Monitor the directory for anytime a new file is created
        _directoryStreamer = new DirectoryStreamer(_directory, _pattern);
        _directoryStreamer.FileCreated += OnFileCreated;
        _directoryStreamer.Start();
    }

    private void OnFileCreated(string newFilePath)
    {
        lock (_lock)
        {
            // 8. If a new file is created...
            // Simple check: is this lexicographically newer than what we are streaming?
            if (_currentLiveFilePath == null || string.Compare(newFilePath, _currentLiveFilePath, StringComparison.OrdinalIgnoreCase) > 0)
            {
                SwitchToNewFile(newFilePath);
            }
        }
    }

    private void SwitchToNewFile(string newFilePath)
    {
        // 8. Read all lines to end of file of the currently streaming file and close the file
        if (_liveStreamer != null)
        {
            _liveStreamer.DrainAndClose();
            _liveStreamer = null;
        }

        _currentLiveFilePath = newFilePath;

        // 9. Switch the filestream to the newly created file
        StartLiveStream(newFilePath, startOffset: 0);
    }

    private void StartLiveStream(string filePath, long startOffset)
    {
        _liveStreamer = new FileStreamer(filePath, startOffset: startOffset);
        _liveStreamer.Lines += OnLiveLines;
        _liveStreamer.Connect();
    }

    private void OnLiveLines(List<string> lines) => LiveLines?.Invoke(lines);

    public List<string> LoadHistory(int count)
    {
        lock (_lock)
        {
            if (_historyReader == null) return new List<string>();
            return _historyReader.ReadHistory(count);
        }
    }

    private static long GetLastNewlinePosition(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return -1;
                long pos = fs.Length;
                byte[] buffer = new byte[4096];

                while (pos > 0)
                {
                    int toRead = (int)Math.Min(pos, buffer.Length);
                    pos -= toRead;
                    fs.Seek(pos, SeekOrigin.Begin);
                    int read = fs.Read(buffer, 0, toRead);
                    for (int i = read - 1; i >= 0; i--)
                    {
                        if (buffer[i] == '\n') return pos + i;
                    }
                }
                return -1;
            }
        }
        catch { return -1; }
    }

    public void Dispose()
    {
        _directoryStreamer?.Dispose();
        _liveStreamer?.Dispose();
        lock (_lock) { _historyReader?.Dispose(); }
    }
}