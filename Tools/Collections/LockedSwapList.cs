using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools;

/// <summary>
/// Fixed-capacity, zero-alloc key→value store using dense (packed) parallel arrays
/// and a reusable SeqLock. Prevents torn reads by writing the value first, then the key.
/// Writers are serialized via SeqLock; readers use optimistic snapshots validated by epochs.
/// </summary>
public sealed class LockedSwapList<TKey, TValue>
{

    // ===================== Fields / Properties =====================
    public int Capacity { get; }
    public IEqualityComparer<TKey> Comparer { get; }

    private readonly TKey[] _Keys;
    private readonly TValue[] _values;

    private int _count;
    public int Count => Volatile.Read(ref _count);

    private ISeqLockWriter _seqlockWriter;

    // ===================== Ctor =====================
    public LockedSwapList(bool isMultiWriter = true, int capacity = 16, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        Comparer = comparer ?? EqualityComparer<TKey>.Default;

        _Keys = new TKey[capacity];
        _values = new TValue[capacity];

        _seqlockWriter = isMultiWriter ? new MultiSeqLockWriter() : new SingleSeqLockWriter(); // even epoch
        _count = 0;
    }

    // ===================== Internal helpers (writers only) =====================
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int IndexOf(in TKey key)
    {
        int n = _count; // writers only under lock
        var comparer = Comparer;
        for (int i = 0; i < n; i++)
        {
            if (comparer.Equals(_Keys[i], key)) return i;
        }
        return -1;
    }

    // ===================== Writers =====================
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
    {
        _seqlockWriter.BeginWrite();
        try
        {
            if (IndexOf(key) >= 0) return false;
            int n = _count;
            if (n >= Capacity)
                throw new InvalidOperationException($"LockedSwapList is full. Capacity: {Capacity}");

            // Publish value first, then key. EndWrite() provides the release fence.
            _values[n] = value;
            _Keys[n] = key;
            _count = n + 1;
            return true;
        }
        finally { _seqlockWriter.EndWrite(); }
    }

    /// <summary>
    /// Non-reentrant AddOrUpdate. Computes updated value outside the write lock to avoid user-code re-entrancy.
    /// Performs an optimistic read → compute → write-with-revalidation loop.
    /// Infinite-spin semantics on contention (by request).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
    {
        if (updateValueFactory == null) throw new ArgumentNullException(nameof(updateValueFactory));
        var valueComparer = EqualityComparer<TValue>.Default;

        var comparer = Comparer;

        while (true)
        {
            // -------- Phase 1: read snapshot (no locks), find current value --------
            ulong s0 = SeqLockReader.BeginRead(in _seqlockWriter.SeqRef);
            int snapshotCount = Volatile.Read(ref _count);

            int foundIndex = -1;
            TValue priorValue = default!;
            for (int i = 0; i < snapshotCount; i++)
            {
                if (comparer.Equals(_Keys[i], key))
                {
                    foundIndex = i;
                    priorValue = _values[i];
                    break;
                }
            }

            if (!SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            // Compute the candidate value OUTSIDE the lock (prevents re-entrancy).
            TValue computed = (foundIndex >= 0)
                ? updateValueFactory(key, priorValue)
                : addValue;

            // -------- Phase 2: write attempt under lock with re-validation --------
            _seqlockWriter.BeginWrite();
            try
            {
                int n = _count;

                // Search again (state may have changed since Phase 1)
                int currentIndex = -1;
                for (int i = 0; i < n; i++)
                {
                    if (comparer.Equals(_Keys[i], key))
                    {
                        currentIndex = i;
                        break;
                    }
                }

                if (foundIndex >= 0)
                {
                    if (currentIndex >= 0)
                    {
                        // Apply only if the value we observed earlier is still current.
                        if (valueComparer.Equals(_values[currentIndex], priorValue))
                        {
                            _values[currentIndex] = computed;
                            return computed;
                        }
                        // Value changed under us; retry to respect RMW semantics.
                    }
                    // else: the key disappeared; retry as add
                }
                else
                {
                    // Missing in snapshot; if still missing, add it.
                    if (currentIndex < 0)
                    {
                        if (n >= Capacity)
                            throw new InvalidOperationException($"LockedSwapList is full. Capacity: {Capacity}");

                        _values[n] = computed; // value first
                        _Keys[n] = key;        // then key
                        _count = n + 1;
                        return computed;
                    }
                    // Someone else added it meanwhile; fall through to retry as update.
                }
            }
            finally { _seqlockWriter.EndWrite(); }

            // Detected a race; retry.
            X86BaseWrapper.Pause();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOrUpdate(TKey key, TValue value)
    {
        _seqlockWriter.BeginWrite();
        try
        {
            int i = IndexOf(key);
            if (i >= 0)
            {
                _values[i] = value;
                return;
            }

            int n = _count;
            if (n >= Capacity)
                throw new InvalidOperationException($"LockedSwapList is full. Capacity: {Capacity}");

            _values[n] = value;  // value first
            _Keys[n] = key;      // then key
            _count = n + 1;
        }
        finally { _seqlockWriter.EndWrite(); }
    }

    /// <summary>
    /// Swap-with-last removal (unordered). Clears tail only if the type contains references.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]

    public TValue GetOrAdd(TKey key, TValue addValue)
    {
        var comparer = Comparer;

        while (true)
        {
            // -------- Phase 1: optimistic read --------
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

            int n = Volatile.Read(ref _count);
            for (int i = 0; i < n; i++)
            {
                if (comparer.Equals(_Keys[i], key))
                {
                    TValue existing = _values[i];
                    if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef)) return existing;
                    goto retry;
                }
            }

            if (!SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef)) { X86BaseWrapper.Pause(); continue; }

            // -------- Phase 2: write (no factory compute) --------
            _seqlockWriter.BeginWrite();
            try
            {
                // Re-check under the write lock
                int j = IndexOf(key);
                if (j >= 0) return _values[j];

                int tail = _count;
                if (tail >= Capacity)
                    throw new InvalidOperationException($"LockedSwapList is full. Capacity: {Capacity}");

                _values[tail] = addValue; // value first
                _Keys[tail] = key;      // then key
                _count = tail + 1;
                return addValue;
            }
            finally { _seqlockWriter.EndWrite(); }

        retry:
            X86BaseWrapper.Pause();
        }
    }

