using Data;
using Execution;
using Socket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Provider;

public class WebSocketServer
{
    public event Action<WebSocket>? Connected;
    private readonly HttpListener _listener;
    private Thread? _acceptThread;
    private volatile bool _running;

    public WebSocketServer(string host, int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{port}/");
    }

    public void Listen()
    {
        if (_running) return;
        _running = true;
        _listener.Start();
        _acceptThread = new Thread(AcceptLoop) { Name = "WebSocketServer-Accept", IsBackground = false };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext httpContext;
            try { httpContext = _listener.GetContext(); }
            catch { if (!_running) return; throw; }

            if (!httpContext.Request.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = 400;
                httpContext.Response.Close();
                continue;
            }

            // Accept the WS upgrade. This is async-only in the framework — block.
            HttpListenerWebSocketContext wsContext =
                httpContext.AcceptWebSocketAsync(subProtocol: null).GetAwaiter().GetResult();

            var ws = new WebSocket(wsContext.WebSocket);
            Connected?.Invoke(ws);
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }
}

public static class WebSocketClient
{
    public static WebSocket Connect(string host, int port)
    {
        var client = new System.Net.WebSockets.ClientWebSocket();
        var uri = new Uri($"ws://{host}:{port}/");
        client.ConnectAsync(uri, default).GetAwaiter().GetResult();
        return new WebSocket(client);
    }
}

// wraps a .net tcp client connection
public class WebSocket : IDisposable
{
    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private readonly byte[] _writeBuffer = new byte[64 * 1024];
    private Task<WebSocketReceiveResult>? _receiveTask;
    public event Action<WebSocket>? Disconnected;
    private bool _disposed;

    public WebSocket(System.Net.WebSockets.WebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    // Blocks until a message arrives, returns false on close/error.
    // Returned span is valid only until the next TryRead.
    public bool TryRead(out ReadOnlySpan<byte> rsrc)
    {
        rsrc = default;
        if (_disposed || _webSocket.State != WebSocketState.Open) return false;

        // Kick off a receive if none is in flight
        if (_receiveTask == null)
            _receiveTask = _webSocket.ReceiveAsync(_readBuffer, default);

        // Not done yet — no data available
        if (!_receiveTask.IsCompleted) return false;

        // Done — consume result
        Task<WebSocketReceiveResult> task = _receiveTask;
        _receiveTask = null;

        if (task.IsFaulted)
        {
            Disconnected?.Invoke(this);
            return false;
        }

        var result = task.Result;
        if (result.MessageType == WebSocketMessageType.Close)
        {
            Disconnected?.Invoke(this);
            return false;
        }
        if (!result.EndOfMessage)
            throw new InvalidOperationException("Message larger than buffer");

        rsrc = _readBuffer.AsSpan(0, result.Count);

        return true;
    }

    public void Write<T>(in T value) where T : unmanaged
    {
        ReadOnlySpan<byte> srcObj = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
        srcObj.CopyTo(_writeBuffer);
        Write(_writeBuffer.AsMemory(0, srcObj.Length));
    }

    public void Write(ReadOnlySpan<byte> rsrc)
    {
        rsrc.CopyTo(_writeBuffer.AsSpan());
        Write(_writeBuffer.AsMemory(0, rsrc.Length));
    }

    public void Write(ReadOnlyMemory<byte> rsrc)
    {
        if (_disposed || _webSocket.State != WebSocketState.Open)
            return;
        try
        {
            _webSocket.SendAsync(rsrc, WebSocketMessageType.Binary, endOfMessage: true, default)
               .GetAwaiter().GetResult();
        }
        catch
        {
            Disconnected?.Invoke(this);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _webSocket.Dispose(); } catch { }
    }
}


class RemoteContextProxy
{
    private ServerSocket _serverSocket;
    private ServerContext _serverContext;
    private WebSocket _webSocket;
    private ConcurrentDictionary<string, int> _clientIdByClientName = new();
    private ClientContext[] _clientContexts;
    public RemoteContextProxy(WebSocket webSocket, ServerContext serverContext, ServerSocket serverSocket)
    {
        _webSocket = webSocket;
        _serverContext = serverContext;
        _serverSocket = serverSocket;
        _clientContexts = new ClientContext[serverContext.ServerHeader.GetReadonlyRef().ClientIds.Length];
        _serverSocket.ClientAllocated += OnClientAllocated;
        _serverSocket.ClientDeallocated += OnClientDeallocated;
        _serverSocket.AllocateClientId = AllocateClientId;
        _serverSocket.DeallocateClient = _serverContext.DeallocateClient;
        _serverSocket.Listen();
    }

