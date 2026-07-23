using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tools;

/// <summary>
/// Ultra-fast single-threaded fixed-size ring queue over unmanaged T.
/// Layout: capacity * sizeof(T) bytes, power-of-two capacity, 64B-aligned slab.
/// Zero-copy: enqueue in place (ref), peek in place (span len=1 -> ref), dequeue without moves.
/// </summary>
[SkipLocalsInit]
public unsafe sealed class Queue<T> : IDisposable
    where T : unmanaged
{
    private const nuint s_defaultAlign = 64;

    private readonly byte* _base;          // aligned slab
    private readonly nuint _capacity;      // number of T slots (power of two)
    private readonly nuint _mask;          // capacity - 1
    private readonly nuint _elemSize;      // sizeof(T)

    private nuint _wseq;                   // write index (monotonic, in elements)
    private nuint _rseq;                   // read  index (monotonic, in elements)
    private bool _disposed;

    /// <summary>Create a ring with power-of-two element capacity (>= 1).</summary>
    public Queue(int capacity)
    {
        if (capacity <= 0 || ((capacity & (capacity - 1)) != 0))
            throw new ArgumentOutOfRangeException(nameof(capacity), "Power of two, >= 1.");

        _capacity = (nuint)capacity;
        _mask = (nuint)capacity - 1u;
        _elemSize = (nuint)Unsafe.SizeOf<T>();

        // allocate aligned slab
        nuint bytes = checked(_capacity * _elemSize);
        _base = (byte*)NativeMemory.AlignedAlloc(bytes, s_defaultAlign);
        if (_base is null) throw new OutOfMemoryException();

        _wseq = 0u;
        _rseq = 0u;
    }

    /// <summary>Total element capacity.</summary>
    public int Capacity => checked((int)_capacity);

    /// <summary>Current element count.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => checked((int)(_wseq - _rseq));
    }

    /// <summary>True if queue has no elements.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _wseq == _rseq;
    }

    /// <summary>True if queue is full.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_wseq - _rseq) == _capacity;
    }

    /// <summary>Drop all elements (invalidates any outstanding refs/spans).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() { _rseq = _wseq; }

    /// <summary>Free unmanaged memory.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        NativeMemory.AlignedFree(_base);
        _disposed = true;
    }

    // ============================= ENQUEUE (in place) =============================

    /// <summary>
    /// Reserve + commit one element slot and return a writable reference to it (throws if full or disposed).
    /// Write your value directly into the returned ref.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetEnqueueRef()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Queue<T>));
        nuint wseq = _wseq;
        nuint rseq = _rseq;

        // Full? (used == capacity)
        if ((wseq - rseq) == _capacity)
            throw new InsufficientMemoryException("Queue is full.");

        nuint woff = wseq & _mask;
        byte* p = _base + (woff * _elemSize);
        _wseq = wseq + 1u; // commit immediately
        return ref Unsafe.AsRef<T>(p);
    }

    /// <summary>
    /// Non-throwing in-place enqueue. Returns a span of length 1 referencing the slot (use MemoryMarshal.GetReference to get ref T).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetEnqueueRef(out Span<T> slot)
    {
        if (_disposed) { slot = Span<T>.Empty; return false; }

        nuint wseq = _wseq;
        if ((wseq - _rseq) == _capacity)
        {
            slot = Span<T>.Empty;
            return false;
        }

        nuint woff = wseq & _mask;
        byte* p = _base + (woff * _elemSize);
        _wseq = wseq + 1u;

        slot = new Span<T>(p, 1);
        return true;
    }

    // ============================= PEEK (in place) =============================

    /// <summary>
    /// Peek the front element without consuming it. Returns a span of length 1 aliasing the front slot.
    /// Use <c>ref var x = ref MemoryMarshal.GetReference(span);</c> to get a byref without copying.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out ReadOnlySpan<T> span)
    {
        if (_disposed || _rseq == _wseq)
        {
            span = ReadOnlySpan<T>.Empty;
            return false;
        }

        nuint roff = _rseq & _mask;
        byte* p = _base + (roff * _elemSize);
        span = new ReadOnlySpan<T>(p, 1);
        return true;
    }

    /// <summary>
    /// Peek the front element as a writable ref (throws if empty). Use carefully; mutation changes the queued value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T PeekRef()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Queue<T>));
        if (_rseq == _wseq) throw new InvalidOperationException("Queue is empty.");

        nuint roff = _rseq & _mask;
        byte* p = _base + (roff * _elemSize);
        return ref Unsafe.AsRef<T>(p);
    }

    // ============================= DEQUEUE =============================

    /// <summary>
    /// Consume the front element (throws if empty). The element memory will be reused on future enqueues.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dequeue()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Queue<T>));
        if (_rseq == _wseq) throw new InvalidOperationException("Queue is empty.");
        _rseq = _rseq + 1u;
    }

    /// <summary>
    /// Non-throwing dequeue; returns false if empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue()
    {
        if (_disposed || _rseq == _wseq) return false;
        _rseq = _rseq + 1u;
        return true;
    }
}
