//BEGIN_FILE HFT/Provider/Context.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Data;
using Execution;
using Socket;
using Tools;

namespace Provider;

public static class ContextManager
{
    private static readonly object s_lock = new object();
    private static readonly Dictionary<string, ClientContext> s_clientContexts = new Dictionary<string, ClientContext>();

    public static FileSystemPath ServerName { get; private set; } = null!;
    public static ServerContext ServerContext { get; private set; } = null!;

    public static bool IsInitialized { get; private set; }

    public static void Initialize(FileSystemPath serverName)
    {
        Console.WriteLine($"ContextManager::Initialize({serverName})");

        lock (s_lock)
        {
            if (!IsInitialized)
            {
                ServerName = serverName;
                ServerContext = new ServerContext(serverName, Access.Read);
                IsInitialized = true;
            }
        }
    }

    public static ClientContext GetClientContext(FileSystemPath clientName)
    {
        lock (s_lock)
        {
            ClientContext? context;
            if (!s_clientContexts.TryGetValue(clientName, out context))
            {
                context = new ClientContext(clientName, ServerName, Access.Read);
                s_clientContexts[clientName] = context;
            }
            return context;
        }
    }

    public static void Dispose()
    {
        lock (s_lock)
        {
            if (IsInitialized)
            {
                foreach (ClientContext context in s_clientContexts.Values)
                {
                    context.Dispose();
                }
                s_clientContexts.Clear();
                ServerContext.Dispose();
                IsInitialized = false;
            }
        }
    }
}

public abstract class Context
{
    public FileSystemPath ServerName { get; }
    public FileSystemPath LoggingServerName { get; }
    // Strategy 0's directory: the house book lives in the Strategies tree like any other strategy (see Spec.md).
    public FileSystemPath ServerStrategyName { get; }
    public FileSystemPath DirectoryPath { get; }

    public Access ServerAccess { get; }
    public Access ClientAccess { get; }

    public static FileSystemPath GetAlertsFilePath(FileSystemPath directoryPath, DateTime date)
    {
        string fileName = $"{date:yyyy-MM-dd}.alert";
        return Path.Combine(GetAlertsDirectoryPath(directoryPath), fileName);
    }

    public static FileSystemPath GetAlertsDirectoryPath(FileSystemPath directoryPath)
    {
        return Path.Combine(directoryPath, "Alerts");
    }

    public static FileSystemPath GetPositionFilePath(FileSystemPath directoryPath, string symbol)
    {
        return Path.Combine(directoryPath, "Positions", $"{symbol}.position");
    }

    public static FileSystemPath GetFillsFilePath(FileSystemPath directoryPath, string symbol)
    {
        return Path.Combine(directoryPath, "Fills", $"{symbol}.fill");
    }

    public static FileSystemPath GetAuditFilePath(FileSystemPath directoryPath, DateTime date)
    {
        string fileName = $"{date:yyyy-MM-dd}.audit";
        return Path.Combine(GetAuditDirectoryPath(directoryPath), fileName);
    }

    public static FileSystemPath GetAuditDirectoryPath(FileSystemPath directoryPath)
    {
        return Path.Combine(directoryPath, "Audit");
    }

    public static FileSystemPath GetWorkspacesDirectoryPath(FileSystemPath directoryPath)
    {
        return Path.Combine(directoryPath, "Workspaces");
    }

    public static FileSystemPath GetWorkspaceFilePath(FileSystemPath directoryPath, string workspaceName)
    {
        return Path.Combine(GetWorkspacesDirectoryPath(directoryPath), workspaceName + ".workspace");
    }

    public static FileSystemPath GetRiskLimitsFilePath(FileSystemPath directoryPath, string symbol)
    {
        return Path.Combine(directoryPath, "RiskLimits", symbol + ".risklimit");
    }

    public static FileSystemPath GetMessageEfficiencyFilePath(FileSystemPath directoryPath, string symbol)
    {
        return Path.Combine(directoryPath, "MessageEfficiency", symbol + ".messageefficiency");
    }

    public static FileSystemPath GetLoggingServerDirectoryPath(FileSystemPath directoryPath)
    {
        return Path.Combine(directoryPath, "LoggingServer");
    }

    public FileSystemPath FillsDirectoryPath { get; }
    public FileSystemPath PositionsDirectoryPath { get; }
    public FileSystemPath AlertsDirectoryPath { get; }
    public FileSystemPath RiskLimitsDirectoryPath { get; }
    public FileSystemPath MessageEfficiencyDirectoryPath { get; }
    public FileSystemPath AuditDirectoryPath { get; }
    public FileSystemPath SeriesDirectoryPath { get; }
    public FileSystemPath WorkspaceDirectoryPath { get; }


    // server
    protected readonly LetterBox<ServerHeader> _serverHeaderBox;
    protected readonly SharedArray<SocketHeader> _clientSocketHeaders;

    // instruments
    protected readonly SharedArray<InstrumentHeader128> _instrumentHeaders;
    protected readonly SharedArray<int> _instrumentHeaderIdByInstrumentId;

    // subscriptions
    protected readonly SharedArray<Bitset64> _instrumentIdsByClientId;
    protected readonly SharedArray<Bitset64> _clientIdsByInstrumentId;
    protected readonly SharedArray<MarketByPrice64> _marketsByPrice;


    // execution
    protected readonly SharedArray<RiskLimit> _riskLimits;
    protected readonly SharedArray<MessageEfficiency> _messageEfficiency;

    protected readonly SharedArray<OrderState> _orderStates;
    protected readonly SharedArray<OrderRisk> _orderRisks;
    protected readonly SharedArray<OrderTarget> _orderTargets;

    // positions
    protected readonly SharedArray<PositionHeader> _localPositionHeaders;

    protected readonly Instrument[] _instruments;
    protected readonly Position[] _positions;


