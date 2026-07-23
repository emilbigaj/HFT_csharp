using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tools;

/// <summary>
/// High-performance, ArrayPool-backed dynamic list with <see cref="Span{T}"/> access and O(1) swap-remove.
/// Single-threaded: callers must provide external synchronization if shared.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
/// <remarks>
/// <para><b>Performance &amp; semantics</b></para>
/// <list type="bullet">
/// <item><description>No per-operation allocations in steady state. Capacity grows using a geometric strategy.</description></item>
/// <item><description><b>Spans/Enumerators are invalidated by any operation that may resize the backing array.</b>
/// Do not hold spans or enumerators across mutations that can grow the list.</description></item>
/// <item><description>Disposal is enforced in all build configurations; any member access after <see cref="Dispose"/> throws.</description></item>
/// </list>
/// </remarks>
public sealed class ArrayList<T> : IDisposable
{
    private T[] _array;
    private int _count;
    private bool _disposed;

    /// <summary>Number of elements currently stored.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { ThrowIfDisposed(); return _count; }
    }

    /// <summary>Current capacity of the internal buffer.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { ThrowIfDisposed(); return _array.Length; }
    }

    // ---- Internal helpers (for LockedList<T> only) ----

    /// <summary>Internal: snapshot-style access to current backing array reference (dangerous).</summary>
    internal T[] DangerousArray
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { ThrowIfDisposed(); return _array; }
    }

    /// <summary>Internal: snapshot-style access to current element count (dangerous).</summary>
    internal int DangerousCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { ThrowIfDisposed(); return _count; }
    }

    // --------- Ctors ---------

    public ArrayList(int initialCapacity = 16)
    {
        if (initialCapacity <= 0) initialCapacity = 16;
        _array = System.Buffers.ArrayPool<T>.Shared.Rent(initialCapacity);
        _count = 0;
        _disposed = false;
    }

    /// <summary>
    /// Wrap an existing array segment and transfer ownership (array will be returned to the pool on resize/dispose).
    /// </summary>
    public ArrayList(T[] array, int count)
    {
        if (array == null) throw new ArgumentNullException(nameof(array));
        if ((uint)count > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _array = array;
        _count = count;
        _disposed = false;
    }

    // --------- Basic ops ---------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
    {
        ThrowIfDisposed();
        if ((uint)_count >= (uint)_array.Length) Grow();
        _array[_count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRange(ReadOnlySpan<T> items)
    {
        ThrowIfDisposed();
        int needed = _count + items.Length;
        if (needed < 0) throw new OutOfMemoryException();
        if (_array.Length < needed) EnsureCapacity(needed);
        items.CopyTo(new Span<T>(_array, _count, items.Length));
        _count = needed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRange(ArrayList<T> list)
    {
        ThrowIfDisposed();
        int needed = _count + list.Count;
        if (needed < 0) throw new OutOfMemoryException();
        if (_array.Length < needed) EnsureCapacity(needed);
        list._array.CopyTo(new Span<T>(_array, _count, list.Count));
        _count = needed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AddUninitialized(int count)
    {
        ThrowIfDisposed();
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int start = _count;
        int needed = start + count;
        if (needed < 0) throw new OutOfMemoryException();
        if (_array.Length < needed) EnsureCapacity(needed);
        _count = needed;
        return new Span<T>(_array, start, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        ThrowIfDisposed();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_array, 0, _count);
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SwapRemoveAt(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_count) ThrowOutOfRange();

        int last = _count - 1;
        if (index < last)
            _array[index] = _array[last];

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _array[last] = default!;

        _count = last;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T RemoveAt(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_count) ThrowOutOfRange();

        T value = _array[index];
        int last = _count - 1;
        if (index < last)
            Array.Copy(_array, index + 1, _array, index, last - index);

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _array[last] = default!;

        _count = last;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(T item)
    {
        ThrowIfDisposed();
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _count; i++)
        {
            if (comparer.Equals(_array[i], item))
            {
                RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SwapRemove(T item)
    {
        ThrowIfDisposed();
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _count; i++)
        {
            if (comparer.Equals(_array[i], item))
            {
                SwapRemoveAt(i);
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out T value)
    {
        ThrowIfDisposed();
        if (_count == 0) { value = default!; return false; }
        int last = _count - 1;
        value = _array[last];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _array[last] = default!;
        _count = last;
        return true;
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)_count) ThrowOutOfRange();
            return ref _array[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan()
    {
        ThrowIfDisposed();
        return new Span<T>(_array, 0, _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> AsReadOnlySpan()
    {
        ThrowIfDisposed();
        return new ReadOnlySpan<T>(_array, 0, _count);
    }

    // ---- Insert APIs (used by LockedList<T>) ----

    /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>, shifting tail right.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InsertAt(int index, T item)
    {
        ThrowIfDisposed();
        if ((uint)index > (uint)_count) ThrowOutOfRange();
        if (_count == _array.Length) EnsureCapacity(_count + 1);
        if (index < _count)
            Array.Copy(_array, index, _array, index + 1, _count - index);
        _array[index] = item;
        _count++;
    }

    /// <summary>Insert <paramref name="items"/> at <paramref name="index"/>, preserving order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InsertRangeAt(int index, ReadOnlySpan<T> items)
    {
        ThrowIfDisposed();
        if ((uint)index > (uint)_count) ThrowOutOfRange();
        int k = items.Length;
        if (k == 0) return;

        int needed = _count + k;
        if (needed < 0) throw new OutOfMemoryException();
        if (_array.Length < needed) EnsureCapacity(needed);

        if (index < _count)
            Array.Copy(_array, index, _array, index + k, _count - index);

        items.CopyTo(new Span<T>(_array, index, k));
        _count = needed;
    }

    // ---------- Sorting (unchanged) ----------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort(Func<T, T, int> comparison)
    {
        ThrowIfDisposed();
        if (_count <= 1) return;
        AsSpan().Sort(Comparer<T>.Create((x, y) => comparison(x, y)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort(IComparer<T> comparer)
    {
        ThrowIfDisposed();
        if (_count <= 1) return;
        AsSpan().Sort(comparer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort<TComparer>(TComparer comparer) where TComparer : IComparer<T>
    {
        ThrowIfDisposed();
        if (_count <= 1) return;
        AsSpan().Sort(comparer);
    }

    // ---------- Capacity management ----------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int min)
    {
        ThrowIfDisposed();
        if (_array.Length < min) Resize(ComputeNewSize(_array.Length, min));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Grow()
    {
        int newSize = ComputeNewSize(_array.Length, _count + 1);
        Resize(newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeNewSize(int cur, int needed)
    {
        if (needed < 0) throw new OutOfMemoryException();
        int next = (cur <= 1024) ? (cur == 0 ? 4 : cur << 1) : cur + (cur >> 1);
        if (next < needed) next = needed;

        const int MaxArrayLength = 0x7FEFFFFF; // practical CLR limit
        if (next > MaxArrayLength)
            next = (needed > MaxArrayLength) ? throw new OutOfMemoryException() : MaxArrayLength;

        return next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Resize(int newSize)
    {
        T[] newArr = System.Buffers.ArrayPool<T>.Shared.Rent(newSize);
        Array.Copy(_array, 0, newArr, 0, _count);

        if (_array.Length != 0)
        {
            bool clearValues = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
            System.Buffers.ArrayPool<T>.Shared.Return(_array, clearArray: clearValues);
        }

        _array = newArr;
    }

    // ---------- Enumeration (allocation-free) ----------
    public ref struct Enumerator
    {
        private readonly T[] _arr;
        private readonly int _len;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(T[] arr, int len)
        {
            _arr = arr;
            _len = len;
            _index = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < _len;

        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _arr[_index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        ThrowIfDisposed();
        return new Enumerator(_array, _count);
    }

    // ---------- Disposal ----------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        T[] toReturn = _array;
        int length = toReturn.Length;

        _array = Array.Empty<T>();
        _count = 0;

        if (length != 0)
        {
            bool clearValues = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
            System.Buffers.ArrayPool<T>.Shared.Return(toReturn, clearArray: clearValues);
        }
    }

    // ---------- Helpers ----------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfRange() => throw new ArgumentOutOfRangeException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ArrayList<T>));
    }
}
