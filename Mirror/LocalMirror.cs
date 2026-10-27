using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Execution;
using Provider;
using Socket;
using Tools;

namespace Mirror;

/// The Local end of the mirror: beside the real server, or on a relay over its copy. Every 100 ms, Publish sends each connected
/// remote the shared-memory rows whose bytes changed since the last Publish (every row to a remote that has just connected), then
/// the packets queued for it, then a heartbeat; rows go first, so no reply reaches a GUI before the rows it depends on. Each GUI a
/// remote lists gets a stand-in from newStandIn: a ManualClient beside the server, a RemoteClient on a relay. Cold path: one thread
/// (the Mirror's main thread on housekeeping cores beside the server; the relay thread on a desk); it only reads the rows (see Spec.md "Remote workspaces").
public sealed class LocalMirror
{
    private static readonly Duration s_publishInterval = Duration.FromMilliseconds(100);   // Why one cadence: each pass reads every row (330 ns per hot line the server wrote since), so passes must not scale with desks (see Spec.md)
    private static readonly Duration s_failedStartRetryDelay = Duration.FromSeconds(3);   // Why: a refused GUI is listed every second; each NotReady start re-sends the cancels

    private readonly ServerContext _serverContext;
    private readonly TcpLinkConnector _tcpLinkConnector;
    private readonly Func<string, string, IManualClient> _newStandIn;   // (clientName, principalName): the stand-in for a listed GUI, or throws why not
    private readonly List<MirrorSession> _mirrorSessions = new();
    private readonly byte[]?[] _publishedRowsByArrayId;          // per array, the row bytes last published
    private readonly byte[] _rowPacket = new byte[64 * 1024];   // one row packet at a time; the link copies it
    private readonly GuiStandIn?[] _guiStandIns;                 // by client id: the stand-in serving each attached GUI
    private readonly Dictionary<string, (Timestamp Timestamp, ConnectRefusal ConnectRefusal)> _failedStartByClientName = new();   // the last refusal per GUI name
    private Timestamp _publishTimestamp;                         // when the last Publish ran (Timestamp.StopwatchNow)

    // One connected remote: its link, whether it has had every row yet, the packets waiting for the next Publish, and its folder sync.
    private sealed class MirrorSession(TcpLink tcpLink)
    {
        public TcpLink TcpLink { get; } = tcpLink;
        public bool IsSynced { get; set; }
        public List<byte[]> QueuedPackets { get; } = new();
        public FileSyncLocal FileSyncLocal { get; } = new(Context.DirectoriesRoot);
    }

    // The stand-in serving one attached GUI, for as long as its remote lists that GUI process.
    private sealed class GuiStandIn(IManualClient manualClient, string clientName, MirrorSession mirrorSession, int guiProcessId)
    {
        public IManualClient ManualClient { get; } = manualClient;
        public string ClientName { get; } = clientName;
        public MirrorSession MirrorSession { get; } = mirrorSession;
        public int GuiProcessId { get; } = guiProcessId;
    }

    public LocalMirror(ServerContext serverContext, TcpLinkConnector tcpLinkConnector, Func<string, string, IManualClient> newStandIn)
    {
        _serverContext = serverContext;
        _tcpLinkConnector = tcpLinkConnector;
        _newStandIn = newStandIn;
        _guiStandIns = new GuiStandIn?[serverContext.ServerHeader.GetReadonlyRef().ClientIds.Length];
        _publishedRowsByArrayId = new byte[]?[serverContext.SharedArraysCount];
    }

    /// Publish every 100 ms while a remote is connected: rows, then the packets that must follow them (AllocatedClientId), then the heartbeat.
    public bool IsPublishDue => _mirrorSessions.Count > 0 && Timestamp.StopwatchNow - _publishTimestamp >= s_publishInterval;

    /// Takes new remotes, drops dead ones, applies each remote's packets (each command sent at once), and reads every ManualClient's replies.
    public void Read()
    {
        // Step 1: a new remote gets a session; its first Publish sends it every row
        while (_tcpLinkConnector.TryTakeNewLink(out TcpLink? tcpLink))
            _mirrorSessions.Add(new MirrorSession(tcpLink));

        // Step 2: a remote that dropped: its GUIs' ManualClients close, and the server cancels what they left working
        for (int mirrorSessionIndex = _mirrorSessions.Count - 1; mirrorSessionIndex >= 0; mirrorSessionIndex--)
        {
            MirrorSession mirrorSession = _mirrorSessions[mirrorSessionIndex];
            if (mirrorSession.TcpLink.IsConnected)
                continue;
            DetachGuis(mirrorSession, stillListedGuis: null);
            mirrorSession.TcpLink.Dispose();
            _mirrorSessions.RemoveAt(mirrorSessionIndex);
        }

        // Step 3: each remote's packets, then its folders' news (once it has its snapshot, so the server header still opens the link)
        foreach (MirrorSession mirrorSession in _mirrorSessions)
        {
            ReadPackets(mirrorSession);
            if (mirrorSession.IsSynced)
                mirrorSession.FileSyncLocal.Announce(mirrorSession.TcpLink);
        }

        // Step 4: each stand-in reads what the server sent back (what it sends went out in ApplyMessage)
        foreach (GuiStandIn? guiStandIn in _guiStandIns)
            guiStandIn?.ManualClient.ReadSocket();
    }

