using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools
{
    /// <summary>
    /// Growable, order-preserving list that wraps <see cref="Tools.List{T}"/> and uses a seqlock for synchronization.
    /// <para>
    /// • Backing storage is pooled via <see cref="System.Buffers.ArrayPool{T}"/> (through the inner <see cref="Tools.List{T}"/>).<br/>
    /// • Writers are serialized by a seqlock (<see cref="ISeqLockWriter"/>); readers take optimistic snapshots and validate epochs.<br/>
    /// • Enumeration is snapshot-based (pooled copy) to avoid observing in-flight mutations.<br/>
    /// • Single-threaded semantics for the inner list; concurrency is provided by <see cref="LockedArrayList{T}"/> only.
    /// </para>
    /// </summary>
    public sealed class LockedArrayList<T> : IDisposable
    {
        private readonly ArrayList<T> _list;
        private readonly ISeqLockWriter _writer;
        private bool _disposed;

        /// <summary>Total number of elements currently stored (approximate under contention).</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ThrowIfDisposed(); return _list.Count; }
        }

        /// <summary>Current capacity of the internal buffer.</summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ThrowIfDisposed(); return _list.Capacity; }
        }

        /// <summary>
        /// Create a new <see cref="LockedArrayList{T}"/>.
        /// </summary>
        /// <param name="isMultiWriter">Use multi-writer (CAS) seqlock if true; single-writer if false.</param>
        /// <param name="initialCapacity">Initial capacity for the inner pooled list.</param>
        public LockedArrayList(bool isMultiWriter = true, int initialCapacity = 16)
        {
            _list = new ArrayList<T>(initialCapacity);
            _writer = isMultiWriter ? new MultiSeqLockWriter() : new SingleSeqLockWriter();
            _disposed = false;
        }

        // ===================== Indexer =====================

        /// <summary>
        /// Read-only indexer with optimistic snapshot semantics; throws if contention invalidates the read.
        /// Use <see cref="TryGetAt(int, out T)"/> for non-throwing access.
        /// </summary>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                if (!TryGetAt(index, out T value))
                    throw new InvalidOperationException($"LockedList<{typeof(T).Name}> indexer read failed due to concurrent write contention.");
                return value;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                ThrowIfDisposed();
                _writer.BeginWrite();
                try
                {
                    int n = _list.Count;
                    if ((uint)index >= (uint)n) throw new ArgumentOutOfRangeException(nameof(index));
                    _list[index] = value;
                }
                finally { _writer.EndWrite(); }
            }
        }

        // ===================== Writes =====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                _list.Add(item);
            }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(ReadOnlySpan<T> items)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                _list.AddRange(items);
            }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Insert(int index, T item)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                _list.InsertAt(index, item);
            }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InsertRange(int index, ReadOnlySpan<T> items)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                _list.InsertRangeAt(index, items);
            }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T RemoveAt(int index)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                return _list.RemoveAt(index);
            }
            finally { _writer.EndWrite(); }
        }

        /// <summary>Ordered removal of first occurrence of <paramref name="item"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(T item, IEqualityComparer<T>? comparer = null)
        {
            ThrowIfDisposed();
            comparer ??= EqualityComparer<T>.Default;

            _writer.BeginWrite();
            try
            {
                // Scan using the inner list directly (single-threaded within write section).
                int n = _list.Count;
                for (int i = 0; i < n; i++)
                {
                    if (comparer.Equals(_list[i], item))
                    {
                        _list.RemoveAt(i);
                        return true;
                    }
                }
                return false;
            }
            finally { _writer.EndWrite(); }
        }

        /// <summary>Unordered removal at index; returns removed value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySwapRemoveAt(int index, out T removed)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                int n = _list.Count;
                if ((uint)index >= (uint)n) { removed = default!; return false; }

                removed = _list[index];
                _list.SwapRemoveAt(index);
                return true;
            }
            finally { _writer.EndWrite(); }
        }

        /// <summary>Unordered removal of the first occurrence of <paramref name="item"/>; returns removed value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SwapRemove(T item, out T removed, IEqualityComparer<T>? comparer = null)
        {
            ThrowIfDisposed();
            comparer ??= EqualityComparer<T>.Default;

            _writer.BeginWrite();
            try
            {
                int n = _list.Count;
                for (int i = 0; i < n; i++)
                {
                    if (comparer.Equals(_list[i], item))
                    {
                        removed = _list[i];
                        _list.SwapRemoveAt(i);
                        return true;
                    }
                }
                removed = default!;
                return false;
            }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try
            {
                _list.Clear();
            }
            finally { _writer.EndWrite(); }
        }

        // ===================== Reads (optimistic snapshot) =====================

        /// <summary>Try to read element at <paramref name="index"/> with optimistic snapshot semantics.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetAt(int index, out T value)
        {
            ThrowIfDisposed();

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0))
                {
                    X86BaseWrapper.Pause();
                    continue;
                }

                // Snapshot current array + count (may be inconsistent; validated via seqlock)
                T[] arr = _list.DangerousArray;
                int n = _list.DangerousCount;

                if ((uint)index < (uint)n)
                    value = arr[index];
                else
                    value = default!;

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1))
                    return (uint)index < (uint)n;

                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Index lookup under optimistic snapshot.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(T item, IEqualityComparer<T>? comparer = null)
        {
            ThrowIfDisposed();
            comparer ??= EqualityComparer<T>.Default;

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                T[] arr = _list.DangerousArray;
                int n = _list.DangerousCount;

                int found = -1;
                for (int i = 0; i < n; i++)
                {
                    if (comparer.Equals(arr[i], item)) { found = i; break; }
                }

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1))
                    return found;

                X86BaseWrapper.Pause();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item, IEqualityComparer<T>? comparer = null)
        {
            return IndexOf(item, comparer) >= 0;
        }

        /// <summary>
        /// Copy snapshot into a caller-provided buffer. Returns a span over the written portion.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> CopyTo(Span<T> buffer)
        {
            ThrowIfDisposed();

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                T[] arr = _list.DangerousArray;
                int n = _list.DangerousCount;
                int copy = Math.Min(n, buffer.Length);

                for (int i = 0; i < copy; i++)
                    buffer[i] = arr[i];

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1))
                    return buffer.Slice(0, copy);

                X86BaseWrapper.Pause();
            }
        }

        /// <summary>
        /// Create a pooled snapshot list that the caller must dispose to return memory to the pool.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ArrayList<T> Copy()
        {
            ThrowIfDisposed();
            // Rent to the current Count (approximate; CopyTo validates & returns actual length)
            T[] buf = System.Buffers.ArrayPool<T>.Shared.Rent(Count);
            ReadOnlySpan<T> span = CopyTo(buf);
            return new ArrayList<T>(buf, span.Length);
        }

        // ===================== Enumeration (snapshot-based) =====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() => new Enumerator(Copy());

        /// <summary>Snapshot enumerator over pooled storage; disposes snapshot on completion.</summary>
        public struct Enumerator : System.Collections.Generic.IEnumerator<T>
        {
            private ArrayList<T> _snapshot;
            private int _index;
            private T _current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ArrayList<T> snapshot)
            {
                _snapshot = snapshot;
                _index = -1;
                _current = default!;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                int next = _index + 1;
                if ((uint)next < (uint)_snapshot.Count)
                {
                    _index = next;
                    _current = _snapshot[next];
                    return true;
                }
                return false;
            }

            public T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }

            object System.Collections.IEnumerator.Current => _current!;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset() => _index = -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose()
            {
                _snapshot.Dispose();
            }
        }

        // ===================== Utilities / Disposal =====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LockedArrayList<T>));
        }

        /// <summary>
        /// Dispose inside a write epoch so readers either fail validation or see an empty, valid state.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _writer.BeginWrite();
            try
            {
                if (_disposed) return;
                _list.Dispose();
                _disposed = true;
            }
            finally
            {
                _writer.EndWrite();
            }
        }
    }
}
