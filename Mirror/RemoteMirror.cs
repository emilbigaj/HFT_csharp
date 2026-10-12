using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Provider;
using Socket;
using Tools;

namespace Mirror;

/// The Remote end of the mirror, where people sit. Keeps a read-only copy of the Local end's server under the same server
/// name, and runs a ServerSocket under that name, where only RemoteClients attach. Each GUI's RemoteClient gets the client id
/// of the ManualClient serving it on the Local end, and its calls are passed to that ManualClient; this end pings the Local
/// end every 100 ms and writes the link's latency into the copy (Latency[0]), where every Workspace here reads it. Cold path:
/// the Mirror's main thread, plus the ServerSocket's listen thread, which runs AllocateClientId.
public sealed class RemoteMirror
{
    private const int s_waitingClientId = int.MinValue;                                     // a GUI in AllocateClientId with no AllocatedClientId yet
    private static readonly Duration s_guiListInterval = Duration.FromSeconds(1);
    private static readonly Duration s_allocateClientIdTimeout = Duration.FromSeconds(10);   // Why: an AllocatedClientId comes within a pass; only a down link takes this long
    private static readonly Duration s_pingInterval = Duration.FromMilliseconds(100);

    private readonly ServerContext _serverContext;
    private readonly ServerSocket _serverSocket;
    private readonly TcpLinkConnector _tcpLinkConnector;
    private TcpLink _tcpLink;
    private readonly MirrorGui[] _mirrorGuisByClientId;    // the GUIs attached here, with local names; the listen thread writes each before it opens
    private readonly ConcurrentDictionary<(string ClientName, int GuiProcessId), int> _pendingClientIds = new();   // GUIs in AllocateClientId: their AllocatedClientId
    private Timestamp _localStartTimestamp = Timestamp.MinValue;
    private Timestamp _guiListTimestamp;                    // when the last GuiList went out (Timestamp.StopwatchNow: a wall-clock step must not stop it)
    private Timestamp _pingTimestamp;                       // when the last ping went out (Timestamp.StopwatchNow)
    private readonly Duration[] _roundTrips = new Duration[10];   // the last 10 pongs' round trips, round-robin by _pongCount
    private uint _pongCount;
    private Timestamp _pongedPingTimestamp = Timestamp.StopwatchNow;   // the newest answered ping (the start, before the first pong)
    private volatile bool _isGuiListDue;
    private bool _isListening;

    public RemoteMirror(ServerContext serverContext, TcpLinkConnector tcpLinkConnector, TcpLink tcpLink)
    {
        _serverContext = serverContext;
        _tcpLinkConnector = tcpLinkConnector;
        _tcpLink = tcpLink;
        int clientCapacity = serverContext.ServerHeader.GetReadonlyRef().ClientIds.Length;
        _mirrorGuisByClientId = new MirrorGui[clientCapacity];
        _serverSocket = new ServerSocket(serverContext.ServerName, clientCapacity);
        // Why: a closed GUI goes Detached, not Closed, so a Workspace reopened at once is answered (no 1 s Closed window)
        _serverSocket.Persistance = true;
        _serverSocket.AllocateClientId = AllocateClientId;
        _serverSocket.ClientOpened += clientId => _pendingClientIds.TryRemove((_mirrorGuisByClientId[clientId].ClientName.ToString(), _mirrorGuisByClientId[clientId].GuiProcessId), out _);
    }

    /// One pass: take a newer link, apply what came down, close a dead link, forward the GUIs' messages, ping, list the GUIs.
    public void Read()
    {
        // Step 1: a newer link replaces this one; it starts with a full snapshot
        if (_tcpLinkConnector.TryTakeNewLink(out TcpLink? tcpLink))
        {
            _tcpLink.Dispose();
            _tcpLink = tcpLink;
            _isGuiListDue = true;
        }

        // Step 2: down: rows, AllocatedClientId, pongs, heartbeats
        ApplyPackets();

        // Step 3: a dead link is closed at once, not when a new one arrives: the Local end then sees it end and redials now, not after its own 10 s
        if (!_tcpLink.IsConnected)
            _tcpLink.Dispose();

        // Step 4: up: each GUI's messages
        WriteGuiMessages();

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
        if (_isGuiListDue || now - _guiListTimestamp >= s_guiListInterval)
        {
            _isGuiListDue = false;
            _guiListTimestamp = now;
            WriteGuiList();
        }
    }

