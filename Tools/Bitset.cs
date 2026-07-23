//BEGIN_FILE HFT/Tools/Bitset.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Tools;

/// <summary>
/// Fixed-width 64-bit bitset optimized for ultra-low-latency operations.
/// </summary>
/// <remarks>
/// <para><b>Design goals:</b> branch-minimal hot paths, zero allocations, and predictable codegen.
/// All operations are <c>O(1)</c> unless stated otherwise. 
/// Ideal for order-book masks, level activity tracking, and bit-wise utilities in HFT pipelines.</para>
/// 
/// <para><b>Thread-safety:</b> mutable value type; do not share the same instance across threads 
/// without external synchronization.</para>
///
/// <para><b>Indexing:</b> valid bit indices are 0 – 63 inclusive.
/// Caller is responsible for ensuring valid indices; out-of-range access is undefined.</para>
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public struct Bitset64
{
    private ulong _bits;

    // ─────────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a new bitset with all bits cleared.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64() => _bits = 0UL;

    /// <summary>Creates a new bitset with a specified raw mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bitset64(ulong initial) => _bits = initial;

    /// <summary>Gets or sets the underlying 64-bit mask.</summary>
    public ulong Raw
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _bits = value;
    }

    public readonly int Length => 64;

    // ─────────────────────────────────────────────────────────────────────────
    // Bit access
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Gets or sets the bit at <paramref name="index"/>.</summary>
    public bool this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_bits & (1UL << index)) != 0UL;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            ulong mask = 1UL << index;
            _bits = value ? (_bits | mask) : (_bits & ~mask);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mutations
    // ─────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong AtomicLoad()
    {
        return Volatile.Read(ref _bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AtomicSet(int index)
    {
        ulong mask = 1UL << index;
        Interlocked.Or(ref _bits, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AtomicClear(int index)
    {
        ulong mask = ~(1UL << index);
        Interlocked.And(ref _bits, mask);
    }

    /// <summary>Sets the bit at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index) => _bits |= (1UL << index);

    /// <summary>Clears the bit at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(int index) => _bits &= ~(1UL << index);

    /// <summary>Toggles the bit at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Toggle(int index) => _bits ^= (1UL << index);

    /// <summary>Sets all bits to 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAll() => _bits = ~0UL;

    /// <summary>Clears all bits to 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearAll() => _bits = 0UL;

    /// <summary>Fills all bits with the given value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(bool value) => _bits = value ? ~0UL : 0UL;

    // ─────────────────────────────────────────────────────────────────────────
    // Properties (no-parameter queries)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Number of set bits (population count).</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.PopCount(_bits);
    }

    /// <summary>True if all 64 bits are 0.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == 0UL;
    }

    /// <summary>True if all 64 bits are 1.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == ~0UL;
    }

    /// <summary>Lowest set bit index or −1 if empty.</summary>
    public int LowestSet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == 0UL ? -1 : BitOperations.TrailingZeroCount(_bits);
    }

    /// <summary>Highest set bit index or −1 if empty.</summary>
    public int HighestSet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == 0UL ? -1 : (63 - BitOperations.LeadingZeroCount(_bits));
    }

    /// <summary>
    /// Gets the index of the highest cleared bit (the last '0').
    /// Returns -1 if all bits are set (full).
    /// </summary>
    public int HighestClear
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ulong inverted = ~_bits;
            if (inverted == 0UL) return -1;
            return 63 - BitOperations.LeadingZeroCount(inverted);
        }
    }

    /// <summary>
    /// Gets the index of the lowest cleared bit (the first '0').
    /// Returns -1 if all bits are set (full).
    /// </summary>
    public int LowestClear
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ulong inverted = ~_bits;
            if (inverted == 0UL) return -1;
            return BitOperations.TrailingZeroCount(inverted);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scanning / selection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears all bits whose index is strictly greater than <paramref name="maxIndexInclusive"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearAbove(int maxIndexInclusive)
    {
        if (maxIndexInclusive >= 63) return;
        if (maxIndexInclusive < 0) { _bits = 0UL; return; }
        ulong keepMask = (1UL << (maxIndexInclusive + 1)) - 1UL;
        _bits &= keepMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MaskBetween(int from, int to)
    {
        ulong upToTo = (to == 63) ? ~0UL : ((1UL << (to + 1)) - 1UL);
        ulong fromOn = (from == 0) ? ~0UL : ~((1UL << from) - 1UL);
        return (from <= to) ? (upToTo & fromOn) : (fromOn | upToTo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearOutside(int from, int to)
    {
        ulong keepMask = MaskBetween(from, to);
        _bits &= keepMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearBelow(int minIndexInclusive)
    {
        if (minIndexInclusive <= 0) return;
        if (minIndexInclusive >= 64) { _bits = 0UL; return; }
        _bits &= ~((1UL << minIndexInclusive) - 1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FirstSet(int from)
    {
        if ((uint)from >= 64U) return -1;
        ulong shifted = _bits >> from;
        return shifted == 0UL ? -1 : from + BitOperations.TrailingZeroCount(shifted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Bitset64 RotateRight(int count)
    {
        return new Bitset64(BitOperations.RotateRight(_bits, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopLowest(out int index)
    {
        ulong b = _bits;
        if (b == 0UL) { index = -1; return false; }
        index = BitOperations.TrailingZeroCount(b);
        _bits = b & (b - 1UL);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopHighest(out int index)
    {
        ulong b = _bits;
        if (b == 0UL) { index = -1; return false; }
        index = 63 - BitOperations.LeadingZeroCount(b);
        _bits &= ~(1UL << index);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNthSetBit(int n)
    {
        ulong result = System.Runtime.Intrinsics.X86.Bmi2.X64.ParallelBitDeposit(1UL << n, _bits);
        return BitOperations.TrailingZeroCount(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Relations and operators
    // ─────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Overlaps(Bitset64 other) => (_bits & other._bits) != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSubsetOf(Bitset64 other) => (_bits & ~other._bits) == 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSupersetOf(Bitset64 other) => other.IsSubsetOf(this);

    public static Bitset64 operator &(Bitset64 a, Bitset64 b) => new Bitset64(a._bits & b._bits);
    public static Bitset64 operator |(Bitset64 a, Bitset64 b) => new Bitset64(a._bits | b._bits);
    public static Bitset64 operator ^(Bitset64 a, Bitset64 b) => new Bitset64(a._bits ^ b._bits);
    public static Bitset64 operator ~(Bitset64 a) => new Bitset64(~a._bits);
    public static bool operator ==(Bitset64 a, Bitset64 b) => a._bits == b._bits;
    public static bool operator !=(Bitset64 a, Bitset64 b) => a._bits != b._bits;

    // ─────────────────────────────────────────────────────────────────────────
    // Enumeration
    // ─────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new Enumerator(_bits);

    public struct Enumerator
    {
        private ulong _remaining;
        private int _current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(ulong bits)
        {
            _remaining = bits;
            _current = -1;
        }

        public int Current => _current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_remaining == 0UL) return false;
            int idx = BitOperations.TrailingZeroCount(_remaining);
            _remaining &= _remaining - 1UL;
            _current = idx;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() => this;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Equality / ToString
    // ─────────────────────────────────────────────────────────────────────────

    public override string ToString()
    {
        ulong b = _bits;
        if (b == 0) return "[]";

        var sb = new System.Text.StringBuilder(2 + Count * 4);
        sb.Append("[ ");

        bool first = true;
        while (b != 0)
        {
            int i = BitOperations.TrailingZeroCount(b);
            b &= b - 1;

            if (!first) sb.Append(", ");
            sb.Append(i);
            first = false;
        }

        sb.Append(" ]");
        return sb.ToString();
    }

    public string ToBitString()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
        sb.Append('|');
        for (int i = 0; i < 64; i++)
        {
            sb.Append(' ');
            sb.Append(((_bits >> i) & 1UL) != 0UL ? '1' : '0');
            sb.Append(" |");
        }
        return sb.ToString();
    }
}