    /// Ends every remote's session: its stand-ins close (the server cancels what they left working) and its link is dropped, so it
    /// redials and takes a fresh snapshot. A relay does this when its server restarts, as the Local Mirror beside a server restarts with it.
    public void Dispose()
    {
        foreach (MirrorSession mirrorSession in _mirrorSessions)
        {
            DetachGuis(mirrorSession, stillListedGuis: null);
            mirrorSession.TcpLink.Dispose();
        }
        _mirrorSessions.Clear();
    }

    /// Sends each remote the server header, the rows whose bytes changed (every row to a new remote), its queued packets, then a heartbeat.
    public void Publish()
    {
        // Step 1: the server header opens every pass: a new remote sizes its copy from it, and its Timestamp moves every pass
        _publishTimestamp = Timestamp.StopwatchNow;
        _serverContext.ServerHeader.TryRead(out ServerHeader serverHeader);
        serverHeader.ServerName = MirrorPacket.RewriteName(serverHeader.ServerName, isTravelling: true);
        MirrorServerHeader mirrorServerHeader = new() { ServerHeader = serverHeader, LayoutHash = _serverContext.GetLayoutHash() };
        ReadOnlySpan<byte> serverHeaderPacket = MemoryMarshal.AsBytes(new ReadOnlySpan<MirrorServerHeader>(in mirrorServerHeader));
        foreach (MirrorSession mirrorSession in _mirrorSessions)
            mirrorSession.TcpLink.Write(serverHeaderPacket);

        // Step 2: rows
        bool hasUnsyncedSession = _mirrorSessions.Exists(mirrorSession => !mirrorSession.IsSynced);
        for (int arrayId = 0; arrayId < _serverContext.SharedArraysCount; arrayId++)
            PublishRows(arrayId, hasUnsyncedSession);

        // Step 3: queued packets after the rows they depend on, then the heartbeat that closes the pass
        MirrorHeartbeat mirrorHeartbeat = new();
        ReadOnlySpan<byte> heartbeatPacket = MemoryMarshal.AsBytes(new ReadOnlySpan<MirrorHeartbeat>(in mirrorHeartbeat));
        foreach (MirrorSession mirrorSession in _mirrorSessions)
        {
            foreach (byte[] queuedPacket in mirrorSession.QueuedPackets)
                mirrorSession.TcpLink.Write(queuedPacket);
            mirrorSession.QueuedPackets.Clear();
            mirrorSession.TcpLink.Write(heartbeatPacket);
            mirrorSession.IsSynced = true;
        }
    }

    // One array's rows: each whose bytes differ from the last published goes to every remote, and every row to an unsynced one.
    private void PublishRows(int arrayId, bool hasUnsyncedSession)
    {
        SharedArray sharedArray = _serverContext.GetSharedArray(arrayId);
        // Why: each end's Latency row is its own link's (a Remote writes it every pass); a relay's must not overwrite a phone's
        if (sharedArray is SharedArray<Duration>)
            return;
        byte[] publishedRows = _publishedRowsByArrayId[arrayId] ??= new byte[sharedArray.Capacity * sharedArray.TypeSize];
        int rowHeaderLength = Unsafe.SizeOf<MirrorRowHeader>();
        for (int index = 0; index < sharedArray.Capacity; index++)
        {
            // Step 1: the row now, copied straight into the packet; one unwritten or being written right now is skipped (caught next
            // pass). Why not TryRead: it spins while a write is in progress, forever on a row a client that died mid-write left
            Span<byte> row = _rowPacket.AsSpan(rowHeaderLength, sharedArray.TypeSize);
            Span<byte> publishedRow = publishedRows.AsSpan(index * sharedArray.TypeSize, sharedArray.TypeSize);
            if (!sharedArray.TryCopy(index, row))
            {
                // Why: forget what was published: a remote taking its snapshot is marked synced after this pass and then gets only
                // changed rows, so a row skipped now must count as changed next pass, or that remote never gets it
                if (hasUnsyncedSession)
                    publishedRow.Clear();
                continue;
            }

            // Step 2: against what was last published. Why: bytes, not the seq: the server writes some fields in place
            // (Timestamp, TradingStatus, QuantityAhead, RateLimits) without moving it
            bool isChanged = !row.SequenceEqual(publishedRow);
            if (!isChanged && !hasUnsyncedSession)
                continue;
            row.CopyTo(publishedRow);

            // Step 3: names travel relative to the root, then to every remote that needs the row
            MirrorPacket.RewriteNames(sharedArray, _rowPacket.AsSpan(rowHeaderLength, row.Length), isTravelling: true);
            MemoryMarshal.Write(_rowPacket, new MirrorRowHeader() { ArrayId = arrayId, Index = index });
            ReadOnlySpan<byte> rowPacket = _rowPacket.AsSpan(0, rowHeaderLength + row.Length);
            foreach (MirrorSession mirrorSession in _mirrorSessions)
            {
                if (isChanged || !mirrorSession.IsSynced)
                    mirrorSession.TcpLink.Write(rowPacket);
            }
        }
    }