    // Down: the server header and each row into the copy, each AllocatedClientId to its GUI, each pong timed, each heartbeat checks the Local end.
    private void ApplyPackets()
    {
        while (_tcpLink.TryRead(out Span<byte> packet))
        {
            switch ((MirrorPacketType)packet[0])
            {
                case MirrorPacketType.ServerHeader:
                {
                    // RecoveryWrite keeps the copy's LetterBox posted (it sets Length), as the first header was posted at startup
                    ServerHeader serverHeader = MemoryMarshal.Read<MirrorServerHeader>(packet).ServerHeader;
                    serverHeader.ServerName = MirrorPacket.RewriteName(serverHeader.ServerName, isTravelling: false);
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
                    ApplyAllocatedClientId(MirrorPacket.ReadMirrorGuis(packet)[0]);
                    break;
                case MirrorPacketType.Heartbeat:
                    ApplyHeartbeat(MemoryMarshal.Read<MirrorHeartbeat>(packet));
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

    // The first heartbeat completes the copy, so GUIs may attach; a different start time means the Local end restarted.
    private void ApplyHeartbeat(MirrorHeartbeat mirrorHeartbeat)
    {
        // Step 1: a restarted Local end may have handed out other client ids, and its rows are a new lifetime: start over
        if (_localStartTimestamp == Timestamp.MinValue)
            _localStartTimestamp = mirrorHeartbeat.LocalStartTimestamp;
        if (mirrorHeartbeat.LocalStartTimestamp != _localStartTimestamp)
        {
            Console.WriteLine("RemoteMirror: the Local end restarted. Close the Workspaces on this machine; the Mirror starts again once they are closed.");
            // Why: this process stops rewriting the latency, so its last value would freeze on every Workspace until the 10 s age check
            _serverContext.GetLatency(0).Write(Duration.FromNanoseconds(-1));
            Environment.Exit(1);
        }

        // Step 2: GUIs read the copy, so they attach only once it is complete
        if (!_isListening)
        {
            _serverSocket.Listen();
            _isListening = true;
            Console.WriteLine("RemoteMirror: copy complete, Workspaces may start");
        }
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
            _listedMirrorGuis.Add(new MirrorGui() { GuiProcessId = guiProcessId, ClientName = MirrorPacket.RewriteName(new String128(clientName), isTravelling: true) });
        _tcpLink.Write(MirrorPacket.CreateGuiPacket(MirrorPacketType.GuiList, CollectionsMarshal.AsSpan(_listedMirrorGuis)));
    }

    // Listen thread: lists the new GUI so the Local end starts a ManualClient for it, and waits for its AllocatedClientId: that client's id
    // is the GUI's id here, or the Local end's reason for refusing it. NotReady when no answer comes (the link is down).
    private int AllocateClientId(in SocketHeader socketHeader)
    {
        // Step 1: only a RemoteClient attaches here; a ManualClient is told this is a remote desk, and ManualClient.Open opens one instead
        if (!ManualClient.TryGetPrincipalName(socketHeader.ClientName.ToString(), out _))
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
        while (Timestamp.StopwatchNow < deadline)
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

        // Step 4: no answer: the link is down
        _pendingClientIds.TryRemove(pendingKey, out _);
        Console.WriteLine($"RemoteMirror: {socketHeader.ClientName} got no answer from the Local end in 10 s (link down)");
        return (int)ConnectRefusal.NotReady;
    }
}