    private int AllocateClientId(in SocketHeader socketHeader)
    {
        string clientName = socketHeader.ClientName.ToString();
        AllocateClient allocateClient = new AllocateClient()
        {
            ClientId = -1,
            ClientName = socketHeader.ClientName,
        };
        _webSocket.Write(allocateClient);
        int clientId = -1;
        while (!_clientIdByClientName.TryGetValue(clientName, out clientId))
        {
            Thread.Sleep(1);
        }
        SocketHeader socketHeaderCopy = socketHeader;
        socketHeaderCopy.ClientId = clientId;
        clientId = _serverContext.AllocateClientId(in socketHeaderCopy);
        if (clientId != socketHeaderCopy.ClientId)
            throw new InvalidOperationException($"{GetType().Name}::AllocateClientId({clientName}): Failed. Requested clientId: {socketHeaderCopy.ClientId} but returned: {clientId}. Try Restarting.");
        return clientId;
    }

    private void OnClientAllocated(in SocketHeader socketHeader)
    {
        AllocateClient allocateClient = new AllocateClient()
        {
            ClientId = socketHeader.ClientId,
            ClientName = socketHeader.ClientName
        };
        _serverSocket.Write(socketHeader.ClientId, SocketChannel.Admin, allocateClient);
    }
    private void OnClientDeallocated(in SocketHeader socketHeader)
    {
        _clientIdByClientName.Remove(socketHeader.ClientName.ToString(), out _);
        AllocateClient allocateClient = new AllocateClient()
        {
            ClientId = socketHeader.ClientId,
            ClientName = socketHeader.ClientName,
        };
        _webSocket.Write(allocateClient);
    }

    public void Read()
    {
        ReadManualClients();
        ReadTCP();
    }

    public void ReadManualClients()
    {
        Bitset64 coreGroupIds = _serverContext.ServerHeader.GetReadonlyRef().CoreGroupIds;
        Bitset64 clientIds = _serverSocket.ClientIds();
        ReadOnlySpan<byte> rsrcObj = ReadOnlySpan<byte>.Empty;
        ReadStatus readStatus = ReadStatus.Empty;
        foreach (int clientId in clientIds)
        {
            // Drain every channel (channel 0 admin + channels 1..N execution) and forward to the web side.
            Bitset64 channels = coreGroupIds;
            while (!channels.IsEmpty)
            {
                int channel = channels.LowestSet;
                channels.Clear(channel);
                while ((readStatus = _serverSocket.TryRead(clientId, channel, out rsrcObj)) == ReadStatus.New)
                {
                    if (rsrcObj[0] == (byte)OrderType.OrderTarget)
                    {
                        ref readonly OrderTarget orderTarget = ref MemoryMarshal.AsRef<OrderTarget>(rsrcObj);
                        Console.WriteLine(orderTarget);
                    }
                    _webSocket.Write(rsrcObj);
                }
            }
        }
    }