    // One remote's packets: the GUIs it hosts, their commands, and its pings.
    private void ReadPackets(MirrorSession mirrorSession)
    {
        while (mirrorSession.TcpLink.TryRead(out Span<byte> packet))
        {
            switch ((MirrorPacketType)packet[0])
            {
                case MirrorPacketType.GuiList:
                    AttachGuis(mirrorSession, MirrorPacket.ReadMirrorGuis<MirrorGuiList>(packet), MemoryMarshal.Read<MirrorGuiList>(packet).IsFolderSyncOn);
                    break;
                case MirrorPacketType.Message:
                {
                    MirrorMessageHeader mirrorMessageHeader = MemoryMarshal.Read<MirrorMessageHeader>(packet);
                    ApplyMessage(mirrorSession, in mirrorMessageHeader, packet[Unsafe.SizeOf<MirrorMessageHeader>()..]);
                    break;
                }
                case MirrorPacketType.Ping:
                    // Why straight back: the round trip is then the loop and the link; the heartbeat that closes every publish says whether the screen is still fed
                    MemoryMarshal.AsRef<MirrorPing>(packet).Header = new(MirrorPacketType.Pong);
                    mirrorSession.TcpLink.Write(packet);
                    break;
                case MirrorPacketType.FileRequest:
                    mirrorSession.FileSyncLocal.OnRequest(packet, mirrorSession.TcpLink);
                    break;
            }
        }
    }

    // One call from a GUI, made on its stand-in; ignored unless that GUI is attached through this remote.
    private void ApplyMessage(MirrorSession mirrorSession, in MirrorMessageHeader mirrorMessageHeader, ReadOnlySpan<byte> message)
    {
        // Step 1: only a GUI this remote attached
        int clientId = mirrorMessageHeader.ClientId;
        if ((uint)clientId >= (uint)_guiStandIns.Length || _guiStandIns[clientId] is not GuiStandIn guiStandIn
            || guiStandIn.MirrorSession != mirrorSession || message.IsEmpty)
            return;

        // Step 2: the same call on its stand-in
        IManualClient manualClient = guiStandIn.ManualClient;
        switch (message[0])
        {
            case (byte)AllocateType.Instrument:
                manualClient.OnAllocateInstrument(MemoryMarshal.AsRef<AllocateInstrument>(message).InstrumentHeaderId);
                break;
            case (byte)OrderType.OrderTarget:
            {
                OrderTarget orderTarget = MemoryMarshal.Read<OrderTarget>(message);
                manualClient.OnOrderTarget(ref orderTarget);
                break;
            }
            case (byte)ControlType.AlgoStatus:
                manualClient.OnControlAlgoStatus(in MemoryMarshal.AsRef<ControlAlgoStatus>(message));
                break;
            case (byte)ControlType.RiskLimit:
                manualClient.OnControlRiskLimit(in MemoryMarshal.AsRef<ControlRiskLimit>(message));
                break;
        }

        // Step 3: sent at once: a GUI detached later in this batch keeps its last calls
        manualClient.WriteSocket();
    }

