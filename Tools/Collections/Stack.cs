using System;
using System.Runtime.CompilerServices;

namespace Tools;

public sealed class Stack<T> where T : struct
{
    private readonly T[] _buffer;
    private int _top;

    public Stack(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
        _top = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T value)
    {
        if (_top >= _buffer.Length)
            throw new InvalidOperationException("Stack overflow");
        _buffer[_top++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Pop()
    {
        if (_top == 0)
            throw new InvalidOperationException("Stack underflow");
        return _buffer[--_top];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Peek()
    {
        if (_top == 0)
            throw new InvalidOperationException("Stack is empty");
        return _buffer[_top - 1];
    }

    public int Count => _top;
    public int Capacity => _buffer.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _top = 0;
}