    public void ReadTCP()
    {
        while(_webSocket.TryRead(out ReadOnlySpan<byte> rsrc))
        {
            int headerSize = sizeof(int) * 2;
            int arrayId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(rsrc);
            int index = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(rsrc.Slice(sizeof(int)));
            ReadOnlySpan<byte> rsrcObj = rsrc.Slice(headerSize);


            byte type = rsrcObj[0];


            switch (type)
            {
                case (byte)AllocateType.Client:
                    {
                        ref readonly AllocateClient allocateClient = ref MemoryMarshal.AsRef<AllocateClient>(rsrcObj);
                        Console.WriteLine(allocateClient);
                        SocketHeader socketHeader = new SocketHeader(_serverContext.ServerHeader.GetReadonlyRef().ServerName.ToString(), allocateClient.ClientName.ToString(), [], [], 0);
                        
                        int clientId = _serverContext.AllocateClientId(in socketHeader);
                        if (clientId != allocateClient.ClientId)
                            throw new InvalidOperationException($"{GetType().Name}::AllocateClient({allocateClient.ClientName}): Failed. Requested clientId: {allocateClient.ClientId} but returned: {clientId}. Try Restarting.");
                        _clientContexts[clientId] = new ClientContext(allocateClient.ClientName.ToString(), _serverContext.ServerHeader.GetReadonlyRef().ServerName.ToString(), Access.Write);
                        _clientIdByClientName.TryAdd(allocateClient.ClientName.ToString(), allocateClient.ClientId);
                        break;
                    }
                case (byte)AllocateType.Instrument:
                    {
                        ref readonly AllocateInstrument allocateInstrument = ref MemoryMarshal.AsRef<AllocateInstrument>(rsrcObj);
                        Console.WriteLine(allocateInstrument);
                        if (allocateInstrument.ClientId < 0)
                        {
                            int instrumentId = _serverContext.AllocateInstrument(allocateInstrument.InstrumentHeaderId);
                            if (instrumentId != allocateInstrument.InstrumentId)
                                throw new InvalidOperationException($"{GetType().Name}::AllocateInstrument({allocateInstrument.Symbol}): Failed. Requested instrumentId: {allocateInstrument.InstrumentId} but returned: {instrumentId}. Try Restarting.");
                        }
                        else
                        {
                            _serverContext.AllocateInstrument(allocateInstrument.ClientId, allocateInstrument.InstrumentId);
                            _serverSocket.Write(allocateInstrument.ClientId, SocketChannel.Admin, rsrcObj);
                        }
                        break;
                    }
                case (byte)OrderType.OrderTarget:
                    {
                        ref readonly OrderTarget orderTarget = ref MemoryMarshal.AsRef<OrderTarget>(rsrcObj);
                        Console.WriteLine(orderTarget);
                        if (_clientContexts[orderTarget.OrderHeader.OrderId.ClientId] == null) //dont overwrite OrderTarget if it originated from here.
                            _serverContext.Mirror(arrayId, index, rsrcObj);
                        break;
                    }
                case (byte)OrderType.OrderRejected:
                    {
                        ref readonly OrderRejected orderRejected = ref MemoryMarshal.AsRef<OrderRejected>(rsrcObj);
                        _serverSocket.Write(orderRejected.OrderHeader.OrderId.ClientId, _serverContext.GetInstrument(orderRejected.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, rsrcObj);
                        break;
                    }
                case (byte)OrderType.OrderState:
                    {
                        _serverContext.Mirror(arrayId, index, rsrcObj);
                        ref readonly OrderState orderState = ref MemoryMarshal.AsRef<OrderState>(rsrcObj);
                        Console.WriteLine(orderState);
                        if (_clientContexts[orderState.OrderHeader.OrderId.ClientId] != null)
                            _serverSocket.Write(orderState.OrderHeader.OrderId.ClientId, _serverContext.GetInstrument(orderState.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, rsrcObj);
                        break;
                    }
                
                case (byte)OrderType.Fill:
                    {
                        ref readonly Fill fill = ref MemoryMarshal.AsRef<Fill>(rsrcObj);
                        _serverSocket.Write(fill.OrderHeader.OrderId.ClientId, _serverContext.GetInstrument(fill.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, rsrcObj);
                        break;
                    }
                case (byte)OrderType.Position:
                    {
                        _serverContext.Mirror(arrayId, index, rsrcObj);
                        ref readonly PositionHeader position = ref MemoryMarshal.AsRef<PositionHeader>(rsrcObj);
                        _serverSocket.Write(position.OrderHeader.OrderId.ClientId, _serverContext.GetInstrument(position.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, rsrcObj);
                        break;
                    }
                default:
                    {
                        _serverContext.Mirror(arrayId, index, rsrcObj);
                        break;
                    }
            }
        }
    }
}

public static class ContextProxy
{
    public const int HeaderSize = sizeof(int) * 2;
    public static ReadOnlyMemory<byte> NewPacket(Memory<byte> dst, int arrayId, int index, ReadOnlySpan<byte> rsrcObj)
    {
        rsrcObj.CopyTo(dst.Span.Slice(HeaderSize));
        WriteHeader(dst, arrayId, index);
        return dst.Slice(0, HeaderSize + rsrcObj.Length);
    }

    public static void WriteHeader(Memory<byte> dst, int arrayId, int index)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Span, arrayId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Span.Slice(sizeof(int)), index);
    }
}


// SERVER SIDE ===== ALL THIS RUNS ON CME DC3 

public class LocalContextProxy
{
    public event Action<LocalContextProxy>? Disconnected;
    public readonly FileSystemPath ServerName;
    private ManualClient?[] _manualClients;
    private WebSocket _webSocket;
    private byte[] _writeBuffer = new byte[64*1024];

    public LocalContextProxy(WebSocket webSocket, FileSystemPath serverName, int capacity)
    {
        ServerName = serverName;
        _webSocket = webSocket;
        _manualClients = new ManualClient[capacity];

    }

    public void Write<T>(in T value) where T : unmanaged
    {
        Console.WriteLine(value);
        ReadOnlySpan<byte> rsrcObj = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
        ReadOnlyMemory<byte> rsrc = ContextProxy.NewPacket(_writeBuffer, -1, -1, rsrcObj);
        Write(rsrc);
    }

    public void Write(ReadOnlyMemory<byte> rsrc)
    {
        _webSocket.Write(rsrc);
    }

    private void OnPosition(in PositionHeader position) => Write(position);
    private void OnFill(in Fill fill) => Write(fill);
    private void OnOrderRejected(in OrderRejected orderRejected) => Write(orderRejected);

    private void OnAllocateInstrument(AllocateInstrument allocateInstrument)
    {
        Instrument instrument = _manualClients[allocateInstrument.ClientId]!.GetInstrument(allocateInstrument.InstrumentHeaderId);
        allocateInstrument.InstrumentId = instrument.InstrumentId;
        Write(allocateInstrument);
    }

    private void OnAllocateClient(AllocateClient allocateClient)
    {
        if (allocateClient.ClientId < 0) // create
        {
            FileSystemPath clientName = allocateClient.ClientName.ToString();
            ManualClient manualClient = new ManualClient(clientName, ServerName);
            manualClient.OrderRejected += OnOrderRejected;
            manualClient.Fill += OnFill;
            manualClient.PositionHeader += OnPosition;
            int clientId = manualClient.Context.ClientId;
            _manualClients[clientId] = manualClient;
            allocateClient.ClientId = clientId;
            Write(allocateClient);
        }
        else // dispose
        {
            int clientId = allocateClient.ClientId;
            ManualClient manualClient = _manualClients[clientId]!;
            manualClient.Dispose();
            manualClient.OrderRejected -= OnOrderRejected;
            manualClient.Fill -= OnFill;
            manualClient.PositionHeader -= OnPosition;
            _manualClients[clientId] = null;
        }
    }

    public void Read()
    {
        ReadManualClients();
        ReadTCP();
    }

    private void ReadManualClients()
    {
        foreach (ManualClient? manualClient in _manualClients)
        {
            if (manualClient == null)
                continue;
            manualClient.ReadSocket();
        }
    }

    private void ReadTCP()
    {
        while (_webSocket.TryRead(out ReadOnlySpan<byte> rsrc))
        {
            byte type = rsrc[0];
            switch (type)
            {
                case (byte)AllocateType.Client:
                    {
                        ref readonly AllocateClient allocateClient = ref MemoryMarshal.AsRef<AllocateClient>(rsrc);
                        Console.WriteLine(allocateClient);
                        OnAllocateClient(allocateClient);
                        break;
                    }
                case (byte)AllocateType.Instrument:
                    {
                        ref readonly AllocateInstrument allocateInstrument = ref MemoryMarshal.AsRef<AllocateInstrument>(rsrc);
                        Console.WriteLine(allocateInstrument);
                        OnAllocateInstrument(allocateInstrument);
                        break;
                    }
                case (byte)OrderType.OrderTarget:
                    {
                        OrderTarget orderTarget = MemoryMarshal.AsRef<OrderTarget>(rsrc);
                        Console.WriteLine(orderTarget);
                        _manualClients[orderTarget.OrderHeader.OrderId.ClientId]!.OnOrderTarget(ref orderTarget);
                        break;
                    }
                case (byte)OrderType.OrderRejected:
                    {
                        OrderRejected orderRejected = MemoryMarshal.AsRef<OrderRejected>(rsrc);
                        Console.WriteLine(orderRejected);
                        //_manualClients[orderRejected.OrderHeader.OrderId.ClientId]!.OnOrderRejected(ref orderRejected);
                        break;
                    }
                case (byte)ControlType.AlgoStatus:
                    {
                        ref readonly ControlAlgoStatus controlAlgoStatus = ref MemoryMarshal.AsRef<ControlAlgoStatus>(rsrc);
                        Console.WriteLine(controlAlgoStatus);
                        _manualClients[controlAlgoStatus.ClientId]!.OnControlAlgoStatus(in controlAlgoStatus);
                        break;
                    }
                
            }
        }
    }
}



public class LocalTCPServerContext
{
    private ServerHeader _serverHeader;
    private WebSocketServer _webSocketServer = new WebSocketServer("localHost", 5000);
    private ServerContext _serverContext;
    private ConcurrentDictionary<LocalContextProxy, bool> _proxies = new();
    int _snapshot = 0;
    private byte[] _writeBuffer = new byte[64 * 1024];

    public LocalTCPServerContext()
    {
        _serverContext = ContextManager.ServerContext;
        _serverHeader = _serverContext.ServerHeader.Read();
        _webSocketServer.Connected += OnConnected;
        _webSocketServer.Listen();
    }

    private void OnDisconnected(LocalContextProxy proxy)
    {
        _proxies.TryRemove(proxy, out _);
    }
    private void OnConnected(WebSocket webSocket)
    {
        LocalContextProxy proxy = new LocalContextProxy(webSocket, _serverHeader.ServerName.ToString(), _serverHeader.ClientIds.Length);
        proxy.Disconnected += OnDisconnected;
        _proxies.TryAdd(proxy, true);
        Interlocked.Or(ref _snapshot, 1);
    }

    public void Read()
    {
        foreach (LocalContextProxy proxy in _proxies.Keys)
            proxy.Read();
    }

    public void Update()
    {
        bool snapshot = Interlocked.And(ref _snapshot, 0) == 1;

        _serverHeader = _serverContext.ServerHeader.Read();
        //Update(-1, -1, bytes);

        Span<byte> dstObj = _writeBuffer.AsSpan().Slice(ContextProxy.HeaderSize);

        for (int arrayId = 0; arrayId < _serverContext.SharedArraysCount; arrayId++)
        {
            foreach (var (index, rdstObj) in _serverContext.EnumerateSharedArray(arrayId, snapshot, dstObj))
            {
                Memory<byte> dst = _writeBuffer.AsMemory().Slice(0, ContextProxy.HeaderSize + rdstObj.Length);
                if (rdstObj[0] == (byte)OrderType.OrderState)
                {
                    ref readonly OrderState orderState = ref MemoryMarshal.AsRef<OrderState>(rdstObj);
                    Console.WriteLine(orderState);  
                }
                Update(arrayId, index, dst);
            }
        }
    }   

    private void Update(int arrayId, int index, Memory<byte> src)
    {
        ContextProxy.WriteHeader(src, arrayId, index);
        foreach (LocalContextProxy proxy in _proxies.Keys)
        {
            proxy.Write(src);
        }
    }
}
    