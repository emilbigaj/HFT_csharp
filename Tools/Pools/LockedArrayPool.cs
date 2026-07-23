using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools;

/// <summary>
/// Thread-safe array pool that wraps a plain <see cref="FastArrayPool{T}"/> and protects
/// all mutations with a seq lock.
/// 
/// Good when:
///  - you have multiple producers/consumers hitting the same pool,
///  - but you still want the same bucketed, LIFO, power-of-two semantics.
/// 
/// NOTE:
///  - This is not "wait-free"; writers spin if another writer is active.
///  - Rent/Return are short critical sections, so spin is acceptable for HFT-style queues.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public sealed class LockedArrayPool<T>
{
    private readonly FastArrayPool<T> _inner;
    private readonly ISeqLockWriter _writer;

    /// <summary>
    /// Create a seq-lock protected array pool.
    /// </summary>
    /// <param name="maxLength">Max array length to cache (elements, not bytes).</param>
    /// <param name="isMultiWriter">true → use CAS-based writer; false → single-writer fast writer.</param>
    public LockedArrayPool(int maxLength = 1 << 16, bool isMultiWriter = true)
    {
        _inner = new FastArrayPool<T>(maxLength);
        _writer = isMultiWriter ? new MultiSeqLockWriter() : new SingleSeqLockWriter();
    }

    /// <summary>
    /// Rent with seq-lock. Spins if another writer is active.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] Rent(int minimumLength)
    {
        _writer.BeginWrite();
        try
        {
            return _inner.Rent(minimumLength);
        }
        finally
        {
            _writer.EndWrite();
        }
    }

    /// <summary>
    /// Return with seq-lock. Spins if another writer is active.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T[] array)
    {
        _writer.BeginWrite();
        try
        {
            _inner.Return(array);
        }
        finally
        {
            _writer.EndWrite();
        }
    }

    /// <summary>
    /// Warm up with seq-lock. Intended to be called rarely (startup).
    /// </summary>
    public void WarmUp(int length, int count)
    {
        _writer.BeginWrite();
        try
        {
            _inner.WarmUp(length, count);
        }
        finally
        {
            _writer.EndWrite();
        }
    }
}