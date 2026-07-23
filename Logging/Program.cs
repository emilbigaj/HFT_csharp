using Strategy;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Logging;

public static class Program
{
    private static readonly ManualResetEventSlim s_quitEvent = new ManualResetEventSlim(false);

    public static void Main(string[] args)
    {
        try
        {
            string serverName = "";
            int hostPid = 0;
            bool shouldMonitorPid = false;

            // Argument Parsing
            if (args.Length == 0)
            {
                Console.WriteLine("Starting in standalone mode (no HostPID provided).");
                Console.WriteLine("===================================================");
                while(string.IsNullOrEmpty(serverName))
                {
                    Console.Write("Enter LoggingServerName: ");
                    serverName = Console.ReadLine() ?? "";
                }
            }
            else if (args.Length == 1)
            {
                serverName = args[0];
            }
            else if (args.Length >= 2)
            {
                serverName = string.Join(" ", args[..^1]);
                if (!int.TryParse(args[^1], out hostPid))
                {
                    Console.Error.WriteLine($"Invalid HostPID: {args[^1]}");
                    Console.ReadKey();
                    return;
                }
                shouldMonitorPid = true;
            }
            else
            {

                Console.Error.WriteLine("Usage: LoggingServer [ServerName] [HostPID]");
                Console.Error.WriteLine("Or run with no arguments for standalone mode.");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Starting LoggingServer: {serverName}...");

            // Start PID Monitor only if arguments were provided
            if (shouldMonitorPid)
            {
                Console.WriteLine($"Monitoring PID {hostPid}...");
                Thread monitorThread = new Thread(() => MonitorHostProcess(hostPid))
                {
                    IsBackground = true,
                    Name = "HostPIDMonitor"
                };
                monitorThread.Start();
            }

            LoggingServer server = new LoggingServer(serverName);

            server.Exception += ex =>
            {
                Console.Error.WriteLine($"LoggingServer exception: {ex}");
            };
            server.Start();

            // Termination handlers
            Console.CancelKeyPress += (_, e) =>
            {
                Console.WriteLine("Ctrl + C Pressed ... exiting.");
                e.Cancel = true;
                s_quitEvent.Set();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                s_quitEvent.Set();
            };

            Console.WriteLine("LoggingServer is now running.");

            // Diagnostic loop (prints ToString() ~1/sec)
            while (!s_quitEvent.IsSet)
            {
                try
                {
                    //string status = server.ToString();
                    //Console.WriteLine(status);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Diagnostic loop error: {ex}");
                }

                // Sleep ~1 second but still responsive to quit
                s_quitEvent.Wait(TimeSpan.FromSeconds(1));
            }

            Console.WriteLine("Shutting down LoggingServer...");
            server.Dispose();
            Console.WriteLine("Shutdown complete.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error in LoggingServer: {ex}");
            Console.ReadKey();
            return;
        }
        
    }

    private static void MonitorHostProcess(int pid)
    {
        try
        {
            using System.Diagnostics.Process hostProcess = System.Diagnostics.Process.GetProcessById(pid);
            while (!s_quitEvent.IsSet)
            {
                if (hostProcess.HasExited)
                {
                    Console.WriteLine($"Host process {pid} has exited. Initiating shutdown.");
                    s_quitEvent.Set();
                    return;
                }
                Thread.Sleep(100);
            }
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"Host process {pid} does not exist. Initiating shutdown.");
            s_quitEvent.Set();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Monitor thread error: {ex.Message}");
            s_quitEvent.Set();
        }
    }
}