using Provider;
using System;
using System.Runtime.Versioning;
using System.Threading;
using Tools;

namespace Workspace;

internal class Program
{
    // Entry point. Parses command line arguments and delegates to WorkspaceRunner.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [STAThread]
    public static void Main(string[] args)
    { 
        string rawServerName = string.Empty;
        string rawClientName = string.Empty;
        string rawMode = string.Empty;
        string rawWorkspaceName = string.Empty;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-s")
                rawServerName = args[++i];
            else if (args[i] == "-c")
                rawClientName = args[++i];
            else if (args[i] == "-m")
                rawMode = args[++i];
            else if (args[i] == "-w")
                rawWorkspaceName = args[++i];
        }

        // Validation: Client and Workspace are optional, Server and Mode are required.
        if (string.IsNullOrEmpty(rawServerName) || string.IsNullOrEmpty(rawMode))
        {
            Console.WriteLine("Usage: Workspace -s <ServerName> -m <Mode> [-c <ClientName>] [-w <WorkspaceName>]");
            return;
        }



        ClockMode mode = rawMode.Equals("Realtime", StringComparison.OrdinalIgnoreCase)
            ? ClockMode.Realtime
            : rawMode.Equals("Simulation", StringComparison.OrdinalIgnoreCase) ? ClockMode.Simulation : throw new Exception($"Invalid ClockMode '{rawMode}'");

        Clock.Mode = mode;

        string serverName = ServerContext.GetDirectoryPath(rawServerName);
        string clientName = string.IsNullOrEmpty(rawClientName) ? serverName : ClientContext.GetDirectoryPath(rawClientName);

        // Resolve path based on whether we are a client or server instance
        string workspacePath = string.Empty;
        if (!string.IsNullOrEmpty(rawWorkspaceName))
        {
            workspacePath = Context.GetWorkspaceFilePath(clientName, rawWorkspaceName);
        }


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

        WorkspaceRunner.RunOnThisThread(serverName, clientName, mode, workspacePath);
    }
}