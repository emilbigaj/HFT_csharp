using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Tools;

namespace Socket;

public sealed unsafe class LetterBox<T> : IDisposable where T : unmanaged
{
    public readonly string Name;
    public readonly Access Access;

    private bool _disposed;
    private SharedMemory _sharedMemory;
    private SharedMemoryView _view;
    private Protocol.Header64* _headerPtr;
    private T* _valuePtr;
    private SharedArrayEntry<T> _entry;

    public LetterBox(string name, Access access)
    {
        Name = name;
        Access = access;

        // 1. Calculate Required Dimensions
        int totalSize = Protocol.GetAlignedEntryLength(Unsafe.SizeOf<T>());

        // 2. Open Shared Memory and View
        _sharedMemory = SharedMemory.CreateOrOpen(Name + "LetterBox", totalSize);
        _view = _sharedMemory.GetView(0, totalSize, Access);

        // 3. Map Internal Pointers
        byte* basePtr = _view.GetPtr();
        _headerPtr = (Protocol.Header64*)basePtr;
        _valuePtr = (T*)(basePtr + Unsafe.SizeOf<Protocol.Header64>());
        _entry = new SharedArrayEntry<T>((byte*)_headerPtr, _view.Access);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 1. Dispose Resource Handles
        _view.Dispose();
        _sharedMemory.Dispose();

        // 2. Clear State
        _headerPtr = null;
        _valuePtr = null;
        _disposed = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireLock()
    {
        MultiSeqLockWriter.AcquireLock(ref _headerPtr->Sequence);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseLock()
    {
        MultiSeqLockWriter.ReleaseLock(ref _headerPtr->Sequence);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
    {
        return _headerPtr->Length == 0;
    }

    public ref T GetRef()
    {
        if (_disposed) throw new ObjectDisposedException(Name);
        if (_view.Access == Access.Read) throw new InvalidOperationException($"LetterBox '{Name}' is ReadOnly.");
        return ref Unsafe.AsRef<T>(_valuePtr);
    }

    public ref readonly T GetReadonlyRef()
    {
        if (_disposed) throw new ObjectDisposedException(Name);
        return ref Unsafe.AsRef<T>(_valuePtr);
    }

    public ref SharedArrayEntry<T> GetEntry()
    {
        if (_disposed) throw new ObjectDisposedException(Name);
        return ref _entry;
    }

    public bool TryPeek(out T value)
    {
        value = default;

        // 1. Validate Object Lifecycle
        if (_disposed) throw new ObjectDisposedException(Name);

        ref SharedArrayEntry<T> entry = ref GetEntry();

        while (true)
        {
            // 2. Read Sequence
            ulong seq0 = entry.GetSeq();

            // 3. Spin if Writer is Currently Active
            if (Protocol.IsWriteInProgress(seq0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            // 4. Safely Read Payload
            bool isEmpty = IsEmpty();
            value = Unsafe.Read<T>(_valuePtr);
            Thread.MemoryBarrier();

            // 5. Post-Read Sequence Validation
            ulong seq1 = entry.GetSeq();
            if (seq0 == seq1)
                return !isEmpty;
        }
    }

    public bool TryStore(in T value)
    {
        // 1. Verify Access Rights and Lifecycle State
        if (_view.Access == Access.Read) throw new InvalidOperationException($"LetterBox '{Name}' is ReadOnly.");
        if (_disposed) throw new ObjectDisposedException(Name);

        // 2. Acquire Sequence Lock (Multi-writer safe)
        AcquireLock();
        try
        {
            // 3. Abort if Already Full
            if (!IsEmpty())
            {
                return false;
            }

            // 4. Write Payload and Update Length
            *_valuePtr = value;
            _headerPtr->Magic = Protocol.s_magic;
            _headerPtr->Length = Unsafe.SizeOf<T>();
            return true;
        }
        finally
        {
            ReleaseLock();
        }
    }

    public bool TryEmpty(out T value)
    {
        value = default;

        // 1. Validate Lifecycle State
        if (_disposed) throw new ObjectDisposedException(Name);

        // 2. Lock-free early exit check (saves pounding the SeqLock)
        if (IsEmpty())
        {
            return false;
        }

        // 3. Acquire global SeqLock for Letterbox mutation
        AcquireLock();
        try
        {
            // 4. Double-check after acquiring lock
            if (IsEmpty())
            {
                return false;
            }

            // 5. Move payload out and mark as empty
            value = Unsafe.Read<T>(_valuePtr);
            _headerPtr->Magic = 0;
            _headerPtr->Length = 0;
            return true;
        }
        finally
        {
            ReleaseLock();
        }
    }
}
