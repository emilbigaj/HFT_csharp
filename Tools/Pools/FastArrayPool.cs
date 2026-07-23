//BEGIN_FILE HFT/HFT/Tools/Pools/FastArrayPool.cs
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tools;

/// <summary>
/// Plain, single-thread, ultra-low-overhead array pool.
/// - Struct-based buckets to eliminate double-indirection.
/// - Optimized BitOperations.Log2 math for bucket calculation.
/// - Eliminates memory leaks from retained popped references.
/// </summary>
public sealed class FastArrayPool<T>
{
    private struct Bucket
    {
        public readonly int ArrayLength;
        public T[][] Stack;
        public int Count;

        public Bucket(int arrayLength)
        {
            ArrayLength = arrayLength;
            Stack = Array.Empty<T[]>();
            Count = 0;
        }
    }

    private readonly Bucket[] _buckets;
    private readonly int _maxBucketIndex;

    // JIT-time, per-T decision. For byte/int/... this becomes a constant false.
    private static readonly bool s_needsClear = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public FastArrayPool(int maxLength = 1 << 16)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        int roundedMax = Tools.NextPowerOfTwo(maxLength);
        int bucketCount = BitOperations.Log2((uint)roundedMax) + 1;
        _maxBucketIndex = bucketCount - 1;

        _buckets = new Bucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            _buckets[i] = new Bucket(1 << i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
        {
            minimumLength = 1;
        }

        int bucketIndex = GetBucketIndex(minimumLength);
        if (bucketIndex > _maxBucketIndex)
        {
            return new T[minimumLength];
        }

        // Mutate the struct in-place directly in the array memory
        ref Bucket bucket = ref _buckets[bucketIndex];

        int count = bucket.Count;
        if (count == 0)
        {
            return new T[bucket.ArrayLength];
        }

        count--;
        bucket.Count = count;
        T[] array = bucket.Stack[count];

        // CRITICAL: Clear the slot so the pool doesn't act as a GC Root 
        // for the rented array if the caller drops the reference.
        bucket.Stack[count] = null!;

        return array;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T[] array)
    {
        if (array == null)
        {
            return;
        }

        int length = array.Length;
        int bucketIndex = GetBucketIndex(length);

        if (bucketIndex > _maxBucketIndex)
        {
            return;
        }

        if (s_needsClear)
        {
            Array.Clear(array, 0, length);
        }

        ref Bucket bucket = ref _buckets[bucketIndex];
        int count = bucket.Count;

        if (count == bucket.Stack.Length)
        {
            Grow(ref bucket, count + 1);
        }

        bucket.Stack[count] = array;
        bucket.Count = count + 1;
    }

    public void WarmUp(int length, int count)
    {
        if (length <= 0 || count <= 0)
        {
            return;
        }

        int bucketIndex = GetBucketIndex(length);
        if (bucketIndex > _maxBucketIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "WarmUp length is larger than pool maxLength.");
        }

        ref Bucket bucket = ref _buckets[bucketIndex];

        if (count > bucket.Stack.Length)
        {
            Grow(ref bucket, count);
        }

        for (int i = bucket.Count; i < count; i++)
        {
            bucket.Stack[i] = new T[bucket.ArrayLength];
        }

        bucket.Count = count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBucketIndex(int length)
    {
        // 1 -> 0
        // 2 -> 1
        // 3, 4 -> 2
        // 5..8 -> 3
        if (length <= 1)
        {
            return 0;
        }

        return BitOperations.Log2((uint)length - 1u) + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(ref Bucket bucket, int requiredCapacity)
    {
        int newCapacity = bucket.Stack.Length == 0 ? 4 : bucket.Stack.Length * 2;
        if (newCapacity < requiredCapacity)
        {
            newCapacity = requiredCapacity;
        }

        T[][] newStack = new T[newCapacity][];
        if (bucket.Count != 0)
        {
            Array.Copy(bucket.Stack, 0, newStack, 0, bucket.Count);
        }

        bucket.Stack = newStack;
    }
}
//END_FILE HFT/HFT/Tools/Pools/FastArrayPool.cs