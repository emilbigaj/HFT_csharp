using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using System.Threading;
using Provider;
using Socket;
using Tools;

namespace Mirror;

/// The Remote end of the mirror, where people sit. Keeps a read-only copy of the Local end's server under the same server
/// name, and runs a ServerSocket under that name, where only RemoteClients attach. Each GUI's RemoteClient gets the client id
/// of the ManualClient serving it on the Local end, and its calls are passed to that ManualClient; this end pings the Local
/// end every 100 ms and writes the link's latency into the copy (Latency[0]), where every Workspace here reads it. One object
/// serves one server process: a restarted server gets a fresh RemoteMirror over the same copy and link (RunRemote). Cold path:
/// the Mirror's main thread, plus the ServerSocket's listen thread, which runs AllocateClientId.
public sealed class RemoteMirror : IDisposable
{
    private const int s_waitingClientId = int.MinValue;                                     // a GUI in AllocateClientId with no AllocatedClientId yet
    private static readonly Duration s_guiListInterval = Duration.FromSeconds(1);
    private static readonly Duration s_allocateClientIdTimeout = Duration.FromSeconds(10);   // Why: an AllocatedClientId comes within a pass; only a down link takes this long
    private static readonly Duration s_pingInterval = Duration.FromMilliseconds(100);

    private readonly ServerContext _serverContext;
    private readonly ServerSocket _serverSocket;
    private readonly LetterBox<int> _remoteMirrorProcessIdBox;   // this process's id, posted once it listens: Workspaces of an earlier Remote rebuild on it
    private readonly TcpLinkConnector _tcpLinkConnector;
    private readonly FileSyncRemote? _fileSyncRemote;   // null: this desk does not sync folders (MirrorConfig.IsFolderSyncOn)
    private TcpLink _tcpLink;
    private readonly uint _layoutHash;                      // this desk's array layout; every server header must carry the same
    private readonly int _serverProcessId;                  // the server process this object serves; another in a header is a restarted server
    private ServerHeader? _pendingServerHeader;            // a new server's header, written when its snapshot completes (the first heartbeat); null after
    private readonly MirrorGui[] _mirrorGuisByClientId;    // the GUIs attached here, with local names; the listen thread writes each before it opens
    private readonly ConcurrentDictionary<(string ClientName, int GuiProcessId), int> _pendingClientIds = new();   // GUIs in AllocateClientId: their AllocatedClientId
    private bool _isLinkChecked = true;                     // the link's first server header was checked against this server; until then nothing goes up (main thread only)
    private Timestamp _guiListTimestamp;                    // when the last GuiList went out (Timestamp.StopwatchNow: a wall-clock step must not stop it)
    private Timestamp _pingTimestamp;                       // when the last ping went out (Timestamp.StopwatchNow)
    private readonly Duration[] _roundTrips = new Duration[10];   // the last 10 pongs' round trips, round-robin by _pongCount
    private uint _pongCount;
    private Timestamp _pongedPingTimestamp = Timestamp.StopwatchNow;   // the newest answered ping (the start, before the first pong)
    private volatile bool _isGuiListDue;
    private volatile bool _isDisposed;                      // read by the listen thread's AllocateClientId wait
    private bool _isListening;

    /// A restarted server's header, once one arrives; this object then stops reading and RunRemote replaces it.
    public ServerHeader? NewServerHeader { get; private set; }

    /// The link this object reads, handed to its replacement.
    public TcpLink TcpLink => _tcpLink;