    protected Context(FileSystemPath serverName, FileSystemPath directoryPath, Access serverAccess, Access clientAccess)
    {
        Console.WriteLine($"{GetType().Name}({serverName}, {directoryPath}, {serverAccess}, {clientAccess})");

        ServerContext.ThrowIfInvalidServerName(serverName);

        ServerName = serverName;
        LoggingServerName = GetLoggingServerDirectoryPath(ServerName);
        ServerStrategyName = ClientContext.GetDirectoryPath(ServerName);
        DirectoryPath = directoryPath;
        ServerAccess = serverAccess;
        ClientAccess = clientAccess;

        FillsDirectoryPath = Path.Combine(DirectoryPath, "Fills");
        Directory.CreateDirectory(FillsDirectoryPath);
        PositionsDirectoryPath = Path.Combine(DirectoryPath, "Positions");
        Directory.CreateDirectory(PositionsDirectoryPath);
        RiskLimitsDirectoryPath = Path.Combine(DirectoryPath, "RiskLimits");
        Directory.CreateDirectory(RiskLimitsDirectoryPath);
        MessageEfficiencyDirectoryPath = Path.Combine(DirectoryPath, "MessageEfficiency");
        Directory.CreateDirectory(MessageEfficiencyDirectoryPath);
        AlertsDirectoryPath = Path.Combine(DirectoryPath, "Alerts");
        Directory.CreateDirectory(AlertsDirectoryPath);
        AuditDirectoryPath = GetAuditDirectoryPath(DirectoryPath);
        Directory.CreateDirectory(AuditDirectoryPath);
        SeriesDirectoryPath = Path.Combine(DirectoryPath, "Series");
        Directory.CreateDirectory(SeriesDirectoryPath);
        WorkspaceDirectoryPath = Path.Combine(DirectoryPath, "Workspaces");
        Directory.CreateDirectory(WorkspaceDirectoryPath);

        // Region names are PATH-JOINED, not concatenated — Tools.Memory sanitizes every character
        // outside [A-Za-z0-9_\-.] to '_', so the separator becomes the same '_' the C++ side's
        // std::filesystem::path join produces. Concatenating here opens a different, empty region:
        // CreateOrOpen happily creates it, so the symptom is a hang in EnsureConnected or a
        // permanently empty read, never an error. Keep every site below joined.
        _serverHeaderBox = new LetterBox<ServerHeader>(ServerName / "ServerHeader", ServerAccess);
        _ = NewSharedArray<ServerHeader>((ServerName / "ServerHeader") + "LetterBox", 1, ServerAccess); // just for mirror

        EnsureConnected();

        ref readonly ServerHeader serverHeader = ref ServerHeader.GetReadonlyRef();

        _clientSocketHeaders = NewSharedArray<SocketHeader>(serverName / "ClientHeaders", serverHeader.ClientIds.Length, ServerAccess);

        _instrumentHeaders = NewSharedArray<InstrumentHeader128>(serverName / "InstrumentHeaders", serverHeader.InstrumentsCapacity, ServerAccess);
        _instrumentHeaderIdByInstrumentId = NewSharedArray<int>(serverName / "InstrumentHeaderIdByInstrumentId", serverHeader.InstrumentIds.Length, ServerAccess);

        _instrumentIdsByClientId = NewSharedArray<Bitset64>(serverName / "InstrumentIdsByClientId", serverHeader.ClientIds.Length, ServerAccess);
        _clientIdsByInstrumentId = NewSharedArray<Bitset64>(serverName / "ClientIdsByInstrumentId", serverHeader.InstrumentIds.Length, ServerAccess);
        // Book ownership: the server owns the authoritative book (serverName-keyed, writable); each client
        // owns its own replica (clientName-keyed, writable) that it builds from snapshot + ring deltas.
        // directoryPath == serverName for a ServerContext and == clientName for a ClientContext, so this
        // single line keeps the array-id ordering (mirror-safe) while giving each context its own book.
        _marketsByPrice = NewSharedArray<MarketByPrice64>(directoryPath / "MarketsByPrice", serverHeader.InstrumentIds.Length, this is ServerContext ? serverAccess : clientAccess);
        _riskLimits = NewSharedArray<RiskLimit>(serverName / "RiskLimits", serverHeader.InstrumentIds.Length, ServerAccess);
        _messageEfficiency = NewSharedArray<MessageEfficiency>(serverName / "MessageEfficiency", serverHeader.InstrumentIds.Length, ClientAccess);

        _orderStates = NewSharedArray<OrderState>(serverName / "OrderStates", serverHeader.OrdersCapacity, ServerAccess, false);
        _orderRisks = NewSharedArray<OrderRisk>(serverName / "OrderRisks", serverHeader.OrdersCapacity, ServerAccess, false);
        _orderTargets = NewSharedArray<OrderTarget>(serverName / "OrderTargets", serverHeader.OrdersCapacity, ClientAccess, false);

        _localPositionHeaders = NewSharedArray<PositionHeader>(serverName / "LocalPositionHeaders", serverHeader.LocalPositionsCapacity, ServerAccess, false);

        _instruments = new Instrument[serverHeader.InstrumentIds.Length];
        _positions = new Position[serverHeader.InstrumentIds.Length];
    }

    public int SharedArraysCount => _sharedArrays.Count;
    private Dictionary<int, SharedArray> _sharedArrays = new();
    protected SharedArray<T> NewSharedArray<T>(string name, int capacity, Access access, bool isDense = true) where T : unmanaged
    {
        _sharedArrays[_sharedArrays.Count] = new SharedArray<T>(name, capacity, Access.Write, isDense: isDense);
        //because this is used for Mirror, we need a secret write ability
        SharedArray<T> sharedArray = new SharedArray<T>(name, capacity, access, isDense: isDense);
        return sharedArray;
    }
    internal void Mirror(int arrayId, int index, ReadOnlySpan<byte> srcObj) // done on the mirror side (mirror receives over tcp a pack { arrayId, index, bytes }
    {
        _sharedArrays[arrayId].Write(index, srcObj);
    }

    public SharedArrayEnumerable EnumerateSharedArray(int arrayId, bool snapshot, Span<byte> dstObj) => new SharedArrayEnumerable(_sharedArrays[arrayId], snapshot, dstObj);


