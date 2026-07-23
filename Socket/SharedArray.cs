using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;

namespace Socket;

public unsafe struct SharedArrayEntry<T> where T : unmanaged
{
    private readonly Protocol.Header64* _headerPtr;
    private readonly Access _access;
    private ulong _seq;

    public readonly byte* GetEntryPtr() => (byte*)_headerPtr;

    internal SharedArrayEntry(byte* entryPtr, Access access)
    {
        _headerPtr = (Protocol.Header64*)entryPtr;
        _access = access;
        _seq = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEmpty()
    {
        return Protocol.ReadSequence(_headerPtr) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* GetPtr()
    {
        return Protocol.GetValuePointer<T>(GetEntryPtr());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly TCast* GetPtr<TCast>() where TCast : unmanaged
    {
        int castSize = Unsafe.SizeOf<TCast>();
        int storedSize = Unsafe.SizeOf<T>();

        if (castSize > storedSize)
        {
            throw new InvalidCastException($"Invalid cast: {typeof(TCast).Name} > {typeof(T).Name}.");
        }

        return Protocol.GetValuePointer<TCast>(GetEntryPtr());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef()
    {
        if (_access == Access.Read) throw new InvalidOperationException("Readonly");
        return ref Unsafe.AsRef<T>(GetPtr());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadonlyRef()
    {
        return ref Unsafe.AsRef<T>(GetPtr());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TCast GetRef<TCast>() where TCast : unmanaged
    {
        if (_access == Access.Read) throw new InvalidOperationException("Readonly");
        return ref Unsafe.AsRef<TCast>(GetPtr<TCast>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TCast GetReadonlyRef<TCast>() where TCast : unmanaged
    {
        return ref Unsafe.AsRef<TCast>(GetPtr<TCast>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(out T value)
    {
        ReadStatus readStatus = Protocol.TryRead(_headerPtr, out value, ref _seq);
        return readStatus;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadStatus TryRead(Span<byte> dstObj, out ReadOnlySpan<byte> rdstObj)
    {
        fixed (byte* dstp = dstObj)
        {
            ReadStatus readStatus = Protocol.TryRead(_headerPtr, dstp, dstObj.Length, out int srcObjLen, ref _seq);
            rdstObj = dstObj.Slice(0, srcObjLen);
            return readStatus;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> Read(Span<byte> dstObj)
    {
        ReadStatus status = TryRead(dstObj, out ReadOnlySpan<byte> rdstObj);
        if (status == ReadStatus.Empty) throw new InvalidOperationException("Slot is empty.");
        return rdstObj;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read()
    {
        // 1. Delegate to lock-free memory read
        ReadStatus readStatus = Protocol.TryRead(_headerPtr, out T value, ref _seq);
        if (readStatus == ReadStatus.Empty)
        {
            // 2. Throw if Slot was Empty
            throw new InvalidOperationException("Slot is empty.");
        }
        return value;
    }

    public void AcquireLock()
    {
        Protocol.AcquireLock(_headerPtr);
    }

    public void ReleaseLock()
    {
        Protocol.ReleaseLock(_headerPtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(in T value)
    {
        // 1. Ensure caller has permission to write
        if (_access == Access.Read)
            throw new InvalidOperationException("Entry is read-only.");

        // 2. Execute memory block write
        int dstLen = Protocol.HeaderLength + Unsafe.SizeOf<T>();
        Protocol.Write(in value, _headerPtr, dstLen);
    }

    // Recovery write for a slot whose client process is confirmed dead (Server.CancelAllOrders):
    // bypasses the Read access tag (the OS mapping is always ReadWrite; Access is a software-only
    // tag) and re-bases the seqlock so a slot the client left mid-write (odd sequence) recovers cleanly.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecoveryWrite(in T value)
    {
        int dstLen = Protocol.HeaderLength + Unsafe.SizeOf<T>();
        Protocol.RecoveryWrite(in value, _headerPtr, dstLen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetSeq()
    {
        return Protocol.ReadSequence(_headerPtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsNew()
    {
        return Protocol.IsThisNewerThan(GetSeq(), _seq);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SharedArrayEntry<TCast> Cast<TCast>() where TCast : unmanaged
    {
        return new SharedArrayEntry<TCast>(GetEntryPtr(), _access);
    }
}

public abstract class SharedArray : IDisposable
{
    public abstract string Name { get; }
    public abstract int Capacity { get; }
    public abstract Access Access { get; }
    public abstract ReadOnlySpan<byte> Read(int index, Span<byte> dstObj);
    public abstract ReadStatus TryRead(int index, Span<byte> dstObj, out ReadOnlySpan<byte> rdstObj);

    public abstract int TypeSize { get; }
    public abstract bool IsEmpty(int index);

    public abstract ulong GetSeq(int index);

    public abstract void Write(int index, ReadOnlySpan<byte> src);

    public abstract void Dispose();
    public abstract bool IsDense { get; }


}

public sealed class SharedArray<T> : SharedArray where T : unmanaged
{
    public override string Name { get; }
    public override int Capacity { get; }
    public override Access Access { get; }
    public override int TypeSize { get; }
    public override bool IsDense { get; }


    private readonly int _entryLength;
    private readonly SharedArrayEntry<T>[] _entries;
    private readonly SharedMemory _mmf;
    private readonly SharedMemoryView _view;
    private unsafe byte* _basePtr;

    public SharedArray(string name, int capacity, Access access, bool isDense = true)
    {
        Name = name;
        Capacity = capacity;
        Access = access;
        TypeSize = Unsafe.SizeOf<T>();
        IsDense = isDense;

        // 1. Capacity Check
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        // 2. Calculate Aligned Entry Size
        _entryLength = Protocol.GetAlignedEntryLength(Unsafe.SizeOf<T>());
        long fileLength = checked((long)_entryLength * capacity);

        // 3. Create or Open Backing Shared Memory and View
        _mmf = SharedMemory.CreateOrOpen(name, fileLength);
        _view = _mmf.GetView(0, fileLength, access);

        unsafe
        {
            _basePtr = _view.GetPtr();

            // 4. Map Individual Entries to Memory Offsets
            _entries = new SharedArrayEntry<T>[capacity];
            for (int i = 0; i < capacity; i++)
            {
                byte* entryPtr = _basePtr + ((long)i * _entryLength);
                _entries[i] = new SharedArrayEntry<T>(entryPtr, access);
            }
        }
    }

    public ref SharedArrayEntry<T> this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref GetEntry(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SharedArrayEntry<T> GetEntry(int index)
    {
        if ((uint)index >= (uint)Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return ref _entries[index];
    }

    // --- Non-generic byte-level overrides for the mirror ---
    public override ReadStatus TryRead(int index, Span<byte> dstObj, out ReadOnlySpan<byte> rdstObj)
    {
        return GetEntry(index).TryRead(dstObj, out rdstObj);
    }

    public override ReadOnlySpan<byte> Read(int index, Span<byte> dstObj)
    {
        return GetEntry(index).Read(dstObj);
    }


    public override void Write(int index, ReadOnlySpan<byte> srcObj)
    {
        if (srcObj.Length < Unsafe.SizeOf<T>())
            throw new ArgumentException($"SharedArray<{typeof(T).Name}>.Write: srcObj is {srcObj.Length} bytes, must be {TypeSize} bytes.", nameof(srcObj));
        ref readonly T obj = ref MemoryMarshal.AsRef<T>(srcObj);
        GetEntry(index).Write(in obj);
    }

    public override bool IsEmpty(int index)
    {
        return GetEntry(index).IsEmpty();
    }

    public override ulong GetSeq(int index)
    {
        return GetEntry(index).GetSeq();
    }

    public override void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
        unsafe { _basePtr = null; }
    }
}
