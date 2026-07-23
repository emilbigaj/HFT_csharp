//BEGIN_FILE HFT/Tools/LowLatency.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tools;

public static class LowLatency
{
    public static int[] HouseKeepingCores = Platform.IsLinux ? new int[] { 0, 1, 2, 3 } : Enumerable.Range(0, Environment.ProcessorCount).ToArray();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr hProcess, out UIntPtr lpProcessAffinityMask, out UIntPtr lpSystemAffinityMask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);

    [DllImport("libc")]
    private static extern int getpid();

    // --- Signal Handling P/Invokes ---
    private const int SIG_BLOCK = 0;
    private const int SIGHUP = 1;   // Terminal closed
    private const int SIGINT = 2;   // Ctrl+C
    private const int SIGQUIT = 3;  // Ctrl+\ (Quit + Core Dump)
    private const int SIGTERM = 15; // Graceful termination request

    [DllImport("libc", SetLastError = true)]
    private static extern int pthread_sigmask(int how, ref sigset_t set, IntPtr oldset);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct sigset_t
    {
        // glibc uses a 1024-bit mask (16 * 64-bit words)
        public fixed ulong val[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int SchedPriority;
    }

    private const int s_threadPriorityNormal = 0;
    private const int s_threadPriorityTimeCritical = 15;
    private const uint s_realtimePriorityClass = 0x00000100;
    private const int s_schedFifo = 1;
    private const int s_fifoMaxPriority = 99;

    private static readonly object s_lock = new object();

    public static int CoreCount { get; } = GetCoreCount();

    /// <summary>
    /// Spawns a new thread that explicitly escapes the current thread's pinning and priority.
    /// By offloading the Start() call to the ThreadPool, the new thread natively inherits the 
    /// ThreadPool's normal priority and default OS affinity (which natively respects isolcpus).
    /// </summary>
    public static Thread StartBackgroundThread(string name, Action action)
    {
        using ManualResetEventSlim startedEvent = new ManualResetEventSlim(false);

        Thread thread = new Thread(() =>
        {
            PinCurrentThreadToCoreRange(HouseKeepingCores);
            SetThreadPriorityNormal();
            startedEvent.Set();
            action();
        });

        thread.Name = name;
        thread.IsBackground = true;

        Console.WriteLine("Starting background thread: " + name);
        thread.Start();

        startedEvent.Wait();

        return thread;
    }

    /// <summary>
    /// Blocks SIGINT and SIGTERM on the calling thread. 
    /// The Linux kernel will refuse to deliver these signals to this thread, 
    /// forcing them to be handled by unblocked housekeeping/main threads.
    /// </summary>
    public static unsafe void BlockInterruptSignalsCurrentThread()
    {
        if (!OperatingSystem.IsLinux())
            return;

        sigset_t set = new sigset_t();

        set.val[0] |= (1UL << (SIGHUP - 1));
        set.val[0] |= (1UL << (SIGINT - 1));
        set.val[0] |= (1UL << (SIGQUIT - 1));
        set.val[0] |= (1UL << (SIGTERM - 1));

        int result = pthread_sigmask(SIG_BLOCK, ref set, IntPtr.Zero);
        if (result != 0)
        {
            Console.WriteLine($"WARNING: Failed to set pthread_sigmask. Error: {result}");
        }
    }

    public static void PinCurrentThreadToCore(int core)
    {
        if (core < 0 || core >= CoreCount)
            throw new ArgumentOutOfRangeException(nameof(core));

        ApplyAffinityMask(1UL << core);
        BlockInterruptSignalsCurrentThread();
        SetThreadPriorityCritical();
        
    }

    public static void PinCurrentThreadToCoreRange(int[] cores)
    {
        if (cores == null || cores.Length == 0)
            throw new ArgumentException("Cores array cannot be null or empty.", nameof(cores));

        ulong mask = 0;

        for (int i = 0; i < cores.Length; i++)
        {
            int core = cores[i];

            if (core < 0 || core >= CoreCount)
                throw new ArgumentOutOfRangeException(nameof(cores), $"Invalid core index: {core}");

            mask |= (1UL << core);
        }

        ApplyAffinityMask(mask);
    }

    private static int GetCoreCount()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                string content = File.ReadAllText("/sys/devices/system/cpu/present").Trim();
                string[] parts = content.Split('-');
                int cores = int.Parse(parts[1]) + 1;

                return cores;
            }
            catch (Exception)
            {
                return Environment.ProcessorCount;
            }
        }

        return Environment.ProcessorCount;
    }

    private static void ApplyAffinityMask(ulong mask)
    {
        try
        {
            lock (s_lock)
            {
                if (OperatingSystem.IsWindows())
                {
                    IntPtr hThread = GetCurrentThread();
                    UIntPtr windowsMask = (UIntPtr)mask;

                    if (SetThreadAffinityMask(hThread, windowsMask) == UIntPtr.Zero)
                        throw new InvalidOperationException("Failed to set affinity on Windows.");
                }
                else if (OperatingSystem.IsLinux())
                {
                    IntPtr size = (IntPtr)8;

                    if (sched_setaffinity(0, size, ref mask) != 0)
                        throw new InvalidOperationException($"Failed to set affinity on Linux. Error: {Marshal.GetLastPInvokeError()}");
                }
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine($"LowLatency::ApplyAffinityMask({mask}) Failed to set thread affinity{Environment.NewLine}{ex.Message}");
        }

    }

    private static int[] DecodeMask(ulong mask)
    {
        List<int> cores = new List<int>();

        for (int i = 0; i < CoreCount; i++)
        {
            if ((mask & (1UL << i)) != 0)
                cores.Add(i);
        }

        return cores.ToArray();
    }

    public static void SetThreadPriorityCritical()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!SetPriorityClass(GetCurrentProcess(), s_realtimePriorityClass))
                    throw new InvalidOperationException($"Failed to set REALTIME_PRIORITY_CLASS. Error: {Marshal.GetLastPInvokeError()}");

                if (!SetThreadPriority(GetCurrentThread(), s_threadPriorityTimeCritical))
                    throw new InvalidOperationException($"Failed to set THREAD_PRIORITY_TIME_CRITICAL. Error: {Marshal.GetLastPInvokeError()}");
            }
            else if (OperatingSystem.IsLinux())
            {
                SchedParam parameter = new SchedParam();
                parameter.SchedPriority = s_fifoMaxPriority;

                if (sched_setscheduler(0, s_schedFifo, ref parameter) != 0)
                    throw new InvalidOperationException($"Failed to set SCHED_FIFO priority on Linux. Error: {Marshal.GetLastPInvokeError()}. Ensure you are running as root or have CAP_SYS_NICE.");
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine($"LowLatency::SetThreadPriorityCritical() Failed to set thread priority to critical{Environment.NewLine}{ex.Message}");
        }

    }

    public static void SetThreadPriorityNormal()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadPriority(GetCurrentThread(), s_threadPriorityNormal);
            }
            else if (OperatingSystem.IsLinux())
            {
                SchedParam parameter = new SchedParam();
                parameter.SchedPriority = 0;

                sched_setscheduler(0, 0, ref parameter);
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine($"LowLatency::SetThreadPriorityNormal() Failed to set thread priority to normal{Environment.NewLine}{ex.Message}");
        }
    }
}
//END_FILE HFT/Tools/LowLatency.cs