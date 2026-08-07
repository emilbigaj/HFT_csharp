//BEGIN_FILE HFT/Provider/Server.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Data;
using Execution;
using Socket;
using Tools;

namespace Provider;

// Port of HFT/Provider/Server.hpp, kept as close to line-for-line as C# allows so the two can be
// diffed. Server owns no latency: everything here happens the moment it is called. ServerSimulator
// wraps it and supplies the timing —
//
//   exchange -> latency queue -> ServerSimulator -> Server -> socket -> client
//   client   -> socket -> Server -> ServerSimulator -> latency queue -> exchange
//
// so the inbound exchange-facing methods (OnFill/OnOrderState/OnOrderRejected/OnMarketByPrice/
// OnTrade) are called on release from the queue, and the outbound callbacks (OrderTarget, Fill, …)
// are where the simulator re-applies delay on the way out. In realtime the same methods are driven
// by the vendor session instead, with no queue at either end.
//
// Divergences from the C++, all forced rather than chosen:
//  - NewSeries/LoggableManager are omitted: Series<T> and LoggableManager live in the Strategy
//    project, which references Provider, so Provider cannot reference them back.
//  - WriteToExecution's one-argument template took value.OrderHeader by duck typing. C# generics
//    cannot read a field off an unconstrained T, so the header is passed explicitly.
//  - Tools has no RAIISpinLock; ExecutionLock below is the minimal equivalent with try/finally.
//  - Context indexes order rows by OrderId rather than a raw global index, so CancelAllOrders
//    builds a probe OrderId per local slot instead of walking first..last global index.
public class Server : IDisposable
{
    public FileSystemPath ServerName { get; }

    private readonly LetterBox<ServerHeader> _serverHeaderBox;
    private readonly ServerSocket _serverSocket;
    private readonly ServerContext _serverContext;
    private readonly ClientSocket _loggingServer;
    private readonly ClientSocket _audit;
    private readonly RiskLayer _riskLayer;
    private readonly WriteOnlySocket?[] _instrumentData;

    // One spinlock per CoreGroup (index == CoreGroupId; 0 = admin). C++ uses Tools::RAIISpinLock;
    // this is the same thing with the flag isolated to its own cache line.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ExecutionLock
    {
        public int Flag;
    }

    // Guards WriteToExecution (return channel, S->C). Multi-writer per segment (RX fills/states +
    // send create-acks) but same CCD, so the lock line stays CCD-resident.
    private readonly ExecutionLock[] _recvFromExchangeLocks = new ExecutionLock[8];

    // Guards the PRODUCER end of the injection queue below. Only writers contend (hub + vendor RX);
    // the ReadExecution(cg) thread is the sole reader and takes no lock (ByteQueue SPSC read side).
    private readonly ExecutionLock[] _sendToExchangeLocks = new ExecutionLock[8];

    // Per-CoreGroup OrderTarget injection queue: hub + vendor RX EnqueueOrderTarget() here, the
    // ReadExecution(cg) thread drains and sends, so it stays the sole order sender.
    private readonly ByteQueue?[] _orderTargetQueues = new ByteQueue?[8];

    // Per CoreGroup: which clients trade it (= the clients ReadExecution(coreGroupId) polls). Set on
    // the admin thread at instrument allocation, read on the exec threads => atomic ops. Indexed by
    // CoreGroupId; a set bit is a clientId. Sized to highest CoreGroupId + 1 in the ctor.
    private readonly Bitset64[] _clientIdsByCoreGroupId;

    private bool _isDisposed;

    public ServerContext Context => _serverContext;

    public static readonly ServerHeader DefaultServerHeader = new ServerHeader()
    {
        ServerName = new String128("ServerName"),
        Timestamp = new Timestamp(0),
        InstrumentsCapacity = 4096,
        InstrumentsCount = 0,
        InstrumentIds = new Bitset64(),
        ClientIds = new Bitset64(),
        CoreGroupIds = new Bitset64(),
        OrdersPerClient = 64,
        Persistance = true,
    };

    public OrderTargetHandler? OrderTarget;
    public OrderStatedHandler? OrderState;
    public Action<OrderRejected, string>? OrderRejected;
    public FillHandler? Fill;

    public Action<AllocateInstrument>? AllocateInstrument;
    public ClientAllocated? AllocateClient;

    public Action<int>? ClientOpened;
    public Action<int>? ClientClosed;

