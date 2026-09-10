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
    public string? Symbol; // OrderRejected only; resolved by AlertManager, carried on the wire as String64
    public object? Object;
    public string? Message;

    public Alert(AlertType type, object? obj, string? message)
    {
        Header = new Header<AlertType>(type);
        Object = obj;
        Message = message;
    }

    public override string ToString() => Json.Serialize(this);

    // Wire: Header | [OrderRejected | String64 Symbol] | ASCII Message; ToBytes returns the bytes written, Message truncated to fit.
    public static Alert FromBytes(ReadOnlySpan<byte> rsrc)
    {
        Alert alert = new Alert();
        alert.Header = MemoryMarshal.Read<Header<AlertType>>(rsrc);
        rsrc = rsrc[Unsafe.SizeOf<Header<AlertType>>()..];
        switch (alert.Header.Type)
        {
            case AlertType.OrderRejected:
                alert.Object = MemoryMarshal.Read<OrderRejected>(rsrc);
                rsrc = rsrc[Unsafe.SizeOf<OrderRejected>()..];
                alert.Symbol = MemoryMarshal.Read<String64>(rsrc).ToString();
                rsrc = rsrc[Unsafe.SizeOf<String64>()..];
                break;
        }
        alert.Message = Encoding.ASCII.GetString(rsrc);
        return alert;
    }

    public readonly int ToBytes(Span<byte> dst)
    {
        int capacity = dst.Length;
        MemoryMarshal.Write(dst, in Header);
        dst = dst[Unsafe.SizeOf<Header<AlertType>>()..];
        string? message = Message;
        switch (Header.Type)
        {
            case AlertType.OrderRejected when Object is OrderRejected orderRejected:
                MemoryMarshal.Write(dst, in orderRejected);
                dst = dst[Unsafe.SizeOf<OrderRejected>()..];
                String64 symbol = new String64(Symbol ?? string.Empty);
                MemoryMarshal.Write(dst, in symbol);
                dst = dst[Unsafe.SizeOf<String64>()..];
                break;
            case AlertType.Exception when Object is Exception exception:
                message = exception.ToString(); // rendered here, on the alert thread, not at OnException on the caller's
                break;
        }
        if (!string.IsNullOrEmpty(message))
            dst = dst[Encoding.ASCII.GetBytes(message.AsSpan(0, Math.Min(message.Length, dst.Length)), dst)..];
        return capacity - dst.Length;
    }
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
                    WriteToSocket(alert);
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

    private void WriteToSocket(Alert alert)
    {
        if (alert.Object is OrderRejected orderRejected)
            alert.Symbol = GetSymbol(orderRejected.OrderHeader.OrderId.InstrumentId);
        int length = alert.ToBytes(_buffer);
        _logger.Write(_buffer.AsSpan(0, length));
    }

    // Header path, not GetInstrument: no lazy Instrument creation from this thread; a rejection may name an instrument this client never allocated.
    private string GetSymbol(int instrumentId)
    {
        try
        {
            int instrumentHeaderId = Context.GetInstrumentHeaderIdByInstrumentId(instrumentId).Read();
            return Context.GetInstrumentHeader(instrumentHeaderId).GetReadonlyRef().Symbology.Symbol;
        }
        catch
        {
            return $"UnknownSymbol_{instrumentId}";
        }
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
