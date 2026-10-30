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
    private static readonly List<Context> s_retiredContexts = new();   // contexts a Renew replaced: still mapped, disposed with the rest

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

    // Fresh contexts for a remote desk rebuilt on a restarted server: everything a context learnt of the old run (its cached
    // instruments, positions, symbol strings, a client's id) starts over. The old ones stay mapped, views of the same copy, so a
    // thread still reading through one never touches freed memory; they are disposed at exit (see Spec.md "Remote workspaces").
    public static void Renew()
    {
        lock (s_lock)
        {
            s_retiredContexts.Add(ServerContext);
            s_retiredContexts.AddRange(s_clientContexts.Values);
            s_clientContexts.Clear();
            ServerContext = new ServerContext(ServerName, Access.Read);
        }
    }

    // Keeps a context its owner is done with mapped until exit, like those Renew replaces: something may still read through it.
    public static void Retire(Context context)
    {
        lock (s_lock)
            s_retiredContexts.Add(context);
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
                foreach (Context retiredContext in s_retiredContexts)
                    retiredContext.Dispose();
                s_retiredContexts.Clear();
                IsInitialized = false;
            }
        }
    }
}

public abstract class Context
{
    public FileSystemPath ServerName { get; }
    public FileSystemPath LoggingServerName { get; }
    // Strategy 0's directory: the ServerStrategy lives in the Strategies tree like any other strategy (see Spec.md).
    public FileSystemPath ServerStrategyName { get; }
    public FileSystemPath DirectoryPath { get; }

    /// The last part of DirectoryPath (the server's or client's leaf name), or the whole path when it has none.
    public string DirectoryName => Path.GetFileName(DirectoryPath.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } directoryName ? directoryName : DirectoryPath.Path;

    public Access ServerAccess { get; }
    public Access ClientAccess { get; }

    // Root of every server and strategy directory: S:\ (/mnt/S/ on Linux) unless a program sets it at startup, before any path is built.
    public static FileSystemPath DirectoriesRoot { get; set; } = @"S:\";

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

    public static FileSystemPath GetMessageEfficiencyFilePath(FileSystemPath directoryPath, string productGroupRoot)
    {
        return Path.Combine(directoryPath, "MessageEfficiency", productGroupRoot + ".messageefficiency");
    }

    public static FileSystemPath GetRateLimitFilePath(FileSystemPath directoryPath, string coreGroupName)
    {
        return Path.Combine(directoryPath, "RateLimits", coreGroupName + ".ratelimit");
    }

    public static FileSystemPath GetCoreGroupFilePath(FileSystemPath directoryPath, string coreGroupName)
    {
        return Path.Combine(directoryPath, "CoreGroups", coreGroupName + ".coregroup");
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
    public FileSystemPath RateLimitsDirectoryPath { get; }
    public FileSystemPath CoreGroupsDirectoryPath { get; }
    public FileSystemPath AuditDirectoryPath { get; }
    public FileSystemPath SeriesDirectoryPath { get; }
    public FileSystemPath WorkspaceDirectoryPath { get; }


    // server
    protected readonly LetterBox<ServerHeader> _serverHeaderBox;
    protected readonly SharedArray<SocketHeader> _clientSocketHeaders;

    // instruments
    protected readonly SharedArray<InstrumentHeader256> _instrumentHeaders;
    protected readonly SharedArray<int> _instrumentHeaderIdByInstrumentId;

    // subscriptions
    protected readonly SharedArray<Bitset64> _instrumentIdsByClientId;
    protected readonly SharedArray<Bitset64> _clientIdsByInstrumentId;
    protected readonly SharedArray<MarketByPrice64> _marketsByPrice;

    // positions, limits and exposure: the server's per instrument, each strategy's per strategy and instrument
    protected readonly SharedArray<PositionHeader> _serverPositionHeaders;   // every strategy's fills summed; its AlgoStatus is the master switch
    protected readonly SharedArray<PositionHeader> _localPositionHeaders;
    protected readonly SharedArray<RiskLimit> _serverRiskLimits;             // the firm's limit, every order the server reads
    protected readonly SharedArray<RiskLimit> _localRiskLimits;              // the strategy's limit, its own algo orders
    protected readonly SharedArray<WorkingRisk> _serverWorkingRisks;         // every order on the instrument, written by the server's RiskLayer
    protected readonly SharedArray<WorkingRisk> _localWorkingRisks;          // the strategy's own orders, written by its RiskLayer

    // orders
    protected readonly SharedArray<OrderState> _orderStates;
    protected readonly SharedArray<OrderTarget> _orderTargets;

    // execution
    protected readonly SharedArray<MessageEfficiency> _messageEfficiency;
    protected readonly SharedArray<RollingRateLimit> _rateLimits;
    protected readonly SharedArray<CoreGroup> _coreGroups;
    protected readonly SharedArray<Duration> _latencies;   // per CoreGroup's link; below 0: disconnected

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
        RateLimitsDirectoryPath = Path.Combine(DirectoryPath, "RateLimits");
        Directory.CreateDirectory(RateLimitsDirectoryPath);
        CoreGroupsDirectoryPath = Path.Combine(DirectoryPath, "CoreGroups");
        Directory.CreateDirectory(CoreGroupsDirectoryPath);
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

        EnsureConnected();

        ref readonly ServerHeader serverHeader = ref ServerHeader.GetReadonlyRef();

        // Creation order is each array's id in the Mirror; both ends must agree (GetLayoutHash), so they deploy together
        _clientSocketHeaders = NewSharedArray<SocketHeader>(serverName / "ClientHeaders", serverHeader.ClientIds.Length, ServerAccess);

        _instrumentHeaders = NewSharedArray<InstrumentHeader256>(serverName / "InstrumentHeaders", serverHeader.InstrumentsCapacity, ServerAccess);
        _instrumentHeaderIdByInstrumentId = NewSharedArray<int>(serverName / "InstrumentHeaderIdByInstrumentId", serverHeader.InstrumentIds.Length, ServerAccess);

        _instrumentIdsByClientId = NewSharedArray<Bitset64>(serverName / "InstrumentIdsByClientId", serverHeader.ClientIds.Length, ServerAccess);
        _clientIdsByInstrumentId = NewSharedArray<Bitset64>(serverName / "ClientIdsByInstrumentId", serverHeader.InstrumentIds.Length, ServerAccess);
        // The server's book, which the server writes and every other process reads; a strategy's own book is a field of its Instrument
        _marketsByPrice = NewSharedArray<MarketByPrice64>(serverName / "MarketsByPrice", serverHeader.InstrumentIds.Length, ServerAccess);

        // Why every context maps the server's: its AlgoStatus is the master switch each algo checks
        _serverPositionHeaders = NewSharedArray<PositionHeader>(serverName / "ServerPositionHeaders", serverHeader.InstrumentIds.Length, ServerAccess);
        _localPositionHeaders = NewSharedArray<PositionHeader>(serverName / "LocalPositionHeaders", serverHeader.LocalPositionsCapacity, ServerAccess, false);
        _serverRiskLimits = NewSharedArray<RiskLimit>(serverName / "ServerRiskLimits", serverHeader.InstrumentIds.Length, ServerAccess);
        // The server loads and edits every row, a strategy's RiskLayer reads its own
        _localRiskLimits = NewSharedArray<RiskLimit>(serverName / "LocalRiskLimits", serverHeader.LocalPositionsCapacity, ServerAccess, false);
        _serverWorkingRisks = NewSharedArray<WorkingRisk>(serverName / "ServerWorkingRisks", serverHeader.InstrumentIds.Length, ServerAccess);
        // Under the server's name so the Mirror copies them to a remote desk. Why writable on both sides: the server seeds each row at allocation, the strategy moves it
        _localWorkingRisks = NewSharedArray<WorkingRisk>(serverName / "LocalWorkingRisks", serverHeader.LocalPositionsCapacity, ServerAccess == Access.Write ? Access.Write : ClientAccess, false);

        _orderStates = NewSharedArray<OrderState>(serverName / "OrderStates", serverHeader.OrdersCapacity, ServerAccess, false);
        _orderTargets = NewSharedArray<OrderTarget>(serverName / "OrderTargets", serverHeader.OrdersCapacity, ClientAccess, false);

        // Why writable on both sides: the server claims, loads and ends each group's day; strategies count into it
        _messageEfficiency = NewSharedArray<MessageEfficiency>(serverName / "MessageEfficiency", serverHeader.InstrumentIds.Length, ServerAccess == Access.Write ? Access.Write : ClientAccess);
        _rateLimits = NewSharedArray<RollingRateLimit>(serverName / "RateLimits", serverHeader.CoreGroupIds.Length, ServerAccess);
        _coreGroups = NewSharedArray<CoreGroup>(serverName / "CoreGroups", serverHeader.CoreGroupIds.Length, ServerAccess);
        _latencies = NewSharedArray<Duration>(serverName / "Latency", serverHeader.CoreGroupIds.Length, ServerAccess);

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

    // Every array by id, in creation order, write-capable: the mirror reads and writes them (see Spec.md "Remote workspaces").
    public SharedArray GetSharedArray(int arrayId) => _sharedArrays[arrayId];

    // A fingerprint of the array layout (each array's row size and capacity, in creation order): two Mirror ends agree only when theirs match.
    public uint GetLayoutHash()
    {
        uint _layoutHash = 2166136261;   // Why FNV-1a, not HashCode: HashCode is seeded per process, so two machines would never agree
        for (int arrayId = 0; arrayId < SharedArraysCount; arrayId++)
            _layoutHash = (_layoutHash ^ (uint)GetSharedArray(arrayId).TypeSize ^ ((uint)GetSharedArray(arrayId).Capacity << 16)) * 16777619;
        return _layoutHash;
    }

    // Finds a client by its exact name; false when no client has it.
    public bool TryGetClientId(in String256 clientName, out int clientId)
    {
        foreach (int candidateClientId in ServerHeader.GetReadonlyRef().ClientIds)
        {
            if (!_clientSocketHeaders[candidateClientId].IsEmpty() && _clientSocketHeaders[candidateClientId].Read().ClientName == clientName)
            {
                clientId = candidateClientId;
                return true;
            }
        }
        clientId = -1;
        return false;
    }


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
        _serverPositionHeaders.Dispose();
        _serverRiskLimits.Dispose();
        _localRiskLimits.Dispose();
        _serverWorkingRisks.Dispose();
        _localWorkingRisks.Dispose();
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

    public abstract ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId);

