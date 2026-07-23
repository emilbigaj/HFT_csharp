using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tools;

/// <summary>
/// Cross-platform named mutex.
///   Linux:   flock(LOCK_EX) on /dev/shm/HFT_Lock_&lt;name&gt;
///   Windows: Mutex("Global\HFT_Lock_&lt;name&gt;")
/// Used to serialise CreateOrOpenShared against TryUnlinkIfOrphan for the same region.
/// </summary>
public sealed class MutexFlock : IDisposable
{
    private readonly IDisposable _inner;

    public MutexFlock(string name)
    {
        _inner = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsImpl(name)
            : new LinuxImpl(name);
    }

    public void Dispose() => _inner.Dispose();

    private sealed class WindowsImpl : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        public WindowsImpl(string name)
        {
            _mutex = new Mutex(false, "Global\\" + Memory.Namespace + Memory.LockInfix + name);
            try { _mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    private sealed class LinuxImpl : IDisposable
    {
        private const int O_RDWR = 2;
        private const int O_CREAT = 64;
        private const int LOCK_EX = 2;
        private const int LOCK_UN = 8;

        [DllImport("libc", SetLastError = true, EntryPoint = "open", CharSet = CharSet.Ansi)]
        private static extern int Open(string path, int flags, int mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int Close(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "flock")]
        private static extern int Flock(int fd, int op);

        private readonly int _fd;
        private bool _disposed;

        public LinuxImpl(string name)
        {
            string path = "/dev/shm/" + Memory.Namespace + Memory.LockInfix + name;
            _fd = Open(path, O_CREAT | O_RDWR, 0x1B6);   // 0666
            if (_fd == -1)
                throw new IOException($"MutexFlock: open {path} failed (errno={Marshal.GetLastWin32Error()})");
            if (Flock(_fd, LOCK_EX) == -1)
            {
                int e = Marshal.GetLastWin32Error();
                Close(_fd);
                throw new IOException($"MutexFlock: LOCK_EX on {path} failed (errno={e})");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Flock(_fd, LOCK_UN);
            Close(_fd);
        }
    }
}

/// <summary>
/// Page-backed memory region. Two flavours:
///   - Anonymous:    in-process private memory, no file, no name.
///   - Named shared: backed by /dev/hugepages/HFT_&lt;name&gt; or /dev/shm/HFT_&lt;name&gt; on Linux,
///                   or by a named MemoryMappedFile on Windows.
///
/// On Linux: hugepages are selected automatically when length >= HugePageLength. A flock LOCK_SH
/// is held for the life of the region; on Dispose the last holder takes LOCK_EX|LOCK_NB and
/// unlinks the backing file (unlink is what returns huge pages to the pool — munmap alone never
/// does). Call ReclaimOrphans() once at startup to clean up after crashed prior owners.
///
/// On Windows: the OS reference-counts the named mapping for us; no orphan files exist.
/// </summary>
public sealed class Memory : IDisposable
{
    public const int HugePageLength = 2 * 1024 * 1024;
    public const int SmallPageLength = 4 * 1024;
    public const int CacheLine = 64;
    public const string Namespace = "HFT_";
    public const string LockInfix = "Lock_";

    public unsafe byte* Ptr;
    public long Length;
    public bool Huge;
    public string Path = string.Empty;   // empty if anonymous or non-file-backed
    public string Name = string.Empty;   // sanitised key for MutexFlock

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private FileStream? _backingFs;      // Linux file-backed regions only (holds the LOCK_SH fd)
    private IntPtr _anonPtr;             // anonymous Windows allocations (NativeMemory) / Linux mmap base
    private bool _disposed;

    public Memory() { }

    // =========================================================================================
    // ALIGNMENT PRIMITIVE
    // =========================================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetAlignedLength(int length, int alignment) =>
        (length + alignment - 1) & ~(alignment - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetAlignedLength(long length, int alignment) =>
        (length + alignment - 1) & ~((long)alignment - 1);

    // =========================================================================================
    // FACTORIES
    // =========================================================================================

    public static unsafe Memory CreateAnonymous(long length)
    {
        EnsureMLocked();

        bool useHugePages = length >= HugePageLength;
        length = AlignLengthForPage(length, useHugePages);

        if (OperatingSystem.IsLinux())
        {
            int flags = MAP_PRIVATE | MAP_ANONYMOUS | MAP_POPULATE;
            if (useHugePages) flags |= MAP_HUGETLB;

            void* p = LinuxMmap(IntPtr.Zero, (nuint)length, PROT_READ | PROT_WRITE, flags, -1, 0);
            if ((nint)p == -1)
                throw new IOException($"Memory.CreateAnonymous: mmap failed (errno={Marshal.GetLastWin32Error()}). Hugepage region size={length} bytes.");

            Memory m = new Memory { Ptr = (byte*)p, Length = length, Huge = useHugePages, _anonPtr = (IntPtr)p };
            try { m.WarmUp(); }
            catch { m.Dispose(); throw; }
            return m;
        }
        else
        {
            byte* p = (byte*)NativeMemory.AlignedAlloc((nuint)length, CacheLine);
            if (p == null) throw new OutOfMemoryException();
            Memory m = new Memory { Ptr = p, Length = length, Huge = false, _anonPtr = (IntPtr)p };
            try { m.WarmUp(); }
            catch { m.Dispose(); throw; }
            return m;
        }
    }

    public static unsafe Memory CreateOrOpenShared(string name, long length)
    {
        EnsureMLocked();

        bool useHugePages = length >= HugePageLength;
        length = AlignLengthForPage(length, useHugePages);

        string lockName = name.Sanitize();
        if (lockName.StartsWith(LockInfix, StringComparison.Ordinal))
            throw new ArgumentException($"Memory.CreateOrOpenShared: region name must not start with '{LockInfix}' (reserved): {name}", nameof(name));

        if (OperatingSystem.IsWindows())
            return CreateOrOpenSharedWindows(lockName, length);
        return CreateOrOpenSharedLinux(lockName, length, useHugePages);
    }

    private static unsafe Memory CreateOrOpenSharedWindows(string lockName, long length)
    {
        string mmfName = Namespace + lockName;

        using (new MutexFlock(lockName))
        {
            MemoryMappedFile mmf = MemoryMappedFile.CreateOrOpen(mmfName, length, MemoryMappedFileAccess.ReadWrite);
            MemoryMappedViewAccessor view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            Memory m = new Memory
            {
                Ptr = ptr,
                Length = length,
                Huge = false,
                Name = lockName,
                _file = mmf,
                _view = view,
            };
            try { m.WarmUp(); }
            catch { m.Dispose(); throw; }
            return m;
        }
    }

    private static unsafe Memory CreateOrOpenSharedLinux(string lockName, long length, bool useHugePages)
    {
        string fileName = Namespace + lockName;
        string fullPath = (useHugePages ? "/dev/hugepages/" : "/dev/shm/") + fileName;

        // Drop any stale orphan from a crashed prior owner BEFORE taking the region lock.
        // Re-locking the same flock name within one process would deadlock.
        TryUnlinkIfOrphan(fullPath, lockName);

        // Hold the region lock across open -> LOCK_SH -> mmap so a concurrent reclaim cannot
        // unlink the file between our create and our shared lock (which would split two
        // openers onto different inodes).
        using (new MutexFlock(lockName))
        {
            FileStream fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.None);
            try
            {
                if (fs.Length < length)
                    fs.SetLength(length);

                int fd = (int)fs.SafeFileHandle.DangerousGetHandle();

                int sh;
                do { sh = LinuxFlock(fd, LOCK_SH); }
                while (sh == -1 && Marshal.GetLastWin32Error() == EINTR);
                if (sh == -1)
                    throw new IOException($"Memory.CreateOrOpenShared: LOCK_SH on {fullPath} failed (errno={Marshal.GetLastWin32Error()})");

                // leaveOpen=true: we keep the FileStream so we control LOCK_SH lifetime.
                MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(fs, null, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                MemoryMappedViewAccessor view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
                byte* ptr = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                Memory m = new Memory
                {
                    Ptr = ptr,
                    Length = length,
                    Huge = useHugePages,
                    Path = fullPath,
                    Name = lockName,
                    _file = mmf,
                    _view = view,
                    _backingFs = fs,
                };
                try { m.WarmUp(); }
                catch { m.Dispose(); throw; }
                return m;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
    }

    // =========================================================================================
    // LIFETIME
    // =========================================================================================

    public unsafe void WarmUp()
    {
        if (Ptr == null) return;

        if (OperatingSystem.IsLinux())
        {
            if (LinuxMadvise((IntPtr)Ptr, (nuint)Length, MADV_POPULATE_WRITE) == 0)
                return;
        }

        // Fallback: stride one Interlocked.Add(0) per page. Interlocked guarantees an atomic
        // RW cycle that faults the PTE without corrupting concurrent ring-buffer payloads.
        long stride = Huge ? HugePageLength : SmallPageLength;
        for (long i = 0; i < Length; i += stride)
            Interlocked.Add(ref Unsafe.AsRef<int>(Ptr + i), 0);
    }

    public unsafe void Clear()
    {
        if (Ptr != null)
            NativeMemory.Clear(Ptr, (nuint)Length);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        string path = Path;
        string name = Name;
        Path = string.Empty;
        Name = string.Empty;

        // Anonymous Linux: munmap.
        if (_file == null && _backingFs == null)
        {
            if (_anonPtr != IntPtr.Zero)
            {
                if (OperatingSystem.IsLinux())
                    LinuxMunmap((void*)_anonPtr, (nuint)Length);
                else
                    NativeMemory.AlignedFree((void*)_anonPtr);
                _anonPtr = IntPtr.Zero;
            }
            Ptr = null;
            return;
        }

        // File-backed: tear down view -> mmf -> fs (drops LOCK_SH on Linux), then reclaim if last.
        if (_view != null)
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            // SafeMemoryMappedViewHandle.Dispose() == munmap, no msync.
            // We deliberately don't call _view.Dispose() — it calls Flush() -> msync(MS_SYNC|MS_INVALIDATE),
            // which returns EBUSY on mlocked mappings.
            _view.SafeMemoryMappedViewHandle.Dispose();
            _view = null;
        }
        _file?.Dispose();
        _file = null;
        _backingFs?.Dispose();
        _backingFs = null;
        Ptr = null;

        if (string.IsNullOrEmpty(path)) return;

        // Best-effort reclaim. Dispose must not throw.
        try { TryUnlinkIfOrphan(path, name); }
        catch { }
    }

    ~Memory() { Dispose(); }

    // =========================================================================================
    // RECLAIM (Linux-only; no-op on Windows)
    // =========================================================================================

    /// <summary>
    /// Crash backstop. A process that died without running Dispose() leaves its backing file in
    /// place; the inode pins its pages out of the huge-page pool indefinitely. Call once on
    /// startup, BEFORE opening any shared memory, to unlink every orphan no live process still
    /// holds (across both /dev/hugepages and /dev/shm).
    /// </summary>
    public static void ReclaimOrphans(string prefix = Namespace)
    {
        if (!OperatingSystem.IsLinux()) return;
        ReclaimOrphansIn("/dev/hugepages", prefix);
        ReclaimOrphansIn("/dev/shm", prefix);
    }

    private static void ReclaimOrphansIn(string directory, string prefix)
    {
        if (!Directory.Exists(directory)) return;

        string lockPrefix = Namespace + LockInfix;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFiles(directory); }
        catch { return; }

        foreach (string filePath in entries)
        {
            string fileName = System.IO.Path.GetFileName(filePath);
            if (fileName.StartsWith(lockPrefix, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(prefix) && !fileName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!fileName.StartsWith(Namespace, StringComparison.Ordinal)) continue;

            string lockName = fileName.Substring(Namespace.Length);
            try { TryUnlinkIfOrphan(filePath, lockName); }
            catch { /* skip and keep sweeping */ }
        }
    }

    /// <summary>
    /// Unlink `path` iff no process still maps the region. A file is an orphan iff we can take
    /// LOCK_EX on it: every live mapper holds LOCK_SH for the life of its mapping, so an
    /// exclusive lock proves there are no mappers. The MutexFlock serialises us against a
    /// concurrent CreateOrOpenShared of the same name.
    /// </summary>
    private static bool TryUnlinkIfOrphan(string path, string lockName)
    {
        if (!OperatingSystem.IsLinux()) return false;

        using var regionLock = new MutexFlock(lockName);

        int fd = LinuxOpen(path, O_RDWR | O_CLOEXEC, 0);
        if (fd == -1) return false;

        try
        {
            if (LinuxFlock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                try { File.Delete(path); } catch { }
                LinuxFlock(fd, LOCK_UN);
                return true;
            }
        }
        finally
        {
            LinuxClose(fd);
        }
        return false;
    }

    // =========================================================================================
    // INTERNALS
    // =========================================================================================

    private static long AlignLengthForPage(long length, bool useHugePages)
    {
        int alignment = useHugePages ? HugePageLength : CacheLine;
        if (length <= 0 || length > long.MaxValue - alignment)
            throw new ArgumentOutOfRangeException(nameof(length));
        return GetAlignedLength(length, alignment);
    }

    // ---- mlock (run-once guard) ----

    private static class MLockGuard
    {
        public static readonly bool Done = DoMLock();

        private static bool DoMLock()
        {
            if (!OperatingSystem.IsLinux()) return true;
            int rc = LinuxMlockall(MCL_CURRENT | MCL_FUTURE);
            if (rc == 0)
                Console.WriteLine("Tools.Memory: mlockall success.");
            else
                Console.WriteLine($"Tools.Memory: mlockall failed (errno={Marshal.GetLastWin32Error()}). Check ulimits.");
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureMLocked() => _ = MLockGuard.Done;

    // ---- pinvoke (Linux only) ----

    private const int PROT_READ = 1;
    private const int PROT_WRITE = 2;
    private const int MAP_PRIVATE = 2;
    private const int MAP_ANONYMOUS = 32;
    private const int MAP_POPULATE = 0x8000;
    private const int MAP_HUGETLB = 0x40000;
    private const int LOCK_SH = 1;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const int EINTR = 4;
    private const int MADV_POPULATE_WRITE = 23;
    private const int MCL_CURRENT = 1;
    private const int MCL_FUTURE = 2;

    [DllImport("libc", SetLastError = true, EntryPoint = "mlockall")]
    private static extern int LinuxMlockall(int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "mmap")]
    private static extern unsafe void* LinuxMmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true, EntryPoint = "munmap")]
    private static extern unsafe int LinuxMunmap(void* addr, nuint length);

    [DllImport("libc", SetLastError = true, EntryPoint = "madvise")]
    private static extern int LinuxMadvise(IntPtr addr, nuint length, int advice);

    [DllImport("libc", SetLastError = true, EntryPoint = "open", CharSet = CharSet.Ansi)]
    private static extern int LinuxOpen(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int LinuxClose(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "flock")]
    private static extern int LinuxFlock(int fd, int op);
}
