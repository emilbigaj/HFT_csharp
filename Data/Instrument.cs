using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Socket;
using Tools;

namespace Data;

[StructLayout(LayoutKind.Sequential, Size = 4)]
[RegisterJson]
public struct Header<T>(T type) where T : Enum
{
    // NOT readonly, deliberately. Json.Options sets IncludeFields, and System.Text.Json will happily
    // serialise a readonly field but cannot deserialise into one — so a writable outer Header field
    // (RiskLimit, OrderState) got a freshly zeroed Header<T> assigned over the constructed value, and
    // the message type came back as 0. That silently breaks every dispatcher, since they all switch
    // on the first byte: a RiskLimit restored from <symbol>.risklimit hit `default:` in
    // Server.ReadAdmin and the operator's edit was dropped without a word.
    public T Type = type;
    private unsafe fixed byte _reserved[3];

    static Header()
    {
        if (Unsafe.SizeOf<T>() != sizeof(byte))
        {
            throw new InvalidOperationException($"Enum {typeof(T).Name} must be a byte.");
        }
    }
}

[RegisterJson]
public enum TradingStatus : byte
{
    Unknown = 0,   // uninitialized, CME UnknownorInvalid(20) / NoValue(255)
    Open,          // ReadyToTrade(17)
    Closed,        // Close(4), NotAvailableForTrading(18), PostClose(26)
    Auction,       // PreOpen(21), NewPriceIndication(15), PreCross(24), Cross(25)
    Halted,        // TradingHalt(2)
};



