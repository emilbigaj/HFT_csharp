using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Tools;

namespace Execution;

/// The strategy's own descriptor for a fill, set in its Markouts.Markout handler; None when it sets nothing.
[RegisterJson]
public enum MarkoutTag : byte
{
    None = 0,
    Exit = 1,
    Entry = 2,
}

/// Handles the markout of this fill; the handler may edit the markout, such as tagging it.
public delegate void MarkoutHandler(in Fill fill, ref Markout markout);

/// One observation of one algo fill: the best bid and ask and the strategy's fair value MarkoutDuration of exchange time
/// from it, before it when negative. A fill writes one of these per markout duration it reaches. It carries no fill facts
/// and no instrument: MarkoutId with ExchangeTimestamp names the fill in the strategy's Fills/<symbol>.fill, and the
/// folder the record's file sits in names the symbol, which unlike an instrument id is the same after a weekend. Built by
/// Strategy.Markouts on the feed thread and written through a Series<Markout> (see Spec.md "Markouts").
[StructLayout(LayoutKind.Sequential, Pack = 1)]   // 49 bytes
[RegisterJson]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
public struct Markout()
{
    public ulong MarkoutId;                      // the fill's FillId
    public Timestamp ExchangeTimestamp;          // the fill's exchange time; with MarkoutId, names the fill in Fills/<symbol>.fill
    public double BidPrice = double.NaN;         // NaN = no quote at the markout duration (not open, a side empty, or further back than the recorder remembers)
    public double AskPrice = double.NaN;
    public double FairValuePrice = double.NaN;   // the strategy's fair value at the markout duration; NaN = it had set none
    public Duration MarkoutDuration;             // how long after the fill this quote and fair value stood; negative = before it
    public MarkoutTag MarkoutTag;                // set by the strategy's Markouts.Markout handler

    public readonly double MidPrice() => (BidPrice + AskPrice) * 0.5;

    public override string ToString() => Json.Serialize(this);
}
