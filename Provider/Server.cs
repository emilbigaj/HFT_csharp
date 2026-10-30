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
// by the vendor session on the CoreGroup thread itself — one loop per segment runs ReadFromIlink()
// then ReadFromClients() — with no queue at either end and no separate RX thread.
//
// Divergences from the C++, all forced rather than chosen:
//  - NewSeries/LoggableManager are omitted: Series<T> and LoggableManager live in the Strategy
//    project, which references Provider, so Provider cannot reference them back.
//  - WriteToExecution's one-argument template took value.OrderHeader by duck typing. C# generics
//    cannot read a field off an unconstrained T, so the header is passed explicitly.
//  - Context indexes order rows by OrderId rather than a raw global index, so TryCloseOutOrders
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

    // A long padded to a cache line, so nothing after it shares its line: a drain counter one thread writes must not share another's.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct PaddedLong
    {
        public long Value;
    }

    // Drain handshake (see Spec.md "Client restart"): the listen thread bumps the request at each client close; each CoreGroup
    // thread, at the end of a pass, acknowledges the request it read, writing only when it moved.
    private PaddedLong _drainRequest;
    private readonly PaddedLong[] _drainAcknowledgedByCoreGroupId;
    private readonly long[] _drainRequestAtCloseByClientId;   // per client: the request issued at its close (listen thread only)

    private bool _isDisposed;

    public ServerContext Context => _serverContext;

    public static readonly ServerHeader DefaultServerHeader = new ServerHeader()
    {
        ServerName = new String256("ServerName"),
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
        _drainAcknowledgedByCoreGroupId = new PaddedLong[serverHeader.CoreGroupIds.HighestSet + 1];
        _drainRequestAtCloseByClientId = new long[serverHeader.ClientIds.Length];

        // One OrderTarget injection queue per EXECUTION CoreGroup (admin carries no order targets).
        foreach (int coreGroupId in serverHeader.CoreGroupIds)
        {
            if (coreGroupId != SocketChannel.Admin)
                _orderTargetQueues[coreGroupId] = new ByteQueue(Tools.Memory.SmallPageLength);
        }

        _serverSocket.AllocateClientId = _serverContext.AllocateClientId;
        _serverSocket.IsClientReady = IsClientReady;
        _serverSocket.DeallocateClient = _serverContext.DeallocateClient;
        _serverSocket.ClientAllocated += OnClientAllocated;
        // never called with Persistance
        _serverSocket.ClientDeallocated += OnClientDeallocated;
        _serverSocket.ClientOpened += OnClientOpened;
        _serverSocket.ClientClosed += OnClientClosed;

        _loggingServer.Connect();
        _audit.Connect();

        // The ServerStrategy has no client, so it gets a socket of its own, Detached like a restored strategy's: the server's writes to
        // strategy 0's channel land in it, and Logging taps and files it under ServerStrategyName as it does a strategy's (see Spec.md
        // "Strategy 0"). Why not OnClientAllocated: its subscribers record real clients, and this one must never be reloaded as one.
        int[] serverStrategyChannelLengths = SocketChannel.BuildChannelLengths(serverHeader.CoreGroupIds);
        SocketHeader serverStrategySocketHeader = new(ServerName, _serverContext.ServerStrategyName, serverStrategyChannelLengths, serverStrategyChannelLengths, Environment.ProcessId) { ClientId = OrderIdAllocator.ServerStrategyId };
        _serverSocket.CreateDetatchedClient(in serverStrategySocketHeader);
        _loggingServer.Write(in serverStrategySocketHeader);
    }

    private string[] _subDirectories = new string[] { "Alerts", "Audit", "Fills", "Positions", "Series", "Clients", "Instruments" };
    private void InitDirectories()
    {
        // Before anything is deleted: a simulation run against a live server's name must throw here, not after wiping its files.
        ServerContext.ThrowIfInvalidServerName(ServerName);
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
    // Realtime: the caller wraps this in try/catch -> AlertManager.OnException (as Scenario's ReadSocket loop); simulation: the Clock's exceptions go to the AlertManager.
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
                SendServerOrderTarget(in orderTarget);
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
                        // A manual order is not the algo's: its refusal never pauses the strategy it books to (see Spec.md).
                        if (orderRejected.OrderHeader.OrderId.IsAlgoOrder())
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
                        // Why: a user's pause stops the algo's orders now, not on its next tick; a reject's pause (above) leaves them to the algo
                        if (controlAlgoStatus.AlgoStatus == AlgoStatus.Paused)
                            CancelAlgoOrders(controlAlgoStatus.StrategyId, controlAlgoStatus.InstrumentId);
                        break;
                    }
                    case (byte)ControlType.TransferPosition:
                    {
                        ref readonly ControlTransferPosition controlTransferPosition = ref MemoryMarshal.AsRef<ControlTransferPosition>(rdst);
                        OnControlTransferPosition(in controlTransferPosition);
                        break;
                    }
                    default:
                        break;
                }
            }
        }

        // Why: one cached read per pass; a write only after a client closed: everything this pass read is now handled (see Spec.md)
        long drainRequest = Volatile.Read(ref _drainRequest.Value);
        if (drainRequest != _drainAcknowledgedByCoreGroupId[coreGroupId].Value)
            Volatile.Write(ref _drainAcknowledgedByCoreGroupId[coreGroupId].Value, drainRequest);
    }

    // Realtime: the caller wraps this in try/catch -> AlertManager.OnException (as Scenario's ReadSocket loop); simulation: the Clock's exceptions go to the AlertManager.
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

    // CoreGroup thread only — the row's sole writer. Config fields in place under the seq bump; the working quantities live on
    // WorkingRisk, so an edit cannot rewind a reservation (see Spec.md). Strategy 0's (a server workspace's) is the firm's limit.
    public void OnControlRiskLimit(in ControlRiskLimit controlRiskLimit)
    {
        // Step 1: the row: the firm's for strategy 0, never a strategy's (the ServerStrategy's orders are manual, checked by the firm's alone)
        int strategyId = controlRiskLimit.StrategyId;
        bool isServerRiskLimit = strategyId == OrderIdAllocator.ServerStrategyId;
        ref SharedArrayEntry<RiskLimit> riskLimitEntry = ref isServerRiskLimit ? ref _serverContext.GetRiskLimit(controlRiskLimit.InstrumentId) : ref _serverContext.GetRiskLimit(strategyId, controlRiskLimit.InstrumentId);

        // Step 2: its config fields
        ref RiskLimit riskLimit = ref riskLimitEntry.GetRef();
        riskLimitEntry.AcquireLock();
        riskLimit.MaxOrderQuantity = controlRiskLimit.MaxOrderQuantity;
        riskLimit.MaxPositionQuantity = controlRiskLimit.MaxPositionQuantity;
        riskLimit.MaxFillsPerSession = controlRiskLimit.MaxFillsPerSession;
        riskLimit.Timestamp = Clock.Now;
        riskLimitEntry.ReleaseLock();

        // Step 3: on record, where its file is read back at allocation
        WriteToRecord(strategyId, _serverContext.GetInstrument(controlRiskLimit.InstrumentId).Header.CoreGroupId, in riskLimit);
    }

    // Puts a server-owned row on record: strategy 0's (the server's) on the audit channel, Logging keeping it under <server>; any
    // other strategy's on its execution channel, Logging's tap keeping it under <strategy>. CoreGroup thread only.
    private void WriteToRecord<T>(int strategyId, int coreGroupId, in T row) where T : unmanaged
    {
        if (strategyId == OrderIdAllocator.ServerStrategyId)
            WriteToAudit(coreGroupId, in row);
        else
            WriteToExecution(strategyId, coreGroupId, in row);
    }

    // CoreGroup thread only (ReadExecution, Reject): the position row's sole writer alongside OnFill. A strategy's switch is its own
    // row; strategy 0's (a server workspace's) is the server row, the master switch every algo on the instrument also needs Live.
    public void OnControlAlgoStatus(int strategyId, int instrumentId, AlgoStatus algoStatus)
    {
        // Step 1: the switch: the master is the server row, never a strategy's (the ServerStrategy has no algo of its own)
        bool isMasterSwitch = strategyId == OrderIdAllocator.ServerStrategyId;
        ref SharedArrayEntry<PositionHeader> positionEntry = ref isMasterSwitch ? ref _serverContext.GetPositionHeader(instrumentId) : ref _serverContext.GetPositionHeader(strategyId, instrumentId);

        // Step 2: the row with its new status
        Timestamp now = Clock.Now;
        PositionHeader positionHeader = positionEntry.GetReadonlyRef();
        positionHeader.OrderHeader.ExchangeTimestamp = now;
        positionHeader.OrderHeader.NicTimestamp = now;
        positionHeader.AlgoStatus = algoStatus;
        positionEntry.Write(in positionHeader);

        // Step 3: on record as a fill's rows are
        WriteToRecord(strategyId, _serverContext.GetInstrument(instrumentId).Header.CoreGroupId, in positionHeader);
    }

    // CoreGroup thread only: the position rows' sole writer, as OnFill. Moves part of one book's position into another as two Transfer
    // fills at the request's price, both rows under both locks at once so no reader sees one book moved and the other not; the firm's
    // row, the exchange and every fill count stay as they were (see Spec.md "Position transfer"). A transfer the check refuses moves
    // nothing and is dropped silently: the dialog checks the same, so only a race gets here.
    public void OnControlTransferPosition(in ControlTransferPosition controlTransferPosition)
    {
        // Step 1: the dialog's own check, against the rows as they are now
        if (!_serverContext.CanTransferPosition(in controlTransferPosition))
            return;

        // Step 2: one fill out of the from book, one into the to book, at one price and time
        Timestamp now = Clock.Now;
        int quantity = controlTransferPosition.Quantity;
        Fill fromFill = controlTransferPosition.NewTransferFill(controlTransferPosition.FromStrategyId, -quantity, now);
        Fill toFill = controlTransferPosition.NewTransferFill(controlTransferPosition.ToStrategyId, quantity, now);

        // Step 3: both rows under both locks, acquired from-to and released in reverse, as OnFill does with its two rows
        int instrumentId = controlTransferPosition.InstrumentId;
        double multiplier = _serverContext.GetInstrument(instrumentId).Multiplier;
        ref SharedArrayEntry<PositionHeader> fromPositionEntry = ref _serverContext.GetPositionHeader(controlTransferPosition.FromStrategyId, instrumentId);
        ref SharedArrayEntry<PositionHeader> toPositionEntry = ref _serverContext.GetPositionHeader(controlTransferPosition.ToStrategyId, instrumentId);
        fromPositionEntry.AcquireLock();
        toPositionEntry.AcquireLock();
        fromPositionEntry.GetRef().OnFill(in fromFill, multiplier);
        toPositionEntry.GetRef().OnFill(in toFill, multiplier);
        toPositionEntry.ReleaseLock();
        fromPositionEntry.ReleaseLock();

        // Step 4: each on record once both rows are consistent
        WriteTransferFill(in fromFill);
        WriteTransferFill(in toFill);
    }

    // One Transfer fill on record as OnFill writes a fill: the fill and its book's row on the book's channel (where Logging's tap
    // files them, the ServerStrategy's included), the fill in the audit. Never the firm's row: the firm traded nothing.
    private void WriteTransferFill(in Fill fill)
    {
        int strategyId = fill.OrderHeader.OrderId.StrategyId;
        int instrumentId = fill.OrderHeader.OrderId.InstrumentId;
        int coreGroupId = _serverContext.GetInstrument(instrumentId).Header.CoreGroupId;
        WriteToExecution(strategyId, coreGroupId, in fill);
        WriteToExecution(strategyId, coreGroupId, in _serverContext.GetPositionHeader(strategyId, instrumentId).GetRef());
        WriteToAudit(coreGroupId, in fill);
    }

    // Cancels the working algo orders a user's pause stops: the master switch every strategy's on the instrument, a strategy's
    // switch that strategy's. Algo orders only: a manual order (a GUI's, the ServerStrategy's) is never touched. CoreGroup thread only.
    private void CancelAlgoOrders(int strategyId, int instrumentId)
    {
        bool isMasterSwitch = strategyId == OrderIdAllocator.ServerStrategyId;
        foreach ((OrderState orderState, OrderTarget orderTarget) in _serverContext.EnumerateOrders(instrumentId, isMasterSwitch ? -1 : strategyId))
        {
            // Step 1: orders the server has and still works (an unread Create is refused AlgoIsPaused when it arrives), the algo's own only
            if (!orderState.OrderHeader.OrderId.IsAlgoOrder())
                continue;

            // Step 2: a cancel numbered past the algo's own seqs, as a GUI's cancel of an algo order is (see Spec.md "Manual orders")
            OrderTarget cancelOrderTarget = orderState.NewCancel();
            cancelOrderTarget.OrderHeader.Seq = orderTarget.OrderHeader.Seq + Execution.OrderTarget.s_foreignCancelSeqJump;
            cancelOrderTarget.OrderHeader.NicTimestamp = Clock.Now;
            SendServerOrderTarget(in cancelOrderTarget);
        }
    }

    // Sends a target the server itself originates (a gone client's close-out, a user's pause). CoreGroup thread only.
    // Why: in no audit otherwise; written before OnOrderTarget so the tap records it ahead of its reject or Done
    private void SendServerOrderTarget(in OrderTarget orderTarget)
    {
        WriteToExecution(in orderTarget.OrderHeader, in orderTarget);
        OnOrderTarget(in orderTarget);
    }

    // Closes out a gone client's 64 order slots: cancels what is working, marks a Create the server never read Done, repairs a
    // row left mid-write. Returns true once nothing waits on the exchange (see Spec.md "Client restart").
    public bool TryCloseOutOrders(int clientId)
    {
        bool isClientDrained = IsClientDrained(clientId);
        bool _isClosedOut = true;
        for (int localIndex = 0; localIndex < OrderIdAllocator.OrdersPerClient; localIndex++)
        {
            // Step 1: working? The server's row decides once it is about this order, before that the client's row
            OrderId orderId = new OrderId { ClientId = clientId, LocalIndex = localIndex };
            ref SharedArrayEntry<OrderTarget> orderTargetEntry = ref _serverContext.GetOrderTarget(orderId);
            if (orderTargetEntry.IsEmpty())
                continue;
            OrderTarget orderTarget = orderTargetEntry.GetReadonlyRef();
            // Why: TryRead waits out a write in progress: the CoreGroup thread may be writing this state right now, and a torn read can skip a live order
            _serverContext.GetOrderState(orderId).TryRead(out OrderState orderState);
            bool isSameOrder = orderState.OrderHeader.OrderId == orderTarget.OrderHeader.OrderId;
            // Why: the target cannot decide once the state is this order's: this stamps it Active and nothing stamps it back
            bool isWorking = isSameOrder ? orderState.OrderStateStatus == OrderStateStatus.Active : orderTarget.OrderTargetStatus == OrderStateStatus.Active;
            bool isStuckInTornWrite = Protocol.IsWriteInProgress(orderTargetEntry.GetSeq());   // its writer, the client, is gone
            if (!isWorking && !isStuckInTornWrite)
                continue;

            // Step 2: stamp the cancel in, Active while the exchange may hold the order, and send it if working (see Spec.md)
            // Why: until the client is drained an unread Create may still be on its way out: Active + Cancel lets replay-on-ack cancel it
            bool isAwaitingExchange = isWorking && (isSameOrder || !isClientDrained);
            _isClosedOut &= !isAwaitingExchange;
            orderTarget.OrderTargetStatus = isAwaitingExchange ? OrderStateStatus.Active : OrderStateStatus.Done;
            orderTarget.OrderTargetAction = OrderTargetAction.Cancel;
            orderTarget.OrderHeader.Seq += Execution.OrderTarget.s_foreignCancelSeqJump;
            orderTarget.OrderHeader.NicTimestamp = Clock.Now;
            orderTargetEntry.RecoveryWrite(in orderTarget);
            // Why: only a working slot's cancel is sent: a torn idle row's id may name no instrument, and the lookup would throw
            if (isWorking)
                EnqueueOrderTarget(in orderTarget);
        }
        return _isClosedOut;
    }

    // Ready once the client's previous process left nothing working; until then its orders are cancelled again (the cancel
    // on close can fail, e.g. while iLink is down). Asked only for a name no live process holds.
    private bool IsClientReady(int clientId) => TryCloseOutOrders(clientId);

    // Every CoreGroup thread has finished a pass since the client's close: nothing it read from the client is still in flight.
    private bool IsClientDrained(int clientId)
    {
        foreach (int coreGroupId in _serverContext.ServerHeader.GetReadonlyRef().CoreGroupIds)
        {
            if (coreGroupId != SocketChannel.Admin && Volatile.Read(ref _drainAcknowledgedByCoreGroupId[coreGroupId].Value) < _drainRequestAtCloseByClientId[clientId])
                return false;
        }
        return true;
    }

    public void OnInstrumentHeader(in InstrumentHeader256 instrumentHeader256)
    {
        _serverContext.OnInstrumentHeader(in instrumentHeader256);
    }

    public void OnQuantityAhead(OrderId clientOrderId, int quantityAhead, int quantityBehind)
    {
        ref OrderState orderState = ref _serverContext.GetOrderState(clientOrderId).GetRef();
        if (orderState.OrderHeader.OrderId == clientOrderId)
        {
            // One 64-bit store, no lock (it would contend with OnOrderState): QuantityAhead @56 and QuantityBehind @60 are
            // adjacent and 8-aligned, so a reader never sees one updated without the other. Keep the two fields together.
            Unsafe.As<int, long>(ref orderState.QuantityAhead) = (uint)quantityAhead | ((long)quantityBehind << 32);
        }
    }

    // Keeps the state's NIC stamp: whoever received it from the exchange stamped it (see Spec.md "NIC timestamps")
    public OrderState OnOrderState(ref OrderState orderState)
    {
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
            _riskLayer.OnOrderState(in existingOrderState);
        }
        return ref existingOrderState;
    }

    // The entry point for every refused target, the server's own (OnOrderTarget) and the exchange's (see Spec.md).
    public OrderRejected OnOrderRejected(ref OrderRejected orderRejected, string message)
    {
        // Why per reason: a refusal made only of races (a done order's OrderNotFound, an in-flight NotInSession) is discarded (see Spec.md)
        ref OrderState orderState = ref _serverContext.GetOrderState(orderRejected.OrderHeader.OrderId).GetRef();
        if (orderState.OrderStateStatus == OrderStateStatus.Done)
            orderRejected.OrderRejectedReasons.Replace((int)OrderRejectedReason.OrderNotFound, (int)OrderRejectedReason.StateIsDone);
        OrderRejectedReason notInSessionReason = RiskLayer.GetNotInSessionReason(in _serverContext.GetInstrument(orderRejected.OrderHeader.OrderId.InstrumentId).Header);
        orderRejected.OrderRejectedReasons.Replace((int)OrderRejectedReason.NotInSession, (int)notInSessionReason);

        if (orderState.OrderHeader.OrderId == orderRejected.OrderHeader.OrderId)
        {
            // A refused Create ends its order: the Done (which releases its risk) is published before the reject that explains it (see Spec.md).
            if (orderRejected.OrderTargetAction == OrderTargetAction.Create)
            {
                orderState.NewRejected(in orderRejected, out OrderState rejectedState);
                OnOrderState(ref rejectedState);
            }
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
        if (orderRejected.IsDiscarded)
            return;
        // A manual order is not the algo's: its refusal is reported but never pauses the strategy it books to (see Spec.md).
        if (orderRejected.OrderHeader.OrderId.IsAlgoOrder())
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
            ref readonly MarketByPrice64 mbp64 = ref _serverContext.GetMarketByPrice64(orderTarget.OrderHeader.OrderId.InstrumentId).GetReadonlyRef();
            int quantityAhead = orderTarget.OrderProfile.Side == Side.Buy ? mbp64.Bids.GetQuantity(orderTarget.OrderProfile.Ticks) : mbp64.Asks.GetQuantity(orderTarget.OrderProfile.Ticks);
            orderStateEntry.AcquireLock();
            Execution.OrderState.NewInFlight(in orderTarget, out orderState);   // Why: qualified: inside Server, OrderState names the event
            orderState.QuantityAhead = quantityAhead;
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
            orderTarget.NewOrderRejected(orderRejectedReasons, OrderRejectedSource.Server, out OrderRejected orderRejected);
            orderRejected.OrderHeader.NicTimestamp = Clock.Now;   // Why: the server's own refusal is made now; the header it copied carries the client's send time
            OnOrderRejected(ref orderRejected, "Rejected by Server Risk Layer");
        }
    }

    public int OnAllocateInstrument(ref AllocateInstrument allocateInstrument)
    {
        ref ServerHeader serverHeader = ref _serverContext.ServerHeader.GetRef();
        if (allocateInstrument.InstrumentHeaderId >= serverHeader.InstrumentsCount)
            throw new ArgumentOutOfRangeException(nameof(allocateInstrument), "Server.AllocateInstrument: instrumentHeaderId out of range");

        int instrumentId = _serverContext.AllocateInstrument(allocateInstrument.InstrumentHeaderId);
        allocateInstrument.InstrumentId = instrumentId;

        ref InstrumentHeader256 header256 = ref _serverContext.GetInstrumentHeader(allocateInstrument.InstrumentHeaderId).GetRef();
        allocateInstrument.Symbol = header256.Symbology.Symbol;
        allocateInstrument.ExchangeInstrumentId = header256.AsInstrumentHeader().ExchangeInstrumentId;

        if (_instrumentData[instrumentId] != null)
            return instrumentId; // already attached + seeded

        OpenInstrumentData(instrumentId, allocateInstrument.Symbol.ToString());

        WriteToAudit(SocketChannel.Admin, in allocateInstrument);
        // Why: the ServerStrategy is allocated every instrument (OnAllocateInstrument below) but never asked, so it gets the admin reply a
        // strategy gets for its own request, from which Logging's tap names its fill and position files
        WriteToAdmin(OrderIdAllocator.ServerStrategyId, in allocateInstrument);

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
        ref InstrumentHeader256 header256 = ref _serverContext.GetInstrumentHeader(allocateInstrument.InstrumentHeaderId).GetRef();
        if (header256.AsInstrumentHeader().InstrumentType == InstrumentType.Spread)
        {
            AllocateInstrument allocateLegInstrument = allocateInstrument;
            ref LeggedHeader leggedHeader = ref header256.AsLegged();
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

        // Strategy 0 is the ServerStrategy: it holds the union of every client's allocations (see Spec.md).
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

        // A resent fill (iLink PossRetransFlag) repeats a cumulative filled quantity the row already holds: drop it whole (see Spec.md).
        if (Math.Abs(orderState.QuantityFilled) <= Math.Abs(existingOrderState.QuantityFilled))
            return;

        // Why no stamp here: the receiver gave the state and its fills one NIC stamp, which keeps ring order (state, fill, position) in the audit
        foreach (ref readonly Fill fill in fills)
        {
            // Leg ids differ from the order's only in the InstrumentId bits, so GlobalIndex (client +
            // local slot) is the belongs-to-this-order check plain id equality can no longer be.
            if (fill.OrderHeader.OrderId.GlobalIndex != orderState.OrderHeader.OrderId.GlobalIndex)
                throw new ArgumentOutOfRangeException(nameof(fills), "Server.OnFill: fill does not belong to the order");
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
        ref InstrumentHeader256 instrumentHeader = ref _serverContext.GetInstrumentHeader(instrumentHeaderId).GetRef();
        ref InstrumentHeader header = ref instrumentHeader.AsInstrumentHeader();
        if (header.TradingStatus != tradingStatusUpdate.TradingStatus)
        {
            // Why: the time before the status, so a reader that sees the new status sees its time (see Spec.md)
            header.TradingStatusTimestamp = tradingStatusUpdate.TickHeader.ExchangeTimestamp;
            Volatile.Write(ref Unsafe.As<TradingStatus, byte>(ref header.TradingStatus), (byte)tradingStatusUpdate.TradingStatus);
        }
        WriteToInstrumentData(in tradingStatusUpdate);

        // The exchange closing the instrument ends its session: its product group's message-efficiency day and its fills count (see Spec.md)
        if (tradingStatusUpdate.TradingStatus == TradingStatus.Closed)
        {
            EndMessageEfficiencyDay(in instrumentHeader.AsInstrumentHeader(), in tradingStatusUpdate);
            _riskLayer.OnSessionClosed(tradingStatusUpdate.TickHeader.InstrumentId);
        }
    }

    // Starts the next message-efficiency day of this instrument's product group; in Realtime the day that ended is posted on the
    // audit channel and Logging appends it to the group's file. CoreGroup thread: no I/O.
    private void EndMessageEfficiencyDay(in InstrumentHeader instrumentHeader, in TradingStatusUpdate tradingStatusUpdate)
    {
        ref MessageEfficiency messageEfficiency = ref _serverContext.GetMessageEfficiency(instrumentHeader.ProductGroupId).GetRef();
        MessageEfficiency endedMessageEfficiency = messageEfficiency;
        DateTime local = Session.CME.ConvertToLocal(tradingStatusUpdate.TickHeader.ExchangeTimestamp);
        // Why only on a change: every instrument of the group closes; the first ends the day, the rest find it already ended
        if (messageEfficiency.Reset(local) && Clock.Mode == ClockMode.Realtime)
            WriteToAudit(instrumentHeader.CoreGroupId, in endedMessageEfficiency);
    }

    // Posts every product group's message-efficiency row on the audit channel at shutdown; Logging appends each to its group's file,
    // read back when the group is next allocated. Call before the audit link closes: Logging drains it on close.
    private void PostMessageEfficiencies()
    {
        int productGroupCount = _serverContext.ServerHeader.GetReadonlyRef().InstrumentIds.Length;
        for (int productGroupId = 0; productGroupId < productGroupCount; productGroupId++)
        {
            ref SharedArrayEntry<MessageEfficiency> messageEfficiencyEntry = ref _serverContext.GetMessageEfficiency(productGroupId);
            if (messageEfficiencyEntry.IsEmpty())
                return;
            // Why a plain read: a strategy that died mid-write would make a seqlocked read spin for ever and hang the close
            WriteToAudit(SocketChannel.Admin, in messageEfficiencyEntry.GetReadonlyRef());
        }
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

        // Why after the server socket: its admin thread, the admin channel's only other writer, has stopped
        if (Clock.Mode == ClockMode.Realtime)
            PostMessageEfficiencies();
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
            // Why: at startup a refused client is a broken clients file, not a connect to answer: stop loudly
            if (socketHeader.ClientId < 0)
                throw new InvalidOperationException($"Server.LoadClients: {socketHeader.ClientName} refused: {(ConnectRefusal)socketHeader.ClientId}.");
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
            // The full client path, not the server-only one: it restores the client and ServerStrategy
            // allocations and fires AllocateInstrument, which an exchange adapter needs to rebuild routers
            // and market data for orders still working at the exchange after a restart.
            OnAllocateInstrument(allocateInstrument.ClientId, ref allocateInstrument);
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
    // protocol"), which is what lets the cross-segment TryCloseOutOrders and bitset clearing live
    // here instead of a hub-side PollDisconnects.
    private void OnClientClosed(int clientId)
    {
        // Why: Interlocked, a full fence: a CoreGroup thread that reads the new request also sees the client Detached
        _drainRequestAtCloseByClientId[clientId] = Interlocked.Increment(ref _drainRequest.Value);
        TryCloseOutOrders(clientId);

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
