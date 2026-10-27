using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Execution;
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
TcpLinkConnector tcpLinkConnector = new(mirrorConfig.ListenPort, mirrorConfig.IPWhitelist, mirrorConfig.ConnectAddresses);

if (mirrorConfig.MirrorRole == MirrorRole.Local)
    RunLocal(serverName, tcpLinkConnector);
else
    MirrorRunner.RunRemote(serverName, tcpLinkConnector, mirrorConfig);

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

    // Step 2: remotes' packets every millisecond, rows every 100 ms; each remote GUI stands in as a ManualClient; alerts from here (a remote desk has no Logging)
    // Why its own name: the trading server already connects to Logging as "<server>.alert"
    AlertManager alertManager = new(ContextManager.ServerContext, ContextManager.ServerContext.DirectoryPath / "LocalMirror");
    LocalMirror localMirror = new(ContextManager.ServerContext, tcpLinkConnector, (clientName, principalName) => NewStandIn(clientName, principalName, serverName, alertManager));
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

// A remote GUI's stand-in beside the server: a ManualClient under exactly the GUI's name, reading the channels of the instruments
// its id still holds (an earlier Workspace's: no round trip); its rejects alert here, since a remote desk has no Logging.
static ManualClient NewStandIn(string clientName, string principalName, FileSystemPath serverName, AlertManager alertManager)
{
    ManualClient manualClient = new(clientName, principalName, serverName);
    try
    {
        foreach (int instrumentId in manualClient.Context.InstrumentIds)
            manualClient.GetInstrument(manualClient.Context.GetInstrument(instrumentId).Header.InstrumentHeaderId);
    }
    catch
    {
        manualClient.Dispose();
        throw;
    }
    manualClient.OrderRejected += (in OrderRejected orderRejected) => alertManager.OnOrderRejected(orderRejected, "");
    return manualClient;
}