    // Any strategy's row on the instrument; read-only on a client.
    public ref SharedArrayEntry<PositionHeader> GetPositionHeader(int clientId, int instrumentId)
    {
        int localPositionIndex = GetLocalPositionIndex(clientId, instrumentId);
        return ref _localPositionHeaders.GetEntry(localPositionIndex);
    }

    // An algo may trade the instrument only while both switches are Live: its strategy's row and the server's, the master switch
    // a server workspace turns (see Spec.md "Algo pause"). Hot path: two plain byte reads, no allocation.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlgoLive(int strategyId, int instrumentId) =>
        GetPositionHeader(strategyId, instrumentId).GetReadonlyRef().AlgoStatus == AlgoStatus.Live
        && _serverPositionHeaders.GetEntry(instrumentId).GetReadonlyRef().AlgoStatus == AlgoStatus.Live;

    // True when the transfer may go ahead against the rows as they are now: the server enforces it and a server workspace's dialog
    // checks it before sending, so the two never disagree (see Spec.md "Position transfer"). Cold path.
    public bool CanTransferPosition(in ControlTransferPosition controlTransferPosition)
    {
        int instrumentId = controlTransferPosition.InstrumentId;
        int fromStrategyId = controlTransferPosition.FromStrategyId;
        int toStrategyId = controlTransferPosition.ToStrategyId;

        // Step 1: two books, both trading the instrument
        if (fromStrategyId == toStrategyId)
            return false;
        bool isEachBookAllocated = GetInstrumentIdsByClientId(fromStrategyId).GetReadonlyRef()[instrumentId] && GetInstrumentIdsByClientId(toStrategyId).GetReadonlyRef()[instrumentId];
        if (!isEachBookAllocated)
            return false;

        // Step 2: part of the from book's position, on its side
        int fromQuantity = GetPositionHeader(fromStrategyId, instrumentId).GetReadonlyRef().Quantity;
        int quantity = controlTransferPosition.Quantity;
        bool isPartOfPosition = quantity != 0 && Math.Sign(quantity) == Math.Sign(fromQuantity) && Math.Abs(quantity) <= Math.Abs(fromQuantity);
        if (!isPartOfPosition)
            return false;

        // Step 3: a price to book it at
        if (!double.IsFinite(controlTransferPosition.Price))
            return false;

        // Step 4: no algo may trade either position meanwhile; the ServerStrategy has none
        bool isEitherAlgoLive = (fromStrategyId != OrderIdAllocator.ServerStrategyId && IsAlgoLive(fromStrategyId, instrumentId))
            || (toStrategyId != OrderIdAllocator.ServerStrategyId && IsAlgoLive(toStrategyId, instrumentId));
        return !isEitherAlgoLive;
    }

