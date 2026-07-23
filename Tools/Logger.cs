using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Tools;

// COLD PROCESS LOGGER
// NOT ALLOWED ON THE HOT PATH

[JsonConverter(typeof(LogEntryJsonConverter))]
[RegisterJson]
public struct LogEntry
{
    public Timestamp Timestamp;
    public string Thread;
    public string Source;
    public object[] Objects;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

public sealed class LogEntryJsonConverter : JsonConverter<LogEntry>
{
    public override LogEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException("LogEntry deserialization is not supported.");
    }

    public override void Write(Utf8JsonWriter writer, LogEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("Timestamp", value.Timestamp.ToString());
        writer.WriteString("Thread", value.Thread);
        writer.WriteString("Source", value.Source);

        writer.WritePropertyName("Objects");
        writer.WriteStartArray();

        if (value.Objects != null)
        {
            foreach (object obj in value.Objects)
            {
                writer.WriteRawValue(Json.Serialize(obj));
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

public class Logger : IDisposable
{
    public bool ToConsole { get; set; } = false;

    public bool ToFile { get; set; } = true;

    public event Action<Exception>? Exception;

    public FileSystemPath DirectoryPath { get; }

    public Timestamp Date { get; private set; } = Timestamp.MinValue;

    protected BlockingCollection<LogEntry> LogQueue = new BlockingCollection<LogEntry>();

    private readonly ManualResetEvent _finished = new ManualResetEvent(false);

    private Thread? _workerThread;   

    public Logger(string directoryPath)
    {
        DirectoryPath = directoryPath;
        Directory.CreateDirectory(DirectoryPath);

        Application.AddExitAction($"Dispose Logger {DirectoryPath}", int.MinValue, Dispose);
        Connect();
    }

    public void Log(string source, params object[] objects)
    {
        LogEntry logEntry = new LogEntry()
        {
            Thread = Thread.CurrentThread.Name ?? $"Thread-{Thread.CurrentThread.ManagedThreadId}",
            Source = source,
            Objects = objects,
            Timestamp = Timestamp.UtcNow,
        };

        try
        {
            LogQueue.Add(logEntry);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{GetType().Name}::Log() Exception, failed to log:{Environment.NewLine}{logEntry}{Environment.NewLine}{exception}");
        }
    }

    private void Connect()
    {
        if (_workerThread != null)
            return;

        _workerThread = LowLatency.StartBackgroundThread($"Logger {DirectoryPath}", WriteLoop);
    }

    public FileSystemPath GetFilePath(Timestamp timestamp)
    {
        return System.IO.Path.Combine(DirectoryPath, timestamp.ToDateString() + ".log");
    }

    protected void WriteLoop()
    {
        StreamWriter? streamWriter = null;

        _finished.Reset();

        void CloseFile()
        {
            try { streamWriter?.Flush(); } catch { }
            try { streamWriter?.Dispose(); } catch { }
            streamWriter = null;
        }

        try
        {
            foreach (LogEntry entry in LogQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (streamWriter == null || entry.Timestamp.Date != Date)
                    {
                        Date = entry.Timestamp.Date;
                        CloseFile();

                        Directory.CreateDirectory(DirectoryPath);

                        FileStream fileStream = new FileStream(GetFilePath(Date), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                        streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    string line = entry.ToString();

                    if (ToFile && streamWriter != null)
                        streamWriter.WriteLine(line);

                    if (ToConsole)
                        Console.WriteLine(line);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LOGGER ERROR: {ex.Message}");
                    Console.WriteLine($"LOGGER ERROR: {ex}");

                    Exception?.Invoke(ex);
                }
            }
        }
        finally
        {
            CloseFile();
            _finished.Set();
        }
    }

    private int _disposeCASLock = 0;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeCASLock, 1) == 1)
        {
            return;
        }

        LogQueue.CompleteAdding();

        while (!_finished.WaitOne(1000))
        {
            Console.WriteLine($"Logger::{DirectoryPath} Waiting for logger to close and dispose.");
        }

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(1000);
        }

        LogQueue.Dispose();
        _finished.Dispose();
    }
}