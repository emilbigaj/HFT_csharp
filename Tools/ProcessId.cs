using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tools;

public static class Process
{
	public static bool Exists(string instanceName, out Mutex? mutex)
    {
        string mutexName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Global\\InstanceLock_" + instanceName
            : "InstanceLock_" + instanceName;

        mutex = new Mutex(false, mutexName);

        int result = WaitHandle.WaitAny(new[] { mutex }, 0, false);

        // If result is anything other than 0 or Abandoned, the instance is already running.
        if (result != 0 && result != 0x80)
        {
            mutex.Dispose();
            mutex = null;
            return true;
        }

        // result is 0 (Success) or 0x80 (Abandoned), meaning we now own the mutex.
        return false;
    }


    public static System.Diagnostics.Process? Start(string executableName, string[]? args = null, bool openConsole = false, string? workingDirectory = null)
    {
        string safeWorkingDirectory = workingDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        string exeName = OperatingSystem.IsWindows() ? $"{executableName}.exe" : executableName;
        string exePath = Path.Combine(safeWorkingDirectory, exeName);

        System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = openConsole,
            CreateNoWindow = !openConsole,
            WorkingDirectory = safeWorkingDirectory
        };

        if (File.Exists(exePath))
        {
            startInfo.FileName = exePath;

            if (args != null)
                foreach (string arg in args)
                    startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add(Path.Combine(safeWorkingDirectory, $"{executableName}.dll"));

            if (args != null)
                foreach (string arg in args)
                    startInfo.ArgumentList.Add(arg);
        }

        return System.Diagnostics.Process.Start(startInfo);
    }
}

public static class ProcessId
{
    // WINDOWS API --------------------------------------------------------------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // LINUX API ---------------------------------------------------------------
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private const int ESRCH = 3;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsAlive_Windows(pid);
        }
        else if (OperatingSystem.IsLinux())
        {
            return IsAlive_Linux(pid);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }


    // -------------------- WINDOWS --------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAlive_Windows(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return false;

        uint code;
        bool ok = GetExitCodeProcess(h, out code);
        CloseHandle(h);

        return ok && code == STILL_ACTIVE;
    }


    // -------------------- LINUX ----------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAlive_Linux(int pid)
    {
        int r = kill(pid, 0);

        if (r == 0)
            return true;

        return Marshal.GetLastWin32Error() != ESRCH;
    }
}
