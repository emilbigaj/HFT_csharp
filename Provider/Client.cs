//BEGIN_FILE HFT/Provider/Client.cs
using Data;
using Execution;
using Socket;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;
using System;
using System.Threading;

namespace Provider;

public sealed class ManualClient : Client
{
    public ClientContext _algoClientContext { get; }
    public override int StrategyId() => _algoClientContext.ClientId;

    // Thread-safe entry queue. Callers enqueue from any thread via OnOrderTarget/OnControlAlgoStatus;
    // the owner thread drains via WriteSocket() before each ReadSocket(), so the socket and
    // _isOrderActive bitset only ever see a single mutator.
    private readonly ConcurrentQueue<Action> _writeQueue = new();

    public ManualClient(string clientName, string serverName) : base(clientName.EndsWith("_GUI") ? clientName : clientName + "_GUI", serverName)
    {
        string algoClientName = clientName.EndsWith("_GUI") ? clientName[..^4] : clientName;
        _algoClientContext = new ClientContext(algoClientName, serverName, Access.Read);
        // Subscribe the GUI client to the same instruments the algo trades. GetInstrument blocks per
        // instrument (opens its ring, applies the snapshot) — serial, so the single join buffer is safe.
        foreach(int instrumentId in _algoClientContext.InstrumentIds)
        {
            GetInstrument(_algoClientContext.GetInstrument(instrumentId).Header.InstrumentHeaderId);
        }
    }

    // Any-thread API: enqueue and return. Result flows back via OrderState/OrderRejected events
    // when the owner thread processes the queue in WriteSocket().
    public override bool OnOrderTarget(ref OrderTarget orderTarget)
    {
        OrderTarget copy = orderTarget;
        _writeQueue.Enqueue(() => base.OnOrderTarget(ref copy));
        return true;
    }

    public void OnControlAlgoStatus(in ControlAlgoStatus controlAlgoStatus)
    {
        ControlAlgoStatus copy = controlAlgoStatus;
        _writeQueue.Enqueue(() => _socket.Write(SocketChannel.Admin, in copy));
    }

    // Owner thread drains pending mutations. Call before ReadSocket() each tick.
    public void WriteSocket()
    {
        while (_writeQueue.TryDequeue(out Action? action))
            action();
    }

    // The GUI reads the server's authoritative book directly (ContextManager.ServerContext) at refresh —
    // it never subscribes to a per-instrument ring or maintains a replica.
    protected override void OpenInstrumentDataSocket(int instrumentId, string symbol)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override bool Amend(ref OrderTarget orderTarget)
    {
        ulong clientOrderId = orderTarget.OrderHeader.OrderId;
        int localOrderIndex = orderTarget.OrderHeader.OrderId.LocalIndex;
        int originalClientId = OrderIdAllocator.GetClientId(clientOrderId);
        bool isManualOrder = originalClientId == _clientId;
        ref SharedArrayEntry<OrderTarget> orderTargetEntry = ref (isManualOrder ? ref Context.GetOrderTarget(localOrderIndex) : ref _algoClientContext.GetOrderTarget(localOrderIndex));
        ref readonly OrderTarget existingOrderTarget = ref orderTargetEntry.GetReadonlyRef();
        if (existingOrderTarget.OrderHeader.OrderId != clientOrderId)
        {
            return false;
        }
        orderTarget.OrderHeader.Seq = Math.Max(existingOrderTarget.OrderHeader.Seq + 1, orderTarget.OrderHeader.Seq);
        return Send(ref orderTarget);
    }

    protected override bool Send(ref OrderTarget orderTarget)
    {
        if (Validate(ref orderTarget, out Bitset64 orderRejectedReasons))
        {
            int originalClientId = OrderIdAllocator.GetClientId(orderTarget.OrderHeader.OrderId);
            bool isManualOrder = originalClientId == _clientId;
            if (isManualOrder)
            {
                int localOrderIndex = orderTarget.OrderHeader.OrderId.LocalIndex;
                Context.GetOrderTarget(localOrderIndex).Write(in orderTarget);
            }
            _socket.Write(Context.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, in orderTarget);
            return true;
        }
        else
        {           
            Reject(in orderTarget, orderRejectedReasons, OrderRejectedSource.Client);
            return false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _algoClientContext.Dispose();
    }

}

public sealed class AlgoClient : Client
{


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int StrategyId() => _clientId;

