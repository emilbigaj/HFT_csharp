using System;
using System.Runtime.CompilerServices;

namespace Tools
{
    /// <summary>
    /// Non-thread-safe pool: LIFO stack of T.
    /// - No object creation/reset logic inside; caller handles that.
    /// - Return() grows capacity if needed.
    /// - Rent clears the popped slot to avoid GC retention.
    /// </summary>
    public sealed class ObjectPool<T>
    {
        private T[] _items;
        private int _count; // number of available items on the stack

        public int Count => _count;          // available items
        public int Capacity => _items.Length;   // internal stack capacity

        public ObjectPool(int initialCapacity = 64)
        {
            if (initialCapacity < 0) initialCapacity = 64;
            _items = GC.AllocateUninitializedArray<T>(initialCapacity);
            _count = 0;
        }

        /// <summary>Try to pop an item; returns false if empty.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRent(out T item)
        {
            if (_count > 0)
            {
                int idx = --_count;
                item = _items[idx]!;
                _items[idx] = default!; // drop reference for GC
                return true;
            }
            item = default!;
            return false;
        }

        /// <summary>Push an item; grows if full.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T item)
        {
            // Ignore null for reference types; caller controls semantics.
            if (item is null) return;

            if (_count == _items.Length)
                Grow(_count + 1);

            _items[_count++] = item;
        }

        /// <summary>Push many items; grows once if needed.</summary>
        public void ReturnRange(ReadOnlySpan<T> items)
        {
            if (items.Length == 0) return;

            int needed = _count + items.Length;
            if (needed > _items.Length) Grow(needed);

            // push in order (LIFO nature means last returned will be popped first)
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                if (it is null) continue;
                _items[_count++] = it!;
            }
        }

        public void EnsureCapacity(int min)
        {
            if (min > _items.Length) Grow(min);
        }

        /// <summary>Drop all retained refs. Optionally clear array contents.</summary>
        public void Clear(bool clearArray = true)
        {
            if (clearArray && _count > 0)
                Array.Clear(_items, 0, _count);
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int min)
        {
            int cur = _items.Length;
            int newSize = _items.Length * 2;

            var arr = GC.AllocateUninitializedArray<T>(newSize);
            Array.Copy(_items, 0, arr, 0, _count);
            _items = arr;
        }
    }
}