    protected void EnsureConnected()
    {
        while (!_serverHeaderBox.TryPeek(out ServerHeader serverHeader))
        {
            Console.WriteLine($"Context::EnsureConnected({ServerName}) failed to connect to server {ServerName} ... will try again.");
            Thread.Sleep(1000);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int GetLocalPositionIndex(int clientId, int instrumentId)
    {
        ThrowIfClientIdOutOfRange(clientId);
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return (clientId * ServerHeader.GetReadonlyRef().InstrumentIds.Length) + instrumentId;
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _instrumentHeaders.Dispose();
        _marketsByPrice.Dispose();
        _orderStates.Dispose();
        _orderTargets.Dispose();
        _clientSocketHeaders.Dispose();
        _serverHeaderBox.Dispose();
        _instrumentHeaderIdByInstrumentId.Dispose();
        _instrumentIdsByClientId.Dispose();
        _orderRisks.Dispose();
        _clientSocketHeaders.Dispose();
    }

    private bool _disposed;

    // Order rows are ONE server-wide array (see _orderStates/_orderTargets above: both keyed by
    // serverName, not by directoryPath), so every context is a view over the same storage. These
    // used to be abstract, with ClientContext reading the argument as a local index and
    // ServerContext as a global one — same signature, two meanings, so a caller holding a base
    // Context could not know which to pass. Keyed by the id there is only one meaning: OrderId
    // already carries clientId and localIndex, so GlobalIndex is exact and no caller needs to know
    // whose order it is.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<OrderState> GetOrderState(OrderId orderId) => ref _orderStates.GetEntry(orderId.GlobalIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<OrderTarget> GetOrderTarget(OrderId orderId) => ref _orderTargets.GetEntry(orderId.GlobalIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<OrderRisk> GetOrderRisk(OrderId orderId) => ref _orderRisks.GetEntry(orderId.GlobalIndex);

    public abstract ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId);
    public abstract bool TryGetInstrumentId(int instrumentHeaderId, out int instrumentId);
    public abstract Bitset64 InstrumentIds { get; }



    // server level - shared over all clients
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<RiskLimit> GetRiskLimit(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _riskLimits.GetEntry(instrumentId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<MessageEfficiency> GetMessageEfficiency(int productGroupId)
    {
        //ThrowIfInstrumentIdOutOfRange(productGroupId);
        return ref _messageEfficiency.GetEntry(productGroupId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<MarketByPrice64> GetMarketByPrice64(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _marketsByPrice.GetEntry(instrumentId);
    }

    // Common shared memory accessors
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<InstrumentHeader128> GetInstrumentHeader(int instrumentHeaderId)
    {
        ThrowIfInstrumentHeaderIdOutOfRange(instrumentHeaderId);
        return ref _instrumentHeaders.GetEntry(instrumentHeaderId);
    }

    // Startup-only reverse lookup for Server.LoadInstruments: the persisted record carries the
    // exchange's id, which is stable across sessions, whereas InstrumentHeaderId is just this run's
    // load order. Returns -1 when the contract is no longer listed, so the caller can skip it.
    public int GetInstrumentHeaderIdByExchangeInstrumentId(int exchangeInstrumentId)
    {
        int instrumentsCount = ServerHeader.GetReadonlyRef().InstrumentsCount;
        for (int instrumentHeaderId = 0; instrumentHeaderId < instrumentsCount; instrumentHeaderId++)
        {
            if (_instrumentHeaders[instrumentHeaderId].GetReadonlyRef().AsInstrumentHeader().ExchangeInstrumentId == exchangeInstrumentId)
                return instrumentHeaderId;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<int> GetInstrumentHeaderIdByInstrumentId(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _instrumentHeaderIdByInstrumentId.GetEntry(instrumentId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<Bitset64> GetInstrumentIdsByClientId(int clientId)
    {
        ThrowIfClientIdOutOfRange(clientId);
        return ref _instrumentIdsByClientId.GetEntry(clientId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<Bitset64> GetClientIdsByInstrumentId(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _clientIdsByInstrumentId.GetEntry(instrumentId);
    }

    public ref SharedArrayEntry<ServerHeader> ServerHeader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return ref _serverHeaderBox.GetEntry();
        }
    }

    public InstrumentHeaderEnumerable EnumerateInstrumentHeaders()
    {
        return new InstrumentHeaderEnumerable(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Instrument GetInstrument(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);

        if (_instruments[instrumentId] == null)
        {
            CreateInstrument(instrumentId);
        }
        return _instruments[instrumentId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Position GetPosition(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);

        if (_positions[instrumentId] == null)
        {
            Instrument instrument = GetInstrument(instrumentId);
            CreatePosition(instrument);
        }
        return _positions[instrumentId];
    }

    public abstract void ThrowIfInstrumentIdOutOfRange(int instrumentId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ThrowIfInstrumentHeaderIdOutOfRange(int instrumentHeaderId)
    {
        int maxCount = ServerHeader.GetReadonlyRef().InstrumentsCount;
        if ((uint)instrumentHeaderId >= (uint)maxCount)
            ThrowInstrumentHeaderIdOutOfRange(instrumentHeaderId, maxCount);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInstrumentHeaderIdOutOfRange(int instrumentHeaderId, int maxCount)
        => throw new ArgumentOutOfRangeException(nameof(instrumentHeaderId), $"{GetType()}.ThrowIfInstrumentHeaderIdOutOfRange({instrumentHeaderId}), instrumentHeaderId should be less than: {maxCount}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ThrowIfClientIdOutOfRange(int clientId)
    {
        if (!ServerHeader.GetReadonlyRef().ClientIds[clientId])
            ThrowClientIdOutOfRange(clientId);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowClientIdOutOfRange(int clientId)
        => throw new ArgumentOutOfRangeException(nameof(clientId), $"{GetType()}.ThrowIfClientIdOutOfRange({clientId}), clientId has not been allocated.");

    private ulong _lock = 0;
    private void CreateInstrument(int instrumentId)
    {
        MultiSeqLockWriter.AcquireLock(ref _lock);
        if (_instruments[instrumentId] != null)
        {
            MultiSeqLockWriter.ReleaseLock(ref _lock);
            return;
        }
        int instrumentHeaderId = GetInstrumentHeaderIdByInstrumentId(instrumentId).Read();
        ref SharedArrayEntry<InstrumentHeader128> header128Entry = ref GetInstrumentHeader(instrumentHeaderId);
        InstrumentHeader instrHeader = header128Entry.GetReadonlyRef().AsInstrumentHeader();

        ref SharedArrayEntry<MarketByPrice64> mbpEntry = ref _marketsByPrice[instrumentId];
        Instrument? instrument = null;

        if (instrHeader.InstrumentType == InstrumentType.Future)
        {
            instrument = new Future(header128Entry.Cast<FutureHeader>(), mbpEntry);
        }
        else if (instrHeader.InstrumentType == InstrumentType.Spread)
        {
            ref SpreadHeader sh = ref header128Entry.GetRef().AsSpread();

            Future longLeg = (GetInstrument(sh.LongInstrumentId) as Future)!;
            Future shortLeg = (GetInstrument(sh.ShortInstrumentId) as Future)!;

            instrument = new Spread(header128Entry.Cast<SpreadHeader>(), mbpEntry, longLeg, shortLeg);
        }
        else if (instrHeader.InstrumentType == InstrumentType.Forex)
        {
            instrument = new Forex(header128Entry.Cast<ForexHeader>(), mbpEntry);
        }
        else
        {
            throw new InvalidOperationException($"{GetType()}.CreateInstrument({instrumentId}), Unknown instrument type: {(int)instrHeader.InstrumentType}");
        }
        instrument.SessionManager = new SessionManager(Session.CME);
        _instruments[instrumentId] = instrument;
        MultiSeqLockWriter.ReleaseLock(ref _lock);

    }

    protected void CreatePosition(Instrument instrument)
    {
        MultiSeqLockWriter.AcquireLock(ref _lock);
        if (_positions[instrument.InstrumentId] != null)
        {
            MultiSeqLockWriter.ReleaseLock(ref _lock);
            return;
        }
        // A ServerContext's positions are the server-wide rows; no client owns their order slots
        // (only Client.Create ever sets one), so the house id is the correct inert owner.
        Position position = new Position(instrument, this, (this as ClientContext)?.ClientId ?? OrderIdAllocator.ServerStrategyId);
        _positions[instrument.InstrumentId] = position;
        MultiSeqLockWriter.ReleaseLock(ref _lock);
        return;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int GetInstrumentId(int instrumentHeaderId)
    {
        ThrowIfInstrumentHeaderIdOutOfRange(instrumentHeaderId);

        ref readonly InstrumentHeader128 header128 = ref _instrumentHeaders[instrumentHeaderId].GetReadonlyRef();

        return header128.AsInstrumentHeader().InstrumentId;
    }

    public abstract OrderEnumerable EnumerateOrders(int instrumentId = -1);

    public PositionEnumerable EnumeratePositions()
    {
        return new PositionEnumerable(this);
    }
    public InstrumentEnumerable EnumerateInstruments()
    {
        return new InstrumentEnumerable(this);
    }

    public MessageEfficiencyEnumerable EnumerateMessageEfficiency()
    {
        return new MessageEfficiencyEnumerable(_messageEfficiency, ServerHeader.GetReadonlyRef().InstrumentIds.Length);
    }
}

public sealed class ClientContext : Context
{
    public FileSystemPath ClientName { get; }
    public int ClientId => _clientId;
    private readonly int _clientId;

    public ClientContext(FileSystemPath clientName, FileSystemPath serverName, Access access)
        : base(serverName, clientName, Access.Read, access)
    {
        if (clientName.ToString().Contains(serverName))
        {
            ServerContext.ThrowIfInvalidServerName(clientName);
        }
        else
        {
            ThrowIfInvalidClientName(clientName);
        }

        if (string.IsNullOrWhiteSpace(clientName))
        {
            throw new ArgumentException($"{GetType()}.ClientContext(), Client name must be non-empty.", nameof(clientName));
        }

        ClientName = clientName;
        _clientId = GetClientIdFromMap(ClientName);
    }

    private void SaveMessageEfficiceny(in MessageEfficiency messageEfficiency)
    {
        string messageEfficiencyFilePath = GetMessageEfficiencyFilePath(ServerName, messageEfficiency.ProductGroup.ToString()).ToString();
        string messageEfficiencyLine = Json.SerializeToLine(messageEfficiency);
        Console.WriteLine($"ClientContext::SaveMessageEfficieny({messageEfficiencyFilePath}):{Environment.NewLine}{messageEfficiencyLine}");
        File.AppendAllLines(messageEfficiencyFilePath, new string[] { messageEfficiencyLine });
    }

    public static void ThrowIfInvalidClientName(FileSystemPath clientName)
    {
        string validDirectoryPath = GetDirectoryPath("");
        if (!clientName.ToString().StartsWith(validDirectoryPath))
        {
            throw new ArgumentException($"ClientContext.ThrowIfInvalidClientName({clientName}), clientName is invalid, must start with: {validDirectoryPath}");
        }
    }

    public static FileSystemPath DirectoriesPath => @$"S:\Strategies\{Clock.Mode}";

    public static FileSystemPath GetDirectoryPath(string clientName)
    {
        clientName = Path.GetFileName(clientName);
        return Path.Combine(DirectoriesPath, clientName);
    }

    public void AllocateProductGroupId(Instrument instrument, String4 productGroup)
    {
        int maxProductGroupId = ServerHeader.GetReadonlyRef().InstrumentIds.Length;
        for (int productGroupId = 0; productGroupId < maxProductGroupId; productGroupId++)
        {
            ref SharedArrayEntry<MessageEfficiency> messageEfficiencyEntry = ref _messageEfficiency.GetEntry(productGroupId);
            String4 thisProductGroup = messageEfficiencyEntry.GetReadonlyRef().ProductGroup;
            if (thisProductGroup == productGroup)
            {
                instrument.ProductGroupId = productGroupId;
                return;
            }
            else if (thisProductGroup == "")
            {
                instrument.ProductGroupId = productGroupId;
                {
                    string messageEfficiencyFilePath = GetMessageEfficiencyFilePath(ServerName, productGroup.ToString()).ToString();
                    string? messageEfficiencyLine = Tools.Tools.ReadLastLine(messageEfficiencyFilePath);
                    MessageEfficiency loadedMessageEfficiency = messageEfficiencyLine != null ? Json.Deserialize<MessageEfficiency>(messageEfficiencyLine) : Clock.Mode == ClockMode.Simulation ? MessageEfficiency.GetMaxLimits(productGroup) : MessageEfficiency.GetMinLimits(productGroup);
                    loadedMessageEfficiency.ProductGroup = productGroup;
                    loadedMessageEfficiency.ProductGroupId = productGroupId;
                    DateTime local = instrument.SessionManager.Session.ConvertToLocal(Clock.Now);
                    loadedMessageEfficiency.Reset(local);
                    messageEfficiencyEntry.Write(in loadedMessageEfficiency);
                }
                
                if (Clock.Mode == ClockMode.Realtime)
                {
                    Application.AddExitAction($"Save {productGroup} Message Efficiency", () =>
                    {
                        ref SharedArrayEntry<MessageEfficiency> messageEfficiencyEntry = ref _messageEfficiency.GetEntry(instrument.ProductGroupId);
                        SaveMessageEfficiceny(in messageEfficiencyEntry.GetReadonlyRef());
                    });
                }
                

                instrument.SessionManager.Closed += timestamp =>
                {
                    ref MessageEfficiency messageEfficiency = ref _messageEfficiency.GetEntry(instrument.ProductGroupId).GetRef();
                    DateTime local = instrument.SessionManager.Session.ConvertToLocal(timestamp);
                    MessageEfficiency messageEfficiencyCopy = messageEfficiency;
                    if (messageEfficiency.Reset(local) && Clock.Mode == ClockMode.Realtime)
                    {
                        SaveMessageEfficiceny(in messageEfficiencyCopy);
                    }
                };
                return;
            }
        }
        throw new InvalidOperationException($"{GetType()}.AllocateProductGroupId({productGroup}), ProductGroupIds is Full. ProductGroupIds.Length: {maxProductGroupId}");
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetClientIdFromMap(string clientName)
    {
        String128 clientNameCopy = new String128(clientName);
        foreach (int i in ServerHeader.GetReadonlyRef().ClientIds)
        {
            if (_clientSocketHeaders[i].IsEmpty())
            {
                continue;
            }

            SocketHeader header = _clientSocketHeaders[i].Read();

            if (header.ClientName == clientNameCopy)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"{GetType()}.GetClientIdFromMap({clientName}), Client not connected to server '{ServerName}'.");
    }

    

    // --- Local Implementations ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId)
    {
        int localPositionIndex = GetLocalPositionIndex(_clientId, instrumentId);
        return ref _localPositionHeaders.GetEntry(localPositionIndex);
    }

    public override Bitset64 InstrumentIds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return _instrumentIdsByClientId[_clientId].GetReadonlyRef();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool TryGetInstrumentId(int instrumentHeaderId, out int instrumentId)
    {
        instrumentId = GetInstrumentId(instrumentHeaderId);

        if (instrumentId < 0)
            return false;

        Bitset64 instrumentIds = InstrumentIds;

        if (!instrumentIds[instrumentId])
        {
            instrumentId = -1;
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void ThrowIfInstrumentIdOutOfRange(int instrumentId)
    {
        if (!_instrumentIdsByClientId[_clientId].GetReadonlyRef()[instrumentId])
            ThrowInstrumentIdOutOfRange(instrumentId);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInstrumentIdOutOfRange(int instrumentId)
        => throw new ArgumentOutOfRangeException(nameof(instrumentId), $"{GetType()}.ThrowIfInstrumentIdOutOfRange({instrumentId}), instrumentId has not been allocated.");

    // --- C# Specific Implementations ---

    public override OrderEnumerable EnumerateOrders(int instrumentId = -1)
    {
        return new OrderEnumerable(_orderStates, _orderTargets, OrderIdAllocator.GetFirstGlobalIndex(_clientId), OrderIdAllocator.GetLastGlobalIndex(_clientId), instrumentId);
    }

    public override void Dispose()
    {
        _localPositionHeaders.Dispose();
        base.Dispose();
    }
}

public sealed class ServerContext : Context
{
    private readonly SharedArray<PositionHeader> _serverPositionHeaders;

    // Server-only: the durable record of which clients and instruments were allocated, replayed at
    // startup. Shared memory is never the durable store — it is unlinked once the last mapper drops.
    public FileSystemPath ClientsDirectoryPath { get; }
    public FileSystemPath InstrumentsDirectoryPath { get; }

    public FileSystemPath GetClientsFilePath(Timestamp date)
    {
        return ClientsDirectoryPath / (date.ToDateString() + ".allocateclient");
    }

    public FileSystemPath GetInstrumentsFilePath(Timestamp date)
    {
        return InstrumentsDirectoryPath / (date.ToDateString() + ".allocateinstrument");
    }

    public ServerContext(FileSystemPath serverName, Access access)
        : base(serverName, serverName, access, Access.Read)
    {
        ThrowIfInvalidServerName(serverName);

        ClientsDirectoryPath = ServerName / "Clients";
        Directory.CreateDirectory(ClientsDirectoryPath);
        InstrumentsDirectoryPath = ServerName / "Instruments";
        Directory.CreateDirectory(InstrumentsDirectoryPath);

        _serverPositionHeaders = NewSharedArray<PositionHeader>(serverName / "ServerPositionHeaders", ServerHeader.GetReadonlyRef().InstrumentIds.Length, ServerAccess);
    }

    public static void ThrowIfInvalidServerName(FileSystemPath serverName)
    {
        string validDirectoryPath = GetDirectoryPath("");
        if (!serverName.ToString().StartsWith(validDirectoryPath))
        {
            throw new ArgumentException($"ServerContext.ThrowIfInvalidServerName({serverName}), serverName is invalid, must start with: {validDirectoryPath}");
        }
    }

    public static FileSystemPath DirectoriesPath => @$"S:\Servers\{Clock.Mode}";

    public static FileSystemPath GetDirectoryPath(string serverName)
    {
        serverName = Path.GetFileName(serverName);
        return Path.Combine(DirectoriesPath, serverName);
    }

    public static LetterBox<ServerHeader> Connect(in ServerHeader serverHeader)
    {
        FileSystemPath serverName = serverHeader.ServerName.ToString();
        // Path-joined, matching Context's _serverHeaderBox. Concatenating here names a different
        // region, and Context.EnsureConnected() then spins forever on a box nobody writes.
        LetterBox<ServerHeader> serverHeaderBox = new LetterBox<ServerHeader>(serverName / "ServerHeader", Access.Write);

        // Reserve the house book before anyone can connect. AllocateClientId hands out
        // ClientIds.LowestClear, so without this the FIRST client to attach takes id 0 and every
        // manual order from a server workspace would be attributed to it. Pre-setting the bit both
        // keeps it out of the allocator and makes the slot addressable — ThrowIfClientIdOutOfRange
        // and the RiskLayer's StrategyIdNotAllocated check each test this bit.
        ServerHeader reserved = serverHeader;
        reserved.ClientIds.Set(OrderIdAllocator.ServerStrategyId);

        if (!serverHeaderBox.TryStore(in reserved))
        {
            throw new InvalidOperationException($"ServerContext.Connect({serverName}), Failed to write ServerHeader to shared memory.");
        }

        return serverHeaderBox;
    }

    // --- Global Implementations ---
    public override ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId)
    {
        return ref _serverPositionHeaders.GetEntry(instrumentId);
    }

    public override Bitset64 InstrumentIds
    {
        get
        {
            ServerHeader serverHeader = ServerHeader.GetReadonlyRef();
            return serverHeader.InstrumentIds;
        }
    }

    // --- Specific Server Expositions ---
    public ref SharedArrayEntry<PositionHeader> GetPositionHeader(int clientId, int instrumentId)
    {
        int localPositionIndex = GetLocalPositionIndex(clientId, instrumentId);
        return ref _localPositionHeaders.GetEntry(localPositionIndex);
    }

    public ref readonly SharedArrayEntry<SocketHeader> GetSocketHeader(int clientId)
    {
        ThrowIfClientIdOutOfRange(clientId);
        return ref _clientSocketHeaders[clientId];
    }

    public int AllocateClientId(in SocketHeader socketHeader)
    {
        // The server's leaf name is reserved for strategy 0's directory — a client named after the server would share the house book's files (see Spec.md).
        if (socketHeader.ClientName == ServerStrategyName.Path)
            throw new InvalidOperationException($"ServerContext.AllocateClientId({ServerName}), client name {socketHeader.ClientName} collides with the reserved server strategy name.");

        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();
        ref Bitset64 clientIds = ref ServerHeader.GetRef().ClientIds;

        foreach (int i in clientIds)
        {
            if (_clientSocketHeaders[i].GetReadonlyRef().ClientName == socketHeader.ClientName)
                return i;
        }
        int clientId = socketHeader.ClientId < 0 ? clientIds.LowestClear : socketHeader.ClientId;

        if (clientId < 0)
            throw new InvalidOperationException($"ServerContext.GetOrAddClientId({ServerName}), Failed allocate clientId for {socketHeader.ClientName}.");
        
        serverHeaderEntry.AcquireLock();
        SocketHeader socketHeaderCopy = socketHeader;
        socketHeaderCopy.ClientId = clientId;
        _clientSocketHeaders[clientId].Write(in socketHeaderCopy);
        serverHeader.ClientIds.Set(clientId);
        _instrumentIdsByClientId[clientId].Write(new Bitset64());
        serverHeaderEntry.ReleaseLock();

        return clientId;
    }

    public SocketHeader DeallocateClient(int clientId)
    {
        Bitset64 instrumentIds = GetInstrumentIdsByClientId(clientId).GetReadonlyRef();
        foreach (int instrumentId in instrumentIds)
        {
            ref Bitset64 clientIds = ref GetClientIdsByInstrumentId(instrumentId).GetRef();
            clientIds.Clear(clientId);
        }
        GetInstrumentIdsByClientId(clientId).Write(new Bitset64());
        return _clientSocketHeaders[clientId].GetReadonlyRef();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool TryGetInstrumentId(int instrumentHeaderId, out int instrumentId)
    {
        instrumentId = GetInstrumentId(instrumentHeaderId);

        if (instrumentId < 0)
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void ThrowIfInstrumentIdOutOfRange(int instrumentId)
    {
        if (!ServerHeader.GetReadonlyRef().InstrumentIds[instrumentId])
            ThrowInstrumentIdOutOfRange(instrumentId);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInstrumentIdOutOfRange(int instrumentId)
        => throw new ArgumentOutOfRangeException(nameof(instrumentId), $"{GetType()}.ThrowIfInstrumentIdOutOfRange({instrumentId}), instrumentId has not been allocated.");

    public void OnInstrumentHeader(in InstrumentHeader128 header128)
    {
        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();

        if (serverHeader.InstrumentsCount >= serverHeader.InstrumentsCapacity)
            throw new ArgumentOutOfRangeException(nameof(serverHeader.InstrumentsCount), $"ServerContext.OnInstrumentHeader: InstrumentsCount({serverHeader.InstrumentsCount}) >= InstrumentsCapacity({serverHeader.InstrumentsCapacity})");
        
        ref InstrumentHeader header = ref header128.AsInstrumentHeader();
        header.InstrumentId = -1;
        header.InstrumentHeaderId = serverHeader.InstrumentsCount;

        ref SharedArrayEntry<InstrumentHeader128> header128Entry = ref _instrumentHeaders[header.InstrumentHeaderId];
        header128Entry.Write(header128);

        serverHeaderEntry.AcquireLock();
        serverHeader.InstrumentsCount++;
        serverHeaderEntry.ReleaseLock();
    }

    public int AllocateInstrument(int instrumentHeaderId)
    {
        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();
        ref InstrumentHeader128 header128 = ref GetInstrumentHeader(instrumentHeaderId).GetRef();
        ref InstrumentHeader header = ref header128.AsInstrumentHeader();

        int instrumentId = header.InstrumentId;
        if (instrumentId >= 0 && GetInstrumentHeaderIdByInstrumentId(instrumentId).GetReadonlyRef() == instrumentHeaderId)
        {
            return instrumentId;
        }

        if (serverHeader.InstrumentIds.IsFull)
        {
            throw new InvalidOperationException($"{GetType()}.AllocateInstrument({instrumentHeaderId}), InstrumentIds is Full. InstrumentIds.Length: {serverHeader.InstrumentIds.Length}");
        }
        instrumentId = serverHeader.InstrumentIds.LowestClear;

        serverHeaderEntry.AcquireLock();

        _marketsByPrice[instrumentId].Write(new MarketByPrice64());
        _clientIdsByInstrumentId[instrumentId].Write(new Bitset64());
        _instrumentHeaderIdByInstrumentId[instrumentId].Write(instrumentHeaderId);

        Symbology symbology = header128.Symbology;

        string riskLimitPath = GetRiskLimitsFilePath(DirectoryPath, symbology.Symbol).ToString();
        string? riskLimitLine = Tools.Tools.ReadLastLine(riskLimitPath);
        RiskLimit riskLimit = riskLimitLine != null ? Json.Deserialize<RiskLimit>(riskLimitLine) : Clock.Mode == ClockMode.Simulation ? RiskLimit.GetMaxLimits(instrumentId) : RiskLimit.GetMinLimits(instrumentId);
        riskLimit.InstrumentId = instrumentId;
        riskLimit.WorstShortWorkingQuantity = 0;
        riskLimit.WorstLongWorkingQuantity = 0;
        _riskLimits.GetEntry(instrumentId).Write(riskLimit);


        string positionPath = GetPositionFilePath(DirectoryPath, symbology.Symbol).ToString();
        string? positionLine = Tools.Tools.ReadLastLine(positionPath);
        PositionHeader positionHeader = positionLine != null ? Json.Deserialize<PositionHeader>(positionLine) : new PositionHeader();
        // Server-wide row: no owning order; template id carries the ids (clientId 0 stands in for the old -1 sentinel)
        positionHeader.OrderHeader.OrderId = new OrderId { InstrumentId = instrumentId };
        _serverPositionHeaders.GetEntry(instrumentId).Write(positionHeader);

        header.InstrumentId = instrumentId;
        serverHeader.InstrumentIds.Set(instrumentId);

        serverHeaderEntry.ReleaseLock();

        return instrumentId;
    }

    public void AllocateInstrument(int clientId, int instrumentId)
    {
        int instrumentHeaderId = GetInstrumentHeaderIdByInstrumentId(instrumentId).GetReadonlyRef();

        if (InstrumentIds[instrumentId] == false)
		{
			return;
		}

        ref InstrumentHeader128 header128 = ref GetInstrumentHeader(instrumentHeaderId).GetRef();
        Symbology symbology = header128.Symbology;

        // Strategy 0 has no socket to take a name from; its book lives at ServerStrategyName (see Spec.md).
        string clientName = clientId == OrderIdAllocator.ServerStrategyId
            ? ServerStrategyName.ToString()
            : GetSocketHeader(clientId).GetReadonlyRef().ClientName.ToString();
        string positionPath = GetPositionFilePath(clientName, symbology.Symbol).ToString();
        string? positionLine = Tools.Tools.ReadLastLine(positionPath);
        PositionHeader positionHeader = positionLine != null ? Json.Deserialize<PositionHeader>(positionLine) : new PositionHeader();
        positionHeader.OrderHeader.OrderId = new OrderId { ClientId = clientId, StrategyId = clientId, InstrumentId = instrumentId };

        // A backtest has no operator to un-pause it, and the RiskLayer rejects a paused algo with
        // AlgoIsPaused. Realtime is forced Paused rather than left to whatever the restored row said:
        // a persisted Live would otherwise re-arm a strategy at startup with nobody asking for it.
        // Enabling a strategy in production is a deliberate human act.
        positionHeader.AlgoStatus = Clock.Mode == ClockMode.Simulation ? AlgoStatus.Live : AlgoStatus.Paused;

        GetPositionHeader(clientId, instrumentId).Write(positionHeader);

        ref SharedArrayEntry<Bitset64> instrumentIdsEntry = ref GetInstrumentIdsByClientId(clientId);
        ref SharedArrayEntry<Bitset64> clientIdsEntry = ref GetClientIdsByInstrumentId(instrumentId);
        
        clientIdsEntry.AcquireLock();
        instrumentIdsEntry.AcquireLock();
        
        clientIdsEntry.GetRef().Set(clientId);
        instrumentIdsEntry.GetRef().Set(instrumentId);

        instrumentIdsEntry.ReleaseLock();
        clientIdsEntry.ReleaseLock();


    }


    // --- C# Specific Implementations ---

    public override OrderEnumerable EnumerateOrders(int instrumentId = -1)
    {
        return new OrderEnumerable(_orderStates, _orderTargets, 0, OrderIdAllocator.GetLastGlobalIndex(ServerHeader.GetReadonlyRef().ClientIds.Length - 1), instrumentId);
    }

    // Full state dump for debugging: every entry of every shared structure, zeroed slots included.
    // Reads are unsynchronised — a torn row under concurrent writes is acceptable here; the printed
    // seq tells you (odd = write in progress at read time).
    public void PrintDebug()
    {
        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ServerHeader serverHeader = serverHeaderEntry.GetReadonlyRef();
        int instrumentsLength = serverHeader.InstrumentIds.Length;

        Console.WriteLine($"===== {GetType().Name}.PrintDebug({ServerName}) =====");
        Console.WriteLine($"DirectoryPath: {DirectoryPath}, ServerAccess: {ServerAccess}, ClientAccess: {ClientAccess}, SharedArrays: {SharedArraysCount}");
        Console.WriteLine();
        Console.WriteLine($"--- ServerHeader letterbox seq={serverHeaderEntry.GetSeq()} ---");
        Console.WriteLine(serverHeader.ToString());

        PrintSharedArray(_clientSocketHeaders, "ClientSocketHeaders [clientId]");
        PrintSharedArray(_instrumentIdsByClientId, "InstrumentIdsByClientId [clientId]");
        PrintSharedArray(_clientIdsByInstrumentId, "ClientIdsByInstrumentId [instrumentId]");
        PrintSharedArray(_instrumentHeaderIdByInstrumentId, "InstrumentHeaderIdByInstrumentId [instrumentId]");

        // Custom loop: InstrumentHeader128 has no JSON ToString, and Symbology throws on anything
        // but Future/Spread (Forex symbology is not implemented), so print raw fields + guarded symbol.
        Console.WriteLine();
        Console.WriteLine($"--- InstrumentHeaders [instrumentHeaderId] x {_instrumentHeaders.Capacity} ---");
        for (int i = 0; i < _instrumentHeaders.Capacity; i++)
        {
            ref SharedArrayEntry<InstrumentHeader128> entry = ref _instrumentHeaders.GetEntry(i);
            ref readonly InstrumentHeader128 header128 = ref entry.GetReadonlyRef();
            InstrumentHeader header = header128.AsInstrumentHeader();
            string symbol = header.InstrumentType == InstrumentType.Future || header.InstrumentType == InstrumentType.Spread ? header128.Symbology.Symbol : "-";
            Console.WriteLine($"[{i}] seq={entry.GetSeq()} type={header.InstrumentType} symbol={symbol} instrumentId={header.InstrumentId} exchangeInstrumentId={header.ExchangeInstrumentId} coreGroupId={header.CoreGroupId} tradingStatus={header.TradingStatus} tickSize={header.TickSize}");
        }

        PrintSharedArray(_riskLimits, "RiskLimits [instrumentId]");
        PrintSharedArray(_serverPositionHeaders, "ServerPositionHeaders [instrumentId]");

        Console.WriteLine();
        Console.WriteLine($"--- LocalPositionHeaders [index] clientId.instrumentId x {_localPositionHeaders.Capacity} ---");
        for (int i = 0; i < _localPositionHeaders.Capacity; i++)
        {
            ref SharedArrayEntry<PositionHeader> entry = ref _localPositionHeaders.GetEntry(i);
            Console.WriteLine($"[{i}] {i / instrumentsLength}.{i % instrumentsLength} seq={entry.GetSeq()} {entry.GetReadonlyRef()}");
        }

        // Orders grouped per slot: target, state and risk side by side is what slot-level debugging
        // (id mismatches, leaked reservations) actually needs.
        Console.WriteLine();
        Console.WriteLine($"--- Orders [globalOrderIndex] clientId.localOrderIndex x {_orderTargets.Capacity} ---");
        for (int globalOrderIndex = 0; globalOrderIndex < _orderTargets.Capacity; globalOrderIndex++)
        {
            int clientId = globalOrderIndex >> OrderIdAllocator.OrdersPerClientBitShift;
            int localOrderIndex = globalOrderIndex & (OrderIdAllocator.OrdersPerClient - 1);
            ref SharedArrayEntry<OrderTarget> targetEntry = ref _orderTargets.GetEntry(globalOrderIndex);
            ref SharedArrayEntry<OrderState> stateEntry = ref _orderStates.GetEntry(globalOrderIndex);
            ref SharedArrayEntry<OrderRisk> riskEntry = ref _orderRisks.GetEntry(globalOrderIndex);
            OrderRisk orderRisk = riskEntry.GetReadonlyRef();
            Console.WriteLine($"[{globalOrderIndex}] {clientId}.{localOrderIndex} target seq={targetEntry.GetSeq()} {targetEntry.GetReadonlyRef()}");
            Console.WriteLine($"[{globalOrderIndex}] {clientId}.{localOrderIndex} state  seq={stateEntry.GetSeq()} {stateEntry.GetReadonlyRef()}");
            Console.WriteLine($"[{globalOrderIndex}] {clientId}.{localOrderIndex} risk   seq={riskEntry.GetSeq()} absWorstOrderQuantity={orderRisk.GetAbsWorstOrderQuantity(0)}");
        }

        PrintSharedArray(_marketsByPrice, "MarketsByPrice [instrumentId]");
        PrintSharedArray(_messageEfficiency, "MessageEfficiency [productGroupId]");
    }

    private static void PrintSharedArray<T>(SharedArray<T> sharedArray, string title) where T : unmanaged
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} x {sharedArray.Capacity} ---");
        for (int i = 0; i < sharedArray.Capacity; i++)
        {
            ref SharedArrayEntry<T> entry = ref sharedArray.GetEntry(i);
            Console.WriteLine($"[{i}] seq={entry.GetSeq()} {entry.GetReadonlyRef()}");
        }
    }

    public override void Dispose()
    {
        _serverPositionHeaders.Dispose();
        base.Dispose();
    }
}

// --- Enumerables ---

public ref struct SharedArrayEnumerable
{
    private readonly SharedArray _sharedArray;
    private readonly bool _snapshot;
    private readonly Span<byte> _dstObj;

    internal SharedArrayEnumerable(SharedArray array, bool snapshot, Span<byte> dstObj)
    {
        _sharedArray = array;
        _snapshot = snapshot;
        _dstObj = dstObj;
    }

    public Enumerator GetEnumerator() => new Enumerator(_sharedArray, _snapshot, _dstObj);

    public ref struct Enumerator
    {
        private readonly SharedArray _sharedArray;
        private readonly bool _snapshot;
        private Span<byte> _dstObj;
        private int _index;
        private ReadOnlySpan<byte> rdstObj;

        internal Enumerator(SharedArray array, bool snapshot, Span<byte> dstObj)
        {
            _sharedArray = array;
            _snapshot = snapshot;
            _dstObj = dstObj;            
            _index = -1;
            rdstObj = default;
        }

        public Record Current => new Record(_index, rdstObj);

        public bool MoveNext()
        {
            while (++_index < _sharedArray.Capacity)
            {
                ReadStatus readStatus = _sharedArray.TryRead(_index, _dstObj, out rdstObj);
                if (readStatus == ReadStatus.Empty)
                    continue;
                if (_snapshot || readStatus == ReadStatus.New)
                    return true;
            }
            return false;
        }
    }

    public readonly ref struct Record
    {
        public readonly int Index;
        public readonly ReadOnlySpan<byte> Bytes;

        internal Record(int index, ReadOnlySpan<byte> bytes)
        {
            Index = index;
            Bytes = bytes;
        }

        public void Deconstruct(out int index, out ReadOnlySpan<byte> bytes)
        {
            index = Index;
            bytes = Bytes;
        }
    }
}



public readonly struct PositionEnumerable
{
    private readonly Context _context;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PositionEnumerable(Context context)
    {
        _context = context;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PositionEnumerator GetEnumerator()
    {
        return new PositionEnumerator(_context);
    }
}
public struct PositionEnumerator
{
    private readonly Context _context;
    private Bitset64 _instrumentIds;

    public Position Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PositionEnumerator(Context context)
    {
        _context = context;
        _instrumentIds = context.InstrumentIds;
        Current = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_instrumentIds.IsEmpty)
        {
            Current = default!;
            return false;
        }

        int instrumentId = _instrumentIds.LowestSet;
        _instrumentIds.Clear(instrumentId);

        Current = _context.GetPosition(instrumentId);
        return true;
    }
}

public readonly struct MessageEfficiencyEnumerable
{
    private readonly SharedArray<MessageEfficiency> _messageEfficiency;
    private readonly int _maxProductGroupId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MessageEfficiencyEnumerable(SharedArray<MessageEfficiency> messageEfficiency, int maxProductGroupId)
    {
        _messageEfficiency = messageEfficiency;
        _maxProductGroupId = maxProductGroupId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MessageEfficiencyEnumerator GetEnumerator()
    {
        return new MessageEfficiencyEnumerator(_messageEfficiency, _maxProductGroupId);
    }
}

public struct MessageEfficiencyEnumerator
{
    private readonly SharedArray<MessageEfficiency> _messageEfficiency;
    private readonly int _maxProductGroupId;
    private int _productGroupId;

    public MessageEfficiency Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageEfficiencyEnumerator(SharedArray<MessageEfficiency> messageEfficiency, int maxProductGroupId)
    {
        _messageEfficiency = messageEfficiency;
        _maxProductGroupId = maxProductGroupId;
        _productGroupId = 0;
        Current = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // ProductGroups are allocated densely from 0; stop at the first empty slot.
        if (_productGroupId >= _maxProductGroupId || _messageEfficiency[_productGroupId].IsEmpty())
        {
            Current = default!;
            return false;
        }

        Current = _messageEfficiency[_productGroupId].Read();
        _productGroupId++;
        return true;
    }
}

public readonly struct OrderEnumerable
{
    private readonly SharedArray<OrderState> _orderStates;
    private readonly SharedArray<OrderTarget> _orderTargets;

    private readonly int _start;
    private readonly int _end;
    private readonly int _instrumentId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderEnumerable(SharedArray<OrderState> orderStates, SharedArray<OrderTarget> orderTargets, int start, int end, int instrumentId = -1)
    {
        _orderStates = orderStates;
        _orderTargets = orderTargets;
        _start = start;
        _end = end;
        _instrumentId = instrumentId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderEnumerator GetEnumerator()
    {
        return new OrderEnumerator(_orderStates, _orderTargets, _start, _end, _instrumentId);
    }
}

public struct OrderEnumerator
{
    private readonly SharedArray<OrderState> _orderStates;
    private readonly SharedArray<OrderTarget> _orderTargets;

    private readonly int _lastGlobalOrderIndex;
    private readonly int _instrumentId;
    private int _globalOrderIndex;

    public (OrderState State, OrderTarget Target) Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderEnumerator(SharedArray<OrderState> orderStates, SharedArray<OrderTarget> orderTargets, int firstGlobalIndex, int lastGlobalIndex, int instrumentId)
    {
        _orderStates = orderStates;
        _orderTargets = orderTargets;
        _globalOrderIndex = firstGlobalIndex - 1;
        _lastGlobalOrderIndex = lastGlobalIndex;
        _instrumentId = instrumentId;
        Current = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
    NextGlobalOrderIndex:
        while (++_globalOrderIndex <= _lastGlobalOrderIndex)
        {
            if (_orderStates[_globalOrderIndex].IsEmpty())
            {
                continue;
            }

            ref SharedArrayEntry<OrderState> stateEntry = ref _orderStates.GetEntry(_globalOrderIndex);
            ref SharedArrayEntry<OrderTarget> targetEntry = ref _orderTargets.GetEntry(_globalOrderIndex);

            ref readonly OrderState state = ref stateEntry.GetReadonlyRef();
            ref readonly OrderTarget target = ref targetEntry.GetReadonlyRef();

            ulong stateSeq0, stateSeq1 = 0;
            ulong targetSeq0, targetSeq1 = 0;

            do
            {
                stateSeq0 = stateEntry.GetSeq();
                targetSeq0 = targetEntry.GetSeq();

                if (Protocol.IsWriteInProgress(stateSeq0) || Protocol.IsWriteInProgress(targetSeq0))
                {
                    continue;
                }

                if (_instrumentId != -1 && target.OrderHeader.OrderId.InstrumentId != _instrumentId || target.OrderHeader.OrderId == 0)
                {
                    goto NextGlobalOrderIndex;
                }

                if (state.OrderHeader.OrderId == target.OrderHeader.OrderId)
                {
                    if (state.OrderStateStatus == OrderStateStatus.Done)
                    {
                        stateSeq1 = stateEntry.GetSeq();
                        targetSeq1 = targetEntry.GetSeq();

                        if (stateSeq0 != stateSeq1 || targetSeq0 != targetSeq1)
                        {
                            continue;
                        }

                        goto NextGlobalOrderIndex;
                    }

                    Current = (state, target);
                }
                else
                {
                    Current = (default!, target);
                }

                stateSeq1 = stateEntry.GetSeq();
                targetSeq1 = targetEntry.GetSeq();
            }
            while (stateSeq0 != stateSeq1 || targetSeq0 != targetSeq1);

            return true;
        }

        return false;
    }
}

public readonly struct InstrumentHeaderEnumerable
{
    private readonly Context _context;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InstrumentHeaderEnumerable(Context context)
    {
        _context = context;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InstrumentHeaderEnumerator GetEnumerator()
    {
        return new InstrumentHeaderEnumerator(_context);
    }
}

public struct InstrumentHeaderEnumerator
{
    private readonly Context _context;
    private int _instrumentHeaderId;
    private int _count;

    public InstrumentHeader128 Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal InstrumentHeaderEnumerator(Context context)
    {
        _context = context;
        _count = context.ServerHeader.GetReadonlyRef().InstrumentsCount;
        _instrumentHeaderId = -1;
        Current = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (++_instrumentHeaderId < _count)
        {
            Current = _context.GetInstrumentHeader(_instrumentHeaderId).GetReadonlyRef();
            return true;
        }
        return false;
    }
}

public readonly struct InstrumentEnumerable
{
    private readonly Context _context;
    private readonly Bitset64 _subscribed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InstrumentEnumerable(Context context)
    {
        _context = context;
        _subscribed = context.InstrumentIds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InstrumentEnumerator GetEnumerator()
    {
        return new InstrumentEnumerator(_context, _subscribed);
    }
}

public struct InstrumentEnumerator
{
    private readonly Context _context;
    private Bitset64 _subscribed;

    public Instrument Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal InstrumentEnumerator(Context context, Bitset64 subscribed)
    {
        _context = context;
        _subscribed = subscribed;
        Current = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_subscribed.IsEmpty)
        {
            Current = default!;
            return false;
        }

        int instrumentId = _subscribed.LowestSet;
        _subscribed.Clear(instrumentId);

        Current = _context.GetInstrument(instrumentId);
        return true;
    }
}

public class WorkspaceContext
{
    public Context Primary { get; }
    public ManualClient Manual { get; }

    public WorkspaceContext(Context primary, ManualClient manual)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary), $"WorkspaceContext.WorkspaceContext(), primary context cannot be null.");
        Manual = manual ?? throw new ArgumentNullException(nameof(manual), $"WorkspaceContext.WorkspaceContext(), manual client cannot be null.");
    }

    // A ServerContext already enumerates every client's orders — including this GUI's own slice —
    // so compositing the manual context on top of it would list those twice.
    public CompositeOrderEnumerable EnumerateOrders(int instrumentId = -1)
    {
        return new CompositeOrderEnumerable(Primary, Primary is ServerContext ? null : Manual.Context, instrumentId);
    }
}

public readonly struct CompositeOrderEnumerable
{
    private readonly Context _primary;
    private readonly Context? _secondary;
    private readonly int _instrumentId;

    // secondary may be null when primary already covers its rows.
    public CompositeOrderEnumerable(Context primary, Context? secondary, int instrumentId)
    {
        _primary = primary;
        _secondary = secondary;
        _instrumentId = instrumentId;
    }

    public CompositeOrderEnumerator GetEnumerator()
    {
        return new CompositeOrderEnumerator(_primary, _secondary, _instrumentId);
    }
}

public struct CompositeOrderEnumerator
{
    private OrderEnumerator _primary;
    private OrderEnumerator _secondary;
    private bool _inPrimary;
    private readonly bool _hasSecondary;

    public CompositeOrderEnumerator(Context primary, Context? secondary, int instrumentId)
    {
        _primary = primary.EnumerateOrders(instrumentId).GetEnumerator();
        _hasSecondary = secondary != null;
        _secondary = _hasSecondary ? secondary!.EnumerateOrders(instrumentId).GetEnumerator() : default;
        _inPrimary = true;
        Current = default;
    }

    public (OrderState State, OrderTarget Target) Current { get; private set; }

    public bool MoveNext()
    {
        if (_inPrimary)
        {
            if (_primary.MoveNext())
            {
                Current = _primary.Current;
                return true;
            }
            _inPrimary = false;
        }

        if (!_hasSecondary)
            return false;

        if (_secondary.MoveNext())
        {
            Current = _secondary.Current;
            return true;
        }

        return false;
    }
}
//END_FILE HFT/Provider/Context.cs