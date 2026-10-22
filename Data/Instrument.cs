using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using Socket;
using Tools;

namespace Data;

[StructLayout(LayoutKind.Sequential, Size = 4)]
[RegisterJson]
public struct Header<T> where T : Enum
{
    // NOT readonly, deliberately. Json.Options sets IncludeFields, and System.Text.Json will happily
    // serialise a readonly field but cannot deserialise into one — so a writable outer Header field
    // (RiskLimit, OrderState) got a freshly zeroed Header<T> assigned over the constructed value, and
    // the message type came back as 0. That silently breaks every dispatcher, since they all switch
    // on the first byte: a RiskLimit restored from <symbol>.risklimit hit `default:` in
    // Server.ReadAdmin and the operator's edit was dropped without a word.
    public T Type;

    // Why: the byte check lives here, not in a static constructor: that cost every row's constructor a class-init check
    // and kept it from inlining; Unsafe.SizeOf<T>() is a JIT-time constant, so for a byte enum this compiles to nothing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Header(T type)
    {
        if (Unsafe.SizeOf<T>() != sizeof(byte))
            ThrowTypeIsNotByte();
        Type = type;
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTypeIsNotByte() => throw new InvalidOperationException($"Header<T>.Header: enum {typeof(T).Name} must be a byte: every row's first byte is its type.");
}





[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 128)]
[RegisterJson]
public struct InstrumentHeader()
{
    public Header<InstrumentType> Header = new Header<InstrumentType>(InstrumentType.Instrument);
    public InstrumentType InstrumentType;
    public byte CoreGroupId;
    public TradingStatus TradingStatus;
    public sbyte ProductGroupId;   // its row in <server>/MessageEfficiency, set by the server at allocation; -1 until then
    public String8 Exchange;
    public String8 Root;
    public double TickSize;
    public double InverseTickSize;
    public double DisplayFactor;
    public int InstrumentHeaderId;
    public int InstrumentId;
    public int ExchangeInstrumentId;
    public String4 ProductGroupRoot;   // the group CME counts its message efficiency under (MDP3 SecurityGroup), set by whoever builds the header
    public Timestamp TradingStatusTimestamp;   // @64: exchange time TradingStatus last changed; MinValue never. Server only, written before TradingStatus
}

