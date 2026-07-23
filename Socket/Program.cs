//BEGIN_FILE HFT/Socket/Program.cs
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Socket;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PingMessage
{
    public long SentTimestamp;
    public long SequenceId;
}

public sealed class Program
{
    
    private const string ServerName = "PingPongServer";
    private const string ClientName = "PingPongClient";
    private const int Iterations = 1_00000_000;

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--client")
        {
            RunClient();
        }
        else
        {
            RunServer();
        }
    }

    private static void RunServer()
    {
        Console.WriteLine($"[Server] Starting Host Process {Environment.ProcessId}...");

        using (ServerSocket server = new ServerSocket(ServerName, 64))
        {
            using (ManualResetEventSlim connectedEvent = new ManualResetEventSlim(false))
            {
                server.ClientAllocated += delegate (in SocketHeader socketHeader)
                {
                    connectedEvent.Set();
                };

                server.Listen();

                string? currentPath = Environment.ProcessPath;
                if (currentPath == null)
                {
                    Console.WriteLine("[Server] Fatal: Could not determine ProcessPath.");
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = currentPath,
                    Arguments = "--client",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                using (Process? childProcess = Process.Start(startInfo))
                {
                    if (childProcess == null)
                    {
                        Console.WriteLine("[Server] Fatal: Failed to start child process.");
                        return;
                    }

                    Console.WriteLine($"[Server] Spawned Client (PID: {childProcess.Id}). Waiting for connection...");

                    if (!connectedEvent.Wait(TimeSpan.FromSeconds(5)))
                    {
                        Console.WriteLine("[Server] Timeout waiting for client connection.");
                        childProcess.Kill();
                        return;
                    }

                    Console.WriteLine("[Server] Client connected. Starting Ping-Pong benchmark...");

                    int clientId = 0;
                    PingMessage msg = new PingMessage
                    {
                        SentTimestamp = 0,
                        SequenceId = 0
                    };

                    Stopwatch sw = new Stopwatch();
                    long minTicks = long.MaxValue;
                    long maxTicks = 0;
                    long totalTicks = 0;

                    for (int i = 0; i < 1000; i++)
                    {
                        PerformPingPong(server, clientId, ref msg);
                    }

                    sw.Start();

                    for (int i = 0; i < Iterations; i++)
                    {
                        long start = Stopwatch.GetTimestamp();

                        PerformPingPong(server, clientId, ref msg);

                        long end = Stopwatch.GetTimestamp();
                        long elapsed = end - start;

                        if (elapsed < minTicks)
                        {
                            minTicks = elapsed;
                        }

                        if (elapsed > maxTicks)
                        {
                            maxTicks = elapsed;
                        }

                        totalTicks += elapsed;
                    }

                    sw.Stop();

                    double totalSeconds = sw.Elapsed.TotalSeconds;
                    double avgLatencyNs = (totalSeconds * 1_000_000_000.0) / Iterations;
                    double msgsPerSec = Iterations / totalSeconds;
                    double frequency = Stopwatch.Frequency;
                    double avgTicks = (double)totalTicks / Iterations;

                    Console.WriteLine("--------------------------------------------------");
                    Console.WriteLine($"[Server] Completed {Iterations:N0} round-trips in {totalSeconds:F4}s");
                    Console.WriteLine($"[Server] Throughput : {msgsPerSec:N0} msg/s");
                    Console.WriteLine($"[Server] Latency    : {avgLatencyNs:F2} ns (avg)");
                    Console.WriteLine($"[Server]              {(minTicks * 1_000_000_000.0 / frequency):F2} ns (min)");
                    Console.WriteLine($"[Server]              {(maxTicks * 1_000_000_000.0 / frequency):F2} ns (max)");
                    Console.WriteLine("--------------------------------------------------");
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PerformPingPong(ServerSocket server, int clientId, ref PingMessage msg)
    {
        msg.SequenceId++;

        server.Write(clientId, SocketChannel.Admin, in msg);

        ReadOnlySpan<byte> response;

        while (true)
        {
            ReadStatus status = server.TryRead(clientId, SocketChannel.Admin, out response);

            if (status == ReadStatus.New)
            {
                break;
            }
            else if (status == ReadStatus.Closed)
            {
                throw new InvalidOperationException("Client disconnected unexpectedly.");
            }
        }
    }

    private static void RunClient()
    {
        try
        {
            using (ClientSocket client = new ClientSocket(ClientName, ServerName, new int[] { 4096 }, new int[] { 4096 }))
            {
                client.Connect();

                ReadOnlySpan<byte> data;

                while (true)
                {
                    ReadStatus status = client.TryRead(SocketChannel.Admin, out data);

                    if (status == ReadStatus.New)
                    {
                        client.Write(SocketChannel.Admin, data);
                    }
                    else if (status == ReadStatus.Closed)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Client] Error: {ex.Message}");
        }
    }
}
//END_FILE HFT/Socket/Program.cs