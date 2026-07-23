using System;
using System.Runtime.CompilerServices;

namespace Tools
{
    public unsafe struct UnsafeStackList<T> where T : unmanaged
    {
        private readonly T* _ptr;
        private int _count;

        public int Count => _count;

        // Expose Raw Pointer for manual optimization loops
        public T* Ptr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _ptr;
        }

        // Implicit conversion to T* for cleaner syntax
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator T*(UnsafeStackList<T> list) => list._ptr;

        // ---------------------------------------------------------------------
        // Constructor 1: Initialize Empty (Count = 0)
        // Use this when you plan to use .Add()
        // ---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public UnsafeStackList(T* ptr)
        {
            _ptr = ptr;
            _count = 0;
        }

        // ---------------------------------------------------------------------
        // Constructor 2: Initialize Full/Sized (Count = N)
        // Use this when you treat it as an array and access via [i]
        // ---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public UnsafeStackList(T* ptr, int count)
        {
            _ptr = ptr;
            _count = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T item)
        {
            _ptr[_count++] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Add()
        {
            return ref _ptr[_count++];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InsertionSort<TComparer>(TComparer comparer) where TComparer : System.Collections.Generic.IComparer<T>
        {
            for (int i = 1; i < _count; i++)
            {
                T key = _ptr[i];
                int j = i - 1;

                while (j >= 0 && comparer.Compare(_ptr[j], key) > 0)
                {
                    _ptr[j + 1] = _ptr[j];
                    j--;
                }
                _ptr[j + 1] = key;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SwapRemoveAt(int index)
        {
            _ptr[index] = _ptr[--_count];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T>.Enumerator GetEnumerator()
        {
            return new Span<T>(_ptr, _count).GetEnumerator();
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _ptr[index];
        }
    }
}