    public AlgoClient(string clientName, string serverName) : base(clientName, serverName)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override bool Amend(ref OrderTarget orderTarget)
    {
        //using Latency latency = new Latency((int)CallId.ClientAmend);

        int localOrderIndex = orderTarget.OrderHeader.OrderId.LocalIndex;
        ref readonly OrderTarget existingOrderTarget = ref Context.GetOrderTarget(localOrderIndex).GetReadonlyRef();

        if (existingOrderTarget.OrderHeader.OrderId != orderTarget.OrderHeader.OrderId)
        {
            return false;
        }
        orderTarget.OrderHeader.Seq = Math.Max(existingOrderTarget.OrderHeader.Seq + 1, orderTarget.OrderHeader.Seq);

        return Send(ref orderTarget);

    }
}

public delegate void OrderRejectedHandler(in OrderRejected orderRejected);
public delegate void OrderTargetHandler(in OrderTarget orderTarget);
public delegate void OrderStatedHandler(in OrderState orderState);
public delegate void PositionHeaderHandler(in PositionHeader positionHeader);
public delegate void FillHandler(in Fill fill);


public abstract class Client
{
    public Timestamp ExchangeTimestamp { get; set; } = Clock.Now;
    public Timestamp NicTimestamp { get; set; } = Clock.Now;

    public event OrderRejectedHandler? OrderRejected;
    public event OrderStatedHandler? OrderState;
    public event OrderTargetHandler? OrderTarget;
    public event PositionHeaderHandler? PositionHeader;
    public event Action<Instrument>? Instrument;


    public event FillHandler? Fill;
    private bool _isDisposed = false;

    public RiskLayer RiskLayer { get; }
    public string ClientName { get; }
    public string ServerName { get; }

    public ClientContext Context { get; }
    protected readonly ClientSocket _socket;
    // CoreGroupIds of the instruments this client has allocated => which execution channels ReadSocket drains.
    private Bitset64 _coreGroupIds = new Bitset64();

    // Per-instrument broadcast ring readers (strategy live-delta path); the GUI reads the server's book directly.
    private readonly ReadOnlySocket?[] _instrumentData;

    protected int _clientId = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public abstract int StrategyId();
    public int ClientId() => _clientId;

    public Client(string clientName, string serverName)
    {
        ClientName = clientName;
        ServerName = serverName;

        string auditDirectoryPath = Provider.Context.GetAuditDirectoryPath(ClientName);
        Directory.CreateDirectory(auditDirectoryPath);
        if (Clock.Mode == ClockMode.Simulation)
        {
            foreach (string filePath in Directory.EnumerateFiles(auditDirectoryPath))
            {
                File.Delete(filePath);
            }
        }

        // Channel 0 is admin; channels 1..7 are per-CoreGroupId execution channels. Size them from the
        // server's declared CoreGroupIds (read before connecting).
        int[] channelLengths = SocketChannel.BuildChannelLengths(ContextManager.ServerContext.ServerHeader.GetReadonlyRef().CoreGroupIds);
        _socket = new ClientSocket(ClientName, ServerName, channelLengths, channelLengths);
        _socket.Connect();

        ReadOnlySpan<byte> rsrc = ReadAdmin();
        _clientId = OnClientAllocated(in MemoryMarshal.AsRef<AllocateClient>(rsrc));

        Context = new ClientContext(ClientName, ServerName, Access.Write);

        _instrumentData = new ReadOnlySocket?[Context.ServerHeader.GetReadonlyRef().InstrumentIds.Length];

        RiskLayer = new RiskLayer(ContextManager.ServerContext, OrderRejectedSource.Client);
    }