    // Matches the GUIs a remote lists: one no longer listed (or listed with a new process) is closed, each new one is started, and
    // their folders are synced to the remote when it asks (isFolderSyncOn).
    private void AttachGuis(MirrorSession mirrorSession, ReadOnlySpan<MirrorGui> mirrorGuis, bool isFolderSyncOn)
    {
        // Step 1: names under this end's root
        HashSet<(string ClientName, int GuiProcessId)> _listedGuis = new();
        foreach (MirrorGui mirrorGui in mirrorGuis)
            _listedGuis.Add((MirrorPacket.RewriteName(mirrorGui.ClientName, isTravelling: false).ToString(), mirrorGui.GuiProcessId));

        // Step 2: a GUI no longer listed has closed
        DetachGuis(mirrorSession, _listedGuis);

        // Step 3: each listed GUI with no stand-in yet gets one, or is told why not
        foreach ((string clientName, int guiProcessId) in _listedGuis)
        {
            if (!Array.Exists(_guiStandIns, candidate => candidate?.MirrorSession == mirrorSession && candidate.GuiProcessId == guiProcessId && candidate.ClientName == clientName))
                StartGuiStandIn(mirrorSession, clientName, guiProcessId);
        }

        // Step 4: the listed GUIs' folders, when the remote syncs them
        mirrorSession.FileSyncLocal.Serve(_listedGuis.Select(listedGui => listedGui.ClientName), isFolderSyncOn);
    }

    // Closes the stand-in of each GUI this remote drives that is not in stillListedGuis (every one, when null): the server
    // cancels what it left working, as for a local Workspace that closes.
    private void DetachGuis(MirrorSession mirrorSession, HashSet<(string ClientName, int GuiProcessId)>? stillListedGuis)
    {
        for (int clientId = 0; clientId < _guiStandIns.Length; clientId++)
        {
            GuiStandIn? guiStandIn = _guiStandIns[clientId];
            if (guiStandIn != null && guiStandIn.MirrorSession == mirrorSession
                && (stillListedGuis == null || !stillListedGuis.Contains((guiStandIn.ClientName, guiStandIn.GuiProcessId))))
            {
                guiStandIn.ManualClient.Dispose();
                _guiStandIns[clientId] = null;
            }
        }
    }

    // Starts the stand-in for a newly listed GUI and replies with its id, or with the reason for refusing it (open elsewhere, a
    // previous holder's orders still working); a refused name is tried again at most every 3 s.
    private void StartGuiStandIn(MirrorSession mirrorSession, string clientName, int guiProcessId)
    {
        // Step 1: within 3 s of a refused start the answer is the same: give it at once, so the GUI moves on to its next name
        if (_failedStartByClientName.TryGetValue(clientName, out (Timestamp Timestamp, ConnectRefusal ConnectRefusal) failedStart)
            && Timestamp.StopwatchNow - failedStart.Timestamp < s_failedStartRetryDelay)
        {
            SendAllocatedClientId(mirrorSession, clientName, guiProcessId, (int)failedStart.ConnectRefusal);
            return;
        }

        // Step 2: the stand-in under exactly the GUI's name: the GUI picked the number; this side never picks one
        IManualClient manualClient;
        try
        {
            if (!ManualClient.TryParseRemoteGuiName(clientName, out string principalName, out _))
                throw new InvalidDataException($"LocalMirror.StartGuiStandIn: {clientName} is not a GUI name");
            manualClient = _newStandIn(clientName, principalName);
        }
        catch (Exception exception)
        {
            ConnectRefusal connectRefusal = exception is ConnectRefusedException connectRefusedException ? connectRefusedException.ConnectRefusal : ConnectRefusal.ServerError;
            _failedStartByClientName[clientName] = (Timestamp.StopwatchNow, connectRefusal);
            Console.WriteLine($"LocalMirror: {clientName} refused ({exception.Message})");
            SendAllocatedClientId(mirrorSession, clientName, guiProcessId, (int)connectRefusal);
            return;
        }

        // Step 3: serve the GUI: its id goes back after the rows that hold this client (Publish order)
        int clientId = manualClient.ClientId();
        _guiStandIns[clientId] = new GuiStandIn(manualClient, clientName, mirrorSession, guiProcessId);
        _failedStartByClientName.Remove(clientName);
        SendAllocatedClientId(mirrorSession, clientName, guiProcessId, clientId);
    }

    // Queues an AllocatedClientId: the GUI's id, or a negative ConnectRefusal.
    private static void SendAllocatedClientId(MirrorSession mirrorSession, string clientName, int guiProcessId, int clientId)
    {
        MirrorGui mirrorGui = new() { ClientId = clientId, GuiProcessId = guiProcessId, ClientName = MirrorPacket.RewriteName(new String256(clientName), isTravelling: true) };
        mirrorSession.QueuedPackets.Add(MirrorPacket.NewGuiPacket(new Header<MirrorPacketType>(MirrorPacketType.AllocatedClientId), new ReadOnlySpan<MirrorGui>(in mirrorGui)));
    }
}