// Why every member is readonly: rows are read through ref readonly, where a non-readonly call copies the row and its As* ref points into the dead copy
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InstrumentHeader256
{
    private unsafe fixed byte _raw[256];
    public readonly Symbology Symbology
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly InstrumentHeader header = ref Unsafe.As<InstrumentHeader256, InstrumentHeader>(ref Unsafe.AsRef(in this));

            switch (header.InstrumentType)
            {
                case InstrumentType.Future:
                    return AsFuture().Symbology;
                case InstrumentType.Forex:
                    return AsForex().Symbology;
                case InstrumentType.Spread:
                    return AsLegged().Symbology;
                default:
                    throw new NotSupportedException($"Instrument type '{header.InstrumentType}' is not supported.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref FutureHeader AsFuture()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Future)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader256, FutureHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref ForexHeader AsForex()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Forex)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader256, ForexHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref LeggedHeader AsLegged()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Spread)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader256, LeggedHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref InstrumentHeader AsInstrumentHeader()
    {
        return ref Unsafe.As<InstrumentHeader256, InstrumentHeader>(ref Unsafe.AsRef(in this));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ForexHeader
{
    public InstrumentHeader InstrumentHeader;
    public String4 BaseCurrency;
    public String4 QuoteCurrency;

    public Symbology Symbology => throw new NotImplementedException();
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct FutureHeader
{
    public InstrumentHeader InstrumentHeader;
    public double Multiplier;
    public Timestamp MaturityDate;

    public FutureSymbology Symbology =>
        new FutureSymbology(
            InstrumentHeader.Exchange.ToString(),
            InstrumentHeader.Root.ToString(),
            MaturityDate);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct LegHeader
{
    public int InstrumentHeaderId;
    public int Weight;
}

// A resolved leg for risk decomposition: instrument id + signed weight. 8 bytes, so a full 6-leg
// view fits in one cache line; ids not Instrument refs — the risk loops only need the id.
public readonly record struct InstrumentLeg(int InstrumentId, int Weight);



[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct LeggedHeader
{
    // 256-byte overlay budget: InstrumentHeader 128 + Multiplier 8 + LegCount 4 + reserved 4 + 6*8 legs = 192.
    public InstrumentHeader InstrumentHeader;
    public double Multiplier;
    public int LegCount;
    private unsafe fixed byte _reserved[4];
    public LegHeader Leg0;
    public LegHeader Leg1;
    public LegHeader Leg2;
    public LegHeader Leg3;
    public LegHeader Leg4;
    public LegHeader Leg5;

    static LeggedHeader()
    {
        if (Unsafe.SizeOf<LeggedHeader>() > Unsafe.SizeOf<InstrumentHeader256>())
            throw new InvalidOperationException($"LeggedHeader ({Unsafe.SizeOf<LeggedHeader>()} bytes) must fit within InstrumentHeader256.");
    }

    // Live legs, maturity-ascending — same invariant as the ticker. Clamped: LegCount comes from
    // shared memory, and CreateSpan does no validation.
    [UnscopedRef]
    [JsonIgnore]   // derived view over Leg0..Leg5 — the leg fields are the serialized payload
    public Span<LegHeader> Legs => MemoryMarshal.CreateSpan(ref Leg0, Math.Clamp(LegCount, 0, 6));

    // Legs reference sibling headers by id; the context hooks this, like InstrumentDetails.GetLeg.
    public static Func<int, InstrumentHeader256>? GetLegHeader;

    public SpreadSymbology Symbology
    {
        get
        {
            List<Symbology> symbologies = new List<Symbology>(LegCount);
            List<int> weights = new List<int>(LegCount);
            foreach (ref readonly LegHeader legHeader in Legs)
            {
                symbologies.Add(GetLegHeader!(legHeader.InstrumentHeaderId).Symbology);
                weights.Add(legHeader.Weight);
            }
            return new SpreadSymbology(InstrumentHeader.Exchange.ToString(), InstrumentHeader.Root.ToString(), symbologies, weights);
        }
    }
}

public delegate void MarketByPriceDeltaEvent(in MarketByPrice delta, ReadOnlySpan<byte> bytes);

public delegate void TradeEvent(in Trade trade);
public delegate void SettlementEvent(in Settlement settlement);
public delegate void TradingStatusUpdateEvent(in TradingStatusUpdate tradingStatusUpdate);

public abstract class Instrument
{
    public int ProductGroupId => Header.ProductGroupId;

    protected SharedArrayEntry<InstrumentHeader256> _headerEntry;
    public ref readonly InstrumentHeader Header => ref _headerEntry.GetReadonlyRef().AsInstrumentHeader();

    // This process's own book, built by its Client from the server's snapshot and the instrument's feed, on the feed thread
    // only; never filled in a process with no feed (the server writes its shared book through its Context, a GUI reads that).
    private MarketByPrice64 _marketByPrice;

    public ref MarketByPrice64 MarketByPrice
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _marketByPrice;
    }


    public Quote Quote => _quote;
    private Quote _quote;
    private bool _isQuoteValid = false;
    private Quote _quoteAtPassStart;                    // the quote when this ReadSocket pass first touched the book
    private bool _isDirty;
    public event Action? QuoteChanged;

    public event Action? MarketByPriceChanged;

    public event MarketByPriceDeltaEvent? MarketByPriceDelta;

    // Phase 1 of a ReadSocket pass, per delta: fold into the image, fire only the per-delta consumers (see Spec.md).
    public void ApplyMarketByPriceDelta(in MarketByPrice delta, ReadOnlySpan<byte> bytes)
    {
        if (!_isDirty)
        {
            _quoteAtPassStart = _quote;
            _isDirty = true;
        }
        ref readonly MarketByPrice64 mbp64 = ref MarketByPrice;
        _quote.Bid = mbp64.BidsCount > 0 ? mbp64.BestBid : default!;
        _quote.Ask = mbp64.AsksCount > 0 ? mbp64.BestAsk : default!;
        _isQuoteValid = mbp64.BidsCount > 0 && mbp64.AsksCount > 0;
        MarketByPriceDelta?.Invoke(delta, bytes);
    }

    // Phase 2, once per pass: QuoteChanged only if the quote differs from the start of the pass.
    public void RaiseChanged()
    {
        _isDirty = false;
        if (_quote.Bid != _quoteAtPassStart.Bid || _quote.Ask != _quoteAtPassStart.Ask)
        {
            QuoteChanged?.Invoke();
        }
        MarketByPriceChanged?.Invoke();
    }

    public event TradeEvent? TradeChanged;

    public void OnTrade(in Trade trade)
    {
        TradeChanged?.Invoke(in trade);
    }

    public event SettlementEvent? SettlementChanged;

    public void OnSettlement(in Settlement settlement)
    {
        SettlementChanged?.Invoke(in settlement);
    }

    public event TradingStatusUpdateEvent? TradingStatusUpdateEvent;
    private TradingStatus _tradingStatus = TradingStatus.Unknown;
    public void OnTradingStatusUpdate(in TradingStatusUpdate tradingStatusUpdate)
    {
        if (_tradingStatus != tradingStatusUpdate.TradingStatus)
        {
            _tradingStatus = tradingStatusUpdate.TradingStatus;
            TradingStatusUpdateEvent?.Invoke(in tradingStatusUpdate);
        }
    }

    // This process's own book's quote: the one its feed last set, else read from the book (before the first update after the
    // snapshot). False while the instrument is not open or either side is empty. Feed thread only.
    public bool TryGetQuote(out Quote quote)
    {
        if (_isQuoteValid && Header.TradingStatus == TradingStatus.Open)
        {
            quote = _quote;
            return true;
        }
        return TryGetQuote(in _marketByPrice, out quote);
    }

    // The quote this book shows (this process's own or a copy of the server's); false while the instrument is not open or either side is empty.
    public bool TryGetQuote(in MarketByPrice64 marketByPrice, out Quote quote)
    {
        bool isQuotable = Header.TradingStatus == TradingStatus.Open && marketByPrice.BidsCount > 0 && marketByPrice.AsksCount > 0;
        quote = isQuotable ? new Quote(marketByPrice.BestBid, marketByPrice.BestAsk, TickSize) : default;
        return isQuotable;
    }

    public Symbology Symbology { get; }

    public string Symbol => Symbology.Symbol;
    public string ShortSymbol => Symbology.ShortSymbol;
    public string Exchange => Symbology.Exchange;
    public string Root => Symbology.Root;

    public int InstrumentId => Header.InstrumentId;

    public double InverseTickSize => Header.InverseTickSize;
    public double TickSize => Header.TickSize;
    public int TickDecimals { get; }
    public string PriceFormat { get; }   // "N<TickDecimals>": a price to the tick's decimals, with thousands separators
    public double Multiplier { get; protected set; } = 1.0;

    // Legs view for risk decomposition: an outright is its own single leg (weight +1); a Spread
    // overwrites with its resolved legs. Frozen at construction — header identity is immutable.
    protected InstrumentLeg[] _legs;
    public ReadOnlySpan<InstrumentLeg> Legs => _legs;
    // > 1: every instrument is its own single leg (base ctor); legged means legs BEYOND itself.
    public bool IsLegged => _legs.Length > 1;

    protected Instrument(SharedArrayEntry<InstrumentHeader256> headerEntry)
    {
        _headerEntry = headerEntry;
        _quote = new Quote() { TickSize = TickSize };
        TickDecimals = Tools.Tools.GetNumberOfDecimalPlaces(TickSize);
        PriceFormat = $"N{TickDecimals}";
        Symbology = headerEntry.GetReadonlyRef().Symbology;
        _legs = [new InstrumentLeg(InstrumentId, 1)];
    }

    // --- Core HFT Helpers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetProfit(double buyPrice, double sellPrice, int quantity) => quantity == 0 ? 0 : GetValue(sellPrice - buyPrice) * quantity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetValue(double price) => price * Multiplier;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double TicksToPrice(int ticks) => ticks * TickSize;

    /// The price these ticks show as in a GUI (PriceFormat). Cold path: it allocates the string.
    public string TicksToPriceString(int ticks) => TicksToPrice(ticks).ToString(PriceFormat);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RoundToTicks(double price) => (price * InverseTickSize).RoundToInt();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double RoundPrice(double price) => (price * InverseTickSize).RoundToInt() * TickSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FloorToTicks(double price) => (price * InverseTickSize).FloorToInt();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double FloorPrice(double price) => (price * InverseTickSize).FloorToInt() * TickSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CeilingToTicks(double price) => (price * InverseTickSize).CeilingToInt();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double CeilingPrice(double price) => (price * InverseTickSize).CeilingToInt()*TickSize;



    public override string ToString() => $"Instrument {InstrumentId} {Symbology}";
}

// === FUTURE ===
public class Future : Instrument
{
    public Timestamp MaturityDate => _headerEntry.GetReadonlyRef().AsFuture().MaturityDate;
    public ref readonly FutureHeader FutureHeader => ref _headerEntry.GetReadonlyRef().AsFuture();

    public Future(SharedArrayEntry<FutureHeader> headerEntry)
        : base(headerEntry.Cast<InstrumentHeader256>())
    {
        Multiplier = FutureHeader.Multiplier;
    }
}

public class FutureChain
{
    public Future Active {get; private set;}
    public List<Future> Futures { get; } = new List<Future>();
    public int Count => Futures.Count;

    public event Action<Future>? Rolled;

    public FutureChain(List<Future> futures)
    {
        Futures.AddRange(futures);
        Active = Futures[0];
    }

    public void Roll()
    {
        Active = Futures[1];
        Rolled?.Invoke(Active);
    }
}

// === FOREX ===
public sealed class Forex : Instrument
{
    public String4 BaseCurrency => ForexHeader.BaseCurrency;
    public String4 QuoteCurrency => ForexHeader.QuoteCurrency;

   public ref readonly ForexHeader ForexHeader => ref _headerEntry.GetReadonlyRef().AsForex();


    public Forex(SharedArrayEntry<ForexHeader> headerEntry)
        : base(headerEntry.Cast<InstrumentHeader256>())
    {
    }
}

/// === SPREAD ===
public sealed class Spread : Instrument
{
    public Timestamp LongMaturityDate => Long.MaturityDate;
    public Timestamp ShortMaturityDate => Short.MaturityDate;

    public Future Long { get; }
    public Future Short { get; }

    public ref readonly LeggedHeader SpreadHeader => ref _headerEntry.GetReadonlyRef().AsLegged();


    public Spread(SharedArrayEntry<LeggedHeader> headerEntry, Future @long, Future @short)
        : base(headerEntry.Cast<InstrumentHeader256>())
    {
        Long = @long;
        Short = @short;

        // Resolve the legged header's (headerId, weight) pairs to instrument ids via the leg
        // instruments in hand; LegCount 0 = legacy header, default calendar weights.
        LeggedHeader leggedHeader = SpreadHeader;
        _legs = [new InstrumentLeg(@long.InstrumentId, 1), new InstrumentLeg(@short.InstrumentId, -1)];
    }
}