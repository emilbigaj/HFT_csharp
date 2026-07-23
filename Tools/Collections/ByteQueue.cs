using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#nullable enable

/// <summary>
/// Ultra-fast single-threaded byte ring queue backed by unmanaged memory.
/// 
/// DESIGN PHILOSOPHY:
/// 1. Zero-Allocation: Uses unmanaged memory (NativeMemory) to avoid GC pressure.
/// 2. Contiguous Payloads: Unlike standard ring buffers, this guarantees that every 
///    payload Enqueued allows you to get a contiguous Span back. 
///    It achieves this by leaving "Gaps" at the end of the buffer if a payload won't fit,
///    jumping the writer to index 0 immediately.
/// 3. Power-of-Two: Capacity is always 2^N to allow fast bitwise masking instead of slow modulo division.
/// </summary>
[SkipLocalsInit]
public unsafe sealed class ByteQueue : IDisposable
{
    // =========================================================================================
    // CONSTANTS & FIELDS
    // =========================================================================================

    // Overhead per message. We store [Length (4 bytes)] [Payload (N bytes)].
    private const nuint s_headerSize = 4;

    // Safety limit to ensure a single message doesn't overflow integer logic.
    private const int s_maxPayload = 0x7FFF_FFFF;

    // A special marker written into the buffer when we skip the end of the ring to wrap around.
    // The reader sees this and knows to jump its cursor to index 0.
    private const uint s_wrapSentinel = 0xFFFF_FFFF;

    // Aligns memory to 64 bytes (standard CPU cache line) to prevent false sharing and unaligned access penalties.
    private const nuint s_defaultAlign = 64;

    private byte* _base;                           // Pointer to the raw unmanaged memory slab.
    private nuint _capacity;                       // Total size (Must be Power of Two).
    private nuint _mask;                           // Bitmask (Capacity - 1). Used to calculate circular indices.

    // Monotonic cursors. They keep growing (don't reset at capacity).
    // The physical index is calculated via (cursor & mask).
    // This simplifies calculating "UsedBytes" (wseq - rseq) without worrying about wrap-around math.
    private nuint _wseq;                           // Write cursor (Total bytes written ever).
    private nuint _rseq;                           // Read cursor (Total bytes read ever).
    private bool _disposed;

    /// <summary>
    /// Initializes the queue. 
    /// WHY: We enforce Power of Two to allow `index & mask` operations, which are 
    /// significantly faster (~1 cycle) than `index % capacity` (div instruction ~20+ cycles).
    /// </summary>
    public ByteQueue(int capacityBytes)
    {
        if ((capacityBytes & (capacityBytes - 1u)) != 0u || capacityBytes < 4096)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Power of two, >= 4096.");

        _capacity = (nuint)capacityBytes;
        _mask = (nuint)capacityBytes - 1u;

        // Allocate unmanaged memory aligned to cache lines.
        _base = (byte*)NativeMemory.AlignedAlloc(_capacity, s_defaultAlign);
        if (_base is null) throw new OutOfMemoryException();

        _wseq = 0u;
        _rseq = 0u;
    }

    // =========================================================================================
    // PRODUCER (WRITER)
    // =========================================================================================

    /// <summary>
    /// The "Hot Path" for writing.
    /// WHY: This method is inlined and contains only the happy-path logic (data fits at the tail).
    /// If complex logic (resize or wrapping) is needed, it offloads to `SlowEnqueue` to keep
    /// the instruction cache hot for the common case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> Enqueue(int length)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ByteQueue));
        // Reject length 0: the reader (TryPeek/Dequeue) treats a len==0 header as corruption and
        // throws, so a zero-length frame would permanently wedge the consumer. Negatives are
        // rejected by the unsigned compare as before.
        if (length <= 0 || (uint)length > s_maxPayload) throw new ArgumentOutOfRangeException(nameof(length), "Length must be in [1, s_maxPayload].");

        nuint need = s_headerSize + (nuint)length;

        // Capture current state
        nuint wseq = _wseq;
        nuint rseq = _rseq;

        // Calculate free space. Note: This works even if pointers have wrapped uint.MaxValue.
        nuint used = wseq - rseq;
        nuint free = _capacity - used;

        // Calculate physical offset in the buffer.
        nuint woff = wseq & _mask;
        nuint tail = _capacity - woff;