    public Server(in ServerHeader serverHeader)
    {
        ServerName = serverHeader.ServerName.ToString();

        // Publish the header before the context is built: Context.EnsureConnected spins on this box.
        _serverHeaderBox = ServerContext.Connect(in serverHeader);
        _serverSocket = new ServerSocket(ServerName, serverHeader.ClientIds.Length);
        _serverContext = new ServerContext(ServerName, Access.Write);
        _loggingServer = new ClientSocket(ServerName + ".server", _serverContext.LoggingServerName, [SocketChannel.AdminChannelLength], [SocketChannel.AdminChannelLength]);
        _audit = new ClientSocket(ServerName + ".audit", _serverContext.LoggingServerName, SocketChannel.BuildChannelLengths(serverHeader.CoreGroupIds), [SocketChannel.AdminChannelLength]);
        _riskLayer = new RiskLayer(_serverContext, OrderRejectedSource.Server);

        // Before anything else: LoadClients() runs between construction and Connect(), and
        // CreateDetatchedClient() refuses to build a Detached socket while this is false.
        _serverSocket.Persistance = serverHeader.Persistance;

        _instrumentData = new WriteOnlySocket?[serverHeader.InstrumentIds.Length];
        // +1: channel index == CoreGroupId, so we need slots 0..HighestSet (matches BuildChannelLengths).
        _clientIdsByCoreGroupId = new Bitset64[serverHeader.CoreGroupIds.HighestSet + 1];

        // One OrderTarget injection queue per EXECUTION CoreGroup (admin carries no order targets).
        foreach (int coreGroupId in serverHeader.CoreGroupIds)
        {
            if (coreGroupId != SocketChannel.Admin)
                _orderTargetQueues[coreGroupId] = new ByteQueue(Tools.Memory.SmallPageLength);
        }

        _serverSocket.AllocateClientId = _serverContext.AllocateClientId;
        _serverSocket.DeallocateClient = _serverContext.DeallocateClient;
        _serverSocket.ClientAllocated += OnClientAllocated;
        // never called with Persistance
        _serverSocket.ClientDeallocated += OnClientDeallocated;
        _serverSocket.ClientOpened += OnClientOpened;
        _serverSocket.ClientClosed += OnClientClosed;

        _loggingServer.Connect();
        _audit.Connect();
    }

    // Starts the listen thread. Call after LoadClients()/LoadInstruments() so the poll thread
    // cannot race them for the same clientId. Persistance comes from the ServerHeader, not here.
    public void Connect()
    {
        _serverSocket.Listen();
    }

    // Disconnect housekeeping — run by ONE thread (the hub), NOT per-segment: it owns _clientIds and
    // CancelAllOrders is cross-segment. Cancels a dropped client's working orders and clears it from
    // each CoreGroup's client set so ReadExecution stops polling it (AtomicClear => race-free vs readers).
    private Bitset64 _clientIds = new Bitset64();
    public void PollDisconnects()
    {
        Bitset64 clientIds = _serverSocket.ClientIds();
        Bitset64 closedClientIds = _clientIds & ~clientIds;
        _clientIds = clientIds;
        if (closedClientIds.IsEmpty)
            return;

        foreach (int clientId in closedClientIds)
            CancelAllOrders(clientId);

        for (int coreGroupId = 0; coreGroupId < _clientIdsByCoreGroupId.Length; coreGroupId++)
        {
            ref Bitset64 coreGroupClientIds = ref _clientIdsByCoreGroupId[coreGroupId];
            foreach (int clientId in closedClientIds)
                coreGroupClientIds.AtomicClear(clientId);
        }
    }

    // Producer API for the injection queue (derives cg from the instrument). Hub + vendor RX call this
    // instead of sending; writers serialise on _sendToExchangeLocks, full queue spins (never drops).
    public void EnqueueOrderTarget(in OrderTarget orderTarget)
    {
        Instrument instrument = _serverContext.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId);
        int coreGroupId = instrument.Header.CoreGroupId;
        ByteQueue queue = _orderTargetQueues[coreGroupId]!;

