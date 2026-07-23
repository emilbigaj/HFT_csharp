using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tools
{
    [SkipLocalsInit]
    public sealed class HashMap<TKey, TValue> : IDisposable
    {
        private TKey[] _keys;
        private TValue[] _values;
        private int[] _hashes; // -1 = empty, >= 0 = occupied

        private int _count;
        private int _hashMask;
        private int _cap;
        private int _resizeThreshold;
        private bool _disposed;
        private readonly bool _allowResize;

        private const float MaxLoad = 0.80f;

        public IEqualityComparer<TKey> Comparer { get; }
        public int Count => _count;
        public int Capacity => _cap;
        public bool AllowResize => _allowResize;

        // ===================== Internal Accessors (Used by LockedHashMap) =====================
        internal TKey[] KeysRef { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _keys; }
        internal TValue[] ValuesRef { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _values; }
        internal int[] HashesRef { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _hashes; }
        internal int Mask { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _hashMask; }
        internal int Threshold { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _resizeThreshold; }

        public HashMap(int initialCapacity = 16, IEqualityComparer<TKey>? comparer = null, bool allowResize = true)
        {
            if (initialCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            int capacity = Tools.NextPowerOfTwo(initialCapacity < 4 ? 4 : initialCapacity);
            _keys = ArrayPool<TKey>.Shared.Rent(capacity);
            _values = ArrayPool<TValue>.Shared.Rent(capacity);
            _hashes = ArrayPool<int>.Shared.Rent(capacity);
            Array.Fill(_hashes, -1, 0, capacity);

            _cap = capacity;
            _hashMask = capacity - 1;
            _resizeThreshold = (int)(capacity * MaxLoad);
            _count = 0;
            Comparer = comparer ?? EqualityComparer<TKey>.Default;
            _allowResize = allowResize;
        }

        // ===================== Public API =====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            int mixed = ComputeMixedHash(key);
            return InsertOrUpdateInternal(key, value, mixed, updateIfExists: false, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAddKnownHash(TKey key, in TValue value, int mixedHash)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            return InsertOrUpdateInternal(key, value, mixedHash, updateIfExists: false, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryUpdate(TKey key, in TValue newValue)
        {
            int mixed = ComputeMixedHash(key);
            int idx = FindIndexInternal(key, mixed);
            if (idx < 0) return false;
            _values[idx] = newValue;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(TKey key, in TValue value)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            int mixed = ComputeMixedHash(key);
            InsertOrUpdateInternal(key, value, mixed, updateIfExists: true, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdateKnownHash(TKey key, in TValue value, int mixedHash)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            InsertOrUpdateInternal(key, value, mixedHash, updateIfExists: true, out _);
        }

        public TValue AddOrUpdate(TKey key, in TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            int mixed = ComputeMixedHash(key);
            int idx = FindIndexInternal(key, mixed);
            if (idx >= 0)
            {
                TValue val = updateValueFactory(key, _values[idx]);
                _values[idx] = val;
                return val;
            }
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            InsertOrUpdateInternal(key, addValue, mixed, updateIfExists: false, out _);
            return addValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue GetOrAdd(TKey key, in TValue addValue)
        {
            int mixed = ComputeMixedHash(key);
            int idx = FindIndexInternal(key, mixed);
            if (idx >= 0) return _values[idx];

            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            InsertOrUpdateInternal(key, addValue, mixed, updateIfExists: false, out _);
            return addValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            int mixed = ComputeMixedHash(key);
            int idx = FindIndexInternal(key, mixed);
            if (idx >= 0) return _values[idx];

            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            TValue val = valueFactory(key);
            InsertOrUpdateInternal(key, val, mixed, updateIfExists: false, out _);
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            int mixed = ComputeMixedHash(key);
            return TryGetValueKnownHash(key, mixed, out value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValueKnownHash(TKey key, int mixedHash, out TValue value)
        {
            int idx = FindIndexInternal(key, mixedHash);
            if (idx >= 0)
            {
                value = _values[idx];
                return true;
            }
            value = default!;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key)
        {
            return FindIndexInternal(key, ComputeMixedHash(key)) >= 0;
        }

        public TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int idx = FindIndexInternal(key, ComputeMixedHash(key));
                if (idx < 0) throw new KeyNotFoundException();
                return _values[idx];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => AddOrUpdate(key, value);
        }

        public bool TryRemove(TKey key, out TValue removed)
        {
            int mixed = ComputeMixedHash(key);
            return TryRemoveKnownHash(key, mixed, out removed);
        }

        public bool TryRemoveKnownHash(TKey key, int mixedHash, out TValue removed)
        {
            int idx = FindIndexInternal(key, mixedHash);
            if (idx < 0) { removed = default!; return false; }

            removed = _values[idx];
            BackshiftDeleteInternal(idx);
            _count--;
            return true;
        }

        public void Clear()
        {
            Array.Fill(_hashes, -1, 0, _cap);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) Array.Clear(_keys, 0, _cap);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()) Array.Clear(_values, 0, _cap);
            _count = 0;
        }

        public void EnsureCapacity(int min)
        {
            if (min <= _cap) return;
            int newCap = Tools.NextPowerOfTwo(min < 4 ? 4 : min);
            ResizeTo(newCap);
        }

        public void TrimExcess()
        {
            int target = _count == 0 ? 4 : Tools.NextPowerOfTwo((int)Math.Ceiling(_count / (double)MaxLoad));
            if (target < 4) target = 4;
            if (target >= _cap) return;
            ResizeTo(target);
        }

        public void Reset(int newInitialCapacity = 4)
        {
            ArrayPool<TKey>.Shared.Return(_keys, RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
            ArrayPool<TValue>.Shared.Return(_values, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
            ArrayPool<int>.Shared.Return(_hashes);

            int capacity = Tools.NextPowerOfTwo(newInitialCapacity < 4 ? 4 : newInitialCapacity);
            _keys = ArrayPool<TKey>.Shared.Rent(capacity);
            _values = ArrayPool<TValue>.Shared.Rent(capacity);
            _hashes = ArrayPool<int>.Shared.Rent(capacity);
            Array.Fill(_hashes, -1, 0, capacity);

            _cap = capacity;
            _hashMask = capacity - 1;
            _resizeThreshold = (int)(capacity * MaxLoad);
            _count = 0;
        }

        // ===================== Snapshot / Copy =====================

        public ReadOnlySpan<TKey> CopyTo(Span<TKey> buffer)
        {
            int target = Math.Min(_count, buffer.Length);
            if (target == 0) return ReadOnlySpan<TKey>.Empty;
            int written = 0;
            for (int i = 0; i < _cap && written < target; i++)
            {
                if (_hashes[i] >= 0) buffer[written++] = _keys[i];
            }
            return buffer.Slice(0, written);
        }

        public ReadOnlySpan<TValue> CopyTo(Span<TValue> buffer)
        {
            int target = Math.Min(_count, buffer.Length);
            if (target == 0) return ReadOnlySpan<TValue>.Empty;
            int written = 0;
            for (int i = 0; i < _cap && written < target; i++)
            {
                if (_hashes[i] >= 0) buffer[written++] = _values[i];
            }
            return buffer.Slice(0, written);
        }

        public ReadOnlySpan<KeyValuePair<TKey, TValue>> CopyTo(Span<KeyValuePair<TKey, TValue>> buffer)
        {
            int target = Math.Min(_count, buffer.Length);
            if (target == 0) return ReadOnlySpan<KeyValuePair<TKey, TValue>>.Empty;
            int written = 0;
            for (int i = 0; i < _cap && written < target; i++)
            {
                if (_hashes[i] >= 0) buffer[written++] = new KeyValuePair<TKey, TValue>(_keys[i], _values[i]);
            }
            return buffer.Slice(0, written);
        }

        public ArrayList<TKey> CopyKeys()
        {
            TKey[] buffer = ArrayPool<TKey>.Shared.Rent(_count);
            var span = CopyTo(buffer);
            return new ArrayList<TKey>(buffer, span.Length);
        }

        public ArrayList<TValue> CopyValues()
        {
            TValue[] buffer = ArrayPool<TValue>.Shared.Rent(_count);
            var span = CopyTo(buffer);
            return new ArrayList<TValue>(buffer, span.Length);
        }

        public ArrayList<KeyValuePair<TKey, TValue>> Copy()
        {
            var buffer = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(_count);
            var span = CopyTo(buffer);
            return new ArrayList<KeyValuePair<TKey, TValue>>(buffer, span.Length);
        }

        public Enumerator GetEnumerator() => new Enumerator(Copy());

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private ArrayList<KeyValuePair<TKey, TValue>> _snap;
            private int _idx;
            private KeyValuePair<TKey, TValue> _curr;
            internal Enumerator(ArrayList<KeyValuePair<TKey, TValue>> snap) { _snap = snap; _idx = -1; _curr = default; }
            public bool MoveNext()
            {
                if (++_idx < _snap.Count) { _curr = _snap[_idx]; return true; }
                return false;
            }
            public KeyValuePair<TKey, TValue> Current => _curr;
            object System.Collections.IEnumerator.Current => _curr;
            public void Reset() => _idx = -1;
            public void Dispose() => _snap.Dispose();
        }

        // ===================== Ref API =====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue TryAddRef(TKey key, out bool success)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            int mixed = ComputeMixedHash(key);
            return ref TryAddRefKnownHash(key, mixed, out success);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue TryAddRefKnownHash(TKey key, int mixedHash, out bool success)
        {
            int idx;
            if (InsertOrUpdateInternal(key, default!, mixedHash, updateIfExists: false, out idx))
            {
                success = true;
                return ref _values[idx];
            }
            success = false;
            return ref Unsafe.NullRef<TValue>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAddRef(TKey key, out bool found)
        {
            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            int mixed = ComputeMixedHash(key);
            return ref GetOrAddRefKnownHash(key, mixed, out found);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAddRefKnownHash(TKey key, int mixedHash, out bool found)
        {
            int idx = FindIndexInternal(key, mixedHash);
            if (idx >= 0)
            {
                found = true;
                return ref _values[idx];
            }

            if (_count + 1 > _resizeThreshold) ResizeOrFail();
            InsertOrUpdateInternal(key, default!, mixedHash, updateIfExists: false, out idx);
            found = false;
            return ref _values[idx];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue TryGetValueRef(TKey key, out bool found)
        {
            int idx = FindIndexInternal(key, ComputeMixedHash(key));
            if (idx >= 0) { found = true; return ref _values[idx]; }
            found = false;
            return ref Unsafe.NullRef<TValue>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue TryGetValueRefKnownHash(TKey key, int mixedHash, out bool found)
        {
            int idx = FindIndexInternal(key, mixedHash);
            if (idx >= 0) { found = true; return ref _values[idx]; }
            found = false;
            return ref Unsafe.NullRef<TValue>();
        }

        public ref TValue GetValueRefOrNullRefKnownHash(TKey key, int mixedHash)
        {
            int idx = FindIndexInternal(key, mixedHash);
            if (idx >= 0) return ref _values[idx];
            return ref Unsafe.NullRef<TValue>();
        }

        // ===================== Core Logic (Fixed) =====================

        /// <summary>
        /// Insert/Update logic with fix for Ref return accuracy.
        /// Tracks the insertion index so Ref returns point to the user's key, not the last swapped victim.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal bool InsertOrUpdateInternal(TKey key, TValue value, int mixedHash, bool updateIfExists, out int finalIndex)
        {
            int mask = _hashMask;
            int index = mixedHash & mask;
            int dist = 0;

            TKey currKey = key;
            TValue currVal = value;
            int currHash = mixedHash;

            int insertionIndex = -1; // FIXED: Tracks where the *original* key lands

            while (true)
            {
                int slotHash = _hashes[index];

                // 1. Empty Slot
                if (slotHash == -1)
                {
                    _keys[index] = currKey;
                    _values[index] = currVal;
                    _hashes[index] = currHash;
                    _count++;

                    // If insertionIndex was set during a previous swap, return that. 
                    // If not, this is the first write, so return current index.
                    finalIndex = (insertionIndex == -1) ? index : insertionIndex;
                    return true;
                }

                // 2. Existing Match (only if we haven't swapped yet)
                if (insertionIndex == -1 && slotHash == currHash && Comparer.Equals(_keys[index], currKey))
                {
                    if (updateIfExists) _values[index] = currVal;
                    finalIndex = index;
                    return false;
                }

                // 3. Robin Hood Swap
                int probeDist = (index - (slotHash & mask)) & mask;
                if (probeDist < dist)
                {
                    TKey tmpK = _keys[index]; _keys[index] = currKey; currKey = tmpK;
                    TValue tmpV = _values[index]; _values[index] = currVal; currVal = tmpV;
                    int tmpH = _hashes[index]; _hashes[index] = currHash; currHash = tmpH;

                    dist = probeDist;

                    // FIXED: If this is the first swap, the user's key lives HERE now.
                    if (insertionIndex == -1) insertionIndex = index;
                }

                index = (index + 1) & mask;
                dist++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal int FindIndexInternal(TKey key, int mixedHash)
        {
            int mask = _hashMask;
            int index = mixedHash & mask;
            int dist = 0;

            while (true)
            {
                int slotHash = _hashes[index];
                if (slotHash == -1) return -1;
                if (slotHash == mixedHash && Comparer.Equals(_keys[index], key)) return index;

                int probeDist = (index - (slotHash & mask)) & mask;
                if (probeDist < dist) return -1;

                index = (index + 1) & mask;
                dist++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BackshiftDeleteInternal(int startIdx)
        {
            int mask = _hashMask;
            int idx = startIdx;
            int next = (idx + 1) & mask;

            while (true)
            {
                int slotHash = _hashes[next];
                if (slotHash == -1) break;

                int ideal = slotHash & mask;
                int dist = (next - ideal) & mask;
                if (dist == 0) break;

                _keys[idx] = _keys[next];
                _values[idx] = _values[next];
                _hashes[idx] = slotHash;

                idx = next;
                next = (next + 1) & mask;
            }

            _hashes[idx] = -1;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) _keys[idx] = default!;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()) _values[idx] = default!;
        }

        private void ResizeOrFail()
        {
            if (!_allowResize) throw new InvalidOperationException("HFT Map full");
            int newCap = _cap << 1;
            if (newCap > (1 << 29)) throw new OutOfMemoryException();
            ResizeTo(newCap);
        }

        internal void ResizeTo(int newCapacity)
        {
            var oldKeys = _keys;
            var oldVals = _values;
            var oldHashes = _hashes;
            int oldCap = _cap;

            _keys = ArrayPool<TKey>.Shared.Rent(newCapacity);
            _values = ArrayPool<TValue>.Shared.Rent(newCapacity);
            _hashes = ArrayPool<int>.Shared.Rent(newCapacity);
            Array.Fill(_hashes, -1, 0, newCapacity);

            _cap = newCapacity;
            _hashMask = newCapacity - 1;
            _resizeThreshold = (int)(newCapacity * MaxLoad);

            // Re-insert 
            for (int i = 0; i < oldCap; i++)
            {
                if (oldHashes[i] >= 0)
                {
                    TKey k = oldKeys[i];
                    TValue v = oldVals[i];
                    int h = oldHashes[i];
                    int idx = h & _hashMask;
                    int dist = 0;

                    while (true)
                    {
                        if (_hashes[idx] == -1)
                        {
                            _keys[idx] = k; _values[idx] = v; _hashes[idx] = h;
                            break;
                        }

                        int probe = (idx - (_hashes[idx] & _hashMask)) & _hashMask;
                        if (probe < dist)
                        {
                            TKey tk = _keys[idx]; _keys[idx] = k; k = tk;
                            TValue tv = _values[idx]; _values[idx] = v; v = tv;
                            int th = _hashes[idx]; _hashes[idx] = h; h = th;
                            dist = probe;
                        }
                        idx = (idx + 1) & _hashMask;
                        dist++;
                    }
                }
            }

            ArrayPool<TKey>.Shared.Return(oldKeys, RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
            ArrayPool<TValue>.Shared.Return(oldVals, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
            ArrayPool<int>.Shared.Return(oldHashes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ComputeMixedHash(TKey key)
        {
            if (typeof(TKey) == typeof(int)) return MixHash(Unsafe.As<TKey, int>(ref key));
            if (typeof(TKey) == typeof(long))
            {
                long v = Unsafe.As<TKey, long>(ref key);
                return MixHash((int)(v ^ (v >> 32)));
            }
            if (typeof(TKey) == typeof(ulong))
            {
                ulong v = Unsafe.As<TKey, ulong>(ref key);
                return MixHash((int)(v ^ (v >> 32)));
            }
            return MixHash(Comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MixHash(int h)
        {
            unchecked
            {
                h ^= (int)((uint)h >> 16);
                h *= -2048144789;
                h ^= (int)((uint)h >> 13);
                h *= -1028477387;
                h ^= (int)((uint)h >> 16);
                return h & 0x7FFFFFFF;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<TKey>.Shared.Return(_keys, RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
            ArrayPool<TValue>.Shared.Return(_values, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
            ArrayPool<int>.Shared.Return(_hashes);
        }
    }

}