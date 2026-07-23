//BEGIN_FILE HFT/Provider/AlertManager.cs
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Execution;
using Socket;
using Tools;

namespace Provider;

[RegisterJson]
public enum AlertType : byte
{
    Exception = 0,
    OrderRejected = 1,
    ExchangeDead = 2, // exchange dc
    InstrumentDead = 3, // No new data for instrument
    OrderDead = 4, // Order was not acked after for 1 second
}

[RegisterJson]
public struct Alert
{
    public Header<AlertType> Header;
    public object? Object;
    public string? Message;

    public Alert(AlertType type, object? obj, string? message)
    {
        Header = new Header<AlertType>(type);
        Object = obj;
        Message = message;
    }

    public override string ToString() => Json.Serialize(this);
}

public sealed class AlertManager : IDisposable
{
    public string MachineName { get; }

    public Context Context { get; }

    private readonly ClientSocket _logger;
    private readonly BlockingCollection<Alert> _queue = new BlockingCollection<Alert>();
    private readonly Thread _thread;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private volatile bool _disposed;

    public AlertManager(Context context)
    {
        Context = context;
        MachineName = Platform.Name;
        _logger = new ClientSocket(Context.DirectoryPath + ".alert", Context.LoggingServerName, [SocketChannel.ExecutionChannelLength], [SocketChannel.AdminChannelLength]);
        _logger.Connect();

        _thread = LowLatency.StartBackgroundThread("AlertManager", ConsumeLoop);

        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        try
        {
            args.SetObserved();
            Exception exception = args.Exception.InnerException ?? args.Exception;
            OnException(exception);
        }
        catch
        {
        }
    }

    public void OnException(Exception exception)
    {
        if (_disposed) return;
        try
        {
            _queue.Add(new Alert(AlertType.Exception, exception, null));
        }
        catch
        {
        }
    }

    public void OnOrderRejected(in OrderRejected orderRejected, string message)
    {
        if (_disposed) return;
        try
        {
            _queue.Add(new Alert(AlertType.OrderRejected, orderRejected, message));
        }
        catch
        {
        }
    }

    private void ConsumeLoop()
    {
        try
        {
            foreach (Alert alert in _queue.GetConsumingEnumerable())
            {
                try
                {
                    WriteToSocket(in alert);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AlertManager.WriteToSocket() failed. Payload:{Environment.NewLine}{alert.Message}{Environment.NewLine}Cause:{Environment.NewLine}{ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AlertManager.ConsumeLoop() crashed: {ex}");
        }
    }

    private void WriteToSocket(in Alert alert)
    {
        Span<byte> buffer = _buffer;
        int pos = 0;

        MemoryMarshal.Write(buffer[pos..], in alert.Header);
        pos += Unsafe.SizeOf<Header<AlertType>>();

        switch (alert.Header.Type)
        {
            case AlertType.OrderRejected when alert.Object is OrderRejected orderRejected:
                MemoryMarshal.Write(buffer[pos..], in orderRejected);
                pos += Unsafe.SizeOf<OrderRejected>();
                break;
        }

        string? message = alert.Message;
        if (message == null && alert.Object is not null && alert.Object is not ValueType)
        {
            message = alert.Object.ToString();
        }

        if (!string.IsNullOrEmpty(message))
        {
            int truncatedLength = Math.Min(message.Length, buffer.Length - pos);
            pos += Encoding.ASCII.GetBytes(message.AsSpan(0, truncatedLength), buffer[pos..]);
        }

        _logger.Write(buffer[..pos]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _queue.CompleteAdding();

        try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { }

        _logger.Dispose();
        _queue.Dispose();
    }
}
//END_FILE HFT/Provider/AlertManager.cs