    public abstract bool TryGetInstrumentId(int instrumentHeaderId, out int instrumentId);
    public abstract Bitset64 InstrumentIds { get; }



    // The strategy whose rows are this context's own: a client's id, or strategy 0 (the ServerStrategy) on the server.
    public int StrategyId => (this as ClientContext)?.ClientId ?? OrderIdAllocator.ServerStrategyId;

    // This context's own limit, the one its RiskLayer checks: the firm's on the server, the strategy's on a client (see Spec.md "Two levels of risk limit").
    public abstract ref SharedArrayEntry<RiskLimit> GetRiskLimit(int instrumentId);

    // Any strategy's limit on the instrument: the server loads and edits them; read-only on a client.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<RiskLimit> GetRiskLimit(int clientId, int instrumentId) => ref _localRiskLimits.GetEntry(GetLocalPositionIndex(clientId, instrumentId));

    // This context's own exposure, the one its RiskLayer moves and checks: the firm's on the server, the strategy's on a client.
    public abstract ref SharedArrayEntry<WorkingRisk> GetWorkingRisk(int instrumentId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<MessageEfficiency> GetMessageEfficiency(int productGroupId)
    {
        //ThrowIfInstrumentIdOutOfRange(productGroupId);
        return ref _messageEfficiency.GetEntry(productGroupId);
    }

    // One per CoreGroup, server-written. RollingRateLimit today; another model would be a different 64-byte view of the same row, cast by the caller (see Spec.md).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<RollingRateLimit> GetRateLimit(int coreGroupId)
    {
        return ref _rateLimits.GetEntry(coreGroupId);
    }

    // One per CoreGroup, server-written at AllocateCoreGroup: the name and the cores its threads pin to.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<CoreGroup> GetCoreGroup(int coreGroupId)
    {
        return ref _coreGroups.GetEntry(coreGroupId);
    }

    // One per CoreGroup: its link's latency, below 0 while disconnected. On a remote copy, index 0 is the
    // Mirror link to the CME box, written by the Remote Mirror (see Spec.md "Remote workspaces").
    public ref SharedArrayEntry<Duration> GetLatency(int coreGroupId) => ref _latencies.GetEntry(coreGroupId);

    // Throws when no loaded CoreGroup has that name: a client asking for a group the server does not have is a setup error.
    public int GetCoreGroupId(String16 coreGroupName)
    {
        foreach (int coreGroupId in ServerHeader.GetReadonlyRef().CoreGroupIds)
        {
            ref SharedArrayEntry<CoreGroup> coreGroupEntry = ref _coreGroups.GetEntry(coreGroupId);
            if (!coreGroupEntry.IsEmpty() && coreGroupEntry.GetReadonlyRef().CoreGroupName == coreGroupName)
                return coreGroupId;
        }
        throw new InvalidOperationException($"{GetType()}.GetCoreGroupId({coreGroupName}), no CoreGroup with that name in {CoreGroupsDirectoryPath}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<MarketByPrice64> GetMarketByPrice64(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _marketsByPrice.GetEntry(instrumentId);
    }

    // The newest NIC time among this context's books: every instrument's for the server's context, the strategy's own for a
    // client's. Cold path (a Workspace's clock, each refresh).
    public Timestamp GetLatestMarketDataTimestamp()
    {
        Timestamp _latestNicTimestamp = Timestamp.MinValue;
        foreach (int instrumentId in InstrumentIds)
        {
            // Why the entry, not GetMarketByPrice64: an instrument freed since InstrumentIds was read would throw; its old time does no harm
            Timestamp nicTimestamp = _marketsByPrice.GetEntry(instrumentId).GetReadonlyRef().NicTimestamp;
            _latestNicTimestamp = Timestamp.Max(nicTimestamp, _latestNicTimestamp);
        }
        return _latestNicTimestamp;
    }

    // The instrument's book, or false while its row is empty: a remote desk's copy is wiped while a restarted Remote Mirror or
    // server refills it, and a GUI then has nothing to draw yet. Cold path: copies the whole book.
    public bool TryReadMarketByPrice64(int instrumentId, out MarketByPrice64 marketByPrice64)
    {
        ref SharedArrayEntry<MarketByPrice64> marketByPriceEntry = ref GetMarketByPrice64(instrumentId);
        bool isWritten = !marketByPriceEntry.IsEmpty();
        marketByPrice64 = isWritten ? marketByPriceEntry.Read() : default;
        return isWritten;
    }

    // The instrument's quote as this context sees it: in a strategy's own context (open for writing, the only one fed) its own
    // book; in any other (a GUI, the server) the server's book, read in place between two reads of the row's sequence (no copy).
    // False while there is no quote, or the server's row is unwritten, mid-write or changes under the read.
    // Why no spin: a writer that died mid-write leaves the row odd forever; a caller just asks again on its next refresh.
    public bool TryGetQuote(int instrumentId, out Quote quote)
    {
        if (ClientAccess == Access.Write)
            return GetInstrument(instrumentId).TryGetQuote(out quote);
        ref SharedArrayEntry<MarketByPrice64> marketByPriceEntry = ref GetMarketByPrice64(instrumentId);
        ulong sequenceBefore = marketByPriceEntry.GetSeq();
        quote = default;
        if (sequenceBefore == 0UL || Protocol.IsWriteInProgress(sequenceBefore))
            return false;
        bool isQuotable = GetInstrument(instrumentId).TryGetQuote(in marketByPriceEntry.GetReadonlyRef(), out quote);
        Thread.MemoryBarrier();   // Why: the field reads above must complete before the sequence is read again (as SharedArrayEntry.TryRead)
        return isQuotable && marketByPriceEntry.GetSeq() == sequenceBefore;
    }

    // Common shared memory accessors
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<InstrumentHeader256> GetInstrumentHeader(int instrumentHeaderId)
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

    private bool _lock;
    private void CreateInstrument(int instrumentId)
    {
        // Materialize a spread's legs BEFORE taking the non-reentrant lock: the Spread branch below
        // resolves them via GetInstrument, which must hit the cache, not re-enter CreateInstrument.
        int instrumentHeaderId = GetInstrumentHeaderIdByInstrumentId(instrumentId).Read();
        if (GetInstrumentHeader(instrumentHeaderId).GetReadonlyRef().AsInstrumentHeader().InstrumentType == InstrumentType.Spread)
        {
            // Value copy via GetReadonlyRef: clients open headers read-only, GetRef would throw.
            LeggedHeader leggedHeader = GetInstrumentHeader(instrumentHeaderId).GetReadonlyRef().AsLegged();
            foreach (ref readonly LegHeader legHeader in leggedHeader.Legs)
                GetInstrument(GetInstrumentId(legHeader.InstrumentHeaderId));
        }

        using RAIISpinLock spinLock = new(ref _lock);
        if (_instruments[instrumentId] != null)
        {
            return;
        }
        ref SharedArrayEntry<InstrumentHeader256> header256Entry = ref GetInstrumentHeader(instrumentHeaderId);
        InstrumentHeader instrHeader = header256Entry.GetReadonlyRef().AsInstrumentHeader();

        Instrument? instrument = null;

        if (instrHeader.InstrumentType == InstrumentType.Future)
        {
            instrument = new Future(header256Entry.Cast<FutureHeader>());
        }
        else if (instrHeader.InstrumentType == InstrumentType.Spread)
        {
            ref readonly LeggedHeader leggedHeader = ref header256Entry.GetReadonlyRef().AsLegged();

            // Legs reference sibling headers; long = the positive-weight leg (2-leg spreads only: Client.GetInstrument refuses any other shape).
            Future longLeg = null!;
            Future shortLeg = null!;
            foreach (ref readonly LegHeader legHeader in leggedHeader.Legs)
            {
                Future legFuture = (GetInstrument(GetInstrumentId(legHeader.InstrumentHeaderId)) as Future)!;
                if (legHeader.Weight > 0)
                    longLeg = legFuture;
                else if (legHeader.Weight < 0)
                    shortLeg = legFuture;
            }

            instrument = new Spread(header256Entry.Cast<LeggedHeader>(), longLeg, shortLeg);
        }
        else if (instrHeader.InstrumentType == InstrumentType.Forex)
        {
            instrument = new Forex(header256Entry.Cast<ForexHeader>());
        }
        else
        {
            throw new InvalidOperationException($"{GetType()}.CreateInstrument({instrumentId}), Unknown instrument type: {(int)instrHeader.InstrumentType}");
        }
        _instruments[instrumentId] = instrument;
    }

    protected void CreatePosition(Instrument instrument)
    {
        using RAIISpinLock spinLock = new(ref _lock);
        if (_positions[instrument.InstrumentId] != null)
        {
            return;
        }
        // A ServerContext's positions are the server-wide rows; no client owns their order slots
        // (only Client.Create ever sets one), so the ServerStrategy id is the correct inert owner.
        Position position = new Position(instrument, this, StrategyId);
        _positions[instrument.InstrumentId] = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int GetInstrumentId(int instrumentHeaderId)
    {
        ThrowIfInstrumentHeaderIdOutOfRange(instrumentHeaderId);

        ref readonly InstrumentHeader256 header256 = ref _instrumentHeaders[instrumentHeaderId].GetReadonlyRef();

        return header256.AsInstrumentHeader().InstrumentId;
    }

    // Every client's orders, or only those booked to strategyId (from whichever client sent them); -1 means any.
    // An order the server has not answered yet is listed only for inFlightClientId (the workspace's own GUI; see Spec.md).
    public OrderEnumerable EnumerateOrders(int instrumentId = -1, int strategyId = -1, int inFlightClientId = -1)
        => new OrderEnumerable(_orderStates, _orderTargets, 0, OrderIdAllocator.GetLastGlobalIndex(ServerHeader.GetReadonlyRef().ClientIds.Length - 1), instrumentId, strategyId, inFlightClientId);

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

    public RateLimitEnumerable EnumerateRateLimits()
    {
        return new RateLimitEnumerable(_rateLimits, ServerHeader.GetReadonlyRef().CoreGroupIds);
    }

    public CoreGroupEnumerable EnumerateCoreGroups()
    {
        return new CoreGroupEnumerable(_coreGroups, ServerHeader.GetReadonlyRef().CoreGroupIds);
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

    public static void ThrowIfInvalidClientName(FileSystemPath clientName)
    {
        string validDirectoryPath = GetDirectoryPath("");
        if (!clientName.ToString().StartsWith(validDirectoryPath))
        {
            throw new ArgumentException($"ClientContext.ThrowIfInvalidClientName({clientName}), clientName is invalid, must start with: {validDirectoryPath}");
        }
    }

    public static FileSystemPath DirectoriesPath => DirectoriesRoot / "Strategies" / Clock.Mode.ToString();

    public static FileSystemPath GetDirectoryPath(string clientName)
    {
        clientName = Path.GetFileName(clientName);
        return Path.Combine(DirectoriesPath, clientName);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetClientIdFromMap(string clientName)
    {
        if (TryGetClientId(new String256(clientName), out int clientId))
            return clientId;
        throw new InvalidOperationException($"{GetType()}.GetClientIdFromMap({clientName}), Client not connected to server '{ServerName}'.");
    }

    

    // --- Local Implementations ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId)
    {
        int localPositionIndex = GetLocalPositionIndex(_clientId, instrumentId);
        return ref _localPositionHeaders.GetEntry(localPositionIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<RiskLimit> GetRiskLimit(int instrumentId) => ref GetRiskLimit(_clientId, instrumentId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<WorkingRisk> GetWorkingRisk(int instrumentId) => ref _localWorkingRisks.GetEntry(GetLocalPositionIndex(_clientId, instrumentId));

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

    public override void Dispose()
    {
        _localPositionHeaders.Dispose();
        base.Dispose();
    }
}

/// A header's symbology strings, as GUI grids show them.
public sealed record SymbolStrings(string Symbol, string ShortSymbol);

public sealed class ServerContext : Context
{
    private static readonly SymbolStrings s_unknownSymbolStrings = new("?", "?");
    private SymbolStrings?[]? _symbolStrings;   // per instrument header id, built on first use (a GUI's; the trading server never asks)

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

        // Why a ServerContext: it lives as long as its process; a ClientContext (a Mirror's stand-in) can close while the process trades on (see Spec.md)
        LeggedHeader.GetLegHeader ??= instrumentHeaderId => GetInstrumentHeader(instrumentHeaderId).Read();

        ClientsDirectoryPath = ServerName / "Clients";
        Directory.CreateDirectory(ClientsDirectoryPath);
        InstrumentsDirectoryPath = ServerName / "Instruments";
        Directory.CreateDirectory(InstrumentsDirectoryPath);

        // Write mode: the server names its CoreGroups in files, one static JSON CoreGroup each, and each group's rate
        // limit follows by name from its .ratelimit file, static JSON too; Max in simulation and Min in realtime without one, like .risklimit.
        // The channel and thread for an id were built from ServerHeader.CoreGroupIds, so a file outside that set is fatal.
        if (access == Access.Write)
        {
            ref readonly ServerHeader serverHeader = ref ServerHeader.GetReadonlyRef();
            foreach (string coreGroupPath in Directory.GetFiles(CoreGroupsDirectoryPath, "*.coregroup"))
            {
                CoreGroup coreGroup = Json.Deserialize<CoreGroup>(File.ReadAllText(coreGroupPath));
                int coreGroupId = coreGroup.CoreGroupId;
                if (!serverHeader.CoreGroupIds[coreGroupId])
                    throw new InvalidOperationException($"{GetType()}({serverName}), {coreGroupPath}: CoreGroupId {coreGroupId} is not set in ServerHeader.CoreGroupIds");
                _coreGroups.GetEntry(coreGroupId).Write(in coreGroup);

                string rateLimitPath = GetRateLimitFilePath(DirectoryPath, coreGroup.CoreGroupName.ToString()).ToString();
                RateLimit rateLimit = File.Exists(rateLimitPath) ? Json.Deserialize<RateLimit>(File.ReadAllText(rateLimitPath)) : Clock.Mode == ClockMode.Simulation ? RateLimit.GetMaxLimits(coreGroupId) : RateLimit.GetMinLimits(coreGroupId);
                rateLimit.RateLimitId = coreGroupId;
                _rateLimits.GetEntry(coreGroupId).Write(new RollingRateLimit(rateLimit));
            }
        }
    }

    public static void ThrowIfInvalidServerName(FileSystemPath serverName)
    {
        string validDirectoryPath = GetDirectoryPath("");
        if (!serverName.ToString().StartsWith(validDirectoryPath))
        {
            throw new ArgumentException($"ServerContext.ThrowIfInvalidServerName({serverName}), serverName is invalid, must start with: {validDirectoryPath}");
        }
    }

    public static FileSystemPath DirectoriesPath => DirectoriesRoot / "Servers" / Clock.Mode.ToString();

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

        // Reserve the ServerStrategy before anyone can connect. AllocateClientId hands out
        // ClientIds.LowestClear, so without this the FIRST client to attach takes id 0 and every
        // manual order from a server workspace would be attributed to it. Pre-setting the bit both
        // keeps it out of the allocator and makes the slot addressable — ThrowIfClientIdOutOfRange
        // and the RiskLayer's StrategyIdNotAllocated check each test this bit.
        ServerHeader reserved = serverHeader;
        reserved.ClientIds.Set(OrderIdAllocator.ServerStrategyId);
        reserved.ServerProcessId = Environment.ProcessId;

        // Why never overwrite a dead run's header: the server would start on that run's rows and rings (see Spec.md)
        if (!serverHeaderBox.TryStore(in reserved))
        {
            int previousServerProcessId = serverHeaderBox.GetReadonlyRef().ServerProcessId;
            serverHeaderBox.Dispose();
            throw new InvalidOperationException(ProcessId.IsAlive(previousServerProcessId)
                ? $"ServerContext.Connect: {serverName} is already running as process {previousServerProcessId}; stop it before starting another"
                : $"ServerContext.Connect: {serverName}'s previous server (process {previousServerProcessId}) has exited, but other processes still map its shared memory; stop its clients (Local Mirror, algos, Workspaces) and start again");
        }

        return serverHeaderBox;
    }

    // --- Global Implementations ---
    public override ref SharedArrayEntry<PositionHeader> GetPositionHeader(int instrumentId)
    {
        return ref _serverPositionHeaders.GetEntry(instrumentId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<RiskLimit> GetRiskLimit(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _serverRiskLimits.GetEntry(instrumentId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ref SharedArrayEntry<WorkingRisk> GetWorkingRisk(int instrumentId)
    {
        ThrowIfInstrumentIdOutOfRange(instrumentId);
        return ref _serverWorkingRisks.GetEntry(instrumentId);
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
    public ref readonly SharedArrayEntry<SocketHeader> GetSocketHeader(int clientId)
    {
        ThrowIfClientIdOutOfRange(clientId);
        return ref _clientSocketHeaders[clientId];
    }

    // The client's id: its existing one, the id it asks for (LoadClients only), or the lowest free one; a negative ConnectRefusal when refused.
    public int AllocateClientId(in SocketHeader socketHeader)
    {
        // The server's leaf name is reserved for strategy 0's directory — a client named after the server would share the ServerStrategy's files (see Spec.md).
        if (socketHeader.ClientName == ServerStrategyName.Path)
            return (int)ConnectRefusal.ReservedClientName;

        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();
        ref Bitset64 clientIds = ref ServerHeader.GetRef().ClientIds;

        if (TryGetClientId(socketHeader.ClientName, out int existingClientId))
            return existingClientId;
        int clientId = socketHeader.ClientId < 0 ? clientIds.LowestClear : socketHeader.ClientId;

        if (clientId < 0)
            return (int)ConnectRefusal.NoClientIdAvailable;
        
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

    public void OnInstrumentHeader(in InstrumentHeader256 header256)
    {
        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();

        if (serverHeader.InstrumentsCount >= serverHeader.InstrumentsCapacity)
            throw new ArgumentOutOfRangeException(nameof(serverHeader.InstrumentsCount), $"ServerContext.OnInstrumentHeader: InstrumentsCount({serverHeader.InstrumentsCount}) >= InstrumentsCapacity({serverHeader.InstrumentsCapacity})");
        
        ref InstrumentHeader header = ref header256.AsInstrumentHeader();
        header.InstrumentId = -1;
        header.ProductGroupId = -1;
        header.InstrumentHeaderId = serverHeader.InstrumentsCount;

        ref SharedArrayEntry<InstrumentHeader256> header256Entry = ref _instrumentHeaders[header.InstrumentHeaderId];
        header256Entry.Write(header256);

        serverHeaderEntry.AcquireLock();
        serverHeader.InstrumentsCount++;
        serverHeaderEntry.ReleaseLock();
    }

    public int AllocateInstrument(int instrumentHeaderId)
    {
        ref SharedArrayEntry<ServerHeader> serverHeaderEntry = ref ServerHeader;
        ref ServerHeader serverHeader = ref serverHeaderEntry.GetRef();
        ref InstrumentHeader256 header256 = ref GetInstrumentHeader(instrumentHeaderId).GetRef();
        ref InstrumentHeader header = ref header256.AsInstrumentHeader();

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

        Symbology symbology = header256.Symbology;

        _serverRiskLimits.GetEntry(instrumentId).Write(RiskLimit.FromFile(GetRiskLimitsFilePath(DirectoryPath, symbology.Symbol).ToString(), instrumentId));


        string positionPath = GetPositionFilePath(DirectoryPath, symbology.Symbol).ToString();
        string? positionLine = Tools.Tools.ReadLastLine(positionPath);
        PositionHeader positionHeader = positionLine != null ? Json.Deserialize<PositionHeader>(positionLine) : new PositionHeader();
        // Server-wide row: no owning order; template id carries the ids (clientId 0 stands in for the old -1 sentinel)
        positionHeader.OrderHeader.OrderId = new OrderId { InstrumentId = instrumentId };
        // Why whatever the file says: the master switch is a kill switch, Live at every start until a server workspace pauses it
        positionHeader.AlgoStatus = AlgoStatus.Live;
        _serverPositionHeaders.GetEntry(instrumentId).Write(positionHeader);
        _serverWorkingRisks.GetEntry(instrumentId).Write(new WorkingRisk { Position = positionHeader.Quantity });

        header.ProductGroupId = (sbyte)AllocateProductGroupId(in header);   // Why a byte fits: one row per instrument id, at most 64
        header.InstrumentId = instrumentId;
        serverHeader.InstrumentIds.Set(instrumentId);

        serverHeaderEntry.ReleaseLock();

        return instrumentId;
    }

    // Returns the message-efficiency row of this instrument's product group, claiming the first empty row and loading the group's
    // file into it on the group's first instrument. Admin thread, at allocation only.
    private int AllocateProductGroupId(in InstrumentHeader header)
    {
        // Step 1: an instrument of a group already claimed shares its row
        String4 productGroupRoot = header.ProductGroupRoot;
        if (productGroupRoot == "")
            throw new InvalidOperationException($"ServerContext.AllocateProductGroupId: instrument header {header.InstrumentHeaderId} ({header.Root}) has no ProductGroupRoot, must have one; the feed or simulator that builds the header sets it");
        int productGroupCount = ServerHeader.GetReadonlyRef().InstrumentIds.Length;
        int productGroupId = 0;
        for (; productGroupId < productGroupCount && !_messageEfficiency[productGroupId].IsEmpty(); productGroupId++)
        {
            if (_messageEfficiency[productGroupId].GetReadonlyRef().ProductGroupRoot == productGroupRoot)
                return productGroupId;
        }
        if (productGroupId == productGroupCount)
            throw new InvalidOperationException($"ServerContext.AllocateProductGroupId: {productGroupRoot} needs a row, but all {productGroupCount} are taken; raise InstrumentIds' capacity");

        // Step 2: the group's first instrument claims the next row and loads the group's last day from its file
        string? messageEfficiencyLine = Tools.Tools.ReadLastLine(GetMessageEfficiencyFilePath(DirectoryPath, productGroupRoot.ToString()).ToString());
        MessageEfficiency messageEfficiency = messageEfficiencyLine != null ? Json.Deserialize<MessageEfficiency>(messageEfficiencyLine) : Clock.Mode == ClockMode.Simulation ? MessageEfficiency.GetMaxLimits(productGroupRoot) : MessageEfficiency.GetMinLimits(productGroupRoot);
        if (Clock.Mode == ClockMode.Simulation)
            messageEfficiency.TradeDate = DateTime.MinValue;
        messageEfficiency.ProductGroupRoot = productGroupRoot;
        messageEfficiency.ProductGroupId = productGroupId;
        messageEfficiency.Reset(Session.CME.ConvertToLocal(Clock.Now));
        _messageEfficiency.GetEntry(productGroupId).Write(in messageEfficiency);
        return productGroupId;
    }

    public void AllocateInstrument(int clientId, int instrumentId)
    {
        int instrumentHeaderId = GetInstrumentHeaderIdByInstrumentId(instrumentId).GetReadonlyRef();

        if (InstrumentIds[instrumentId] == false)
		{
			return;
		}

        // Already allocated: the live row is fresher than its file and may be mid-fill on the CoreGroup thread — initialise once.
        if (GetInstrumentIdsByClientId(clientId).GetReadonlyRef()[instrumentId])
            return;

        ref InstrumentHeader256 header256 = ref GetInstrumentHeader(instrumentHeaderId).GetRef();
        Symbology symbology = header256.Symbology;

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
        // Why the server seeds it too: the row is then readable (a remote desk's Risk Limits) before the strategy first runs; the strategy reseeds it at its start
        _localWorkingRisks.GetEntry(GetLocalPositionIndex(clientId, instrumentId)).Write(new WorkingRisk { Position = positionHeader.Quantity });
        GetRiskLimit(clientId, instrumentId).Write(RiskLimit.FromFile(GetRiskLimitsFilePath(clientName, symbology.Symbol).ToString(), instrumentId));

        ref SharedArrayEntry<Bitset64> instrumentIdsEntry = ref GetInstrumentIdsByClientId(clientId);
        ref SharedArrayEntry<Bitset64> clientIdsEntry = ref GetClientIdsByInstrumentId(instrumentId);
        
        clientIdsEntry.AcquireLock();
        instrumentIdsEntry.AcquireLock();
        
        clientIdsEntry.GetRef().Set(clientId);
        instrumentIdsEntry.GetRef().Set(instrumentId);

        instrumentIdsEntry.ReleaseLock();
        clientIdsEntry.ReleaseLock();


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

        // Custom loop: InstrumentHeader256 has no JSON ToString, and Symbology throws on anything
        // but Future/Spread (Forex symbology is not implemented), so print raw fields + guarded symbol.
        Console.WriteLine();
        Console.WriteLine($"--- InstrumentHeaders [instrumentHeaderId] x {_instrumentHeaders.Capacity} ---");
        for (int i = 0; i < _instrumentHeaders.Capacity; i++)
        {
            ref SharedArrayEntry<InstrumentHeader256> entry = ref _instrumentHeaders.GetEntry(i);
            ref readonly InstrumentHeader256 header256 = ref entry.GetReadonlyRef();
            InstrumentHeader header = header256.AsInstrumentHeader();
            string symbol = header.InstrumentType == InstrumentType.Future || header.InstrumentType == InstrumentType.Spread ? header256.Symbology.Symbol : "-";
            Console.WriteLine($"[{i}] seq={entry.GetSeq()} type={header.InstrumentType} symbol={symbol} instrumentId={header.InstrumentId} exchangeInstrumentId={header.ExchangeInstrumentId} coreGroupId={header.CoreGroupId} tradingStatus={header.TradingStatus} tickSize={header.TickSize}");
        }

        PrintSharedArray(_serverRiskLimits, "ServerRiskLimits [instrumentId]");
        PrintSharedArray(_serverWorkingRisks, "ServerWorkingRisks [instrumentId]");
        PrintSharedArray(_serverPositionHeaders, "ServerPositionHeaders [instrumentId]");

        PrintLocalSharedArray(_localPositionHeaders, "LocalPositionHeaders", instrumentsLength);
        PrintLocalSharedArray(_localRiskLimits, "LocalRiskLimits", instrumentsLength);
        PrintLocalSharedArray(_localWorkingRisks, "LocalWorkingRisks", instrumentsLength);

        // Orders grouped per slot: target and state side by side is what slot-level debugging (id mismatches) needs; an
        // order's risk row lives in its process's RiskLayer, not in shared memory

        Console.WriteLine();
        Console.WriteLine($"--- Orders [globalOrderIndex] clientId.localOrderIndex x {_orderTargets.Capacity} ---");
        for (int globalOrderIndex = 0; globalOrderIndex < _orderTargets.Capacity; globalOrderIndex++)
        {
            int clientId = globalOrderIndex >> OrderIdAllocator.OrdersPerClientBitShift;
            int localOrderIndex = globalOrderIndex & (OrderIdAllocator.OrdersPerClient - 1);
            ref SharedArrayEntry<OrderTarget> targetEntry = ref _orderTargets.GetEntry(globalOrderIndex);
            ref SharedArrayEntry<OrderState> stateEntry = ref _orderStates.GetEntry(globalOrderIndex);
            Console.WriteLine($"[{globalOrderIndex}] {clientId}.{localOrderIndex} target seq={targetEntry.GetSeq()} {targetEntry.GetReadonlyRef()}");
            Console.WriteLine($"[{globalOrderIndex}] {clientId}.{localOrderIndex} state  seq={stateEntry.GetSeq()} {stateEntry.GetReadonlyRef()}");
        }

        PrintSharedArray(_marketsByPrice, "MarketsByPrice [instrumentId]");
        PrintSharedArray(_messageEfficiency, "MessageEfficiency [productGroupId]");
        PrintSharedArray(_rateLimits, "RateLimits [coreGroupId]");
        PrintSharedArray(_coreGroups, "CoreGroups [coreGroupId]");
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

    // A [clientId, instrumentId] array, each row labelled clientId.instrumentId.
    private static void PrintLocalSharedArray<T>(SharedArray<T> sharedArray, string title, int instrumentsLength) where T : unmanaged
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} [index] clientId.instrumentId x {sharedArray.Capacity} ---");
        for (int localIndex = 0; localIndex < sharedArray.Capacity; localIndex++)
        {
            ref SharedArrayEntry<T> entry = ref sharedArray.GetEntry(localIndex);
            Console.WriteLine($"[{localIndex}] {localIndex / instrumentsLength}.{localIndex % instrumentsLength} seq={entry.GetSeq()} {entry.GetReadonlyRef()}");
        }
    }

    // The header's symbol strings, computed once. Why cached: building them in a grid binding froze grids of thousands of spreads.
    public SymbolStrings GetSymbolStrings(int instrumentHeaderId)
    {
        SymbolStrings?[] symbolStrings = _symbolStrings ??= new SymbolStrings?[ServerHeader.GetReadonlyRef().InstrumentsCapacity];
        if ((uint)instrumentHeaderId >= (uint)symbolStrings.Length)
            return NewSymbolStrings(instrumentHeaderId);
        return symbolStrings[instrumentHeaderId] ??= NewSymbolStrings(instrumentHeaderId);   // a race computes the same value twice: benign
    }

    // Computes every written header's strings off-thread, so a grid opens against a warm cache. A GUI calls it when it connects.
    public void WarmSymbolStrings() => System.Threading.Tasks.Task.Run(() =>
    {
        int instrumentsCount = ServerHeader.GetReadonlyRef().InstrumentsCount;
        for (int instrumentHeaderId = 0; instrumentHeaderId < instrumentsCount; instrumentHeaderId++)
            _ = GetSymbolStrings(instrumentHeaderId);
    });

    private SymbolStrings NewSymbolStrings(int instrumentHeaderId)
    {
        try
        {
            Symbology symbology = GetInstrumentHeader(instrumentHeaderId).Read().Symbology;
            return new SymbolStrings(symbology.Symbol, symbology.ShortSymbol);
        }
        catch
        {
            return s_unknownSymbolStrings;   // Why: an instrument type with no Symbology (or an unwritten header) must never take down a widget
        }
    }

    public override void Dispose()
    {
        // Why: a test that starts another server in this process takes the leg lookup over, never keeping a pointer into unmapped memory
        if (LeggedHeader.GetLegHeader?.Target == this)
            LeggedHeader.GetLegHeader = null;
        base.Dispose();
    }
}

// --- Enumerables ---

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

public readonly struct CoreGroupEnumerable
{
    private readonly SharedArray<CoreGroup> _coreGroups;
    private readonly Bitset64 _coreGroupIds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CoreGroupEnumerable(SharedArray<CoreGroup> coreGroups, Bitset64 coreGroupIds)
    {
        _coreGroups = coreGroups;
        _coreGroupIds = coreGroupIds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CoreGroupEnumerator GetEnumerator()
    {
        return new CoreGroupEnumerator(_coreGroups, _coreGroupIds);
    }
}

public struct CoreGroupEnumerator
{
    private readonly SharedArray<CoreGroup> _coreGroups;
    private Bitset64 _coreGroupIds;

    public CoreGroup Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CoreGroupEnumerator(SharedArray<CoreGroup> coreGroups, Bitset64 coreGroupIds)
    {
        _coreGroups = coreGroups;
        _coreGroupIds = coreGroupIds;
        Current = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // CoreGroups are not dense: walk the set bits, one row each, skipping any the server has not allocated.
        while (!_coreGroupIds.IsEmpty)
        {
            int coreGroupId = _coreGroupIds.LowestSet;
            _coreGroupIds.Clear(coreGroupId);
            if (_coreGroups[coreGroupId].IsEmpty())
                continue;
            Current = _coreGroups[coreGroupId].Read();
            return true;
        }

        Current = default!;
        return false;
    }
}

public readonly struct RateLimitEnumerable
{
    private readonly SharedArray<RollingRateLimit> _rateLimits;
    private readonly Bitset64 _coreGroupIds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RateLimitEnumerable(SharedArray<RollingRateLimit> rateLimits, Bitset64 coreGroupIds)
    {
        _rateLimits = rateLimits;
        _coreGroupIds = coreGroupIds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RateLimitEnumerator GetEnumerator()
    {
        return new RateLimitEnumerator(_rateLimits, _coreGroupIds);
    }
}

public struct RateLimitEnumerator
{
    private readonly SharedArray<RollingRateLimit> _rateLimits;
    private Bitset64 _coreGroupIds;

    public RollingRateLimit Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal RateLimitEnumerator(SharedArray<RollingRateLimit> rateLimits, Bitset64 coreGroupIds)
    {
        _rateLimits = rateLimits;
        _coreGroupIds = coreGroupIds;
        Current = default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // CoreGroups are not dense: walk the set bits, one row each, skipping any the server has not written.
        while (!_coreGroupIds.IsEmpty)
        {
            int coreGroupId = _coreGroupIds.LowestSet;
            _coreGroupIds.Clear(coreGroupId);
            if (_rateLimits[coreGroupId].IsEmpty())
                continue;
            Current = _rateLimits[coreGroupId].Read();
            return true;
        }

        Current = default!;
        return false;
    }
}

public readonly struct OrderEnumerable
{
    private readonly SharedArray<OrderState> _orderStates;
    private readonly SharedArray<OrderTarget> _orderTargets;

    private readonly int _start;
    private readonly int _end;
    private readonly int _instrumentId;
    private readonly int _strategyId;
    private readonly int _inFlightClientId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderEnumerable(SharedArray<OrderState> orderStates, SharedArray<OrderTarget> orderTargets, int start, int end, int instrumentId, int strategyId, int inFlightClientId)
    {
        _orderStates = orderStates;
        _orderTargets = orderTargets;
        _start = start;
        _end = end;
        _instrumentId = instrumentId;
        _strategyId = strategyId;
        _inFlightClientId = inFlightClientId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderEnumerator GetEnumerator()
    {
        return new OrderEnumerator(_orderStates, _orderTargets, _start, _end, _instrumentId, _strategyId, _inFlightClientId);
    }
}

public struct OrderEnumerator
{
    private readonly SharedArray<OrderState> _orderStates;
    private readonly SharedArray<OrderTarget> _orderTargets;

    private readonly int _lastGlobalOrderIndex;
    private readonly int _instrumentId;
    private readonly int _strategyId;
    private readonly int _inFlightClientId;
    private int _globalOrderIndex;

    public (OrderState State, OrderTarget Target) Current { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderEnumerator(SharedArray<OrderState> orderStates, SharedArray<OrderTarget> orderTargets, int firstGlobalIndex, int lastGlobalIndex, int instrumentId, int strategyId, int inFlightClientId)
    {
        _orderStates = orderStates;
        _orderTargets = orderTargets;
        _globalOrderIndex = firstGlobalIndex - 1;
        _lastGlobalOrderIndex = lastGlobalIndex;
        _instrumentId = instrumentId;
        _strategyId = strategyId;
        _inFlightClientId = inFlightClientId;
        Current = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
    NextGlobalOrderIndex:
        while (++_globalOrderIndex <= _lastGlobalOrderIndex)
        {
            // Why: by the target row: a Create the server has not answered yet has no state row in a fresh slot
            if (_orderTargets[_globalOrderIndex].IsEmpty())
            {
                continue;
            }

            ref SharedArrayEntry<OrderState> stateEntry = ref _orderStates.GetEntry(_globalOrderIndex);
            ref SharedArrayEntry<OrderTarget> targetEntry = ref _orderTargets.GetEntry(_globalOrderIndex);

            ref readonly OrderState state = ref stateEntry.GetReadonlyRef();
            ref readonly OrderTarget target = ref targetEntry.GetReadonlyRef();

            ulong stateSeq0, stateSeq1;
            ulong targetSeq0, targetSeq1;

            do
            {
                stateSeq0 = stateEntry.GetSeq();
                targetSeq0 = targetEntry.GetSeq();

                // Why: skipped this walk, not retried: a live writer finishes in nanoseconds, but a row a dead client left mid-write would spin this forever
                if (Protocol.IsWriteInProgress(stateSeq0) || Protocol.IsWriteInProgress(targetSeq0))
                {
                    goto NextGlobalOrderIndex;
                }

                if (_instrumentId != -1 && target.OrderHeader.OrderId.InstrumentId != _instrumentId
                    || _strategyId != -1 && target.OrderHeader.OrderId.StrategyId != _strategyId
                    || target.OrderHeader.OrderId == 0)
                {
                    goto NextGlobalOrderIndex;
                }

                // Why: until the server's row is about this order, the client's row says whether one is in flight
                bool isSameOrder = state.OrderHeader.OrderId == target.OrderHeader.OrderId;
                // Why: another client's order is listed only once the server has it: a cancel from here could otherwise reach the
                // server ahead of that client's Create and be dropped; this GUI's own orders follow its channel, so they show from the click
                bool isDone = isSameOrder ? state.OrderStateStatus == OrderStateStatus.Done
                    : target.OrderTargetStatus != OrderStateStatus.Active || target.OrderHeader.OrderId.ClientId != _inFlightClientId;
                if (isDone)
                {
                    stateSeq1 = stateEntry.GetSeq();
                    targetSeq1 = targetEntry.GetSeq();

                    if (stateSeq0 != stateSeq1 || targetSeq0 != targetSeq1)
                    {
                        continue;
                    }

                    goto NextGlobalOrderIndex;
                }

                if (isSameOrder)
                {
                    Current = (state, target);
                }
                else
                {
                    OrderState.NewInFlight(in target, out OrderState inFlightState);
                    Current = (inFlightState, target);
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

    public InstrumentHeader256 Current { get; private set; }

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
    public IManualClient Manual { get; }

    public WorkspaceContext(Context primary, IManualClient manual)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary), $"WorkspaceContext.WorkspaceContext(), primary context cannot be null.");
        Manual = manual ?? throw new ArgumentNullException(nameof(manual), $"WorkspaceContext.WorkspaceContext(), manual client cannot be null.");
    }

    // A server workspace lists every client's orders; a strategy workspace every order booked to its strategy, whichever
    // client sent it: the algo, this GUI, a sibling GUI or a remote desk's (see Spec.md "Workspaces (GUIs)").
    public OrderEnumerable EnumerateOrders(int instrumentId = -1)
        => Primary.EnumerateOrders(instrumentId, (Primary as ClientContext)?.ClientId ?? -1, Manual.Context.ClientId);

    // Allocates the instrument to this workspace's manual client unless it already holds it. Why: a control travels on the
    // instrument's execution channel, which the server polls only for clients allocated in that CoreGroup (queued ahead of the
    // control on the manual client's owner thread).
    public void AllocateToManualClient(int instrumentId)
    {
        if (!Manual.Context.InstrumentIds[instrumentId])
            Manual.OnAllocateInstrument(Primary.GetInstrument(instrumentId).Header.InstrumentHeaderId);
    }
}
//END_FILE HFT/Provider/Context.cs
