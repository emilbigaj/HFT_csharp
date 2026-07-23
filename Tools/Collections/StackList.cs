using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tools;


public ref struct StackList<T>
{
    private Span<T> Span;

    public int Count { get; private set; } = 0;
    public int Capacity => Span.Length;


    [Obsolete("Do not use parameterless ctor. Use StackList(Span<T> buffer).", error: true)]
    public StackList() => throw new NotSupportedException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StackList(Span<T> buffer)
    {
        Span = buffer;
    }

    // ----- Core ops -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
    {
        if ((uint)Count >= (uint)Span.Length) ThrowOutOfSpace();
        Span[Count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Add()
    {
        if ((uint)Count >= (uint)Span.Length) ThrowOutOfSpace();
        return ref Span[Count++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InsertAt(int index, in T value)
    {
        int count = Count;
        if ((uint)index > (uint)count) ThrowOutOfRange();
        if ((uint)count >= (uint)Span.Length) ThrowOutOfSpace();

        // Fast-path append
        if (index == count)
        {
            Span[count] = value;
            Count = count + 1;
            return;
        }

        // Shift right by one: [index..count-1] -> [index+1..count]
        Span.Slice(index, count - index).CopyTo(Span.Slice(index + 1, count - index));

        // Write new value
        Span[index] = value;
        Count = count + 1;
    }

    // O(1) unordered removal (swap-remove)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SwapRemoveAt(int index)
    {
        int last = Count - 1;
        if ((uint)index > (uint)last) ThrowOutOfRange();
        if (index < last) Span[index] = Span[last];
        Count = last; // no clearing — typically backed by stackalloc
    }

    // Search + swap-remove
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SwapRemove(T item)
    {
        var eq = EqualityComparer<T>.Default;
        for (int i = 0; i < Count; i++)
        {
            if (eq.Equals(Span[i], item))
            {
                SwapRemoveAt(i);
                return true;
            }
        }
        return false;
    }

    // Optional ordered removal (O(n)) — keep if you ever need stability
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveAt(int index)
    {
        int last = Count - 1;
        if ((uint)index > (uint)last) ThrowOutOfRange();
        if (index < last)
            Span.Slice(index + 1, last - index).CopyTo(Span.Slice(index));
        Count = last;
    }

    // ----- Accessors -----

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Count) ThrowOutOfRange();
            return ref Span[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan() => Span.Slice(0, Count);

    // ----- Sorts (in-place, zero-alloc) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort()
    {
        if (Count <= 1) return;
        MemoryExtensions.Sort(Span.Slice(0, Count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort(IComparer<T> comparer)
    {
        if (Count <= 1) return;
        MemoryExtensions.Sort(Span.Slice(0, Count), comparer);
    }

    // Avoids interface dispatch when TComparer is a struct
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sort<TComparer>(TComparer comparer) where TComparer : IComparer<T>
    {
        if (Count <= 1) return;
        MemoryExtensions.Sort(Span.Slice(0, Count), comparer);
    }

    // ----- Zero-alloc foreach support -----

    public ref struct Enumerator
    {
        private readonly Span<T> _s;
        private int _i;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator(Span<T> s)
        {
            _s = s;
            _i = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_i < _s.Length;

        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _s[_i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new Enumerator(Span.Slice(0, Count));

    // ----- Errors -----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfRange() => throw new ArgumentOutOfRangeException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfSpace() => throw new InvalidOperationException("Fixed capacity exceeded");
}

