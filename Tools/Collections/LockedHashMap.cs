using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools
{
    /// <summary>
    /// Seqlock-guarded, open-addressed Robin–Hood hash map built on top of <see cref="HashMap{TKey, TValue}"/>.
    /// <para>
    /// • Writers are serialized via <see cref="ISeqLockWriter"/> (very small critical sections).<br/>
    /// • Readers are optimistic: snapshot arrays/mask, validate the snapshot is coherent,
    ///   probe, then validate the epoch again; retry on contention.<br/>
    /// • Storage is pooled through the wrapped core; no tombstones (backshift deletion in core).<br/>
    /// • Factory overloads compute outside the write epoch and re-validate before commit.
    /// </para>
    /// <b>Note:</b> This type assumes you provide <c>ISeqLockWriter</c>, <c>SingleSeqLockWriter</c>, <c>MultiSeqLockWriter</c>,
    /// and <c>SeqLockReader</c> with the usual <c>Read/Validate/IsWriteInProgress</c> API and a <c>SeqRef</c> field/property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Snapshot coherence (the “mid-validate”):</b> readers take four independent reads of the
    /// core’s array/mask references. A writer’s resize can land between any two of those reads,
    /// leaving a torn snapshot (e.g. mask from new capacity, hashes from old array) that would
    /// index out of range during the probe. To avoid relying on exception handling for control flow
    /// (and the latency jitter that comes with it), every optimistic reader performs an extra
    /// epoch read immediately after taking the snapshot and discards the snapshot if the writer
    /// ran in that window. This costs one extra <see cref="Volatile.Read"/> per successful query.
    /// </para>
    /// </remarks>
    public sealed class LockedHashMap<TKey, TValue> : IDisposable
    {
        private readonly HashMap<TKey, TValue> _core;
        private readonly ISeqLockWriter _writer;
        private bool _disposed;

        /// <summary>Comparer forwarded from the core.</summary>
        public IEqualityComparer<TKey> Comparer => _core.Comparer;

        /// <summary>Approximate count (can change under contention).</summary>
        public int Count => _core.Count;

        /// <summary>Current logical capacity (power-of-two).</summary>
        public int Capacity => _core.Capacity;

        /// <summary>Create a new seqlock-guarded dictionary around the pooled Robin–Hood core.</summary>
        public LockedHashMap(bool isMultiWriter = true, int initialCapacity = 16, IEqualityComparer<TKey>? comparer = null, bool allowResize = true)
        {
            _core = new HashMap<TKey, TValue>(initialCapacity, comparer, allowResize);
            _writer = isMultiWriter ? new MultiSeqLockWriter() : new SingleSeqLockWriter();
            _disposed = false;
        }

        // ====================== Writers (small critical sections) ======================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, TValue value)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { return _core.TryAdd(key, value); }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryUpdate(TKey key, TValue newValue)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { return _core.TryUpdate(key, newValue); }
            finally { _writer.EndWrite(); }
        }

        /// <summary>Blind set (add or replace).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(TKey key, TValue value)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { _core.AddOrUpdate(key, value); }
            finally { _writer.EndWrite(); }
        }

        /// <summary>
        /// Add if missing; otherwise update using <paramref name="updateValueFactory"/>. The factory runs outside the write epoch,
        /// and we re-validate the pre-read before committing; on mismatch we retry (so semantics are correct and writers stay small).
        /// </summary>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (updateValueFactory is null) throw new ArgumentNullException(nameof(updateValueFactory));
            ThrowIfDisposed();

            var cmp = _core.Comparer;

            while (true)
            {
                // -------- Optimistic read --------
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                // Snapshot (may be inconsistent; mid-validate before use)
                TKey[] keys = _core.KeysRef;
                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int mask = _core.Mask;

                // Mid-validate: ensure the four reads above all came from the same epoch
                // before we trust them for indexed access. A resize between any pair would
                // leave mask and hashes mismatched, producing OOB on the probe.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int mixed = ComputeMixedHash(key, cmp);
                int idx = mixed & mask;
                int probe = 0;

                bool found = false;
                TValue prior = default!;
                while (true)
                {
                    int h = hashes[idx];
                    if (h == -1) break;
                    if (h == mixed && cmp.Equals(keys[idx], key)) { found = true; prior = vals[idx]; break; }

                    int ideal = h & mask;
                    int dist = (idx - ideal) & mask;
                    if (dist < probe) break;
                    idx = (idx + 1) & mask;
                    probe++;
                }

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, s1)) { X86BaseWrapper.Pause(); continue; }

                // Compute outside the lock to keep the epoch short
                TValue computed = found ? updateValueFactory(key, prior) : addValue;

                // -------- Commit under write epoch with re-validation --------
                _writer.BeginWrite();
                try
                {
                    ThrowIfDisposed();

                    // Re-check under the lock
                    if (_core.TryGetValueKnownHash(key, mixed, out TValue current))
                    {
                        if (found && EqualityComparer<TValue>.Default.Equals(current, prior))
                        {
                            // Safe to commit the computed value
                            _core.AddOrUpdateKnownHash(key, computed, mixed);
                            return computed;
                        }

                        // Value changed between read and write: recompute on the *current* value for correctness
                        TValue recomputed = updateValueFactory(key, current);
                        _core.AddOrUpdateKnownHash(key, recomputed, mixed);
                        return recomputed;
                    }
                    else
                    {
                        // Still missing → add
                        _core.TryAddKnownHash(key, computed, mixed);
                        return computed;
                    }
                }
                finally { _writer.EndWrite(); }
            }
        }

        /// <summary>Get existing value or add provided value if missing (factory-free, optimistic).</summary>
        public TValue GetOrAdd(TKey key, TValue addValue)
        {
            ThrowIfDisposed();
            var cmp = _core.Comparer;

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TKey[] keys = _core.KeysRef;
                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int mask = _core.Mask;

                // Mid-validate: discard a torn snapshot before any indexed access.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int mixed = ComputeMixedHash(key, cmp);
                int idx = mixed & mask;
                int probe = 0;

                while (true)
                {
                    int h = hashes[idx];
                    if (h == -1) break;
                    if (h == mixed && cmp.Equals(keys[idx], key))
                    {
                        TValue existing = vals[idx];
                        ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                        if (SeqLockReader.Validate(s0, s1)) return existing;
                        goto retry;
                    }

                    int ideal = h & mask;
                    int dist = (idx - ideal) & mask;
                    if (dist < probe) break;
                    idx = (idx + 1) & mask;
                    probe++;
                }

                // Not found in the snapshot; validate epoch then add under write epoch
                {
                    ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                    if (!SeqLockReader.Validate(s0, s1)) { X86BaseWrapper.Pause(); continue; }
                }

                _writer.BeginWrite();
                try
                {
                    ThrowIfDisposed();
                    // Double-check: maybe someone added it already
                    if (_core.TryGetValueKnownHash(key, mixed, out TValue existing)) return existing;

                    _core.TryAddKnownHash(key, addValue, mixed);
                    return addValue;
                }
                finally { _writer.EndWrite(); }

            retry:
                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Get existing value or create/add via factory if missing (factory outside epoch; re-validate on commit).</summary>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory is null) throw new ArgumentNullException(nameof(valueFactory));
            ThrowIfDisposed();

            var cmp = _core.Comparer;

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TKey[] keys = _core.KeysRef;
                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int mask = _core.Mask;

                // Mid-validate: discard a torn snapshot before any indexed access.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int mixed = ComputeMixedHash(key, cmp);
                int idx = mixed & mask;
                int probe = 0;

                while (true)
                {
                    int h = hashes[idx];
                    if (h == -1) break;
                    if (h == mixed && cmp.Equals(keys[idx], key))
                    {
                        TValue existing = vals[idx];
                        ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                        if (SeqLockReader.Validate(s0, s1)) return existing;
                        goto retry;
                    }

                    int ideal = h & mask;
                    int dist = (idx - ideal) & mask;
                    if (dist < probe) break;
                    idx = (idx + 1) & mask;
                    probe++;
                }

                // Validate the snapshot and compute outside the write epoch
                {
                    ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                    if (!SeqLockReader.Validate(s0, s1)) { X86BaseWrapper.Pause(); continue; }
                }

                TValue created = valueFactory(key);

                _writer.BeginWrite();
                try
                {
                    ThrowIfDisposed();
                    if (_core.TryGetValueKnownHash(key, mixed, out TValue existing)) return existing;

                    _core.TryAddKnownHash(key, created, mixed);
                    return created;
                }
                finally { _writer.EndWrite(); }

            retry:
                X86BaseWrapper.Pause();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key, out TValue removed)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { return _core.TryRemove(key, out removed); }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { _core.Clear(); }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int min)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { _core.EnsureCapacity(min); }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrimExcess()
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { _core.TrimExcess(); }
            finally { _writer.EndWrite(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset(int newInitialCapacity = 4)
        {
            ThrowIfDisposed();
            _writer.BeginWrite();
            try { _core.Reset(newInitialCapacity); }
            finally { _writer.EndWrite(); }
        }

        // ====================== Readers (optimistic snapshot with epoch validation) ======================

        /// <summary>Try to get the value associated with <paramref name="key"/> using an optimistic snapshot.</summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            ThrowIfDisposed();
            var cmp = _core.Comparer;

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TKey[] keys = _core.KeysRef;
                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int mask = _core.Mask;

                // Mid-validate: ensure the four field reads above are a coherent snapshot.
                // Without this a concurrent resize can leave mask paired with the wrong-sized
                // hashes/keys/vals, and the probe below would index out of range.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int mixed = ComputeMixedHash(key, cmp);
                int idx = mixed & mask;
                int probe = 0;

                TValue tmp = default!;
                bool found = false;

                while (true)
                {
                    int h = hashes[idx];
                    if (h == -1) break;

                    if (h == mixed && cmp.Equals(keys[idx], key))
                    {
                        tmp = vals[idx];
                        found = true;
                        break;
                    }

                    int ideal = h & mask;
                    int dist = (idx - ideal) & mask;
                    if (dist < probe) break;

                    idx = (idx + 1) & mask;
                    probe++;
                }

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1))
                {
                    value = found ? tmp : default!;
                    return found;
                }

                X86BaseWrapper.Pause();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key) => TryGetValue(key, out _);

        // ====================== Snapshot copy & enumeration ======================

        /// <summary>Copy keys into a caller-provided buffer (occupied slots only). Returns the written slice.</summary>
        public ReadOnlySpan<TKey> CopyTo(Span<TKey> buffer)
        {
            ThrowIfDisposed();

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TKey[] keys = _core.KeysRef;
                int[] hashes = _core.HashesRef;
                int snapCount = _core.Count;

                // Mid-validate: keys.Length and hashes.Length must agree (a resize between
                // these two reads would mismatch them and trip the loop bound).
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int target = Math.Min(snapCount, buffer.Length);
                int written = 0;

                for (int i = 0; written < target && i < hashes.Length; i++)
                    if (hashes[i] >= 0) buffer[written++] = keys[i];

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1)) return buffer.Slice(0, written);

                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Copy values into a caller-provided buffer (occupied slots only). Returns the written slice.</summary>
        public ReadOnlySpan<TValue> CopyTo(Span<TValue> buffer)
        {
            ThrowIfDisposed();

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int snapCount = _core.Count;

                // Mid-validate: vals.Length and hashes.Length must come from the same epoch.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int target = Math.Min(snapCount, buffer.Length);
                int written = 0;

                for (int i = 0; written < target && i < hashes.Length; i++)
                    if (hashes[i] >= 0) buffer[written++] = vals[i];

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1)) return buffer.Slice(0, written);

                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Copy pairs into a caller-provided buffer (occupied slots only). Returns the written slice.</summary>
        public ReadOnlySpan<KeyValuePair<TKey, TValue>> CopyTo(Span<KeyValuePair<TKey, TValue>> buffer)
        {
            ThrowIfDisposed();

            while (true)
            {
                ulong s0 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.IsWriteInProgress(s0)) { X86BaseWrapper.Pause(); continue; }

                TKey[] keys = _core.KeysRef;
                TValue[] vals = _core.ValuesRef;
                int[] hashes = _core.HashesRef;
                int snapCount = _core.Count;

                // Mid-validate: all three array references must come from the same epoch.
                ulong sMid = SeqLockReader.Read(in _writer.SeqRef);
                if (!SeqLockReader.Validate(s0, sMid)) { X86BaseWrapper.Pause(); continue; }

                int target = Math.Min(snapCount, buffer.Length);
                int written = 0;

                for (int i = 0; written < target && i < hashes.Length; i++)
                    if (hashes[i] >= 0) buffer[written++] = new KeyValuePair<TKey, TValue>(keys[i], vals[i]);

                ulong s1 = SeqLockReader.Read(in _writer.SeqRef);
                if (SeqLockReader.Validate(s0, s1)) return buffer.Slice(0, written);

                X86BaseWrapper.Pause();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ArrayList<TKey> CopyKeys()
        {
            ThrowIfDisposed();
            int rent = Math.Max(1, Count); // avoid Rent(0)
            TKey[] buf = System.Buffers.ArrayPool<TKey>.Shared.Rent(rent);
            ReadOnlySpan<TKey> span = CopyTo(buf);
            return new ArrayList<TKey>(buf, span.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ArrayList<TValue> CopyValues()
        {
            ThrowIfDisposed();
            int rent = Math.Max(1, Count);
            TValue[] buf = System.Buffers.ArrayPool<TValue>.Shared.Rent(rent);
            ReadOnlySpan<TValue> span = CopyTo(buf);
            return new ArrayList<TValue>(buf, span.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ArrayList<KeyValuePair<TKey, TValue>> Copy()
        {
            ThrowIfDisposed();
            int rent = Math.Max(1, Count);
            KeyValuePair<TKey, TValue>[] buf = System.Buffers.ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(rent);
            ReadOnlySpan<KeyValuePair<TKey, TValue>> span = CopyTo(buf);
            return new ArrayList<KeyValuePair<TKey, TValue>>(buf, span.Length);
        }

        /// <summary>Enumerator over a pooled snapshot; disposes the snapshot automatically.</summary>
        public Enumerator GetEnumerator() => new Enumerator(Copy());

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private ArrayList<KeyValuePair<TKey, TValue>> _snapshot;
            private int _index;
            private KeyValuePair<TKey, TValue> _current;

            internal Enumerator(ArrayList<KeyValuePair<TKey, TValue>> snapshot)
            {
                _snapshot = snapshot;
                _index = -1;
                _current = default!;
            }

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

            public KeyValuePair<TKey, TValue> Current => _current;
            object System.Collections.IEnumerator.Current => _current!;
            public void Reset() => _index = -1;
            public void Dispose() => _snapshot.Dispose();
        }

        // ====================== Disposal ======================

        public void Dispose()
        {
            if (_disposed) return;
            _writer.BeginWrite();
            try
            {
                if (_disposed) return;
                _core.Dispose();
                _disposed = true;
            }
            finally { _writer.EndWrite(); }
        }

        // ====================== Helpers ======================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LockedHashMap<TKey, TValue>));
        }

        /// <summary>Must match core's mixing so snapshot readers probe the same slots.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeMixedHash(TKey key, IEqualityComparer<TKey> cmp)
        {
            // Primitive fast paths to avoid virtual GetHashCode on hot paths.
            if (typeof(TKey) == typeof(int))
            {
                int v = Unsafe.As<TKey, int>(ref key);
                return Mix(v);
            }
            if (typeof(TKey) == typeof(long))
            {
                long v = Unsafe.As<TKey, long>(ref key);
                int h = (int)(v ^ (v >> 32));
                return Mix(h);
            }
            if (typeof(TKey) == typeof(ulong))
            {
                ulong v = Unsafe.As<TKey, ulong>(ref key);
                int h = (int)(v ^ (v >> 32));
                return Mix(h);
            }
            return Mix(cmp.GetHashCode(key!));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int Mix(int h)
            {
                unchecked
                {
                    // Murmur3-style finalizer; identical to the core
                    h ^= (int)((uint)h >> 16);
                    h *= -2048144789;   // 0x85ebca6b
                    h ^= (int)((uint)h >> 13);
                    h *= -1028477387;   // 0xC2B2AE35
                    h ^= (int)((uint)h >> 16);
                    return h & 0x7FFFFFFF;
                }
            }
        }
    }
}