using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools;

/// <summary>
/// Seqlock-guarded min-heap priority queue optimized for hot paths.
/// - Backed by ArrayPool&lt;T&gt; (no per-op allocations; growth rents).
/// - Stable ordering for equal priorities via enqueue sequence.
/// - Writers serialized via SeqLock; readers (TryPeek) are optimistic with epoch validation.
/// - Slots/buffers are cleared only when the element type holds references,
///   avoiding unnecessary work for pure value types.
/// </summary>
public sealed class LockedPriorityQueue<TPriority, TValue> : IDisposable
    where TPriority : IComparable<TPriority>
{
    private static readonly System.Buffers.ArrayPool<TPriority> PoolP = System.Buffers.ArrayPool<TPriority>.Shared;
    private static readonly System.Buffers.ArrayPool<TValue> PoolV = System.Buffers.ArrayPool<TValue>.Shared;
    private static readonly System.Buffers.ArrayPool<ulong> PoolO = System.Buffers.ArrayPool<ulong>.Shared;

    // Clear policies per array type
    private static readonly bool ClearPriorities = RuntimeHelpers.IsReferenceOrContainsReferences<TPriority>();
    private static readonly bool ClearValues = RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
    // ulong never needs clearing for GC purposes

    private TPriority[] priorities;
    private TValue[] values;
    private ulong[] enqueueOrder;

    private int count;
    private ulong orderCounter;
    private bool disposed;

    private readonly ISeqLockWriter SeqLockWriter; // protects structural mutations & logical state

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref count);
    }

    /// <param name="initialCapacity">Min heap capacity to rent initially.</param>
    /// <param name="isMultiWriter">true = CAS-based writer; false = single-writer for lower overhead.</param>
    public LockedPriorityQueue(bool isMultiWriter = true, int initialCapacity = 16)
    {
        if (initialCapacity < 1) initialCapacity = 1;

        priorities = PoolP.Rent(initialCapacity);
        values = PoolV.Rent(initialCapacity);
        enqueueOrder = PoolO.Rent(initialCapacity);

        count = 0;
        orderCounter = 0;
        disposed = false;

        SeqLockWriter = isMultiWriter ? new MultiSeqLockWriter() : new SingleSeqLockWriter();
    }

    // ============================= Core heap ops (in-place) =============================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity()
    {
        if (count < priorities.Length) return;
        Grow(priorities.Length << 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Grow(int newCapacity)
    {
        // Rent new arrays
        var newP = PoolP.Rent(newCapacity);
        var newV = PoolV.Rent(newCapacity);
        var newO = PoolO.Rent(newCapacity);

        // Copy current contents
        int n = count;
        Array.Copy(priorities, 0, newP, 0, n);
        Array.Copy(values, 0, newV, 0, n);
        Array.Copy(enqueueOrder, 0, newO, 0, n);

        // Return old arrays to the pool with type-aware clearing
        PoolP.Return(priorities, clearArray: ClearPriorities);
        PoolV.Return(values, clearArray: ClearValues);
        PoolO.Return(enqueueOrder, clearArray: false);

        // Switch references
        priorities = newP;
        values = newV;
        enqueueOrder = newO;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Compare(int i, int j)
    {
        int c = priorities[i].CompareTo(priorities[j]);
        if (c == 0) c = enqueueOrder[i].CompareTo(enqueueOrder[j]); // earlier enqueue wins
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Swap(int i, int j)
    {
        // Manual swap to avoid tuple deconstruction overhead
        TPriority tp = priorities[i]; priorities[i] = priorities[j]; priorities[j] = tp;
        TValue tv = values[i]; values[i] = values[j]; values[j] = tv;
        ulong to = enqueueOrder[i]; enqueueOrder[i] = enqueueOrder[j]; enqueueOrder[j] = to;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BubbleUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (Compare(index, parent) >= 0) break;
            Swap(index, parent);
            index = parent;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BubbleDown(int index)
    {
        int c = count;
        while (true)
        {
            int left = (index << 1) + 1;
            if (left >= c) break;

            int right = left + 1;
            int smallest = (right < c && Compare(right, left) < 0) ? right : left;

            if (Compare(index, smallest) <= 0) break;
            Swap(index, smallest);
            index = smallest;
        }
    }

    // ============================= Public API (writers) =============================

    /// <summary>Insert an item into the queue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(TPriority priority, TValue value)
    {
        EnsureNotDisposed();

        SeqLockWriter.BeginWrite();
        try
        {
            EnsureCapacity();

            int i = count;
            priorities[i] = priority;
            values[i] = value;
            enqueueOrder[i] = orderCounter++; // unchecked wrap is fine for practical horizons

            BubbleUp(i);
            count = i + 1;
        }
        finally { SeqLockWriter.EndWrite(); }
    }

    /// <summary>Remove the top (min) item; returns false if empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out TPriority priority, out TValue value)
    {
        EnsureNotDisposed();

        SeqLockWriter.BeginWrite();
        try
        {
            if (count == 0)
            {
                priority = default!;
                value = default!;
                return false;
            }

            priority = priorities[0];
            value = values[0];

            int last = --count;
            if (last > 0)
            {
                priorities[0] = priorities[last];
                values[0] = values[last];
                enqueueOrder[0] = enqueueOrder[last];

                if (ClearPriorities) priorities[last] = default!;
                if (ClearValues) values[last] = default!;

                BubbleDown(0);
            }
            else
            {
                // We just removed the last element
                if (ClearPriorities) priorities[0] = default!;
                if (ClearValues) values[0] = default!;
            }

            return true;
        }
        finally { SeqLockWriter.EndWrite(); }
    }

    /// <summary>
    /// Removes the first occurrence of <paramref name="value"/> from the queue (by EqualityComparer&lt;TValue&gt;.Default).
    /// Returns true if an element was found and removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(TValue value)
    {
        EnsureNotDisposed();

        SeqLockWriter.BeginWrite();
        try
        {
            int idx = IndexOfValue(value);
            if (idx < 0) return false;

            RemoveAt(idx);
            return true;
        }
        finally { SeqLockWriter.EndWrite(); }
    }

    /// <summary>Clears logical contents. Does not shrink or return buffers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        EnsureNotDisposed();

        SeqLockWriter.BeginWrite();
        try
        {
            if (count > 0)
            {
                if (ClearPriorities) Array.Clear(priorities, 0, count);
                if (ClearValues) Array.Clear(values, 0, count);
                // enqueueOrder does not need clearing
            }
            count = 0;
            orderCounter = 0; // keep stable FIFO within a new episode
        }
        finally { SeqLockWriter.EndWrite(); }
    }

    /// <summary>Return rented arrays to the pool. After Dispose, the instance must not be used.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // No need to take the lock for disposal; instance is dead for concurrent use.
        PoolP.Return(priorities, clearArray: ClearPriorities);
        PoolV.Return(values, clearArray: ClearValues);
        PoolO.Return(enqueueOrder, clearArray: false);

        // Poison references to catch accidental reuse in debug
        priorities = Array.Empty<TPriority>();
        values = Array.Empty<TValue>();
        enqueueOrder = Array.Empty<ulong>();
        count = 0;
    }

    // ============================= Public API (reader) =============================

    /// <summary>Peek the top (min) item without removing it; returns false if empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out TPriority priority, out TValue value)
    {
        EnsureNotDisposed();

        while (true)
        {
            // Phase 1: optimistic snapshot
            ulong s0 = SeqLockReader.Read(in SeqLockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            int n = Volatile.Read(ref count);
            if (n == 0)
            {
                // Revalidate empty state
                if (SeqLockReader.Validate(s0, in SeqLockWriter.SeqRef))
                {
                    priority = default!;
                    value = default!;
                    return false;
                }
                X86BaseWrapper.Pause();
                continue;
            }

            // Copy candidates
            TPriority p = priorities[0];
            TValue v = values[0];

            // Validate snapshot
            if (SeqLockReader.Validate(s0, in SeqLockWriter.SeqRef))
            {
                priority = p;
                value = v;
                return true;
            }

            X86BaseWrapper.Pause();
        }
    }

    // ============================= Internals (writers only) =============================

    private static readonly EqualityComparer<TValue> CmpV = EqualityComparer<TValue>.Default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int IndexOfValue(TValue value)
    {
        int n = count;
        for (int i = 0; i < n; i++)
            if (CmpV.Equals(values[i], value)) return i;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveAt(int index)
    {
        int last = --count;

        if (index == last)
        {
            if (ClearPriorities) priorities[index] = default!;
            if (ClearValues) values[index] = default!;
            return;
        }

        // Move last into hole
        priorities[index] = priorities[last];
        values[index] = values[last];
        enqueueOrder[index] = enqueueOrder[last];

        if (ClearPriorities) priorities[last] = default!;
        if (ClearValues) values[last] = default!;

        // Restore heap property (either bubble up or down)
        int parent = (index - 1) >> 1;
        if (index > 0 && Compare(index, parent) < 0)
            BubbleUp(index);
        else
            BubbleDown(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureNotDisposed()
    {
        if (disposed) ThrowObjectDisposed();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(LockedPriorityQueue<TPriority, TValue>));
}
