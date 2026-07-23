using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Tools;

namespace Socket;

public enum ReadStatus : byte
{
    New = 0,   // Data read successfully and is newer than last observed.
    Old = 1,   // Data read successfully but is stale.
    Empty = 2, // No data available.
    Closed = 3 // Channel is closed.
}

/// <summary>
/// Wire-format codec for the seqlock-protected slot and ring channels. Owns the Header64
/// layout, the magic / wrap-marker constants, and the publish/observe sequence protocol
/// shared with the C++ server (Socket/Protocol.hpp). Pure framing — no mapping, no I/O.
/// </summary>
public static class Protocol
{
    public const ulong s_ringWrapMarker = ulong.MaxValue;
    public const ulong s_magic = 0x48465421_48465421UL;

    public const int CacheLine = 64;
    public const int HeaderLength = CacheLine;

    [StructLayout(LayoutKind.Sequential, Size = CacheLine)]
    public struct Header64
    {
        public ulong Sequence;
        public ulong Magic;
        public int Length;
        public int ObjectType;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetAlignedEntryLength(int valueLength) => (HeaderLength + valueLength + (CacheLine - 1)) & ~(CacheLine - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe T* GetValuePointer<T>(byte* slotPtr) where T : unmanaged => (T*)(slotPtr + HeaderLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsThisNewerThan(this ulong @this, ulong other) => @this > other;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWriteInProgress(this ulong sequence) => (sequence & 1UL) != 0UL;

    // -------- Lock primitives (slot path) --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void AcquireLock(Header64* hdr)
    {
        ulong seq = Volatile.Read(ref hdr->Sequence);
        Volatile.Write(ref hdr->Sequence, seq + 1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ReleaseLock(Header64* hdr)
    {
        ulong seq = Volatile.Read(ref hdr->Sequence);
        Volatile.Write(ref hdr->Sequence, seq + 1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ulong ReadSequence(Header64* hdr) => Volatile.Read(ref hdr->Sequence);

    // -------- Slot Write --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Write<T>(in T obj, Header64* dstHdr, int dstLen) where T : unmanaged
    {
        fixed (T* srcObj = &obj)
        {
            Write((byte*)srcObj, Unsafe.SizeOf<T>(), dstHdr, dstLen);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Write(byte* srcObj, int srcObjLen, Header64* dstHdr, int dstLen)
    {
        // 1. Validate Available Space
        if (dstLen < HeaderLength)
            throw new ArgumentOutOfRangeException(nameof(dstLen));

        byte* dstObj = (byte*)dstHdr + HeaderLength;
        int dstObjLen = dstLen - HeaderLength;

        ulong seq = Volatile.Read(ref dstHdr->Sequence);

        // 2. Acquire: even -> odd
        Volatile.Write(ref dstHdr->Sequence, seq + 1UL);

        // 3. Header metadata + payload copy
        Volatile.Write(ref dstHdr->Magic, s_magic);
        dstHdr->Length = srcObjLen;
        Copy(srcObj, srcObjLen, dstObj, dstObjLen);

        // 4. Release: odd -> even (publish)
        Volatile.Write(ref dstHdr->Sequence, seq + 2UL);
    }

    // -------- Recovery Write (dead-client slot) --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void RecoveryWrite<T>(in T obj, Header64* dstHdr, int dstLen) where T : unmanaged
    {
        fixed (T* srcObj = &obj)
        {
            RecoveryWrite((byte*)srcObj, Unsafe.SizeOf<T>(), dstHdr, dstLen);
        }
    }

    // Like Write() but re-bases the seqlock instead of assuming an even start: a writer that died
    // mid-write can leave the sequence ODD. For recovering a slot whose original writer (a client
    // process) is confirmed dead — see Server.CancelAllOrders. evenBase = next even >= seq+1, so the
    // copy window is always odd (a concurrent reader sees odd -> retries, never tears) and the
    // published value is even and strictly greater than any prior publish (readers observe it New).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void RecoveryWrite(byte* srcObj, int srcObjLen, Header64* dstHdr, int dstLen)
    {
        if (dstLen < HeaderLength)
            throw new ArgumentOutOfRangeException(nameof(dstLen));

        byte* dstObj = (byte*)dstHdr + HeaderLength;
        int dstObjLen = dstLen - HeaderLength;

        // evenBase = next even >= seq+1: seq+1 when seq is odd (crash mid-write), seq when even.
        ulong seq = Volatile.Read(ref dstHdr->Sequence);
        ulong evenBase = (seq + 1UL) & ~1UL;

        Volatile.Write(ref dstHdr->Sequence, evenBase + 1UL); // odd: write in progress
        Volatile.Write(ref dstHdr->Magic, s_magic);
        dstHdr->Length = srcObjLen;
        Copy(srcObj, srcObjLen, dstObj, dstObjLen);
        Volatile.Write(ref dstHdr->Sequence, evenBase + 2UL); // even: published, > old
    }

    // -------- Slot Read --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadStatus TryRead(Header64* srcHdr, Span<byte> dstObj, out ReadOnlySpan<byte> rdstObj, ref ulong lastEvenSeq)
    {
        fixed (byte* dstObjP = dstObj)
        {
            ReadStatus status = TryRead(srcHdr, dstObjP, dstObj.Length, out int srcObjLen, ref lastEvenSeq);
            rdstObj = dstObj.Slice(0, srcObjLen);
            return status;
        }
    }

    public static unsafe ReadStatus TryRead<T>(Header64* srcHdr, out T obj, ref ulong lastEvenSeq) where T : unmanaged
    {
        fixed (T* dstObjP = &obj)
        {
            int sizeOfT = Unsafe.SizeOf<T>();
            ReadStatus status = TryRead(srcHdr, (byte*)dstObjP, sizeOfT, out int srcObjLen, ref lastEvenSeq);

            if ((status == ReadStatus.New || status == ReadStatus.Old) && srcObjLen < sizeOfT)
            {
                throw new InvalidCastException($"Size mismatch: {typeof(T).Name} ({sizeOfT}b) > data ({srcObjLen}b).");
            }

            return status;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadStatus TryRead(Header64* srcHdr, byte* dstObj, int dstObjLen, out int srcObjLen, ref ulong lastEvenSeq)
    {
        byte* srcObj = (byte*)srcHdr + HeaderLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong readSeq()
        {
            ulong seq = Volatile.Read(ref srcHdr->Sequence);
            ulong magic = Volatile.Read(ref srcHdr->Magic);

            if (magic != s_magic)
            {
                return 0UL;
            }
            return seq;
        }

        while (true)
        {
            // 1. Read Initial Sequence
            ulong seq1 = readSeq();

            // 2. Check if empty
            if (seq1 == 0UL)
            {
                srcObjLen = 0;
                return ReadStatus.Empty;
            }

            // 3. Spin if Write is in progress (Odd sequence)
            if (IsWriteInProgress(seq1))
            {
                X86BaseWrapper.ExponentialPause();
                continue;
            }

            srcObjLen = srcHdr->Length;

            // 4. Pre-Copy Validation
            ulong seq2 = Volatile.Read(ref srcHdr->Sequence);
            if (seq1 != seq2)
            {
                continue;
            }

            // 5. Copy Payload Optimistically
            Copy(srcObj, srcObjLen, dstObj, dstObjLen);
            Thread.MemoryBarrier();

            // 6. Post-Copy Validation
            seq2 = Volatile.Read(ref srcHdr->Sequence);
            if (seq1 == seq2)
            {
                // 7. Commit and return status
                if (seq2.IsThisNewerThan(lastEvenSeq))
                {
                    lastEvenSeq = seq2;
                    return ReadStatus.New;
                }
                return ReadStatus.Old;
            }
        }
    }

    // -------- Ring Write --------

    public static unsafe void WriteToRing(ReadOnlySpan<byte> srcObj, ref byte* dst, byte* start, byte* end, ref ulong writerSeqEven)
    {
        fixed (byte* srcObjP = srcObj)
        {
            WriteToRing(srcObjP, srcObj.Length, ref dst, start, end, ref writerSeqEven);
        }
    }

    public static unsafe void WriteToRing<T>(in T obj, ref byte* dst, byte* start, byte* end, ref ulong writerSeqEven) where T : unmanaged
    {
        fixed (T* srcObj = &obj)
        {
            WriteToRing((byte*)srcObj, Unsafe.SizeOf<T>(), ref dst, start, end, ref writerSeqEven);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void WriteToRing(byte* srcObj, int srcObjLen, ref byte* dst, byte* start, byte* end, ref ulong writerSeqEven)
    {
        // 1. Check ring alignment
        if (((ulong)start & 63) != 0 || ((ulong)end & 63) != 0)
        {
            throw new InvalidOperationException("Ring alignment error.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void writeSeq(Header64* hdr, ulong seq)
        {
            Volatile.Write(ref hdr->Magic, s_magic);
            Volatile.Write(ref hdr->Sequence, seq);
        }

        // 2. Calculate Required Space
        int ringSize = (int)(end - start);
        int alignedEntryLength = GetAlignedEntryLength(srcObjLen);

        if (alignedEntryLength >= ringSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ringSize));
        }

        int remaining = (int)(end - dst);
        int require = alignedEntryLength + HeaderLength;

        // 3. Handle Ring Buffer Wrap-Around (if space is short)
        if (remaining < require)
        {
            // 3a. Pre-zero sequence at new start to mark it as empty
            Header64* nextStartHdr = (Header64*)(start + alignedEntryLength);
            writeSeq(nextStartHdr, 0UL);

            // 3b. Write actual data payload at the start of the ring
            WriteToRing(srcObj, srcObjLen, (Header64*)start, ringSize, ref writerSeqEven);

            // 3c. Stamp the wrap marker at the previous tail
            Header64* currentHdr = (Header64*)dst;
            writeSeq(currentHdr, s_ringWrapMarker);

            dst = start + alignedEntryLength;
            return;
        }

        // 4. Handle Linear Write
        byte* nextPos = dst + alignedEntryLength;

        // 4a. Pre-zero next sequence block
        Header64* nextHdr = (Header64*)nextPos;
        writeSeq(nextHdr, 0UL);

        // 4b. Write actual payload
        WriteToRing(srcObj, srcObjLen, (Header64*)dst, remaining, ref writerSeqEven);

        // 4c. Advance pointer
        dst = nextPos;
        if (dst == end)
        {
            dst = start;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteToRing(byte* srcObj, int srcObjLen, Header64* dstHdr, int dstLen, ref ulong writerSeq)
    {
        // 1. Validate Available Space
        if (dstLen < HeaderLength)
            throw new ArgumentOutOfRangeException(nameof(dstLen));

        byte* dstObj = (byte*)dstHdr + HeaderLength;
        int dstObjLen = dstLen - HeaderLength;

        // 2. Acquire: even -> odd. Source of truth is the writer's monotonic cursor.
        Volatile.Write(ref dstHdr->Sequence, writerSeq + 1UL);

        // 3. Header metadata + payload copy
        Volatile.Write(ref dstHdr->Magic, s_magic);
        dstHdr->Length = srcObjLen;
        Copy(srcObj, srcObjLen, dstObj, dstObjLen);

        // 4. Release: odd -> even (publish). Advance the caller's cursor.
        writerSeq += 2UL;
        Volatile.Write(ref dstHdr->Sequence, writerSeq);
    }

    // -------- Ring Read --------

    public static unsafe ReadStatus TryReadFromRing(ref byte* src, byte* start, byte* end, Span<byte> dstObj, out ReadOnlySpan<byte> rdstObj, ref ulong lastReadEvenSeq)
    {
        fixed (byte* dstObjP = dstObj)
        {
            ReadStatus status = TryReadFromRing(ref src, start, end, dstObjP, dstObj.Length, out int srcObjLen, ref lastReadEvenSeq);
            rdstObj = dstObj.Slice(0, srcObjLen);
            return status;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadStatus TryReadFromRing(ref byte* src, byte* start, byte* end, byte* dstObj, int dstObjLen, out int srcObjLen, ref ulong lastReadEvenSeq)
    {
        if (((ulong)start & 63) != 0 || ((ulong)end & 63) != 0)
        {
            throw new InvalidOperationException("Ring alignment error.");
        }

        Header64* srcHdr = (Header64*)src;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong readSeq()
        {
            ulong seq = Volatile.Read(ref srcHdr->Sequence);
            ulong magic = Volatile.Read(ref srcHdr->Magic);
            if (magic != s_magic || seq == s_ringWrapMarker)
            {
                srcHdr = (Header64*)start;
                seq = Volatile.Read(ref srcHdr->Sequence);
            }
            return seq;
        }

        while (true)
        {
            // 1. Initial Read of Sequence
            ulong seq0 = readSeq();

            // 2. Spin while Writer is Active
            while (IsWriteInProgress(seq0))
            {
                X86BaseWrapper.ExponentialPause();
                seq0 = readSeq();
            }

            // 3. Bail early if Stale or Empty
            if (!seq0.IsThisNewerThan(lastReadEvenSeq))
            {
                srcObjLen = 0;
                return ReadStatus.Empty;
            }

            int len = srcHdr->Length;

            // 4. Pre-Copy Validation
            ulong seq1 = readSeq();
            if (seq0 != seq1)
            {
                continue;
            }

            // 5. Copy Data Optimistically
            Copy((byte*)srcHdr + HeaderLength, len, dstObj, dstObjLen);
            Thread.MemoryBarrier();

            // 6. Post-Copy Validation
            ulong seq2 = readSeq();
            if (seq0 != seq2)
            {
                continue;
            }

            // 7. Commit Read and Advance Pointers
            lastReadEvenSeq = seq0;
            srcObjLen = len;

            src = (byte*)srcHdr;
            src += GetAlignedEntryLength(srcObjLen);

            if (src == end)
            {
                src = start;
            }

            return ReadStatus.New;
        }
    }

    // -------- Status probes --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadStatus GetReadStatus(Header64* srcHdr, ulong lastEvenSeq)
    {
        ulong seq = Volatile.Read(ref srcHdr->Sequence);

        if (seq == 0UL)
        {
            return ReadStatus.Empty;
        }

        return seq.IsThisNewerThan(lastEvenSeq) ? ReadStatus.New : ReadStatus.Old;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadStatus GetReadStatusFromRing(Header64* srcHdr, byte* start, byte* end, ulong lastEvenSeq)
    {
        if (((ulong)start & 63) != 0 || ((ulong)end & 63) != 0)
        {
            throw new InvalidOperationException("Ring alignment error.");
        }

        ulong seq = Volatile.Read(ref srcHdr->Sequence);

        if (seq == s_ringWrapMarker)
        {
            srcHdr = (Header64*)start;
        }

        return GetReadStatus(srcHdr, lastEvenSeq) == ReadStatus.New ? ReadStatus.New : ReadStatus.Empty;
    }

    // -------- Internal --------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Copy(byte* srcObj, int srcObjLen, byte* dstObj, int dstObjLen)
    {
        if (srcObjLen > dstObjLen || srcObjLen < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstObjLen), "Protocol.Copy Failed");
        }
        Unsafe.CopyBlockUnaligned(dstObj, srcObj, (uint)srcObjLen);
    }
}