        // HAPPY PATH CHECK:
        // 1. Do we have enough total space? (free >= need)
        // 2. Is the contiguous space at the end (tail) large enough? (tail >= need)
        if (tail >= need && free >= need)
        {
            byte* p = _base + woff;

            // Write the header (Length)
            WriteU32(p, (uint)length);

            // Calculate pointer for the actual payload
            byte* payload = p + s_headerSize;

            // Advance the monotonic write cursor
            _wseq = wseq + need;

            // Return Span so caller can write directly into ring buffer (Zero-Copy)
            return new Span<byte>(payload, length);
        }

        // COLD PATH: Buffer full OR we need to wrap around.
        return SlowEnqueue(length, need);
    }

    /// <summary>
    /// Handles edge cases: Resizing the buffer or Wrapping around the ring.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> SlowEnqueue(int length, nuint need)
    {
        nuint wseq = _wseq;
        nuint used = wseq - _rseq;
        nuint free = _capacity - used;

        nuint woff = wseq & _mask;
        nuint tail = _capacity - woff;

        // If the tail can't hold the frame, wrapping to index 0 burns the unused tail as a
        // "gap" (skipped, not reusable for this write), so the real requirement is need + gap.
        // Checking only `free >= need` would let the wrapped write at index 0 run past the
        // freed prefix and clobber the reader's still-unread bytes near a full buffer.
        nuint gap = (tail < need) ? tail : 0;

        // STEP 1: RESIZE CHECK — not enough room even after accounting for the wrap gap.
        if (free < need + gap)
        {
            // Grow + defragment (linearises everything to index 0, stripping gaps), then retry:
            // the recursive call is guaranteed to hit the happy path with no wrap.
            Resize(need);
            return Enqueue(length);
        }

        // STEP 2: WRAP AROUND — enough total space, but the contiguous tail is too small.
        if (gap != 0)
        {
            // If a 4-byte header fits in the gap, stamp the SENTINEL so the reader skips it.
            if (tail >= s_headerSize)
                WriteU32(_base + woff, s_wrapSentinel);

            // Burn the gap and restart the frame at index 0.
            _wseq = wseq + tail;
            wseq = _wseq;
            woff = 0;
        }

        byte* p = _base + woff;
        WriteU32(p, (uint)length);
        byte* payload = p + s_headerSize;

        _wseq = wseq + need;
        return new Span<byte>(payload, length);
    }

    /// <summary>
    /// Allocates a larger buffer and copies active data over.
    /// CRITICAL: This effectively "Defragments" the ring buffer.
    /// It strips out any "Wrap Sentinels/Gaps", producing a perfectly linear buffer.
    /// </summary>
    private void Resize(nuint requiredBytes)
    {
        // Double capacity until it fits the new requirement
        nuint newCapacity = _capacity * 2;
        nuint used = _wseq - _rseq;

        while (newCapacity - used < requiredBytes)
        {
            newCapacity *= 2;
        }

        // Allocate the new Slab
        byte* newBase = (byte*)NativeMemory.AlignedAlloc(newCapacity, s_defaultAlign);
        if (newBase is null) throw new OutOfMemoryException();

        // COPY LOOP:
        // We cannot just `memcpy` because the old data might wrap around or contain "Sentinels".
        // We must walk the old data frame-by-frame (like a reader) and copy it linearly to the new buffer.

        byte* cursorDst = newBase;         // Destination pointer (starts at 0)
        nuint currentR = _rseq;            // Temp read cursor
        nuint currentW = _wseq;            // Temp write cursor
        nuint currentMask = _mask;
        byte* currentBase = _base;
        nuint currentCap = _capacity;

        while (currentR != currentW)
        {
            nuint roff = currentR & currentMask;
            nuint tail = currentCap - roff;

            // CASE A: We are at the end of the buffer, and not even a header fits.
            // This is a "Physical Gap". Skip it.
            if (tail < s_headerSize)
            {
                currentR += tail;
                continue;
            }

            uint len = ReadU32(currentBase + roff);

            // CASE B: We found a SENTINEL.
            // The writer marked this as a gap. Skip it.
            if (len == s_wrapSentinel)
            {
                currentR += tail;
                continue;
            }

            // CASE C: Valid Frame.
            // Copy [Header + Payload] in one shot to the new contiguous block.
            nuint frameSize = s_headerSize + len;
            Unsafe.CopyBlock(cursorDst, currentBase + roff, (uint)frameSize);

            cursorDst += frameSize;
            currentR += frameSize;
        }

        // Cleanup old memory
        NativeMemory.AlignedFree(_base);

        // HOT SWAP
        // Update pointers to use the new larger buffer.
        _base = newBase;
        _capacity = newCapacity;
        _mask = newCapacity - 1;

        // RESET CURSORS
        // Since we copied everything linearly to the start of the new buffer:
        // Read cursor becomes 0.
        // Write cursor becomes the total size of bytes copied.
        _rseq = 0;
        _wseq = (nuint)(cursorDst - newBase);
    }

    // =========================================================================================
    // CONSUMER (READER)
    // =========================================================================================

    /// <summary>
    /// Peeks the next frame without removing it.
    /// Handles jumping over gaps/sentinels transparently.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out Span<byte> src)
    {
        src = Span<byte>.Empty;
        if (_disposed) return false;

        nuint rseq = _rseq;
        nuint wseq = _wseq;
        if (rseq == wseq) return false; // Empty

        nuint roff = rseq & _mask;
        nuint tail = _capacity - roff;

        // CASE 1: Physical Wrap Gap
        // We are near the end, and not even 4 bytes (header) fit. 
        // Writer must have wrapped. We wrap too.
        if (tail < s_headerSize)
        {
            rseq += tail;       // Advance cursor past the gap
            roff = 0;           // Reset to index 0
            if (rseq == wseq) return false; // Check emptiness again
            tail = _capacity;
        }

        uint len = ReadU32(_base + roff);

        // CASE 2: Sentinel Gap
        // We found the 0xFFFFFFFF marker. 
        // Writer had space for header, but not payload, so they marked it and wrapped.
        if (len == s_wrapSentinel)
        {
            rseq += tail;       // Advance cursor past the rest of the buffer
            roff = 0;           // Reset to index 0
            if (rseq == wseq) return false;

            tail = _capacity;
            len = ReadU32(_base + roff); // Read the REAL header at index 0
        }

        // Sanity Check: Protect against memory corruption or bad logic
        if (len == 0 || len > s_maxPayload || (nuint)len + s_headerSize > tail)
            throw new InvalidOperationException("ByteQueue corrupted or misaligned.");

        // Create Span pointing to the payload
        byte* ptr = _base + roff + s_headerSize;
        src = new Span<byte>(ptr, (int)len);
        return true;
    }

    /// <summary>
    /// Consumes the frame we just peeked.
    /// WHY: Separation of Peek and Dequeue allows the consumer to process data 
    /// "in-place" (Zero-Copy) before discarding it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dequeue()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ByteQueue));

        nuint rseq = _rseq;
        nuint wseq = _wseq;
        if (rseq == wseq) throw new InvalidOperationException("ByteQueue is empty.");

        nuint roff = rseq & _mask;
        nuint tail = _capacity - roff;

        // SKIP PHYSICAL GAP (Same logic as Peek)
        if (tail < s_headerSize)
        {
            rseq += tail;
            roff = 0;
            if (rseq == wseq) throw new InvalidOperationException("ByteQueue is empty.");
            tail = _capacity;
        }

        uint len = ReadU32(_base + roff);

        // SKIP SENTINEL GAP (Same logic as Peek)
        if (len == s_wrapSentinel)
        {
            rseq += tail;
            roff = 0;
            if (rseq == wseq) throw new InvalidOperationException("ByteQueue is empty.");
            tail = _capacity;
            len = ReadU32(_base + roff);
        }

        // Validation
        if (len == 0 || len > s_maxPayload || (nuint)len + s_headerSize > tail)
            throw new InvalidOperationException("ByteQueue corrupted or misaligned.");

        // ADVANCE READ CURSOR
        // We effectively "free" this space for the writer to use later.
        _rseq = rseq + s_headerSize + len;
    }

    // =========================================================================================
    // HELPERS & LIFETIME
    // =========================================================================================

    public nuint CapacityBytes => _capacity;

    public nuint UsedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return _wseq - _rseq; }
    }

    public nuint FreeBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return _capacity - UsedBytes; }
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return _wseq == _rseq; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() { _rseq = _wseq; } // Resetting read cursor to write cursor effectively empties queue instantly.

    public void Dispose()
    {
        if (_disposed) return;
        NativeMemory.AlignedFree(_base); // Crucial: Free the unmanaged memory.
        _disposed = true;
    }

    // Low-level helper to write 4 bytes without overhead
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32(void* p, uint v) { Unsafe.WriteUnaligned(ref *(byte*)p, v); }

    // Low-level helper to read 4 bytes without overhead
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(void* p) { return Unsafe.ReadUnaligned<uint>(ref *(byte*)p); }
}