    /// Serves the server in the copy's header, or, given a restarted server's header, wipes the copy for it first. The folder sync is
    /// the desk's, kept across replacements: its copies are this desk's files, whichever server wrote them.
    public RemoteMirror(ServerContext serverContext, TcpLinkConnector tcpLinkConnector, TcpLink tcpLink, ServerHeader? newServerHeader, FileSyncRemote? fileSyncRemote)
    {
        // Step 1: the server, the link, the folder sync and this desk's layout
        _serverContext = serverContext;
        _tcpLinkConnector = tcpLinkConnector;
        _fileSyncRemote = fileSyncRemote;
        _tcpLink = tcpLink;
        _layoutHash = serverContext.GetLayoutHash();
        _serverProcessId = newServerHeader?.ServerProcessId ?? serverContext.ServerHeader.GetReadonlyRef().ServerProcessId;

        // Step 2: a restarted server: every row back to unwritten, so none of the old run's shows where it has not written; its header waits for its snapshot
        if (newServerHeader != null)
        {
            for (int arrayId = 0; arrayId < serverContext.SharedArraysCount; arrayId++)
            {
                SharedArray sharedArray = serverContext.GetSharedArray(arrayId);
                for (int index = 0; index < sharedArray.Capacity; index++)
                    sharedArray.Clear(index);
            }
            _pendingServerHeader = newServerHeader;
        }

        // Step 3: a ServerSocket of its own, so no GUI of an earlier server stays attached
        int clientCapacity = serverContext.ServerHeader.GetReadonlyRef().ClientIds.Length;
        _mirrorGuisByClientId = new MirrorGui[clientCapacity];
        _serverSocket = new ServerSocket(serverContext.ServerName, clientCapacity);
        // Why: a closed GUI goes Detached, not Closed, so a Workspace reopened at once is answered (no 1 s Closed window)
        _serverSocket.Persistance = true;
        _serverSocket.AllocateClientId = AllocateClientId;
        _serverSocket.ClientOpened += clientId => _pendingClientIds.TryRemove((_mirrorGuisByClientId[clientId].ClientName.ToString(), _mirrorGuisByClientId[clientId].GuiProcessId), out _);
        _remoteMirrorProcessIdBox = new LetterBox<int>(RemoteClient.GetRemoteMirrorProcessIdPath(serverContext.ServerName), Access.Write);
    }

