using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Mirror;
using Provider;
using Socket;
using Tools;

// Mirror [absolute config path]: runs one end of the remote mirror, configured by that file, or MirrorConfig.json in the working directory (see Spec.md "Remote workspaces")
string mirrorConfigPath = args.Length > 0 ? args[0] : MirrorConfig.s_fileName;
MirrorConfig mirrorConfig = MirrorConfig.LoadOrCreate(mirrorConfigPath);
mirrorConfig.ThrowIfInvalid(mirrorConfigPath);
Clock.Mode = mirrorConfig.ClockMode;
// Before any path is built. Why: a remote risk desk has no S: drive; a relative DirectoriesRoot is beside this executable
if (mirrorConfig.DirectoriesRoot.Length > 0)
    Context.DirectoriesRoot = Path.Combine(AppContext.BaseDirectory, mirrorConfig.DirectoriesRoot);
FileSystemPath serverName = ServerContext.GetDirectoryPath(mirrorConfig.ServerName);
// Why: on the CME box this process must stay off the isolated trading cores
LowLatency.PinCurrentThreadToCoreRange(LowLatency.HouseKeepingCores);
TcpLinkConnector tcpLinkConnector = new(mirrorConfig);

if (mirrorConfig.MirrorRole == MirrorRole.Local)
    RunLocal(serverName, tcpLinkConnector);
else
    RunRemote(serverName, tcpLinkConnector);

// Beside the real server: mirrors its shared memory to every remote and runs the remote GUIs' ManualClients.
static void RunLocal(FileSystemPath serverName, TcpLinkConnector tcpLinkConnector)
{
    // Step 1: attach to the running server, read-only; in simulation its clock drives this one
    ContextManager.Initialize(serverName);
    // Why exit with the server: a dead server's stand-ins are read by no one, and its restart cannot post its header while this process maps it
    int serverProcessId = ContextManager.ServerContext.ServerHeader.GetReadonlyRef().ServerProcessId;
    // Why check before the AlertManager: it waits forever for the dead server's Logging, which exits with that server
    if (!ProcessId.IsAlive(serverProcessId))
    {
        Console.WriteLine($"Mirror.RunLocal: the posted server header names process {serverProcessId}, which has exited; start the Local Mirror after the server");
        return;
    }
    if (Clock.Mode == ClockMode.Simulation)
    {
        Clock.Begin = ContextManager.ServerContext.ServerHeader.GetReadonlyRef().Timestamp;
        Clock.End = Timestamp.MaxValue;
        Clock.Interject += timestamp =>
        {
            Clock.OnInterject(ContextManager.ServerContext.ServerHeader.GetReadonlyRef().Timestamp);
            Thread.Sleep(1);
        };
    }
    LowLatency.StartBackgroundThread("Clock", Clock.Start);

    // Step 2: remotes' packets every millisecond; rows when a remote's ping (every 100 ms), a stand-in's news or a GUI's id waits; alerts from here (a remote desk has no Logging)
    AlertManager alertManager = new(ContextManager.ServerContext);
    LocalMirror localMirror = new(ContextManager.ServerContext, tcpLinkConnector, alertManager);
    tcpLinkConnector.Start();
    bool isServerAlive = ProcessId.IsAlive(serverProcessId);
    while (!Application.IsExiting && (isServerAlive = ProcessId.IsAlive(serverProcessId)))
    {
        // Why: one bad packet or refused GUI must not stop the mirror for everyone
        try
        {
            localMirror.Read();
            if (localMirror.IsPublishDue)
                localMirror.Publish();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Mirror.RunLocal: {exception}");
            alertManager.OnException(exception);
        }
        Thread.Sleep(1);
    }
    if (!isServerAlive)
        Console.WriteLine($"Mirror.RunLocal: server process {serverProcessId} has exited; the Local Mirror exits with it");
    alertManager.Dispose();
}

