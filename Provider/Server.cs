//BEGIN_FILE HFT/Provider/Server.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
// by the vendor session on the CoreGroup thread itself — one loop per segment runs ReadFromIlink()
// then ReadFromClients() — with no queue at either end and no separate RX thread.
//
// Divergences from the C++, all forced rather than chosen:
//  - NewSeries/LoggableManager are omitted: Series<T> and LoggableManager live in the Strategy
//    project, which references Provider, so Provider cannot reference them back.
//  - WriteToExecution's one-argument template took value.OrderHeader by duck typing. C# generics
//    cannot read a field off an unconstrained T, so the header is passed explicitly.
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

    // One spinlock flag per CoreGroup (index == CoreGroupId; 0 = admin), guarded by
    // Tools.RAIISpinLock — mirrors the C++ alignas(64) ExecutionLock { atomic<bool> Flag }.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ExecutionLock
    {
        public bool Flag;
    }

    // Guards WriteToExecution (return channel, S->C). The CoreGroup thread is the only writer today
    // (fills, states, rejects, positions); the lock stays as cheap insurance against a rare
    // off-thread writer on the same CCD, so the lock line stays CCD-resident.
    private readonly ExecutionLock[] _recvFromExchangeLocks = new ExecutionLock[8];

    // Guards the PRODUCER end of the injection queue below. Only off-thread writers contend (client
    // close on the listen thread, hub); the ReadExecution(cg) thread is the sole reader and takes no
    // lock (ByteQueue SPSC read side).
    private readonly ExecutionLock[] _sendToExchangeLocks = new ExecutionLock[8];

    // Per-CoreGroup OrderTarget injection queue: off-thread cancels (client close on the listen
    // thread, hub) EnqueueOrderTarget() here, the ReadExecution(cg) thread drains and sends, so it
    // stays the sole order sender.
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
        InitDirectories();

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

        // One order-entry throttle row per CoreGroup, at the CME default until configured (see Spec.md).
        foreach (int coreGroupId in serverHeader.CoreGroupIds)
            _serverContext.GetRateLimit(coreGroupId).Write(new RollingRateLimit(RateLimit.CMEOrderEntry with { RateLimitId = coreGroupId }));

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

    private string[] _subDirectories = new string[] { "Alerts", "Audit", "Fills", "Positions", "Series", "Clients", "Instruments" };
    private void InitDirectories()
    {
        foreach (string subDirectory in _subDirectories)
        {
            string subDirectoryPath = Path.Combine(ServerName, subDirectory);
            Directory.CreateDirectory(subDirectoryPath);
            if (Clock.Mode == ClockMode.Realtime)
                continue;

            foreach (string filePath in Directory.EnumerateFiles(subDirectoryPath))
            {
                try { File.Delete(filePath); }
                catch { }
            }
        }
    }

    // Starts the listen thread. Call after LoadClients()/LoadInstruments() so the poll thread
    // cannot race them for the same clientId. Persistance comes from the ServerHeader, not here.
    public void Connect()
    {
        _serverSocket.Listen();
    }

    // Producer API for the injection queue (derives cg from the instrument). Off-thread cancels (listen
    // thread, hub) call this instead of sending; writers serialise on _sendToExchangeLocks, full queue
    // spins (never drops).
    public void EnqueueOrderTarget(in OrderTarget orderTarget)
    {
        Instrument instrument = _serverContext.GetInstrument(orderTarget.OrderHeader.OrderId.InstrumentId);
        int coreGroupId = instrument.Header.CoreGroupId;
        ByteQueue queue = _orderTargetQueues[coreGroupId]!;

        using RAIISpinLock spinLock = new(ref _sendToExchangeLocks[coreGroupId].Flag);
        Span<byte> dst = queue.Enqueue(Unsafe.SizeOf<OrderTarget>());
        MemoryMarshal.Write(dst, in orderTarget);
    }

    // Per-CoreGroup hot loop: ONE thread per CoreGroup runs ReadFromIlink() then this with its own
    // coreGroupId, so exchange fills/states/rejects and every connected client's targets and controls
    // for THIS segment apply on the same thread — every row the segment owns has exactly one writer.
    // One reader thread per (client, channel) => SPSC-safe; different segments touch different
    // ReadOnlySockets. It also drains the injection queue first (off-thread cancels).
    public void ReadExecution(int coreGroupId)
    {
        // Drain injected OrderTargets first (off-thread cancels): sole reader, no lock. Copy out and
        // Dequeue before sending so the slot frees ahead of a slow SendOrder.
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
                    case (byte)ControlType.RiskLimit:
                    {
                        ref readonly ControlRiskLimit controlRiskLimit = ref MemoryMarshal.AsRef<ControlRiskLimit>(rdst);
                        OnControlRiskLimit(in controlRiskLimit);
                        break;
                    }
                    case (byte)ControlType.AlgoStatus:
                    {
                        ref readonly ControlAlgoStatus controlAlgoStatus = ref MemoryMarshal.AsRef<ControlAlgoStatus>(rdst);
                        OnControlAlgoStatus(controlAlgoStatus.StrategyId, controlAlgoStatus.InstrumentId, controlAlgoStatus.AlgoStatus);
                        break;
                    }
                    default:
                        break;
                }
            }
        }
    }

    public void ReadAdmin()
    {
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
                    default:
                        break;
                }
            }
        }
    }

    // CoreGroup thread only — the row's sole writer. Config fields in place under the seq bump; the
    // working quantities are never touched, so an edit cannot rewind a reservation (see Spec.md).
    public void OnControlRiskLimit(in ControlRiskLimit controlRiskLimit)
    {
        ref SharedArrayEntry<RiskLimit> riskLimitEntry = ref _serverContext.GetRiskLimit(controlRiskLimit.InstrumentId);
        ref RiskLimit riskLimit = ref riskLimitEntry.GetRef();
        riskLimitEntry.AcquireLock();
        riskLimit.MaxOrderQuantity = controlRiskLimit.MaxOrderQuantity;
        riskLimit.MaxPositionQuantity = controlRiskLimit.MaxPositionQuantity;
        riskLimit.Timestamp = Clock.Now;
        riskLimitEntry.ReleaseLock();

        // Server-wide limit: the posted row is what the logging server appends to the server's .risklimit file.
        int coreGroupId = _serverContext.GetInstrument(controlRiskLimit.InstrumentId).Header.CoreGroupId;
        WriteToAudit(coreGroupId, in riskLimit);
    }

    // CoreGroup thread only (ReadExecution, Reject): the local position row's sole writer alongside OnFill.
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

    public void OnInstrumentHeader(in InstrumentHeader128 instrumentHeader128)
    {
        _serverContext.OnInstrumentHeader(in instrumentHeader128);
    }

    public void OnQuantityAhead(OrderId clientOrderId, int quantityAhead)
    {
        ref OrderState orderState = ref _serverContext.GetOrderState(clientOrderId).GetRef();
        if (orderState.OrderHeader.OrderId == clientOrderId)
        {
            // Quick write, its atomic, do not lock, it would contend with OnOrderState
            orderState.QuantityAhead = quantityAhead;
        }
    }

    public OrderState OnOrderState(ref OrderState orderState)
    {
        orderState.OrderHeader.NicTimestamp = Clock.Now;
        ref OrderState existingOrderState = ref WriteOrderState(ref orderState);
        WriteToExecution(in existingOrderState.OrderHeader, in existingOrderState);
        OrderState?.Invoke(in existingOrderState);
        return existingOrderState;
    }

    private ref OrderState WriteOrderState(ref OrderState orderState)
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
            int beforeAckedOrderQuantity = existingOrderState.OrderProfile.Quantity;
            int quantityFilled = Math.Abs(existingOrderState.QuantityFilled) > Math.Abs(orderState.QuantityFilled) ? existingOrderState.QuantityFilled : orderState.QuantityFilled;
            orderStateEntry.AcquireLock();
            existingOrderState.OrderHeader.Seq = orderState.OrderHeader.Seq;
            existingOrderState.ExchangeOrderId = orderState.ExchangeOrderId;
            existingOrderState.OrderProfile = orderState.OrderProfile;
            existingOrderState.OrderStateStatus = orderState.OrderStateStatus;
            existingOrderState.OrderStateReason = orderState.OrderStateReason;
            existingOrderState.QuantityFilled = quantityFilled;
            existingOrderState.OrderHeader.ExchangeTimestamp = orderState.OrderHeader.ExchangeTimestamp;
            existingOrderState.OrderHeader.NicTimestamp = orderState.OrderHeader.NicTimestamp;
            orderStateEntry.ReleaseLock();
            _riskLayer.OnOrderState(in existingOrderState, beforeAckedOrderQuantity);
        }
        return ref existingOrderState;
    }

    private const ulong _orderNotFound = 1UL << (int)OrderRejectedReason.OrderNotFound;
    public OrderRejected OnOrderRejected(ref OrderRejected orderRejected, string message)
    {
        ref OrderState orderState = ref _serverContext.GetOrderState(orderRejected.OrderHeader.OrderId).GetRef();
        if (orderState.OrderStateStatus == OrderStateStatus.Done && orderRejected.OrderRejectedReasons.Raw == _orderNotFound)
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
            return new OrderRejected();
        }
    }

    public void Reject(in OrderRejected orderRejected, string message)
    {
        WriteToExecution(in orderRejected.OrderHeader, in orderRejected);
        if (Client.IsDiscarded(in orderRejected))
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
            orderRejected.OrderHeader.NicTimestamp = Clock.Now;   // server reply: own stamp, so it sorts after the target it copies
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

        ref InstrumentHeader128 header128 = ref _serverContext.GetInstrumentHeader(allocateInstrument.InstrumentHeaderId).GetRef();
        allocateInstrument.Symbol = header128.Symbology.Symbol;
        allocateInstrument.ExchangeInstrumentId = header128.AsInstrumentHeader().ExchangeInstrumentId;

        if (_instrumentData[instrumentId] != null)
            return instrumentId; // already attached + seeded

        OpenInstrumentData(instrumentId, allocateInstrument.Symbol.ToString());

        WriteToAudit(SocketChannel.Admin, in allocateInstrument);

        return instrumentId;
    }

    public void OnAllocateInstrument(int clientId, ref AllocateInstrument allocateInstrument)
    {
        ref ServerHeader serverHeader = ref _serverContext.ServerHeader.GetRef();
        if (clientId >= serverHeader.ClientIds.Length)
            throw new ArgumentOutOfRangeException(nameof(clientId), "Server.OnInstrumentAllocated: clientId out of range");

        // Spread ⇒ legs: a spread's legs must exist BEFORE the spread — CreateInstrument resolves
        // them and throws otherwise. Each leg is fully client-allocated too (the client tracks leg
        // positions and fills), but only the SPREAD — the instrument the client asked for — echoes
        // an admin reply: the client's GetInstrument handshake reads exactly one. See Spec.md.
        ref InstrumentHeader128 header128 = ref _serverContext.GetInstrumentHeader(allocateInstrument.InstrumentHeaderId).GetRef();
        if (header128.AsInstrumentHeader().InstrumentType == InstrumentType.Spread)
        {
            AllocateInstrument allocateLegInstrument = allocateInstrument;
            ref LeggedHeader leggedHeader = ref header128.AsLegged();
            foreach (ref readonly LegHeader legHeader in leggedHeader.Legs)
            {
                allocateLegInstrument.InstrumentHeaderId = legHeader.InstrumentHeaderId;
                OnAllocateInstrument(clientId, ref allocateLegInstrument, writeAdminReply: false);
            }
        }

        OnAllocateInstrument(clientId, ref allocateInstrument, writeAdminReply: true);
    }

    private void OnAllocateInstrument(int clientId, ref AllocateInstrument allocateInstrument, bool writeAdminReply)
    {
        int instrumentId = OnAllocateInstrument(ref allocateInstrument);

        _serverContext.AllocateInstrument(clientId, instrumentId);

        // Strategy 0 is the house book: it holds the union of every client's allocations (see Spec.md).
        if (clientId != OrderIdAllocator.ServerStrategyId)
            _serverContext.AllocateInstrument(OrderIdAllocator.ServerStrategyId, instrumentId);

        // Remember this client now trades the instrument's CoreGroup, so ReadExecution polls that channel.
        int coreGroupId = _serverContext.GetInstrument(instrumentId).Header.CoreGroupId;
        _clientIdsByCoreGroupId[coreGroupId].AtomicSet(clientId);

        if (writeAdminReply)
            WriteToAdmin(clientId, in allocateInstrument);

        // After the work, not before: on entry InstrumentId is still -1 and Symbol is empty.
        Console.WriteLine($"{ServerName}::OnAllocateInstrument()\n{allocateInstrument}");

        AllocateInstrument?.Invoke(allocateInstrument);
    }

    // One fill event: the order's resulting state plus its fills — a single fill whose instrument
    // matches the order's for an outright, one per leg (OrderId.InstrumentId rewritten to the leg)
    // for a spread. The fills' position rows take the fills; the RISK release stays on the ORDER's
    // instrument in order units — that is where ValidateOrder reserved — sized by the state-merge
    // delta, so a state that fails to apply releases nothing. Requires an order's legs to share the
    // order's CoreGroup owner thread.
    public void OnFill(ref OrderState orderState, Span<Fill> fills)
    {
        ref readonly OrderState existingOrderState = ref _serverContext.GetOrderState(orderState.OrderHeader.OrderId).GetReadonlyRef();

        if (existingOrderState.OrderHeader.OrderId != orderState.OrderHeader.OrderId)
            throw new ArgumentOutOfRangeException(nameof(orderState), "Server.OnFill: unknown clientOrderId");

        Timestamp now = Clock.Now;
        orderState.OrderHeader.NicTimestamp = now;   // same stamp as its fills: equal NIC keeps ring order (state, fill, position) in the audit
        foreach (ref Fill fill in fills)
        {
            // Leg ids differ from the order's only in the InstrumentId bits, so GlobalIndex (client +
            // local slot) is the belongs-to-this-order check plain id equality can no longer be.
            if (fill.OrderHeader.OrderId.GlobalIndex != orderState.OrderHeader.OrderId.GlobalIndex)
                throw new ArgumentOutOfRangeException(nameof(fills), "Server.OnFill: fill does not belong to the order");
            fill.OrderHeader.NicTimestamp = now;
        }

        int strategyId = orderState.OrderHeader.OrderId.StrategyId;

        // Fill is atomic - no torn orderstate/position reads by clients. Acquire in fills order,
        // server row before local row per instrument: single-writer makes this a mirror convention,
        // not deadlock avoidance. WriteOrderState, not OnOrderState: row write + ledger only — the
        // forwards and callbacks run after release below.
        foreach (ref readonly Fill fill in fills)
        {
            int instrumentId = fill.OrderHeader.OrderId.InstrumentId;
            _serverContext.GetPositionHeader(instrumentId).AcquireLock();
            _serverContext.GetPositionHeader(strategyId, instrumentId).AcquireLock();
        }

        WriteOrderState(ref orderState);

        // A leg fill IS an outright fill: its instrument, quantity (spread qty × weight) and sign are
        // the leg's, so RiskLayer.OnFill releases the leg's own reservation directly — same as an
        // outright. Raw fill quantity, not a state delta: per-fill releases + the Done remainder
        // telescope to exactly the reserved worst, per leg.
        foreach (ref readonly Fill fill in fills)
        {
            int instrumentId = fill.OrderHeader.OrderId.InstrumentId;
            Instrument instrument = _serverContext.GetInstrument(instrumentId);
            _serverContext.GetPositionHeader(instrumentId).GetRef().OnFill(in fill, instrument.Multiplier);
            _serverContext.GetPositionHeader(strategyId, instrumentId).GetRef().OnFill(in fill, instrument.Multiplier);
            // A legged instrument's own fill is accounting only (volume/position view on the spread
            // row); risk lives on the legs, so releasing it here would double-release the legs the
            // leg fills already covered. Risk is an outright concept.
            if (!instrument.IsLegged)
                _riskLayer.OnFill(in fill);
        }

        for (int i = fills.Length - 1; i >= 0; i--)
        {
            int instrumentId = fills[i].OrderHeader.OrderId.InstrumentId;
            _serverContext.GetPositionHeader(strategyId, instrumentId).ReleaseLock();
            _serverContext.GetPositionHeader(instrumentId).ReleaseLock();
        }

        int coreGroupId = _serverContext.GetInstrument(orderState.OrderHeader.OrderId.InstrumentId).Header.CoreGroupId;
        WriteToExecution(in existingOrderState.OrderHeader, in existingOrderState);
        foreach (ref readonly Fill fill in fills)
        {
            WriteToExecution(strategyId, coreGroupId, in fill);
            WriteToExecution(strategyId, coreGroupId, in _serverContext.GetPositionHeader(strategyId, fill.OrderHeader.OrderId.InstrumentId).GetRef());
        }

        foreach (ref readonly Fill fill in fills)
        {
            WriteToAudit(coreGroupId, in fill);
            WriteToAudit(coreGroupId, in _serverContext.GetPositionHeader(fill.OrderHeader.OrderId.InstrumentId).GetRef());
        }

        OrderState?.Invoke(in existingOrderState);
        foreach (ref readonly Fill fill in fills)
            Fill?.Invoke(in fill);
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

    public void OnTradingStatusUpdate(in TradingStatusUpdate tradingStatusUpdate)
    {
        int instrumentHeaderId = _serverContext.GetInstrumentHeaderIdByInstrumentId(tradingStatusUpdate.TickHeader.InstrumentId).GetReadonlyRef();
        ref InstrumentHeader128 instrumentHeader = ref _serverContext.GetInstrumentHeader(instrumentHeaderId).GetRef();
        instrumentHeader.AsInstrumentHeader().TradingStatus = tradingStatusUpdate.TradingStatus;
        WriteToInstrumentData(in tradingStatusUpdate);
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
    // position) funnels through here. The CoreGroup thread is the only writer today; the
    // per-CoreGroup spinlock stays so a rare off-thread writer on the same CCD can never tear the
    // SPSC ring, and an uncontended lock costs nothing on this (hot) path.
    public void WriteToExecution<T>(int clientId, int coreGroupId, in T value) where T : unmanaged
    {
        using RAIISpinLock spinLock = new(ref _recvFromExchangeLocks[coreGroupId].Flag);
        _serverSocket.Write(clientId, coreGroupId, in value);
    }

    // Convenience overload for the order/admin/reject paths: derive (clientId, CoreGroupId) from the
    // message's OrderHeader, then funnel through the locked writer above. C++ read value.OrderHeader
    // off the template parameter; C# cannot, so the caller passes it.
    public void WriteToExecution<T>(in OrderHeader orderHeader, in T value) where T : unmanaged
    {
        int coreGroupId = _serverContext.GetInstrument(orderHeader.OrderId.InstrumentId).Header.CoreGroupId;
        WriteToExecution(orderHeader.OrderId.ClientId, coreGroupId, in value);
    }

    // Per-CoreGroup audit: channelId == CoreGroupId (0 = admin). Each CoreGroup thread owns its own
    // audit channel and admin owns channel 0, so every channel is single-writer => no lock and no
    // cross-CCD bounce on the fill path.
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

    private void OnClientAllocated(in SocketHeader socketHeader)
    {
        Console.WriteLine($"{ServerName}::OnClientAllocated()\n{socketHeader}");

        // this is the open signal for the logger
        _loggingServer.Write(in socketHeader);
        AllocateClient?.Invoke(in socketHeader);
    }

    // Fired by the listen thread only (exactly-once per close — see Spec.md "Socket close
    // protocol"), which is what lets the cross-segment CancelAllOrders and bitset clearing live
    // here instead of a hub-side PollDisconnects.
    private void OnClientClosed(int clientId)
    {
        CancelAllOrders(clientId);

        for (int coreGroupId = 0; coreGroupId < _clientIdsByCoreGroupId.Length; coreGroupId++)
        {
            _clientIdsByCoreGroupId[coreGroupId].AtomicClear(clientId);
        }

        ClientClosed?.Invoke(clientId);
    }

    private void OnClientOpened(int clientId)
    {
        Console.WriteLine("OnClientOpened: clientId " + clientId);
        foreach (int instrumentId in _serverContext.GetInstrumentIdsByClientId(clientId).GetReadonlyRef())
        {
            _clientIdsByCoreGroupId[_serverContext.GetInstrument(instrumentId).Header.CoreGroupId].AtomicSet(clientId);
            Console.WriteLine($"Trades instrumentId {instrumentId} ({_serverContext.GetInstrument(instrumentId).Symbol})");
        }
            
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