    public ReadOnlySpan<byte> ReadAdmin()
    {
        ReadOnlySpan<byte> rsrc;
        while (_socket.TryRead(SocketChannel.Admin, out rsrc) != ReadStatus.New)
        {
            Thread.Sleep(1);
        }
        return rsrc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int OnClientAllocated(in AllocateClient allocate)
    {
        return allocate.ClientId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Instrument GetInstrument(int instrumendHeaderId)
    {
        if (Context.TryGetInstrumentId(instrumendHeaderId, out int instrumentId))
            return Context.GetInstrument(instrumentId);

        _socket.Write(SocketChannel.Admin, new AllocateInstrument() { ClientId = _clientId, InstrumentHeaderId = instrumendHeaderId });

        ReadOnlySpan<byte> rsrc = ReadAdmin();
        return OnInstrumentAllocated(in MemoryMarshal.AsRef<AllocateInstrument>(rsrc));

    }

    private Instrument OnInstrumentAllocated(in AllocateInstrument allocated)
    {
        int instrumentId = allocated.InstrumentId;
        Instrument instrument = Context.GetInstrument(instrumentId);
        Context.GetPosition(instrument.InstrumentId);
        RiskLayer.OnInstrument(instrument.InstrumentId);
        OpenInstrumentDataSocket(instrumentId, allocated.Symbol.ToString());
        _coreGroupIds.Set(instrument.Header.CoreGroupId);
        Instrument?.Invoke(instrument);
        if (instrument.ProductGroupId < 0)
            Context.AllocateProductGroupId(instrument, instrument.Symbology.Root);
        return instrument;
    }

    // Strategy: open the per-instrument ring and seed the replica from the server's authoritative book.
    // The GUI overrides this to a no-op (it reads the server's book directly, never a ring/replica).
    protected virtual void OpenInstrumentDataSocket(int instrumentId, string symbol)
    {
        if (_instrumentData[instrumentId] != null)
            return;

        string name = SocketChannel.GetInstrumentDataName(ServerName, symbol);
        _instrumentData[instrumentId] = new ReadOnlySocket(name, SharedMemory.CreateOrOpen(name, SocketChannel.InstrumentDataChannelLength));

        //Drain to protect against Lapping
        ReadOnlySocket reader = _instrumentData[instrumentId]!;
        while (reader.TryRead(out _) == ReadStatus.New) { }

        MarketByPrice64 snapshot = ContextManager.ServerContext.GetMarketByPrice64(instrumentId).Read();
        Context.GetMarketByPrice64(instrumentId).Write(in snapshot);
    }
    
    public void ReadSocket()
    {
        ReadOnlySpan<byte> rsrc;
        ReadStatus readStatus;

        // Drain each execution channel this client uses (channel index == CoreGroupId).
        Bitset64 coreGroupIds = _coreGroupIds;
        while (!coreGroupIds.IsEmpty)
        {
            int coreGroupId = coreGroupIds.LowestSet;
            coreGroupIds.Clear(coreGroupId);
            while (true)
            {
                using (Latency latency = new Latency(CallId.ClientReadExecution))
                {
                    readStatus = _socket.TryRead(coreGroupId, out rsrc);
                    if (readStatus != ReadStatus.New)
                    {
                        latency.Cancel();
                        break;
                    }
                }
                OnSocketMessage(rsrc);
            }
        }

        // pump each subscribed instrument's broadcast ring (deltas + trades) into our own book
        Bitset64 instrumentIds = Context.InstrumentIds;
        while (!instrumentIds.IsEmpty)
        {
            int instrumentId = instrumentIds.LowestSet;
            instrumentIds.Clear(instrumentId);
            ReadInstrumentData(instrumentId);
        }
    }

    private void ReadInstrumentData(int instrumentId)
    {
        ReadOnlySocket? readOnlySocket = _instrumentData[instrumentId];
        if (readOnlySocket == null)
            return;

        ReadOnlySpan<byte> bytes;
        while (true)
        {
            using (Latency latency = new Latency(CallId.ClientReadData))
            {
                if (readOnlySocket.TryRead(out bytes) != ReadStatus.New)
                {
                    latency.Cancel();
                    break;
                }
            }
            OnInstrumentData(instrumentId, bytes);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnInstrumentData(int instrumentId, ReadOnlySpan<byte> bytes)
    {
        byte type = bytes[0];
        switch (type)
        {
            case (byte)TickType.MarketByPriceSnapshot:
            case (byte)TickType.MarketByPriceUpdate:
            case (byte)TickType.MarketByPriceDelta:
                OnMarketByPrice(instrumentId, bytes);
                break;
            case (byte)TickType.Trade:
            {
                ref readonly Trade trade = ref MemoryMarshal.AsRef<Trade>(bytes);
                OnTrade(instrumentId, in trade);
                    break;
            }
            case (byte)TickType.Settlement:
            {
                ref readonly Settlement settlement = ref MemoryMarshal.AsRef<Settlement>(bytes);
                Context.GetInstrument(instrumentId).OnSettlement(in settlement);
                break;
            }
            default:
                throw new NotImplementedException($"Unknown instrument data type: {type}");
        }
    }

    private void OnTrade(int instrumentId, in Trade trade)
    {
        NicTimestamp = trade.TickHeader.NicTimestamp;
        ExchangeTimestamp = trade.TickHeader.ExchangeTimestamp;
        Context.GetInstrument(instrumentId).OnTrade(in trade);

    }

    private void OnMarketByPrice(int instrumentId, ReadOnlySpan<byte> rsrc)
    {
        ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(rsrc);
        NicTimestamp = mbp.TickHeader.NicTimestamp;
        ExchangeTimestamp = mbp.TickHeader.ExchangeTimestamp;

        ref SharedArrayEntry<MarketByPrice64> entry = ref Context.GetMarketByPrice64(instrumentId);
        ref MarketByPrice64 mbp64 = ref entry.GetRef();

        Instrument instrument = Context.GetInstrument(instrumentId);
        if (mbp.TickHeader.TickType == TickType.MarketByPriceDelta)
        {
            ReadOnlySpan<byte> deltaSpan = rsrc;
            
            entry.AcquireLock();
            bool isDeltas = mbp64.TrySet(deltaSpan);
            entry.ReleaseLock();
            if (!isDeltas)
                return;

            instrument.OnMarketByPriceDelta(in mbp, deltaSpan);
        }
        else if (mbp.TickHeader.TickType == TickType.MarketByPriceUpdate)
        {
            Span<byte> deltaSpan = stackalloc byte[rsrc.Length];
            rsrc.CopyTo(deltaSpan);

            entry.AcquireLock();
            bool isDeltas = mbp64.TrySetAsDeltas(deltaSpan);
            entry.ReleaseLock();

            if (!isDeltas)
                return;

            ref readonly MarketByPrice delta = ref MemoryMarshal.AsRef<MarketByPrice>(deltaSpan);
            instrument.OnMarketByPriceDelta(in delta, deltaSpan);
        }
        else if (mbp.TickHeader.TickType == TickType.MarketByPriceSnapshot)
        {
            ref readonly MarketByPrice future = ref MemoryMarshal.AsRef<MarketByPrice>(rsrc);
            ReadOnlySpan<byte> futureSpan = rsrc;
            Span<byte> pastSpan = stackalloc byte[MarketByPrice.SizeOf(64, 64)];
            Span<byte> updateSpan = stackalloc byte[MarketByPrice.SizeOf(128, 128)];

            mbp64.CopyToSnapshot(instrumentId, pastSpan);
            ref MarketByPrice update = ref MarketByPrice.SnapshotAsUpdate(pastSpan, futureSpan, updateSpan);
            
            Span<byte> deltaSpan = updateSpan.Slice(0, update.SizeOf());

            entry.AcquireLock();
            bool isDeltas = mbp64.TrySetAsDeltas(deltaSpan);
            entry.ReleaseLock();

            if (!isDeltas)
                return;

            ref readonly MarketByPrice delta = ref MemoryMarshal.AsRef<MarketByPrice>(deltaSpan);
            instrument.OnMarketByPriceDelta(in delta, deltaSpan);
        }
    }

    public ulong States { get; private set; }
    public ulong Rejections { get; private set; }
    public ulong Targets { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnSocketMessage(ReadOnlySpan<byte> rsrcObj)
    {
        byte type = rsrcObj[0];
        switch (type)
        {
            case (byte)OrderType.OrderState:
                States += 1;
                OnOrderState(rsrcObj);
                break;
            case (byte)OrderType.OrderRejected:
                Rejections += 1;
                OnOrderRejected(rsrcObj);
                break;
            case (byte)OrderType.Fill:
                OnFill(rsrcObj);
                break;
            case (byte)OrderType.Position:
                OnPositionHeader(rsrcObj);
                break;
            default:
                throw new NotImplementedException($"Unknown message type: {type}");
        }
    }

    


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnFill(ReadOnlySpan<byte> rsrcObj)
    {
        ref readonly Fill fill = ref MemoryMarshal.AsRef<Fill>(rsrcObj);
        NicTimestamp = fill.OrderHeader.NicTimestamp;
        ExchangeTimestamp = fill.OrderHeader.ExchangeTimestamp;
        Position position = Context.GetPosition(fill.OrderHeader.OrderId.InstrumentId);
        int productGroupId = position.Instrument.ProductGroupId;
        Context.GetMessageEfficiency(productGroupId).GetRef().OnFill(fill.OrderProfile.Quantity);
        position.OnFill(fill);
        Fill?.Invoke(in fill);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnPositionHeader(ReadOnlySpan<byte> rsrcObj)
    {
        ref readonly PositionHeader positionHeader = ref MemoryMarshal.AsRef<PositionHeader>(rsrcObj);
        NicTimestamp = positionHeader.OrderHeader.NicTimestamp;
        ExchangeTimestamp = positionHeader.OrderHeader.ExchangeTimestamp;
        Context.GetPosition(positionHeader.OrderHeader.OrderId.InstrumentId).OnPositionHeader(in positionHeader);
        PositionHeader?.Invoke(in positionHeader);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnOrderState(ReadOnlySpan<byte> rsrcObj)
    {
        ref readonly OrderState orderState = ref MemoryMarshal.AsRef<OrderState>(rsrcObj);
        NicTimestamp = orderState.OrderHeader.NicTimestamp;
        ExchangeTimestamp = orderState.OrderHeader.ExchangeTimestamp;
        int localOrderIndex = orderState.OrderHeader.OrderId.LocalIndex;
        ref OrderTarget orderTarget = ref Context.GetOrderTarget(localOrderIndex).GetRef();
        if (orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId)
        {
            if (orderState.OrderStateStatus == OrderStateStatus.Done)
            {
                orderTarget.OrderTargetStatus = OrderStateStatus.Done;
                Context.GetPosition(orderState.OrderHeader.OrderId.InstrumentId).OnOrderDone(localOrderIndex);
                OrderIdAllocator.Free(ref _isOrderActive, orderState.OrderHeader.OrderId);
            }
            else if (orderState.OrderHeader.Seq >= orderTarget.OrderHeader.Seq)
            {
                orderTarget.OrderTargetStatus = OrderStateStatus.Done;
            }
        }
        OrderState?.Invoke(in orderState);
    }

    

    private Bitset64 _isOrderActive = new Bitset64();
    CMEGetWeightedMessage CMEGetWeightedMessage = new CMEGetWeightedMessage();

    public virtual bool OnOrderTarget(ref OrderTarget orderTarget)
    {
        orderTarget.OrderHeader.NicTimestamp = NicTimestamp;
        orderTarget.OrderHeader.ExchangeTimestamp = ExchangeTimestamp;

        bool sent = false;
        if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
        {
            sent = Create(ref orderTarget);
        }
        else
        {
            sent = Amend(ref orderTarget);
        }
        if (sent)
        {
            Targets += 1UL;
            int productGroupId = Context.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId).ProductGroupId;
            Context.GetMessageEfficiency(productGroupId).GetRef().Send(orderTarget.OrderTargetAction, CMEGetWeightedMessage);
            OrderTarget?.Invoke(in orderTarget);
        }
        return sent;

    }





    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool Create(ref OrderTarget orderTarget)
    {
        orderTarget.OrderHeader.Seq = 1;
        orderTarget.OrderTargetAction = OrderTargetAction.Create;

        ref OrderId orderId = ref orderTarget.OrderHeader.OrderId;
        orderId.ClientId = _clientId;
        orderId.StrategyId = StrategyId();

        if (!OrderIdAllocator.TryAllocate(ref _isOrderActive, ref orderId))
        {
            Bitset64 orderRejectedReasons = new Bitset64();
            orderRejectedReasons.Set((int)OrderRejectedReason.CantAllocateClientOrderId);
            Reject(in orderTarget, orderRejectedReasons, OrderRejectedSource.Client);
            return false;
        }
        int localOrderIndex = orderTarget.OrderHeader.OrderId.LocalIndex;

        if (Send(ref orderTarget))
        {
            Context.GetPosition(orderTarget.OrderHeader.OrderId.InstrumentId).OnOrderActive(localOrderIndex);
            return true;
        }
        else
        {
            OrderIdAllocator.Free(ref _isOrderActive, orderTarget.OrderHeader.OrderId);
            return false;
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool Validate(ref OrderTarget orderTarget, out Bitset64 orderRejectedReasons)
    {
        //using Latency latency = new Latency((int)CallId.ClientValidate);
        bool isValid = RiskLayer.ValidateOrder(in orderTarget, out orderRejectedReasons);
        orderTarget.OrderTargetStatus = isValid ? OrderStateStatus.Active : OrderStateStatus.Done;
        return isValid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected virtual bool Send(ref OrderTarget orderTarget)
    {
        //using Latency latency = new Latency((int)CallId.ClientSend);

        if (Validate(ref orderTarget, out Bitset64 orderRejectedReasons))
        {
            //using Latency latency1 = new Latency((int)CallId.ClientWrite);
            int localOrderIndex = orderTarget.OrderHeader.OrderId.LocalIndex;
            Context.GetOrderTarget(localOrderIndex).Write(in orderTarget);
            _socket.Write(Context.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, in orderTarget);
            return true;
        }
        else
        {
            Reject(in orderTarget, orderRejectedReasons, OrderRejectedSource.Client);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnOrderRejected(ReadOnlySpan<byte> bytes)
    {
        ref readonly OrderRejected orderRejected = ref MemoryMarshal.AsRef<OrderRejected>(bytes);
        NicTimestamp = orderRejected.OrderHeader.NicTimestamp;
        ExchangeTimestamp = orderRejected.OrderHeader.ExchangeTimestamp;
        int localOrderIndex = orderRejected.OrderHeader.OrderId.LocalIndex;
        ref OrderTarget orderTarget = ref Context.GetOrderTarget(localOrderIndex).GetRef();
        bool isTargetDone = orderRejected.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId && orderTarget.OrderHeader.Seq == orderRejected.OrderHeader.Seq;
        if (isTargetDone)
            orderTarget.OrderTargetStatus = OrderStateStatus.Done;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void Reject(in OrderTarget orderTarget, Bitset64 orderRejectedReasons, OrderRejectedSource orderRejectedSource)
    {
        OrderRejected orderRejected = new OrderRejected()
        {
            OrderHeader = orderTarget.OrderHeader,
            OrderProfile = orderTarget.OrderProfile,
            OrderTargetAction = orderTarget.OrderTargetAction,
            OrderRejectedReasons = orderRejectedReasons,
            OrderRejectedSource = orderRejectedSource
        };

        if (!orderRejectedReasons.IsEmpty && orderRejectedReasons.IsSubsetOf(Execution.OrderRejected.OrderDiscarded))
            return;

        if (Clock.Mode == ClockMode.Simulation && orderRejectedReasons.Raw == 1UL << (int)OrderRejectedReason.TooManyOrdersPerSession)
            return;

        

        _socket.Write(Context.GetInstrument(orderRejected.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId, in orderRejected);
        OrderRejected?.Invoke(in orderRejected);
    }

    protected abstract bool Amend(ref OrderTarget orderTarget);

    public Position GetPosition(int instrumentId)
    {
        return Context.GetPosition(instrumentId);
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
            return;

        if (_isOrderActive.IsFull)
            throw new InvalidOperationException();
        
        _isDisposed = true;
        foreach (ReadOnlySocket? reader in _instrumentData)
            reader?.Dispose();
        _socket.Close();
        _socket.Dispose();
        Context.Dispose();
    }
}
//END_FILE HFT/Provider/Client.cs