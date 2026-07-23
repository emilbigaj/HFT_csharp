using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Fixed-capacity map + free-ID stack.
/// O(1) rent/return, ref index access.
/// Throws on access to unused slots.
/// </summary>
public sealed class StackMap<T>
{
    private readonly int _capacity;
    private readonly T[] _items;     // id → object
    private readonly int[] _stack;   // free-ID stack (LIFO)
    private int _stackTop;
    private readonly bool[] _used;   // slot state

    public int Capacity => _capacity;

    public StackMap(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _items = new T[capacity];
        _stack = new int[capacity];
        _used = new bool[capacity];

        int top = 0;
        for (int i = capacity - 1; i >= 0; i--)
        {
            _stack[top] = i;
            top++;
        }
        _stackTop = top;
    }

    /// <summary>
    /// Ref indexer. Throws if slot is not in use.
    /// </summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!_used[index])
                ThrowSlotNotInUse(index);
            return ref _items[index];
        }
    }

    /// <summary>Rent a free ID. Returns true if successful.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRent(out int id)
    {
        if (_stackTop == 0)
        {
            id = -1;
            return false;
        }

        _stackTop--;
        id = _stack[_stackTop];
        _used[id] = true;
        return true;
    }

    /// <summary>Return an ID to the pool and clear the stored object.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(int id)
    {
        if (!_used[id])
            ThrowSlotNotInUse(id);

        _used[id] = false;
        _items[id] = default!;
        _stack[_stackTop] = id;
        _stackTop++;
    }

    /// <summary>
    /// Returns a copy. Throws if slot unused.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get(int id)
    {
        if (!_used[id])
            ThrowSlotNotInUse(id);
        return _items[id];
    }

    /// <summary>
    /// Ref access. Throws if slot unused.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef(int id)
    {
        if (!_used[id])
            ThrowSlotNotInUse(id);
        return ref _items[id];
    }

    /// <summary>
    /// Assign a value to an ID. Throws if slot unused.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int id, T value)
    {
        if (!_used[id])
            ThrowSlotNotInUse(id);
        _items[id] = value;
    }

    /// <summary>True if slot currently in use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(int id)
    {
        return _used[id];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSlotNotInUse(int id)
    {
        throw new InvalidOperationException("StackMap slot not in use: " + id);
    }

    // ------------------------
    // foreach support
    // ------------------------

    /// <summary>
    /// Enumerates values in all used slots.
    /// </summary>
    public ValueEnumerator EnumerateValues() => new ValueEnumerator(this);

    public struct ValueEnumerator : IEnumerator<T>
    {
        public IEnumerator<T> GetEnumerator() => this;
        private readonly StackMap<T> _map;
        private int _index;
        private T _current;

        internal ValueEnumerator(StackMap<T> map)
        {
            _map = map;
            _index = -1;
            _current = default!;
        }

        public T Current => _current;
        object IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            while (true)
            {
                _index++;
                if (_index >= _map._capacity)
                {
                    _current = default!;
                    return false;
                }

                if (_map._used[_index])
                {
                    _current = _map._items[_index];
                    return true;
                }
            }
        }

        public void Reset()
        {
            _index = -1;
            _current = default!;
        }

        public void Dispose()
        {
        }
    }


    /// <summary>
    /// Enumerates ONLY the used slot indexes.
    /// </summary>
    public KeyEnumerator EnumerateKeys() => new KeyEnumerator(this);

    public struct KeyEnumerator : IEnumerator<int>
    {
        public IEnumerator<int> GetEnumerator() => this;
        private readonly StackMap<T> _map;
        private int _index;
        private int _current;

        internal KeyEnumerator(StackMap<T> map)
        {
            _map = map;
            _index = -1;
            _current = -1;
        }

        public int Current => _current;
        object IEnumerator.Current => _current;

        public bool MoveNext()
        {
            while (++_index < _map._capacity)
            {
                if (_map._used[_index])
                {
                    _current = _index;
                    return true;
                }
            }
            return false;
        }

        public void Reset()
        {
            _index = -1;
            _current = -1;
        }

        public void Dispose() { }
    }
}
