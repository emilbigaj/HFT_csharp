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

/// One observation of one algo fill: the best bid and ask MarkoutDuration of exchange time after it. A fill writes one of
/// these per markout duration it reaches, each carrying the few fill facts a reader needs to value it, so the file stands
/// alone. Built by Strategy.Markouts on the feed thread and written through a Series<Markout> (see Spec.md "Markouts").
[StructLayout(LayoutKind.Sequential, Pack = 1)]   // 58 bytes
[RegisterJson]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]   // Why: the compiler zeroes every field, so the JIT judges the constructor too big to inline
public struct Markout()
{
    public ulong FillId;                       // with FillExchangeTimestamp, names the fill in a .fill file
    public Timestamp FillExchangeTimestamp;
    public int InstrumentId;
    public double FillPrice;
    public int FillQuantity;                   // signed: positive bought, negative sold
    public FillType FillType;
    public double BidPrice = double.NaN;       // NaN = no quote at the markout duration (not open, or a side empty)
    public double AskPrice = double.NaN;
    public Duration MarkoutDuration;           // how long after the fill this quote stood
    public MarkoutTag MarkoutTag;              // set by the strategy's Markouts.Markout handler

    public readonly double MidPrice() => (BidPrice + AskPrice) * 0.5;

    public override string ToString() => Json.Serialize(this);
}
