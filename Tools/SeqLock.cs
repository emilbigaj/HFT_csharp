using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools
{
    // ===============================================================
    // Reader: stateless, static, zero-allocation
    // ===============================================================

    // ryuJit enforces full MemoryBarrier on volatile read/write, and x86 CPU enforces store-store ordering
    public static class SeqLockReader
    {
        /// <summary>Acquire read of the sequence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read(in ulong seqRef)
        {
            // Convert the 'in' parameter to a byref the Volatile API can use.
            ref ulong r = ref Unsafe.AsRef(in seqRef);
            ulong value = Volatile.Read(ref r);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWriteInProgress(ulong s) => (s & 1UL) != 0UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsStable(ulong s) => (s & 1UL) == 0UL;

        /// <summary>Spin until an even (stable) epoch is observed, and return it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong BeginRead(in ulong seq)
        {
            while (true)
            {
                ulong s0 = Read(in seq);
                if (IsStable(s0)) return s0;
                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Validate that no writer intervened and post value is still even.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Validate(ulong s0, ulong s1) => s0 == s1 && IsStable(s1);

        /// <summary>Validate that no writer intervened and post value is still even.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Validate(ulong s0, in ulong seqRef)
        {
            ulong s1 = Read(in seqRef);
            return s0 == s1 && IsStable(s1);
        }

        /// <summary>Acquire read then coerce to nearest even (mask off LSB) for diagnostics/metrics.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CurrentEven(in ulong seq)
        {
            ulong s = Read(in seq);
            return s & ~1UL;
        }

        // ---------- Pointer helpers for shared memory ----------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ulong Read(ulong* seqPtr)
        {
            ref ulong r = ref Unsafe.AsRef<ulong>(seqPtr);
            return Volatile.Read(ref r);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ulong BeginRead(ulong* seqPtr)
        {
            while (true)
            {
                ulong s0 = Read(seqPtr);
                if (IsStable(s0)) return s0;
                X86BaseWrapper.Pause();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ulong CurrentEven(ulong* seqPtr)
        {
            ulong s = Read(seqPtr);
            return s & ~1UL;
        }
    }

    // ===============================================================
    // Writer surface: expose the sequence by ref + write operations
    // ===============================================================
    public interface ISeqLockWriter
    {
        /// <summary>
        /// Reference to the shared sequence word. Readers use this with <see cref="SeqLockReader"/>.
        /// WARNING: treat as read-only outside of Begin/End write sections.
        /// </summary>
        ref readonly ulong SeqRef { get; }

        /// <summary>Spin until even→odd is claimed.</summary>
        void BeginWrite();

        /// <summary>Publish odd→even (completes the epoch).</summary>
        void EndWrite();

        /// <summary>Reset to zero (even). Call only under external quiescence.</summary>
        void Reset();
    }

    // ===============================================================
    // Single writer (fastest, no CAS) + static helpers
    // ===============================================================
    public sealed class SingleSeqLockWriter : ISeqLockWriter
    {
        private ulong _sequence;

        public ref readonly ulong SeqRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _sequence;
        }

        // ---------- Instance API (ISeqLockWriter) ----------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginWrite() => BeginWrite(ref _sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndWrite() => EndWrite(ref _sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset() => Reset(ref _sequence);

        // ---------- Static helpers for arbitrary ref/pointer locations ----------

        /// <summary>Spin until even→odd is claimed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BeginWrite(ref ulong seq)
        {
            Volatile.Write(ref seq, seq + 1UL);
        }

        /// <summary>Publish odd→even (completes the epoch).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EndWrite(ref ulong seq)
        {
            Volatile.Write(ref seq, seq + 1UL); // odd → even (release)
        }

        /// <summary>Reset to zero (even). Call only under external quiescence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset(ref ulong seq)
        {
            Volatile.Write(ref seq, 0UL);
        }
    }

    // ===============================================================
    // Multi writer (CAS-protected) + static helpers
    // ===============================================================
    public sealed class MultiSeqLockWriter : ISeqLockWriter
    {
        private ulong _sequence;

        public ref readonly ulong SeqRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _sequence;
        }

        // ---------- Instance API (ISeqLockWriter) ----------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginWrite()
            => AcquireLock(ref _sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndWrite()
            => ReleaseLock(ref _sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
            => Reset(ref _sequence);

        // ---------- Static helpers for arbitrary ref/pointer locations ----------

        /// <summary>Spin until even→odd is claimed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AcquireLock(ref ulong seq)
        {
            while (true)
            {
                ulong s = Volatile.Read(ref seq);
                if ((s & 1UL) == 0UL && Interlocked.CompareExchange(ref seq, s + 1UL, s) == s)
                {
                    // acquired even → odd; Interlocked is a full fence
                    return;
                }
                X86BaseWrapper.Pause();
            }
        }

        /// <summary>Publish odd→even (completes the epoch).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReleaseLock(ref ulong seq)
        {
            // odd → even; Interlocked provides full fence semantics
            Interlocked.Increment(ref seq);
        }

        /// <summary>Reset to zero (even). Call only under external quiescence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset(ref ulong seq)
        {
            Volatile.Write(ref seq, 0UL);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void AcquireLock(ulong* seqPtr)
        {
            ref ulong seq = ref Unsafe.AsRef<ulong>(seqPtr);
            AcquireLock(ref seq);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void ReleaseLock(ulong* seqPtr)
        {
            ref ulong seq = ref Unsafe.AsRef<ulong>(seqPtr);
            ReleaseLock(ref seq);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Reset(ulong* seqPtr)
        {
            ref ulong seq = ref Unsafe.AsRef<ulong>(seqPtr);
            Reset(ref seq);
        }
    }

    // ===============================================================
    // RAII spinlock — C# shape of C++ Tools::RAIISpinLock: ref struct + using.
    // TTAS: exchange to acquire, spin on plain reads while held (no coherence storm), pause in the
    // wait loop. The flag is a bool like the C++ atomic<bool>; Interlocked has no bool overload, so
    // the exchange reinterprets it as the same-width byte (never int — a 4-byte RMW on a 1-byte
    // field would also touch the 3 bytes beside it).
    // ===============================================================
    public readonly ref struct RAIISpinLock
    {
        private readonly ref bool _flag;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RAIISpinLock(ref bool flag)
        {
            _flag = ref flag;
            while (true)
            {
                if (Interlocked.Exchange(ref Unsafe.As<bool, byte>(ref _flag), 1) == 0)
                {
                    return;
                }

                while (Volatile.Read(ref _flag))
                {
                    X86BaseWrapper.Pause();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            Volatile.Write(ref _flag, false);
        }
    }
}
