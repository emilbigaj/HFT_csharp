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
// Why first, once valid: the console opens with which end this is and the config this run uses, defaults included
Console.WriteLine($"################### {mirrorConfig.MirrorRole.ToString().ToUpperInvariant()} MIRROR ###################");
Console.WriteLine($"{Path.GetFullPath(mirrorConfigPath)}{Environment.NewLine}{Json.Serialize(mirrorConfig)}");
// Why after the checks: a config that cannot work still shows its error in the console it was started from
if (!mirrorConfig.IsConsoleVisible)
    Application.DetachConsole(Path.Combine(AppContext.BaseDirectory, "Mirror.log"));
Clock.Mode = ClockMode.Realtime;   // Why: a remote desk only ever mirrors a live server (see Spec.md "Remote workspaces")
// Before any path is built. Why: a remote risk desk has no S: drive; a relative DirectoriesRoot is beside this executable
if (mirrorConfig.DirectoriesRoot.Length > 0)
    Context.DirectoriesRoot = Path.Combine(AppContext.BaseDirectory, mirrorConfig.DirectoriesRoot);
FileSystemPath serverName = ServerContext.GetDirectoryPath(mirrorConfig.ServerName);
// Why: on the CME box this process must stay off the isolated trading cores
LowLatency.PinCurrentThreadToCoreRange(LowLatency.HouseKeepingCores);
// Why: a Windows desk's 1 ms loop otherwise passes every 15.6 ms, which every row and every file request waits for
LowLatency.RaiseTimerResolution();
TcpLinkConnector tcpLinkConnector = new(mirrorConfig);

if (mirrorConfig.MirrorRole == MirrorRole.Local)
    RunLocal(serverName, tcpLinkConnector);
else
    RunRemote(serverName, tcpLinkConnector, mirrorConfig.IsFolderSyncOn);

// Beside the real server: mirrors its shared memory to every remote and runs the remote GUIs' ManualClients.
static void RunLocal(FileSystemPath serverName, TcpLinkConnector tcpLinkConnector)
{
    // Step 1: attach to the running server, read-only
    ContextManager.Initialize(serverName);
    // Why exit with the server: a dead server's stand-ins are read by no one, and its restart cannot post its header while this process maps it
    int serverProcessId = ContextManager.ServerContext.ServerHeader.GetReadonlyRef().ServerProcessId;
    // Why check before the AlertManager: it waits forever for the dead server's Logging, which exits with that server
    if (!ProcessId.IsAlive(serverProcessId))
    {
        Console.WriteLine($"Mirror.RunLocal: the posted server header names process {serverProcessId}, which has exited; start the Local Mirror after the server");
        return;
    }
    LowLatency.StartBackgroundThread("Clock", Clock.Start);

    // Step 2: remotes' packets every millisecond; rows when a remote's ping (every 100 ms), a stand-in's news or a GUI's id waits; alerts from here (a remote desk has no Logging)
    // Why its own name: the trading server already connects to Logging as "<server>.alert"
    AlertManager alertManager = new(ContextManager.ServerContext, ContextManager.ServerContext.DirectoryPath / "LocalMirror");
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
static void RunRemote(FileSystemPath serverName, TcpLinkConnector tcpLinkConnector, bool isFolderSyncOn)
{
    // Step 1: the first link's first packet is the trading server's header; posted here, it sizes the copy
    tcpLinkConnector.Start();
    LetterBox<ServerHeader> serverHeaderBox = new(serverName / "ServerHeader", Access.Write);
    TcpLink tcpLink = ReadServerHeader(serverName, tcpLinkConnector, out ServerHeader serverHeader, out uint localLayoutHash);
    // Why reuse: Workspaces still open from the last Remote keep its copy, header posted; it is rebuilt as on a server restart, and they rebuild on this Remote (see Spec.md "Remote workspaces")
    bool isCopyReused = !serverHeaderBox.TryStore(in serverHeader);
    ServerContext serverContext = new(serverName, Access.Write);
    // Why: until the copy's ServerContext maps the header region, this is the only handle keeping it alive on Windows
    GC.KeepAlive(serverHeaderBox);

    // Step 2: the layouts. Why at startup and loudly: rows are tagged by array number, so a desk built differently would put them in the wrong arrays and show plausible but wrong orders
    if (localLayoutHash != serverContext.GetLayoutHash())
        throw new InvalidDataException(isCopyReused
            ? $"Mirror.RunRemote: the CME box's array layout is {localLayoutHash:X8}, the copy this desk's open Workspaces still map is {serverContext.GetLayoutHash():X8}, must match; the server's sizes or the build changed: close the Workspaces, then start the Mirror again"
            : $"Mirror.RunRemote: the CME box's array layout is {localLayoutHash:X8}, this desk's is {serverContext.GetLayoutHash():X8}, must match; deploy the same build to both ends");
    if (isCopyReused)
        Console.WriteLine("Mirror.RunRemote: Workspaces from the last run are still open; the copy is rebuilt and they rebuild themselves");

    // Step 3: keep the copy current; a restarted server gets a fresh RemoteMirror over the same copy and link (see Spec.md "Remote workspaces")
    FileSyncRemote? fileSyncRemote = isFolderSyncOn ? new(Context.DirectoriesRoot) : null;   // Why null: a desk with the sync off says so in its GuiList, so the box sends it nothing
    RemoteMirror remoteMirror = new(serverContext, tcpLinkConnector, tcpLink, newServerHeader: isCopyReused ? serverHeader : null, fileSyncRemote);
    while (!Application.IsExiting)
    {
        // Why: one bad packet must not stop the copy for everyone
        try
        {
            remoteMirror.Read();
            if (remoteMirror.NewServerHeader is ServerHeader newServerHeader)
            {
                Console.WriteLine($"Mirror.RunRemote: the server restarted (process {serverContext.ServerHeader.GetReadonlyRef().ServerProcessId} is now {newServerHeader.ServerProcessId}); the copy is rebuilt and the Workspaces rebuild themselves");
                remoteMirror.Dispose();
                remoteMirror = new RemoteMirror(serverContext, tcpLinkConnector, remoteMirror.TcpLink, newServerHeader, fileSyncRemote);
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Mirror.RunRemote: {exception}");
        }
        Thread.Sleep(1);
    }
}

// Waits for a link and its first packet, the server header, and checks it is the configured server; returns the link, the
// header with this desk's names, and the Local end's layout hash.
static TcpLink ReadServerHeader(FileSystemPath serverName, TcpLinkConnector tcpLinkConnector, out ServerHeader serverHeader, out uint localLayoutHash)
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
            serverHeader = MemoryMarshal.Read<MirrorServerHeader>(packet).ServerHeader;
            localLayoutHash = MemoryMarshal.Read<MirrorServerHeader>(packet).LayoutHash;
            serverHeader.ServerName = MirrorPacket.RewriteName(serverHeader.ServerName, isTravelling: false);
            FileSystemPath mirroredServerName = serverHeader.ServerName.ToString();
            if (mirroredServerName != serverName)
                throw new InvalidDataException($"Mirror.ReadServerHeader: the Local end serves {mirroredServerName}; this Remote is configured for {serverName} (ServerName must match)");
            return tcpLink;
        }
        tcpLink.Dispose();
    }
}
