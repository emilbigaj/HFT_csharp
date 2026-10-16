using Provider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using Tools;

namespace Workspace;

internal class Program
{
    // Entry point: with no arguments everything comes from WorkspaceConfig.json beside the executable (a risk desk's double-click),
    // otherwise from the command line, which opens one window. Delegates to WorkspaceRunner.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [STAThread]
    public static void Main(string[] args)
    {
        // Step 1: the server, mode, root and windows, from the config file or the command line
        string rawServerName = string.Empty;
        string rawMode = string.Empty;
        string rawDirectoriesRoot = string.Empty;
        WorkspaceConfigEntry commandLineEntry = new();
        WorkspaceConfigEntry[] workspaceConfigEntries = [commandLineEntry];
        if (args.Length == 0)
        {
            string workspaceConfigPath = Path.Combine(AppContext.BaseDirectory, WorkspaceConfig.s_fileName);
            WorkspaceConfig workspaceConfig = WorkspaceConfig.LoadOrCreate(workspaceConfigPath);
            workspaceConfig.ThrowIfInvalid(workspaceConfigPath);
            rawServerName = workspaceConfig.ServerName;
            rawMode = nameof(ClockMode.Realtime);
            rawDirectoriesRoot = workspaceConfig.DirectoriesRoot;
            workspaceConfigEntries = workspaceConfig.Workspaces;
        }
        for (int argumentIndex = 0; argumentIndex < args.Length - 1; argumentIndex++)
        {
            if (args[argumentIndex] == "-s")
                rawServerName = args[++argumentIndex];
            else if (args[argumentIndex] == "-c")
                commandLineEntry.StrategyName = args[++argumentIndex];
            else if (args[argumentIndex] == "-m")
                rawMode = args[++argumentIndex];
            else if (args[argumentIndex] == "-w")
                commandLineEntry.WorkspaceName = args[++argumentIndex];
            else if (args[argumentIndex] == "-d")
                rawDirectoriesRoot = args[++argumentIndex];
        }

        // Step 2: Client, Workspace and DirectoriesRoot are optional, Server and Mode are required
        if (string.IsNullOrEmpty(rawServerName) || string.IsNullOrEmpty(rawMode))
        {
            Console.WriteLine($"Usage: Workspace -s <ServerName> -m <Mode> [-c <ClientName>] [-w <WorkspaceName>] [-d <DirectoriesRoot>], or no arguments for {WorkspaceConfig.s_fileName} beside the executable");
            return;
        }
        ClockMode mode = rawMode.Equals("Realtime", StringComparison.OrdinalIgnoreCase)
            ? ClockMode.Realtime
            : rawMode.Equals("Simulation", StringComparison.OrdinalIgnoreCase) ? ClockMode.Simulation : throw new Exception($"Invalid ClockMode '{rawMode}'");
        Clock.Mode = mode;

        // Step 3: before any path is built. Why: a remote risk desk has no S: drive; a relative DirectoriesRoot is beside this executable
        if (!string.IsNullOrEmpty(rawDirectoriesRoot))
            Context.DirectoriesRoot = Path.Combine(AppContext.BaseDirectory, rawDirectoriesRoot);

        // Step 4: one window per entry, a server workspace for an empty strategy name
        string serverName = ServerContext.GetDirectoryPath(rawServerName);
        List<WorkspaceRequest> workspaceRequests = new(workspaceConfigEntries.Length);
        foreach (WorkspaceConfigEntry workspaceConfigEntry in workspaceConfigEntries)
        {
            string clientName = string.IsNullOrEmpty(workspaceConfigEntry.StrategyName) ? serverName : ClientContext.GetDirectoryPath(workspaceConfigEntry.StrategyName);
            string workspacePath = string.IsNullOrEmpty(workspaceConfigEntry.WorkspaceName) ? string.Empty : Context.GetWorkspaceFilePath(clientName, workspaceConfigEntry.WorkspaceName);
            workspaceRequests.Add(new WorkspaceRequest(serverName, clientName, mode, workspacePath));
        }

        // Step 5: attach to the server (waits until it, or a Remote Mirror's copy of it, is up) and run its clock
        ContextManager.Initialize(serverName);
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

        LowLatency.StartBackgroundThread("Clock", () =>
        {
            Clock.Start();
        });

        // Step 6: the windows, on this thread until the last one closes
        WorkspaceRunner.RunOnThisThread(workspaceRequests);
    }
}