        ref ExecutionLock sendLock = ref _sendToExchangeLocks[coreGroupId];
        Acquire(ref sendLock);
        try
        {
            Span<byte> dst = queue.Enqueue(Unsafe.SizeOf<OrderTarget>());
            MemoryMarshal.Write(dst, in orderTarget);
        }
        finally
        {
            Release(ref sendLock);
        }
    }

    // Per-CoreGroup hot poll: ONE thread per CoreGroup busy-polls this with its own coreGroupId,
    // reading every connected client's channel for THIS segment and dispatching its OrderTargets.
    // One reader thread per (client, channel) => SPSC-safe; different segments touch different
    // ReadOnlySockets. It also drains the injection queue first (hub cancels + RX replays).
    public void ReadExecution(int coreGroupId)
    {
        // Drain injected OrderTargets first (hub cancels + RX replays): sole reader, no lock. Copy out
        // and Dequeue before sending so the slot frees ahead of a slow SendOrder.
        ByteQueue? injected = _orderTargetQueues[coreGroupId];
        if (injected != null)
        {
            while (injected.TryPeek(out Span<byte> qsrc))
            {
                OrderTarget orderTarget = MemoryMarshal.Read<OrderTarget>(qsrc);
                injected.Dequeue();
                OnOrderTarget(in orderTarget);
            }
        }

        Bitset64 clientIds = new Bitset64(_clientIdsByCoreGroupId[coreGroupId].AtomicLoad());
        foreach (int clientId in clientIds)
        {
            while (_serverSocket.TryRead(clientId, coreGroupId, out ReadOnlySpan<byte> rdst) == ReadStatus.New)
            {
                if (rdst.IsEmpty)
                    continue;

                byte msgType = rdst[0];
                switch (msgType)
                {
                    case (byte)OrderType.OrderTarget:
                    {
                        ref readonly OrderTarget orderTarget = ref MemoryMarshal.AsRef<OrderTarget>(rdst);
                        OnOrderTarget(in orderTarget);
                        break;
                    }
                    case (byte)OrderType.OrderRejected:
                    {
                        ref readonly OrderRejected orderRejected = ref MemoryMarshal.AsRef<OrderRejected>(rdst);
                        OnControlAlgoStatus(orderRejected.OrderHeader.OrderId.StrategyId, orderRejected.OrderHeader.OrderId.InstrumentId, AlgoStatus.Paused);
                        break;
                    }
                    default:
                        break;
                }
            }
        }
    }

    public void OnRiskLimit(in RiskLimit riskLimit)
    {
        // The sender read-modify-writes the whole struct, so the running working quantities in its
        // copy are as stale as the moment it opened the edit dialog. They are server-owned state, not
        // config — carry the live ones across or an operator editing a limit silently rewinds them.
        RiskLimit riskLimitCopy = riskLimit;
        ref readonly RiskLimit existing = ref _serverContext.GetRiskLimit(riskLimit.InstrumentId).GetReadonlyRef();
        riskLimitCopy.WorstLongWorkingQuantity = existing.WorstLongWorkingQuantity;
        riskLimitCopy.WorstShortWorkingQuantity = existing.WorstShortWorkingQuantity;

        _serverContext.GetRiskLimit(riskLimit.InstrumentId).Write(in riskLimitCopy);
        if (riskLimitCopy.StrategyId >= 0)
            WriteToExecution(riskLimitCopy.StrategyId, _serverContext.GetInstrument(riskLimit.InstrumentId).Header.CoreGroupId, in riskLimitCopy);
        SaveRiskLimit(riskLimit.InstrumentId, in riskLimitCopy);
    }

    public void SaveRiskLimit(int instrumentId, in RiskLimit riskLimit)
    {
        string symbol = _serverContext.GetInstrument(instrumentId).Symbol;
        FileSystemPath riskLimitFilePath = Provider.Context.GetRiskLimitsFilePath(_serverContext.DirectoryPath, symbol);
        string riskLimitLine = Json.SerializeToLine(riskLimit);
        Console.WriteLine($"Server::SaveRiskLimit({riskLimitFilePath}):{Environment.NewLine}{riskLimitLine}");
        File.AppendAllLines(riskLimitFilePath, new string[] { riskLimitLine });
    }

    public void OnControlAlgoStatus(int strategyId, int instrumentId, AlgoStatus algoStatus)
    {
        Timestamp now = Clock.Now;
        ref SharedArrayEntry<PositionHeader> localPositionEntry = ref _serverContext.GetPositionHeader(strategyId, instrumentId);
        PositionHeader localPosition = localPositionEntry.GetReadonlyRef();
        localPosition.OrderHeader.ExchangeTimestamp = now;
        localPosition.OrderHeader.NicTimestamp = now;
        localPosition.AlgoStatus = algoStatus;
        localPositionEntry.Write(in localPosition);
        int coreGroupId = _serverContext.GetInstrument(instrumentId).Header.CoreGroupId;
        WriteToExecution(strategyId, coreGroupId, in localPosition);
    }

    public void CancelAllOrders(int clientId)
    {
        // C++ walks first..last global index. Context keys order rows by OrderId, so build a probe
        // whose GlobalIndex is (clientId, localIndex) — same row, no extra accessor.
        for (int localIndex = 0; localIndex < OrderIdAllocator.OrdersPerClient; localIndex++)
        {
            OrderId probe = new OrderId { ClientId = clientId, LocalIndex = localIndex };

            ref SharedArrayEntry<OrderTarget> orderTargetEntry = ref _serverContext.GetOrderTarget(probe);
            if (orderTargetEntry.IsEmpty())
                continue;

            OrderTarget orderTarget = orderTargetEntry.GetReadonlyRef(); // dont lock because client may have crashed mid write
            ref readonly OrderState orderState = ref _serverContext.GetOrderState(probe).GetReadonlyRef();
            if (orderState.OrderStateStatus == OrderStateStatus.Active || orderTarget.OrderTargetStatus == OrderStateStatus.Active)
            {
                orderTarget.OrderTargetStatus = OrderStateStatus.Active;
                orderTarget.OrderTargetAction = OrderTargetAction.Cancel;
                orderTarget.OrderHeader.Seq += 1_000_000;
                orderTarget.OrderHeader.NicTimestamp = Clock.Now;
                // Client process is dead, so the server is the slot's sole writer: stamp the cancel in
                // so the vendor's replay-on-ack cancels a still-PendingNew order.
                orderTargetEntry.RecoveryWrite(in orderTarget);
                // Enqueue so an already-working order is cancelled now on its segment thread.
                EnqueueOrderTarget(in orderTarget);
            }
        }
    }

    public void ReadAdmin()
    {
        PollDisconnects();

        foreach (int clientId in _serverSocket.ClientIds())
        {
            while (_serverSocket.TryRead(clientId, SocketChannel.Admin, out ReadOnlySpan<byte> rdst) == ReadStatus.New)
            {
                if (rdst.IsEmpty)
                    continue;

                byte msgType = rdst[0];
                switch (msgType)
                {
                    case (byte)AllocateType.Instrument:
                    {
                        AllocateInstrument allocateInstrument = MemoryMarshal.Read<AllocateInstrument>(rdst);
                        OnAllocateInstrument(clientId, ref allocateInstrument);
                        break;
                    }
                    case (byte)ControlType.AlgoStatus:
                    {
                        ref readonly ControlAlgoStatus controlAlgoStatus = ref MemoryMarshal.AsRef<ControlAlgoStatus>(rdst);
                        OnControlAlgoStatus(controlAlgoStatus.StrategyId, controlAlgoStatus.InstrumentId, controlAlgoStatus.AlgoStatus);
                        break;
                    }
                    case (byte)OrderType.RiskLimit:
                    {
                        ref readonly RiskLimit riskLimit = ref MemoryMarshal.AsRef<RiskLimit>(rdst);
                        OnRiskLimit(in riskLimit);
                        break;
                    }
                    default:
                        break;
                }
            }
        }
    }

    public void OnInstrumentHeader(in InstrumentHeader128 instrumentHeader128)
    {
        _serverContext.OnInstrumentHeader(in instrumentHeader128);
    }

    public void OnQuantityAhead(ulong clientOrderId, int quantityAhead)
    {
        ref SharedArrayEntry<OrderState> orderStateEntry = ref _serverContext.GetOrderState(clientOrderId);
        ref OrderState orderState = ref orderStateEntry.GetRef();
        if (orderState.OrderHeader.OrderId == clientOrderId)
        {
            orderStateEntry.AcquireLock();
            orderState.QuantityAhead = quantityAhead;
            orderStateEntry.ReleaseLock();
        }
    }

    public OrderState OnOrderState(ref OrderState orderState)
    {
        ref SharedArrayEntry<OrderState> orderStateEntry = ref _serverContext.GetOrderState(orderState.OrderHeader.OrderId);
        ref SharedArrayEntry<OrderTarget> orderTargetEntry = ref _serverContext.GetOrderTarget(orderState.OrderHeader.OrderId);
        ref OrderState existingOrderState = ref orderStateEntry.GetRef();
        ref readonly OrderTarget existingOrderTarget = ref orderTargetEntry.GetReadonlyRef();

        bool isSeqInOrder = existingOrderState.OrderStateStatus == OrderStateStatus.Active && (orderState.OrderHeader.Seq >= existingOrderState.OrderHeader.Seq || orderState.OrderStateStatus == OrderStateStatus.Done);
        // handle case where exchange cancels order
        bool isSafeToOverwrite = existingOrderTarget.OrderHeader.OrderId == orderState.OrderHeader.OrderId && isSeqInOrder;

        if (isSafeToOverwrite)
        {
            orderStateEntry.AcquireLock();
            existingOrderState.OrderHeader.Seq = orderState.OrderHeader.Seq;
            existingOrderState.ExchangeOrderId = orderState.ExchangeOrderId;
            existingOrderState.OrderProfile = orderState.OrderProfile;
            existingOrderState.OrderStateStatus = orderState.OrderStateStatus;
            existingOrderState.OrderStateReason = orderState.OrderStateReason;
            existingOrderState.QuantityFilled = orderState.QuantityFilled;
            existingOrderState.OrderHeader.ExchangeTimestamp = orderState.OrderHeader.ExchangeTimestamp;
            existingOrderState.OrderHeader.NicTimestamp = Clock.Now;
            orderStateEntry.ReleaseLock();
        }
        _riskLayer.OnOrderState(in existingOrderState);
        WriteToExecution(in existingOrderState.OrderHeader, in existingOrderState);
        OrderState?.Invoke(in existingOrderState);
        return existingOrderState;
    }

    public OrderRejected OnOrderRejected(ref OrderRejected orderRejected, string message)
    {
        ref OrderState orderState = ref _serverContext.GetOrderState(orderRejected.OrderHeader.OrderId).GetRef();
        if (orderState.OrderStateStatus == OrderStateStatus.Done && orderRejected.OrderRejectedReasons.Raw == 1UL << (int)OrderRejectedReason.OrderNotFound)
            orderRejected.OrderRejectedReasons = new Bitset64(1UL << (int)OrderRejectedReason.StateIsDone);

        if (orderState.OrderHeader.OrderId == orderRejected.OrderHeader.OrderId)
        {
            orderRejected.OrderHeader.NicTimestamp = Clock.Now;
            _riskLayer.OnOrderRejected(in orderRejected);
            Reject(in orderRejected, message);
            return orderRejected;
        }
        else
        {
            Console.WriteLine($"Server::OnOrderRejected: unknown clientOrderId{Environment.NewLine}{orderRejected}");
            return new OrderRejected();
        }
    }

    public void Reject(in OrderRejected orderRejected, string message)
    {
        WriteToExecution(in orderRejected.OrderHeader, in orderRejected);
        if (!orderRejected.OrderRejectedReasons.IsEmpty && orderRejected.OrderRejectedReasons.IsSubsetOf(Execution.OrderRejected.OrderDiscarded))
            return;
        OnControlAlgoStatus(orderRejected.OrderHeader.OrderId.StrategyId, orderRejected.OrderHeader.OrderId.InstrumentId, AlgoStatus.Paused);
        OrderRejected?.Invoke(orderRejected, message);
    }

    // comes from client so parameter is consistent with framework
    public void OnOrderTarget(in OrderTarget orderTarget)
    {
        ref SharedArrayEntry<OrderState> orderStateEntry = ref _serverContext.GetOrderState(orderTarget.OrderHeader.OrderId);
        ref OrderState orderState = ref orderStateEntry.GetRef();

        bool isValid = _riskLayer.ValidateOrder(in orderTarget, out Bitset64 orderRejectedReasons);

        if (orderTarget.OrderTargetAction == OrderTargetAction.Create)
        {
            orderStateEntry.AcquireLock();
            orderState = new OrderState()
            {
                OrderHeader = orderTarget.OrderHeader,
                OrderProfile = orderTarget.OrderProfile,
                TimeInForce = orderTarget.TimeInForce,
                OrderStateStatus = isValid ? OrderStateStatus.Active : OrderStateStatus.Done,
                // Seq 0 already means "not acked"; naming it makes the RiskLayer retire hooks able to
                // tell PendingNew from an ack without inferring it from the sequence.
                OrderStateReason = isValid ? OrderStateReason.PendingNew : OrderStateReason.Rejected,
                QuantityFilled = 0,
                QuantityAhead = 0,
            };
            orderState.OrderHeader.Seq = 0; // indicates new Order but that ordertarget is not acked by exchange
            orderState.OrderHeader.NicTimestamp = Clock.Now;
            orderStateEntry.ReleaseLock();
            WriteToExecution(in orderState.OrderHeader, in orderState);
        }

        if (isValid)
        {
            OrderTarget?.Invoke(in orderTarget);
        }
        else
        {
            OrderRejected orderRejected = new OrderRejected()
            {
                OrderHeader = orderTarget.OrderHeader,
                OrderTargetAction = orderTarget.OrderTargetAction,
                OrderRejectedSource = OrderRejectedSource.Server,
                OrderProfile = orderTarget.OrderProfile,
                OrderRejectedReasons = orderRejectedReasons,
            };
            Reject(in orderRejected, "Rejected by Server Risk Layer");
        }
    }

    public int OnAllocateInstrument(ref AllocateInstrument allocateInstrument)
    {
        ref ServerHeader serverHeader = ref _serverContext.ServerHeader.GetRef();
        if (allocateInstrument.InstrumentHeaderId >= serverHeader.InstrumentsCount)
            throw new ArgumentOutOfRangeException(nameof(allocateInstrument), "Server.AllocateInstrument: instrumentHeaderId out of range");

        int instrumentId = _serverContext.AllocateInstrument(allocateInstrument.InstrumentHeaderId);
        allocateInstrument.InstrumentId = instrumentId;

        if (_instrumentData[instrumentId] != null)
            return instrumentId; // already attached + seeded

        ref InstrumentHeader128 header128 = ref _serverContext.GetInstrumentHeader(allocateInstrument.InstrumentHeaderId).GetRef();
        allocateInstrument.Symbol = header128.Symbology.Symbol;
        allocateInstrument.ExchangeInstrumentId = header128.AsInstrumentHeader().ExchangeInstrumentId;

        OpenInstrumentData(instrumentId, allocateInstrument.Symbol.ToString());

        WriteToAudit(SocketChannel.Admin, in allocateInstrument);

        AllocateInstrument?.Invoke(allocateInstrument);

        return instrumentId;
    }

    public void OnAllocateInstrument(int clientId, ref AllocateInstrument allocateInstrument)
    {
        ref ServerHeader serverHeader = ref _serverContext.ServerHeader.GetRef();
        if (clientId >= serverHeader.ClientIds.Length)
            throw new ArgumentOutOfRangeException(nameof(clientId), "Server.OnInstrumentAllocated: clientId out of range");

        int instrumentId = OnAllocateInstrument(ref allocateInstrument);

        _serverContext.AllocateInstrument(clientId, instrumentId);

        // Remember this client now trades the instrument's CoreGroup, so ReadExecution polls that channel.
        int coreGroupId = _serverContext.GetInstrument(instrumentId).Header.CoreGroupId;
        _clientIdsByCoreGroupId[coreGroupId].AtomicSet(clientId);

        WriteToAdmin(clientId, in allocateInstrument);
    }

    public Fill OnFill(ref Fill fill)
    {
        ref readonly OrderState orderState = ref _serverContext.GetOrderState(fill.OrderHeader.OrderId).GetReadonlyRef();

        if (orderState.OrderHeader.OrderId != fill.OrderHeader.OrderId)
            throw new ArgumentOutOfRangeException(nameof(fill), "Server.OnFill: unknown clientOrderId");

        // Identity (ClientId/StrategyId/InstrumentId) is packed inside ClientOrderId, and the equality
        // check above guarantees it matches the state's - no re-stamping needed.
        fill.OrderHeader.NicTimestamp = Clock.Now;

        int strategyId = fill.OrderHeader.OrderId.StrategyId;
        int instrumentId = fill.OrderHeader.OrderId.InstrumentId;

        Instrument instrument = _serverContext.GetInstrument(instrumentId);

        double multiplier = instrument.Multiplier;
        double tickSize = instrument.TickSize;

        // Global Update
        ref SharedArrayEntry<PositionHeader> serverPositionHeaderEntry = ref _serverContext.GetPositionHeader(instrumentId);
        ref PositionHeader serverPosition = ref serverPositionHeaderEntry.GetRef();
        serverPositionHeaderEntry.AcquireLock();
        serverPosition.OnFill(in fill, tickSize, multiplier);
        serverPositionHeaderEntry.ReleaseLock();

        // Local Update
        ref SharedArrayEntry<PositionHeader> localPositionHeaderEntry = ref _serverContext.GetPositionHeader(strategyId, instrumentId);
        ref PositionHeader localPosition = ref localPositionHeaderEntry.GetRef();
        localPositionHeaderEntry.AcquireLock();
        localPosition.OnFill(in fill, tickSize, multiplier);
        localPositionHeaderEntry.ReleaseLock();

        _riskLayer.OnFill(in fill);

        int coreGroupId = instrument.Header.CoreGroupId;
        WriteToExecution(strategyId, coreGroupId, in fill);
        WriteToExecution(strategyId, coreGroupId, in localPosition);
        WriteToAudit(coreGroupId, in fill);
        WriteToAudit(coreGroupId, in serverPosition);
        Fill?.Invoke(in fill);
        return fill;
    }

    public void OnTrade(in Trade trade)
    {
        WriteToInstrumentData(in trade);
    }

    // Opens (once) the per-instrument broadcast ring this server writes market data to.
    public void OpenInstrumentData(int instrumentId, string symbol)
    {
        if (_instrumentData[instrumentId] != null)
            return;

        string name = SocketChannel.GetInstrumentDataName(ServerName, symbol);
        _instrumentData[instrumentId] = new WriteOnlySocket(name, SharedMemory.CreateOrOpen(name, SocketChannel.InstrumentDataChannelLength));
        _instrumentData[instrumentId]!.Recover();
    }

    // Broadcast a trade/settlement tick verbatim to the instrument's ring.
    public void WriteToInstrumentData<T>(in T tick) where T : unmanaged
    {
        ref readonly TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in tick, 1)));
        _instrumentData[tickHeader.InstrumentId]?.Write(in tick);
    }

    public void OnMarketByPrice(in MarketByPrice marketByPrice, Span<byte> src)
    {
        int instrumentId = marketByPrice.TickHeader.InstrumentId;
        ref SharedArrayEntry<MarketByPrice64> entry = ref _serverContext.GetMarketByPrice64(instrumentId);
        ref MarketByPrice64 mbp64 = ref entry.GetRef();

        // Snapshot->update conversion writes here. Must outlive the Write() below, since deltaSpan
        // points into it on the snapshot path, so it lives at function scope (not inside the branch).
        // Worst case the diff touches book + snapshot levels = up to 128 per side.
        Span<byte> updateBuffer = stackalloc byte[MarketByPrice.SizeOf(128, 128)];
        // scoped: without it deltaSpan defaults to caller-scope and cannot take a slice of the
        // stackalloc above. Nothing here escapes the method, so local scope is what we want.
        scoped Span<byte> deltaSpan = default;
        bool isDeltas = false;

        if (marketByPrice.TickHeader.TickType == TickType.MarketByPriceSnapshot)
        {
            Span<byte> pastSpan = stackalloc byte[MarketByPrice.SizeOf(64, 64)];
            mbp64.CopyToSnapshot(instrumentId, pastSpan);

            ref MarketByPrice update = ref MarketByPrice.SnapshotAsUpdate(pastSpan, src, updateBuffer);
            deltaSpan = updateBuffer.Slice(0, update.SizeOf());

            entry.AcquireLock();
            isDeltas = mbp64.TrySetAsDeltas(deltaSpan);
            entry.ReleaseLock();
        }
        else if (marketByPrice.TickHeader.TickType == TickType.MarketByPriceUpdate)
        {
            deltaSpan = src;

            entry.AcquireLock();
            isDeltas = mbp64.TrySetAsDeltas(deltaSpan);
            entry.ReleaseLock();
        }
        else if (marketByPrice.TickHeader.TickType == TickType.MarketByPriceDelta)
        {
            deltaSpan = src;

            entry.AcquireLock();
            isDeltas = mbp64.TrySet(deltaSpan);
            entry.ReleaseLock();
        }

        if (!isDeltas)
            return;

        _serverContext.ServerHeader.GetRef().Timestamp = marketByPrice.TickHeader.NicTimestamp;

        _instrumentData[instrumentId]?.Write(deltaSpan);
    }

    // Canonical return-channel writer. EVERY write to a CoreGroup channel (fill, state, reject,
    // position) funnels through here so the per-CoreGroup spinlock serialises the segment's RX
    // thread (the near-constant holder) against the rare send/admin/cancel writer on the same CCD.
    // Without the lock on THIS (hot) path the rare concurrent reject would tear the SPSC ring.
    public void WriteToExecution<T>(int clientId, int coreGroupId, in T value) where T : unmanaged
    {
        ref ExecutionLock recvLock = ref _recvFromExchangeLocks[coreGroupId];
        Acquire(ref recvLock);
        try
        {
            _serverSocket.Write(clientId, coreGroupId, in value);
        }
        finally
        {
            Release(ref recvLock);
        }
    }

    // Convenience overload for the order/admin/reject paths: derive (clientId, CoreGroupId) from the
    // message's OrderHeader, then funnel through the locked writer above. C++ read value.OrderHeader
    // off the template parameter; C# cannot, so the caller passes it.
    public void WriteToExecution<T>(in OrderHeader orderHeader, in T value) where T : unmanaged
    {
        int coreGroupId = _serverContext.GetInstrument(orderHeader.OrderId.InstrumentId).Header.CoreGroupId;
        WriteToExecution(orderHeader.OrderId.ClientId, coreGroupId, in value);
    }

    // Per-CoreGroup audit: channelId == CoreGroupId (0 = admin). Each segment's RX thread owns its
    // own audit channel and admin owns channel 0, so every channel is single-writer => no lock and
    // no cross-CCD bounce on the fill path.
    public void WriteToAudit<T>(int channelId, in T value) where T : unmanaged
    {
        _audit.Write(channelId, in value);
    }

    public void WriteToAdmin<T>(int clientId, in T value) where T : unmanaged
    {
        _serverSocket.Write(clientId, SocketChannel.Admin, in value);
    }

    public void Stop()
    {
        _serverSocket.Stop();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        _serverSocket.Dispose();

        foreach (WriteOnlySocket? instrumentData in _instrumentData)
            instrumentData?.Dispose();

        _audit.Dispose();
        _loggingServer.Dispose();
        _serverContext.Dispose();
        _serverHeaderBox.Dispose();
    }

    public void LoadClients(Timestamp date)
    {
        FileSystemPath clientsFilePath = _serverContext.GetClientsFilePath(date);
        if (!File.Exists(clientsFilePath))
            return;

        foreach (string line in File.ReadLines(clientsFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            SocketHeader socketHeader = Json.Deserialize<SocketHeader>(line);
            socketHeader.ClientId = _serverContext.AllocateClientId(in socketHeader);
            _serverSocket.CreateDetatchedClient(in socketHeader);
            OnClientAllocated(in socketHeader);
        }
    }

    public void LoadInstruments(Timestamp date)
    {
        FileSystemPath instrumentsFilePath = _serverContext.GetInstrumentsFilePath(date);
        if (!File.Exists(instrumentsFilePath))
            return;

        foreach (string line in File.ReadLines(instrumentsFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            AllocateInstrument allocateInstrument = Json.Deserialize<AllocateInstrument>(line);
            allocateInstrument.InstrumentHeaderId = _serverContext.GetInstrumentHeaderIdByExchangeInstrumentId(allocateInstrument.ExchangeInstrumentId);
            if (allocateInstrument.InstrumentHeaderId < 0)
            {
                Console.WriteLine($"Server.LoadInstruments: {allocateInstrument.Symbol} (exchange instrument id {allocateInstrument.ExchangeInstrumentId}) is no longer listed; not restored.");
                continue;
            }
            OnAllocateInstrument(ref allocateInstrument);
        }
    }

    public void SaveClient(in SocketHeader socketHeader, Timestamp date)
    {
        FileSystemPath clientsFilePath = _serverContext.GetClientsFilePath(date);
        string line = Json.SerializeToLine(socketHeader);
        File.AppendAllLines(clientsFilePath, new string[] { line });
    }

    public void SaveInstrument(in AllocateInstrument allocateInstrument, Timestamp date)
    {
        FileSystemPath instrumentsFilePath = _serverContext.GetInstrumentsFilePath(date);
        string line = Json.SerializeToLine(allocateInstrument);
        File.AppendAllLines(instrumentsFilePath, new string[] { line });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Acquire(ref ExecutionLock executionLock)
    {
        while (Interlocked.CompareExchange(ref executionLock.Flag, 1, 0) != 0)
            X86BaseWrapper.Pause();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Release(ref ExecutionLock executionLock)
    {
        Volatile.Write(ref executionLock.Flag, 0);
    }

    private void OnClientAllocated(in SocketHeader socketHeader)
    {
        // this is the open signal for the logger
        _loggingServer.Write(in socketHeader);
        AllocateClient?.Invoke(in socketHeader);
    }

    private void OnClientClosed(int clientId)
    {
        CancelAllOrders(clientId);
        ClientClosed?.Invoke(clientId);
    }

    private void OnClientOpened(int clientId)
    {
        foreach (int instrumentId in _serverContext.GetInstrumentIdsByClientId(clientId).GetReadonlyRef())
            _clientIdsByCoreGroupId[_serverContext.GetInstrument(instrumentId).Header.CoreGroupId].AtomicSet(clientId);
    }

    private void OnClientDeallocated(in SocketHeader socketHeader)
    {
        SocketHeader clientSocketHeaderCopy = socketHeader;
        clientSocketHeaderCopy.ClientToServerChannelCount = 0;
        clientSocketHeaderCopy.ServerToClientChannelCount = 0;
        // this is the close signal for logger
        _loggingServer.Write(in clientSocketHeaderCopy);
    }
}
//END_FILE HFT/Provider/Server.cs
