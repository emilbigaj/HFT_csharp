//BEGIN_FILE HFT/Socket/SharedMemory.cs
using System;
using Tools;

namespace Socket;

/// <summary>
/// Window into a shared memory region. Holds only a pointer + length; the underlying
/// mapping is owned by Tools.Memory and lives for the life of the parent SharedMemory.
/// </summary>
public sealed class SharedMemoryView : IDisposable
{
    public unsafe byte* Ptr;
    public readonly long Length;
    public readonly Access Access;
    public bool IsDisposed;

    public unsafe SharedMemoryView(byte* ptr, long length, Access access)
    {
        Ptr = ptr;
        Length = length;
        Access = access;
    }

    public unsafe byte* GetPtr()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(SharedMemoryView));
        return Ptr;
    }

    public void Dispose()
    {
        // The underlying mapping is owned by Tools.Memory (via SharedMemory). Disposing a
        // view doesn't free anything — only marks it logically unusable.
        IsDisposed = true;
    }
}

/// <summary>
/// Thin adapter that exposes a windowed-view API over a Tools.Memory region. The page
/// mapping, hugepage selection, flock refcount + orphan reclaim (Linux), and named MMF
/// lifecycle (Windows) all live in Tools.Memory.
/// </summary>
public sealed class SharedMemory : IDisposable
{
    // Forwarders kept for source compatibility — prefer Tools.Memory.X at new call sites.
    public const int HugePageLength = Tools.Memory.HugePageLength;
    public const int SmallPageLength = Tools.Memory.SmallPageLength;

    private readonly Tools.Memory _memory;
    private bool _disposed;

    private SharedMemory(Tools.Memory memory) { _memory = memory; }

    public static SharedMemory CreateOrOpen(string name, long length) =>
        new SharedMemory(Tools.Memory.CreateOrOpenShared(name, length));

    public static void ReclaimOrphans(string prefix = Tools.Memory.Namespace) =>
        Tools.Memory.ReclaimOrphans(prefix);

    public unsafe byte* Ptr => _memory.Ptr;
    public long Length => _memory.Length;

    public unsafe SharedMemoryView GetView(long offset, long length, Access access)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SharedMemory));
        if (offset < 0 || length < 0 || offset + length > _memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length), $"SharedMemoryView out of bounds (offset={offset}, length={length}, region={_memory.Length})");

        return new SharedMemoryView(_memory.Ptr + offset, length, access);
    }

    public void Clear() => _memory.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
    }
}
//END_FILE HFT/Socket/SharedMemory.cs