[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
[RegisterJson]
public struct InstrumentHeader()
{
    public Header<InstrumentType> Header = new Header<InstrumentType>(InstrumentType.Instrument);
    public InstrumentType InstrumentType;
    public byte CoreGroupId;
    public TradingStatus TradingStatus;
    private unsafe fixed byte _reserved[1];
    public String8 Exchange;
    public String8 Root;
    public double TickSize;
    public double InverseTickSize;
    public double DisplayFactor;
    public int InstrumentHeaderId;
    public int InstrumentId;
    public int ExchangeInstrumentId;
    private unsafe fixed byte _reserved1[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InstrumentHeader128
{
    private unsafe fixed byte _raw[128];
    public Symbology Symbology
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly InstrumentHeader header = ref Unsafe.As<InstrumentHeader128, InstrumentHeader>(ref Unsafe.AsRef(in this));

            switch (header.InstrumentType)
            {
                case InstrumentType.Future:
                    return AsFuture().Symbology;
                case InstrumentType.Forex:
                    return AsForex().Symbology;
                case InstrumentType.Spread:
                    return AsSpread().Symbology;
                default:
                    throw new NotSupportedException($"Instrument type '{header.InstrumentType}' is not supported.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref FutureHeader AsFuture()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Future)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader128, FutureHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ForexHeader AsForex()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Forex)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader128, ForexHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SpreadHeader AsSpread()
    {
        ref InstrumentHeader instrumentHeader = ref AsInstrumentHeader();
        if (instrumentHeader.InstrumentType != InstrumentType.Spread)
            throw new NotSupportedException();
        return ref Unsafe.As<InstrumentHeader128, SpreadHeader>(ref Unsafe.AsRef(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref InstrumentHeader AsInstrumentHeader()
    {
        return ref Unsafe.As<InstrumentHeader128, InstrumentHeader>(ref Unsafe.AsRef(in this));
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
    public MaturityType MaturityType;

    public FutureSymbology Symbology =>
        new FutureSymbology(
            InstrumentHeader.Exchange.ToString(),
            InstrumentHeader.Root.ToString(),
            MaturityType,
            MaturityDate);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct SpreadHeader
{
    public InstrumentHeader InstrumentHeader;
    public double Multiplier;
    public Timestamp LongMaturityDate;
    public Timestamp ShortMaturityDate;
    public int LongInstrumentId;
    public int ShortInstrumentId;
    public MaturityType LongMaturityType;
    public MaturityType ShortMaturityType;


    public SpreadSymbology Symbology =>
        new SpreadSymbology(
            InstrumentHeader.Exchange.ToString(),
            InstrumentHeader.Root.ToString(),
            LongMaturityType,
            LongMaturityDate,
            ShortMaturityType,
            ShortMaturityDate);
}

public delegate void MarketByPriceDeltaEvent(in MarketByPrice delta, ReadOnlySpan<byte> bytes);

public delegate void TradeEvent(in Trade trade);
public delegate void SettlementEvent(in Settlement settlement);
public abstract class Instrument
{
    public int ProductGroupId { get; set; } = -1;

    protected SharedArrayEntry<InstrumentHeader128> _headerEntry;
    public ref readonly InstrumentHeader Header => ref _headerEntry.GetReadonlyRef().AsInstrumentHeader();

    private SharedArrayEntry<MarketByPrice64> _mbpEntry;

    public ref readonly MarketByPrice64 MarketByPrice
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _mbpEntry.GetReadonlyRef();
    }


    public Quote Quote => _quote;
    private Quote _quote;
    private bool _isQuoteValid = false;
    public event Action? QuoteChanged;

    public event Action? MarketByPriceChanged;

    public event MarketByPriceDeltaEvent? MarketByPriceDelta;

    public void OnMarketByPriceDelta(in MarketByPrice delta, ReadOnlySpan<byte> bytes)
    {
        ref readonly MarketByPrice64 mbp64 = ref MarketByPrice;
        bool didQuoteChange = mbp64.BestBid != _quote.Bid || mbp64.BestAsk != _quote.Ask;
        _quote.Bid = mbp64.BidsCount > 0 ? mbp64.BestBid : default!;
        _quote.Ask = mbp64.AsksCount > 0 ? mbp64.BestAsk : default!;
        _isQuoteValid = mbp64.BidsCount > 0 && mbp64.AsksCount > 0;
        if (didQuoteChange)
        {
            QuoteChanged?.Invoke();
        }
        MarketByPriceDelta?.Invoke(delta, bytes);
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

    public bool TryGetQuote(out Quote quote)
    {
        if (!IsInSession)
        {
            quote = default!;
            return false;
        }

        if (_isQuoteValid)
        {
            quote = _quote;
            return true;
        }

        // this path is used by the gui
        ref readonly MarketByPrice64 mbp = ref MarketByPrice;
        ulong seq0;
        ulong seq1;
        while (true)
        {
            seq0 = _mbpEntry.GetSeq();
            if (Protocol.IsWriteInProgress(seq0))
            {
                X86BaseWrapper.Pause();
                continue;
            }

            if (!IsInSession)
            {
                quote = default!;
                return false;
            }

            if (mbp.BidsCount == 0 || mbp.AsksCount == 0)
            {
                seq1 = _mbpEntry.GetSeq();
                if (seq0 == seq1)
                {
                    quote = default!;
                    return false;
                }
                continue;
            }

            quote = new Quote(mbp.BestBid, mbp.BestAsk, TickSize);

            seq1 = _mbpEntry.GetSeq();
            if (seq0 == seq1)
                return true;
        }
    }

    public Symbology Symbology { get; }

    public string Symbol => Symbology.Symbol;
    public string ShortSymbol => Symbology.ShortSymbol;
    public string Exchange => Symbology.Exchange;
    public string Root => Symbology.Root;

    public bool IsInSession => SessionManager?.IsInSession ?? true;

    private SessionManager _sessionManager = null!;
    public SessionManager SessionManager
    {
        get => _sessionManager;
        set
        {
            if (_sessionManager == null)
            {
                _sessionManager = value;
                _sessionManager.Changed += OnSessionChanged;
            }
            else throw new InvalidOperationException($"Instrument::{Symbol}::SessionManager: can only be set once.");
        }
    }

    private void OnSessionChanged(Timestamp timestamp) { }
    public int InstrumentId => Header.InstrumentId;

    public double InverseTickSize => Header.InverseTickSize;
    public double TickSize => Header.TickSize;
    public int TicKDecimals { get; }
    public double Multiplier { get; protected set; } = 1.0;

    protected Instrument(SharedArrayEntry<InstrumentHeader128> headerEntry, SharedArrayEntry<MarketByPrice64> mbpEntry)
    {
        _mbpEntry = mbpEntry;
        _headerEntry = headerEntry;
        _quote = new Quote() { TickSize = TickSize };
        TicKDecimals = Tools.Tools.GetNumberOfDecimalPlaces(TickSize);
        Symbology = headerEntry.GetReadonlyRef().Symbology;
    }

    // --- Core HFT Helpers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetProfit(double buyPrice, double sellPrice, int quantity) => quantity == 0 ? 0 : GetValue(sellPrice - buyPrice) * quantity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetValue(double price) => price * Multiplier;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double TicksToPrice(int ticks) => ticks * TickSize;

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
    public MaturityType MaturityType => _headerEntry.GetReadonlyRef().AsFuture().MaturityType;
    public Timestamp MaturityDate => _headerEntry.GetReadonlyRef().AsFuture().MaturityDate;
    public ref readonly FutureHeader FutureHeader => ref _headerEntry.GetReadonlyRef().AsFuture();

    public Future(SharedArrayEntry<FutureHeader> headerEntry, SharedArrayEntry<MarketByPrice64> mbpEntry)
        : base(headerEntry.Cast<InstrumentHeader128>(), mbpEntry)
    {
        Multiplier = FutureHeader.Multiplier;
    }
}

// === FOREX ===
public sealed class Forex : Instrument
{
    public String4 BaseCurrency => ForexHeader.BaseCurrency;
    public String4 QuoteCurrency => ForexHeader.QuoteCurrency;

   public ref readonly ForexHeader ForexHeader => ref _headerEntry.GetReadonlyRef().AsForex();


    public Forex(SharedArrayEntry<ForexHeader> headerEntry, SharedArrayEntry<MarketByPrice64> mbpEntry)
        : base(headerEntry.Cast<InstrumentHeader128>(), mbpEntry)
    {
    }
}

// === SPREAD ===
public sealed class Spread : Future
{
    public MaturityType LongMaturityType => Long.MaturityType;
    public Timestamp LongMaturityDate => Long.MaturityDate;

    public MaturityType ShortMaturityType => Short.MaturityType;
    public Timestamp ShortMaturityDate => Short.MaturityDate;

    public Future Long { get; }
    public Future Short { get; }

    public ref readonly SpreadHeader SpreadHeader => ref _headerEntry.GetReadonlyRef().AsSpread();


    public Spread(SharedArrayEntry<SpreadHeader> headerEntry, SharedArrayEntry<MarketByPrice64> mbpEntry, Future @long, Future @short)
        : base(headerEntry.Cast<FutureHeader>(), mbpEntry)
    {
        Long = @long;
        Short = @short;
    }
}