    /// One pass: take a newer link, apply what came down, close a dead link, forward the GUIs' messages, ping, list the GUIs.
    public void Read()
    {
        // Step 1: a newer link replaces this one; it starts with a full snapshot, opened by the server header that is checked first
        if (_tcpLinkConnector.TryTakeNewLink(out TcpLink? tcpLink))
        {
            _tcpLink.Dispose();
            _tcpLink = tcpLink;
            _isGuiListDue = true;
            _isLinkChecked = false;
            // Why: latency is the link's; the old link's pongs say nothing about this one, and the first ping goes now
            Array.Clear(_roundTrips);
            _pongedPingTimestamp = Timestamp.StopwatchNow;
            _pingTimestamp = default;
        }

        // Step 2: down: the server header (checked against this server), rows, AllocatedClientId, pongs, heartbeats; a restarted server ends this object's work
        ApplyPackets();
        if (NewServerHeader != null)
            return;

        // Step 3: a dead link is closed at once, not when a new one arrives: the Local end then sees it end and redials now, not after its own 10 s
        if (!_tcpLink.IsConnected)
            _tcpLink.Dispose();

        // Step 4: up: each GUI's messages and its folders' sync, once the link is known to serve this server
        if (_isLinkChecked)
        {
            WriteGuiMessages();
            _fileSyncRemote?.Sync(_tcpLink);
        }

        // Step 5: a ping every 100 ms with this machine's monotonic clock (it doubles as the keep-alive), and the latency into the copy every pass
        Timestamp pingTimestamp = Timestamp.StopwatchNow;
        if (pingTimestamp - _pingTimestamp >= s_pingInterval)
        {
            _pingTimestamp = pingTimestamp;
            MirrorPing mirrorPing = new() { PingTimestamp = pingTimestamp };
            _tcpLink.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<MirrorPing>(in mirrorPing)));
        }
        _serverContext.GetLatency(0).Write(GetLatency());

        // Step 6: the GUIs attached here, every second and at once when one waits, so the Local end retries a stand-in it could not start
        Timestamp now = Timestamp.StopwatchNow;
        if (_isLinkChecked && (_isGuiListDue || now - _guiListTimestamp >= s_guiListInterval))
        {
            _isGuiListDue = false;
            _guiListTimestamp = now;
            WriteGuiList();
        }
    }

    // Down: the server header and each row into the copy, each AllocatedClientId to its GUI, each pong timed, the first heartbeat opens the copy.
    private void ApplyPackets()
    {
        while (_tcpLink.TryRead(out Span<byte> packet))
        {
            switch ((MirrorPacketType)packet[0])
            {
                case MirrorPacketType.ServerHeader:
                {
                    MirrorServerHeader mirrorServerHeader = MemoryMarshal.Read<MirrorServerHeader>(packet);
                    ServerHeader serverHeader = mirrorServerHeader.ServerHeader;
                    serverHeader.ServerName = MirrorPacket.RewriteName(serverHeader.ServerName, isTravelling: false);

                    // Why every header: rows are tagged by array number, so a Local end of another build would put them in the wrong arrays
                    if (mirrorServerHeader.LayoutHash != _layoutHash)
                        StopDesk($"the CME box's array layout is {mirrorServerHeader.LayoutHash:X8}, this desk's is {_layoutHash:X8}; deploy the same build to both ends");

                    // Why only the server's process: a Local end restarted over the same server is a link drop; the rest of the link is the new server's (see Spec.md "Remote workspaces")
                    if (serverHeader.ServerProcessId != _serverProcessId)
                    {
                        NewServerHeader = serverHeader;
                        return;
                    }
                    _isLinkChecked = true;

                    // RecoveryWrite keeps the copy's LetterBox posted (it sets Length), as the first header was posted at startup
                    if (_pendingServerHeader != null)
                        _pendingServerHeader = serverHeader;
                    else
                        _serverContext.ServerHeader.RecoveryWrite(in serverHeader);
                    break;
                }
                case MirrorPacketType.Row:
                {
                    // The copy's only writer, names under this end's root. Why RecoveryWrite: a row a killed Mirror left mid-write stays odd under Write
                    MirrorRowHeader mirrorRowHeader = MemoryMarshal.Read<MirrorRowHeader>(packet);
                    SharedArray sharedArray = _serverContext.GetSharedArray(mirrorRowHeader.ArrayId);
                    Span<byte> row = packet[Unsafe.SizeOf<MirrorRowHeader>()..];
                    MirrorPacket.RewriteNames(sharedArray, row, isTravelling: false);
                    sharedArray.RecoveryWrite(mirrorRowHeader.Index, row);
                    break;
                }
                case MirrorPacketType.Pong:
                {
                    Timestamp pingTimestamp = MemoryMarshal.Read<MirrorPing>(packet).PingTimestamp;
                    _roundTrips[_pongCount++ % _roundTrips.Length] = Timestamp.StopwatchNow - pingTimestamp;
                    _pongedPingTimestamp = pingTimestamp;
                    break;
                }
                case MirrorPacketType.AllocatedClientId:
                    ApplyAllocatedClientId(MirrorPacket.ReadMirrorGuis<Header<MirrorPacketType>>(packet)[0]);
                    break;
                case MirrorPacketType.Heartbeat:
                    ApplyHeartbeat();
                    break;
                case MirrorPacketType.FileAnnounce:
                    _fileSyncRemote?.OnAnnounce(packet);
                    break;
                case MirrorPacketType.FileChunk:
                    _fileSyncRemote?.OnChunk(packet);
                    break;
            }
        }
    }

    // The worst of the last 10 round trips, or the age of the oldest unanswered ping (sent one interval after the newest answered
    // one) when longer, so it climbs through a stall; below 0 once the link is down or nothing has answered for its silence timeout.
    private Duration GetLatency()
    {
        Duration latency = _roundTrips.Append(Timestamp.StopwatchNow - _pongedPingTimestamp - s_pingInterval).Max();
        return _tcpLink.IsConnected && latency < RemoteClient.s_silenceTimeout ? latency : Duration.FromNanoseconds(-1);
    }

    // The Local end's answer for a GUI waiting in AllocateClientId: its id, or a negative ConnectRefusal saying why not.
    private void ApplyAllocatedClientId(MirrorGui mirrorGui)
        => _pendingClientIds.TryUpdate((MirrorPacket.RewriteName(mirrorGui.ClientName, isTravelling: false).ToString(), mirrorGui.GuiProcessId), mirrorGui.ClientId, s_waitingClientId);

    // The first heartbeat completes the copy: a restarted server's header goes in now (each Workspace rebuilds on its process id), and GUIs may attach.
    private void ApplyHeartbeat()
    {
        if (_pendingServerHeader is ServerHeader pendingServerHeader)
        {
            _serverContext.ServerHeader.RecoveryWrite(in pendingServerHeader);
            _pendingServerHeader = null;
        }
        if (!_isListening)
        {
            _serverSocket.Listen();
            _isListening = true;
            // Why after Listen: a Workspace rebuilds on it and connects at once. RecoveryWrite keeps the LetterBox posted, as the server header's
            int processId = Environment.ProcessId;
            _remoteMirrorProcessIdBox.GetEntry().RecoveryWrite(in processId);
            Console.WriteLine("RemoteMirror: copy complete, Workspaces may start");
        }
    }

    // Stops the desk with its reason: every Workspace shows "Connection broken" at once, and the desk is restarted by hand.
    private void StopDesk(string reason)
    {
        Console.WriteLine($"RemoteMirror: {reason}. Close the Workspaces on this machine, then start the Mirror again.");
        // Why: this process stops rewriting the latency, so its last value would freeze on every Workspace until the 10 s age check
        _serverContext.GetLatency(0).Write(Duration.FromNanoseconds(-1));
        Environment.Exit(1);
    }

    // Each attached GUI's calls, up to the Local end; while the link is down TcpLink.Write drops them.
    private void WriteGuiMessages()
    {
        foreach (int clientId in _serverSocket.ClientIds())
        {
            while (_serverSocket.TryRead(clientId, out ReadOnlySpan<byte> message) == ReadStatus.New)
                _tcpLink.Write(MirrorPacket.CreateMessage(clientId, message));
        }
    }

    // The GUIs attached here plus any waiting in AllocateClientId, names relative to the root.
    private void WriteGuiList()
    {
        List<MirrorGui> _listedMirrorGuis = new();
        foreach (int clientId in _serverSocket.ClientIds())
        {
            MirrorGui mirrorGui = _mirrorGuisByClientId[clientId];
            _listedMirrorGuis.Add(new MirrorGui() { GuiProcessId = mirrorGui.GuiProcessId, ClientName = MirrorPacket.RewriteName(mirrorGui.ClientName, isTravelling: true) });
        }
        foreach ((string clientName, int guiProcessId) in _pendingClientIds.Keys)
            _listedMirrorGuis.Add(new MirrorGui() { GuiProcessId = guiProcessId, ClientName = MirrorPacket.RewriteName(new String256(clientName), isTravelling: true) });
        // Why the flag here: the box serves the folders of exactly these GUIs, and only to a desk that asks
        _tcpLink.Write(MirrorPacket.NewGuiPacket(new MirrorGuiList() { IsFolderSyncOn = _fileSyncRemote != null }, CollectionsMarshal.AsSpan(_listedMirrorGuis)));
    }

    // Listen thread: lists the new GUI so the Local end starts a ManualClient for it, and waits for its AllocatedClientId: that client's id
    // is the GUI's id here, or the Local end's reason for refusing it. NotReady when no answer comes (the link is down, or this object was replaced).
    private int AllocateClientId(in SocketHeader socketHeader)
    {
        // Step 1: only a RemoteClient attaches here; a ManualClient is told this is a remote desk, and ManualClient.Open opens one instead
        if (!ManualClient.TryParseRemoteGuiName(socketHeader.ClientName.ToString(), out _, out _))
            return (int)ConnectRefusal.RemoteDesk;

        // Step 2: one Workspace per name, as on the server
        foreach (int attachedClientId in _serverSocket.ClientIds())
        {
            if (_mirrorGuisByClientId[attachedClientId].ClientName == socketHeader.ClientName)
                return (int)ConnectRefusal.AlreadyConnected;
        }

        // Step 3: list it and wait for its AllocatedClientId
        (string ClientName, int GuiProcessId) pendingKey = (socketHeader.ClientName.ToString(), socketHeader.ClientProcessId);
        _pendingClientIds[pendingKey] = s_waitingClientId;
        _isGuiListDue = true;
        Timestamp deadline = Timestamp.StopwatchNow + s_allocateClientIdTimeout;
        while (Timestamp.StopwatchNow < deadline && !_isDisposed)
        {
            if (_pendingClientIds.TryGetValue(pendingKey, out int clientId) && clientId != s_waitingClientId)
            {
                if (clientId < 0)
                {
                    _pendingClientIds.TryRemove(pendingKey, out _);
                    return clientId;   // the Local end's refusal, passed on to the GUI
                }
                _mirrorGuisByClientId[clientId] = new MirrorGui() { ClientId = clientId, GuiProcessId = pendingKey.GuiProcessId, ClientName = socketHeader.ClientName };
                return clientId;   // stays pending until ClientOpened, so no GuiList leaves it out in between
            }
            Thread.Sleep(10);
        }

        // Step 4: no answer: the link is down, or a restarted server replaced this object (the GUI asks again)
        _pendingClientIds.TryRemove(pendingKey, out _);
        Console.WriteLine($"RemoteMirror: {socketHeader.ClientName} got no answer from the Local end ({(_isDisposed ? "the server restarted" : "link down for 10 s")})");
        return (int)ConnectRefusal.NotReady;
    }

    /// Stops this object's ServerSocket; the copy and the link stay, for its replacement.
    public void Dispose()
    {
        _isDisposed = true;
        _serverSocket.Dispose();
        _remoteMirrorProcessIdBox.Dispose();
    }
}
