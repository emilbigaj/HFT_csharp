//BEGIN_FILE HFT/Debug/Program.cs
using System;
using Provider;
using Tools;

// Dumps a server's entire shared-memory state to the console (see ServerContext.PrintDebug).
// Usage: Debug <ServerName> [Realtime|Simulation]
//   ServerName is a leaf name ("CME_NewRelease") or a full path; the mode defaults to Realtime
//   unless the path says otherwise, and must be set before the path is built (it names the root).
if (args.Length < 1 || args.Length > 2)
{
    Console.WriteLine("Usage: Debug <ServerName> [Realtime|Simulation]");
    return;
}

if (args.Length == 2 && Enum.TryParse(args[1], ignoreCase: true, out ClockMode mode))
    Clock.Mode = mode;
else
    Clock.Mode = args[0].Contains("Simulation") ? ClockMode.Simulation : ClockMode.Realtime;

bool isPath = args[0].Contains('/') || args[0].Contains('\\');
FileSystemPath serverName = isPath ? args[0] : ServerContext.GetDirectoryPath(args[0]);

ServerContext serverContext = new ServerContext(serverName, Access.Read);
serverContext.PrintDebug();
serverContext.Dispose();
//END_FILE HFT/Debug/Program.cs