    /// <summary>
    /// Get existing value or create/add via factory if missing.
    /// The factory executes outside the lock; we re-validate under the write lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        if (valueFactory == null) throw new ArgumentNullException(nameof(valueFactory));

        var comparer = Comparer;

        while (true)
        {
            // -------- Phase 1: optimistic read --------
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

            int n = Volatile.Read(ref _count);
            for (int i = 0; i < n; i++)
            {
                if (comparer.Equals(_Keys[i], key))
                {
                    TValue existing = _values[i];
                    if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef)) return existing;
                    goto retry;
                }
            }

            if (!SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef)) { X86BaseWrapper.Pause(); continue; }

            // Compute outside the lock
            TValue created = valueFactory(key);

            // -------- Phase 2: write --------
            _seqlockWriter.BeginWrite();
            try
            {
                // Re-check under the write lock
                int j = IndexOf(key);
                if (j >= 0) return _values[j];

                int tail = _count;
                if (tail >= Capacity)
                    throw new InvalidOperationException($"LockedSwapList is full. Capacity: {Capacity}");

                _values[tail] = created; // value first
                _Keys[tail] = key;     // then key
                _count = tail + 1;
                return created;
            }
            finally { _seqlockWriter.EndWrite(); }

        retry:
            X86BaseWrapper.Pause();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(TKey key, out TValue removed)
    {
        _seqlockWriter.BeginWrite();
        try
        {
            int i = IndexOf(key);
            if (i < 0) { removed = default!; return false; }

            int last = _count - 1;
            removed = _values[i];

            if (i != last)
            {
                _values[i] = _values[last]; // move value first
                _Keys[i] = _Keys[last];     // then key
            }

            // Clear tail slots to avoid retaining references.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()) _values[last] = default!;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) _Keys[last] = default!;

            _count = last;
            return true;
        }
        finally { _seqlockWriter.EndWrite(); }
    }

    // ===================== Readers =====================
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue value)
    {
        var comparer = Comparer;

        while (true)
        {
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            int n = Volatile.Read(ref _count);
            bool found = false;
            TValue tmp = default!;

            for (int i = 0; i < n; i++)
            {
                if (comparer.Equals(_Keys[i], key))
                {
                    tmp = _values[i];
                    found = true;
                    break;
                }
            }

            if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef))
            {
                value = found ? tmp : default!;
                return found;
            }

            X86BaseWrapper.Pause();
        }
    }

    // ===================== Snapshot copies =====================
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<TKey> CopyTo(Span<TKey> buffer)
    {
        while (true)
        {
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            int n = Volatile.Read(ref _count);
            int copy = Math.Min(n, buffer.Length);
            for (int i = 0; i < copy; i++)
                buffer[i] = _Keys[i];

            if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef))
                return buffer.Slice(0, copy);

            X86BaseWrapper.Pause();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<TValue> CopyTo(Span<TValue> buffer)
    {
        while (true)
        {
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            int n = Volatile.Read(ref _count);
            int copy = Math.Min(n, buffer.Length);
            for (int i = 0; i < copy; i++)
                buffer[i] = _values[i];

            if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef))
                return buffer.Slice(0, copy);

            X86BaseWrapper.Pause();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<KeyValuePair<TKey, TValue>> CopyTo(Span<KeyValuePair<TKey, TValue>> buffer)
    {
        while (true)
        {
            ulong s0 = SeqLockReader.Read(in _seqlockWriter.SeqRef);
            if (SeqLockReader.IsWriteInProgress(s0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            int n = Volatile.Read(ref _count);
            int copy = Math.Min(n, buffer.Length);
            for (int i = 0; i < copy; i++)
                buffer[i] = new KeyValuePair<TKey, TValue>(_Keys[i], _values[i]);

            if (SeqLockReader.Validate(s0, in _seqlockWriter.SeqRef))
                return buffer.Slice(0, copy);

            X86BaseWrapper.Pause();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArrayList<TKey> CopyKeys()
    {
        TKey[] buffer = System.Buffers.ArrayPool<TKey>.Shared.Rent(Count);
        ReadOnlySpan<TKey> span = CopyTo(buffer);
        return new ArrayList<TKey>(buffer, span.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArrayList<TValue> CopyValues()
    {
        TValue[] buffer = System.Buffers.ArrayPool<TValue>.Shared.Rent(Count);
        ReadOnlySpan<TValue> span = CopyTo(buffer);
        return new ArrayList<TValue>(buffer, span.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArrayList<KeyValuePair<TKey, TValue>> Copy()
    {
        var buffer = System.Buffers.ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(Count);
        ReadOnlySpan<KeyValuePair<TKey, TValue>> span = CopyTo(buffer);
        return new ArrayList<KeyValuePair<TKey, TValue>>(buffer, span.Length);
    }
}
