using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools;

/// <summary>
/// Static wrapper that gives each thread its own private <see cref="FastArrayPool{T}"/>.
/// This keeps the super-fast non-thread-safe design, but avoids accidental cross-thread use.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public static class ThreadArrayPool<T>
{
    [ThreadStatic]
    private static FastArrayPool<T>? s_threadLocal;

    private const int s_defaultMaxLength = 1 << 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FastArrayPool<T> GetOrCreate()
    {
        FastArrayPool<T>? pool = s_threadLocal;
        if (pool == null)
        {
            pool = new FastArrayPool<T>(s_defaultMaxLength);
            s_threadLocal = pool;
        }
        return pool;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] Rent(int minimumLength)
    {
        FastArrayPool<T> pool = GetOrCreate();
        return pool.Rent(minimumLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(T[] array)
    {
        FastArrayPool<T> pool = GetOrCreate();
        pool.Return(array);
    }

    /// <summary>
    /// Warm up the pool for the current thread only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarmUp(int length, int count)
    {
        FastArrayPool<T> pool = GetOrCreate();
        pool.WarmUp(length, count);
    }
}