// Where people sit: keeps a copy of the server that local Workspaces attach to.
static void RunRemote(FileSystemPath serverName, TcpLinkConnector tcpLinkConnector)
{
    // Step 1: a copy the last run's Workspaces still map belongs to an older Local end: wait for them to close
    WaitForPreviousCopyToClose(serverName);

    // Step 2: the first link's first packet is the trading server's header; posted here, it sizes the copy
    tcpLinkConnector.Start();
    LetterBox<ServerHeader> serverHeaderBox = new(serverName / "ServerHeader", Access.Write);
    TcpLink tcpLink = ReadServerHeader(serverName, serverHeaderBox, tcpLinkConnector, out uint localLayoutHash);
    ServerContext serverContext = new(serverName, Access.Write);
    // Why: until the copy's ServerContext maps the header region, this is the only handle keeping it alive on Windows
    GC.KeepAlive(serverHeaderBox);
    // Why at startup and loudly: rows are tagged by array number, so a desk built differently would put them in the wrong arrays and show plausible but wrong orders
    if (localLayoutHash != serverContext.GetLayoutHash())
        throw new InvalidDataException($"Mirror.RunRemote: the CME box's array layout is {localLayoutHash:X8}, this desk's is {serverContext.GetLayoutHash():X8}, must match; deploy the same build to both ends");

    // Step 3: keep the copy current
    RemoteMirror remoteMirror = new(serverContext, tcpLinkConnector, tcpLink);
    while (!Application.IsExiting)
    {
        // Why: one bad packet must not stop the copy for everyone
        try
        {
            remoteMirror.Read();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Mirror.RunRemote: {exception}");
        }
        Thread.Sleep(1);
    }
}

// Waits while any process still maps the copy's header (the last run's Workspaces), checking once a second.
static void WaitForPreviousCopyToClose(FileSystemPath serverName)
{
    while (true)
    {
        using (LetterBox<ServerHeader> serverHeaderBox = new(serverName / "ServerHeader", Access.Read))
        {
            if (serverHeaderBox.IsEmpty())
                return;
        }
        Console.WriteLine("Mirror.WaitForPreviousCopyToClose: Workspaces of the last run are still open; close them to start");
        Thread.Sleep(1000);
    }
}

// Waits for a link and its first packet, the server header, checks it is the configured server, and posts it in the copy's
// LetterBox as a server posts its own, so the copy's arrays are sized by the real server and Workspaces may connect.
static TcpLink ReadServerHeader(FileSystemPath serverName, LetterBox<ServerHeader> serverHeaderBox, TcpLinkConnector tcpLinkConnector, out uint localLayoutHash)
{
    while (true)
    {
        if (!tcpLinkConnector.TryTakeNewLink(out TcpLink? tcpLink))
        {
            Thread.Sleep(10);
            continue;
        }
        while (tcpLink.IsConnected)
        {
            if (!tcpLink.TryRead(out Span<byte> packet))
            {
                Thread.Sleep(1);
                continue;
            }
            if ((MirrorPacketType)packet[0] != MirrorPacketType.ServerHeader)
                throw new InvalidDataException("Mirror.ReadServerHeader: a mirror link must start with the server header");
            ServerHeader serverHeader = MemoryMarshal.Read<MirrorServerHeader>(packet).ServerHeader;
            localLayoutHash = MemoryMarshal.Read<MirrorServerHeader>(packet).LayoutHash;
            serverHeader.ServerName = MirrorPacket.RewriteName(serverHeader.ServerName, isTravelling: false);
            FileSystemPath mirroredServerName = serverHeader.ServerName.ToString();
            if (mirroredServerName != serverName)
                throw new InvalidDataException($"Mirror.ReadServerHeader: the Local end serves {mirroredServerName}; this Remote is configured for {serverName} (ServerName and ClockMode must match)");
            if (!serverHeaderBox.TryStore(in serverHeader))
                throw new InvalidOperationException("Mirror.ReadServerHeader: the copy's server header is already posted");
            return tcpLink;
        }
        tcpLink.Dispose();
    }
}
