//BEGIN_FILE HFT/Tools/AtomicTransition.cs
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tools;

// Atomic {state, epoch} in one word, for any byte-backed enum. The state lives in the low
// byte; the epoch fills the high 56 bits and advances on EVERY transition, so no value in
// the word's history ever repeats and a compare-exchange against a stale snapshot always
// fails — a thread that slept through state changes cannot act on the world it remembers,
// even if the state byte has cycled back to what it saw (ABA).
//
// Contract: one owner thread calls Store() with plain authority — it may overwrite a
// concurrently successful TryTransition(), which is the intent (the owner's word is final).
// Any thread may Load() and TryTransition() from a Snapshot it holds. The CAS must use the
// snapshot taken when the evidence for the transition was observed — never a fresh Load(),
// which would adopt the new epoch and defeat the check.
public struct AtomicTransition<TState> where TState : unmanaged, Enum
{
    private const int StateBits = 8;
    private const ulong StateMask = (1UL << StateBits) - 1UL;

    private ulong _word;

    public readonly struct Snapshot
    {
        public readonly ulong Word;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Snapshot(ulong word) => Word = word;

        public TState State
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.BitCast<byte, TState>((byte)(Word & StateMask));
        }

        public ulong Epoch
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Word >> StateBits;
        }
    }

    public AtomicTransition(TState state)
    {
        if (Unsafe.SizeOf<TState>() != 1)
            throw new NotSupportedException($"AtomicTransition<{typeof(TState).Name}>: enum must be byte-backed.");
        _word = Pack(0, state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack(ulong epoch, TState state) => (epoch << StateBits) | Unsafe.BitCast<TState, byte>(state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Snapshot Load() => new Snapshot(Volatile.Read(ref _word));

    public TState State
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Load().State;
    }

    public ulong Epoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Load().Epoch;
    }

    // Owner thread only: transition unconditionally, advancing the epoch.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(TState state) => Volatile.Write(ref _word, Pack((_word >> StateBits) + 1, state));

    // Any thread: transition only if the word still IS the snapshot — same state, same
    // epoch. Returns false (and does nothing) if the world moved on since the snapshot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryTransition(Snapshot snapshot, TState desired)
        => Interlocked.CompareExchange(ref _word, Pack(snapshot.Epoch + 1, desired), snapshot.Word) == snapshot.Word;
}
//END_FILE HFT/Tools/AtomicTransition.cs
