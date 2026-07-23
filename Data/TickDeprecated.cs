/*
//BEGIN_FILE HFT/Data/Tick.cs
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Tools;

namespace Data;

[RegisterJson]
public struct MarketByPriceDump
{
    public TickHeader Header { get; set; }
    public int BidsCount { get; set; }
    public int AsksCount { get; set; }
    public Level[] Bids { get; set; }
    public Level[] Asks { get; set; }
}

[RegisterJson]
public enum Side : sbyte
{
    Flat = 0,
    Buy = 1,
    Sell = -1,
}

// dont ever edit this! TickHistory will break.
[RegisterJson]
public enum TickType : byte
{
    Trade = 0,
    Quote = 1,
    MarketByPrice = 2,
    MarketByPriceSnapshot = 3,
    MarketByPriceUpdate = 4,
    MarketByPricePartialUpdate = 5,
    MarketByPriceDelta = 6,
    Settlement = 7,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct Level(int Ticks, int Quantity)
{
    public int Ticks = Ticks;
    public int Quantity = Quantity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Level left, Level right)
    {
        return left.Ticks == right.Ticks && left.Quantity == right.Quantity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Level left, Level right)
    {
        return !(left == right);
    }

    public override string ToString() => Json.Serialize(this);

}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson] // 24 bytes
public struct TickHeader(TickType tickType, int instrumentId, Timestamp exchangeTimestamp, Timestamp nicTimestamp)
{
    public TickType TickType = tickType;
    private unsafe fixed byte _reserved[3];
    public int InstrumentId = instrumentId;
    public Timestamp ExchangeTimestamp = exchangeTimestamp;
    public Timestamp NicTimestamp = nicTimestamp;

    public override string ToString() => Json.Serialize(this);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct Quote
{
    public double TickSize;
    public Level Bid;
    public Level Ask;

    public Quote(Level bid, Level ask, double tickSize)
    {
        Bid = bid;
        Ask = ask;
        TickSize = tickSize;
    }

    public double MidPrice => (Bid.Ticks + Ask.Ticks) * 0.5 * TickSize;
    public double BidPrice => Bid.Ticks * TickSize;
    public double AskPrice => Ask.Ticks * TickSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
[RegisterJson]
public struct Tick
{
    public TickHeader TickHeader;

    public int SizeOf()
    {
        return TickHeader.TickType switch
        {
            TickType.Trade => Unsafe.SizeOf<Trade>(),
            TickType.Settlement => Unsafe.SizeOf<Settlement>(),
            _ => throw new NotSupportedException($"Unsupported tick type: {TickHeader.TickType}")
        };
    }

    public ref Settlement AsSettlement()
    {
        ref Settlement settlement = ref Unsafe.As<Tick, Settlement>(ref Unsafe.AsRef(in this));
        if (settlement.TickHeader.TickType != TickType.Settlement)
            throw new NotSupportedException();
        return ref settlement;
    }
    public ref Trade AsTrade()
    {
        ref Trade trade = ref Unsafe.As<Tick, Trade>(ref Unsafe.AsRef(in this));
        if (trade.TickHeader.TickType != TickType.Trade)
            throw new NotSupportedException();
        return ref trade;
    }
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
[RegisterJson]
public struct Settlement
{
    public TickHeader TickHeader;
    public double Price;
    public Settlement(int instrumentId, Timestamp timestamp, double price)
    {
        TickHeader = new(TickType.Settlement, instrumentId, timestamp, timestamp);
        Price = price;
    }
    public override string ToString()
    {
        return Json.Serialize(this);
    }

}


[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
[RegisterJson]
public struct Trade
{
    public TickHeader TickHeader;
    public Level Level;
    public sbyte Direction;
    public Trade(int instrumentId, Timestamp exchangeTimestamp, Timestamp nicTimestamp, int ticks, int quantity, sbyte direction)
    {
        TickHeader = new(TickType.Trade, instrumentId, exchangeTimestamp, nicTimestamp);
        Level.Ticks = ticks;
        Level.Quantity = quantity;
        Direction = direction;
    }
    public override string ToString()
    {
        return Json.Serialize(this);
    }

}

/// <summary>
/// Market-by-Price wire message: header + counts + trailing Level arrays (bids then asks).
/// Layout:
/// [ TickHeader | BidsCount:int | AsksCount:int | bids[0..BidsCount-1] | asks[0..AsksCount-1] ]
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct MarketByPrice
{
    public TickHeader TickHeader;
    public int BidsCount;
    public int AsksCount;
    public MarketByPrice(TickType mbpType, int instrumentId, Timestamp exchangeTimestamp, Timestamp nicTimestamp, int bidsCount, int asksCount)
    {
        if (!(mbpType == TickType.MarketByPriceSnapshot || mbpType == TickType.MarketByPriceUpdate || mbpType == TickType.MarketByPricePartialUpdate || mbpType == TickType.MarketByPricePartialUpdate))
            throw new ArgumentException($"Invalid mbpType: {mbpType}");
        TickHeader = new TickHeader(mbpType, instrumentId, exchangeTimestamp, nicTimestamp);
        BidsCount = bidsCount;
        AsksCount = asksCount;
    }


    // ----- Pointer helpers (unsafe) -----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe static Level* GetBidsPtr(MarketByPrice* mbpPtr)
        => (Level*)((byte*)mbpPtr + Unsafe.SizeOf<MarketByPrice>());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe static Level* GetAsksPtr(MarketByPrice* mbpPtr)
        => GetBidsPtr(mbpPtr) + mbpPtr->BidsCount;

    public readonly string BidsAsString
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            unsafe
            {
                fixed (MarketByPrice* mbpPtr = &this)
                {
                    Level* bidsPtr = GetBidsPtr(mbpPtr);
                    ReadOnlySpan<Level> bids = new ReadOnlySpan<Level>(bidsPtr, BidsCount);
                    return ToString(bids);
                }
            }
        }
    }

    public readonly string AsksAsString
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            unsafe
            {
                fixed (MarketByPrice* mbpPtr = &this)
                {
                    Level* asksPtr = GetAsksPtr(mbpPtr);
                    ReadOnlySpan<Level> asks = new ReadOnlySpan<Level>(asksPtr, AsksCount);
                    return ToString(asks);
                }
            }
        }
    }

    public static string ToString(ReadOnlySpan<Level> levels)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("[");
        for (int i = 0; i < levels.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"({levels[i].Ticks}, {levels[i].Quantity})");
        }
        sb.Append("]");
        return sb.ToString();
    }



    // ----- Size helpers -----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SizeOf() => SizeOf(BidsCount, AsksCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SizeOfLevels() => (BidsCount + AsksCount) * Unsafe.SizeOf<Level>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeOf(int bidsCount, int asksCount)
        => Unsafe.SizeOf<MarketByPrice>() + (bidsCount + asksCount) * Unsafe.SizeOf<Level>();

    // ===== Instance Span-based accessors (no unsafe/pinning) =====

    /// <summary>Span over bids stored immediately after the header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Level> BidsAsSpan(Span<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes, BidsCount * levelBytes));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Level> BidsAsSpan(ReadOnlySpan<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes, BidsCount * levelBytes));
    }

    /// <summary>Span over asks stored after the bids block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Level> AsksAsSpan(Span<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        int bidsBytes = BidsCount * levelBytes;
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes + bidsBytes, AsksCount * levelBytes));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Level> AsksAsSpan(ReadOnlySpan<byte> src)
    {
        int headerBytes = Unsafe.SizeOf<MarketByPrice>();
        int levelBytes = Unsafe.SizeOf<Level>();
        int bidsBytes = BidsCount * levelBytes;
        return MemoryMarshal.Cast<byte, Level>(src.Slice(headerBytes + bidsBytes, AsksCount * levelBytes));
    }


    public readonly override string ToString()
    {
        unsafe
        {
            // Take pointer to this struct instance
            fixed (MarketByPrice* mbpPtr = &this)
            {
                // Derive level pointers
                Level* bidsPtr = GetBidsPtr(mbpPtr);
                Level* asksPtr = GetAsksPtr(mbpPtr);

                // Wrap contiguous memory as spans (no allocation, no copy)
                ReadOnlySpan<Level> bids = new ReadOnlySpan<Level>(bidsPtr, BidsCount);
                ReadOnlySpan<Level> asks = new ReadOnlySpan<Level>(asksPtr, AsksCount);

                // Build a concrete DTO for AOT JSON serialization
                var obj = new MarketByPriceDump
                {
                    Header = TickHeader,
                    BidsCount = BidsCount,
                    AsksCount = AsksCount,
                    Bids = bids.ToArray(), // unavoidable small copy for JSON
                    Asks = asks.ToArray()
                };

                return Json.Serialize(obj);
            }
        }

    }



    public static ref MarketByPrice SnapshotAsUpdate(ReadOnlySpan<byte> past, ReadOnlySpan<byte> future, Span<byte> dst)
    {
        ref readonly MarketByPrice pastMbp = ref MemoryMarshal.AsRef<MarketByPrice>(past);
        ref readonly MarketByPrice futureMbp = ref MemoryMarshal.AsRef<MarketByPrice>(future);

        int maxBidChanges = pastMbp.BidsCount + futureMbp.BidsCount;
        int maxAskChanges = pastMbp.AsksCount + futureMbp.AsksCount;

        int maxSize = SizeOf(maxBidChanges, maxAskChanges);
        if (dst.Length < maxSize)
            throw new ArgumentException($"Destination span too small: {dst.Length} < {maxSize}");

        ref MarketByPrice updateMbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);

        updateMbp.TickHeader = new TickHeader(
            TickType.MarketByPriceUpdate,
            futureMbp.TickHeader.InstrumentId,
            futureMbp.TickHeader.ExchangeTimestamp,
            futureMbp.TickHeader.NicTimestamp
        );

        ReadOnlySpan<Level> pastBids = pastMbp.BidsAsSpan(past);
        ReadOnlySpan<Level> pastAsks = pastMbp.AsksAsSpan(past);
        ReadOnlySpan<Level> futureBids = futureMbp.BidsAsSpan(future);
        ReadOnlySpan<Level> futureAsks = futureMbp.AsksAsSpan(future);

        // ---- BIDS ----
        // temporarily claim max space so BidsAsSpan(...) returns a big span
        updateMbp.BidsCount = maxBidChanges;
        int bidsWritten = DiffBids(pastBids, futureBids, updateMbp.BidsAsSpan(dst));
        updateMbp.BidsCount = bidsWritten;   // final count

        // ---- ASKS ----
        // BidsCount is final now, so asks will start at the correct offset
        updateMbp.AsksCount = maxAskChanges;
        int asksWritten = DiffAsks(pastAsks, futureAsks, updateMbp.AsksAsSpan(dst));
        updateMbp.AsksCount = asksWritten;   // final count

        return ref MemoryMarshal.AsRef<MarketByPrice>(dst);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DiffBids(ReadOnlySpan<Level> past, ReadOnlySpan<Level> future, Span<Level> dst)
    {
        return Diff(past, future, dst, false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DiffAsks(ReadOnlySpan<Level> past, ReadOnlySpan<Level> future, Span<Level> dst)
    {
        return Diff(past, future, dst, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Diff(ReadOnlySpan<Level> past, ReadOnlySpan<Level> future, Span<Level> dst, bool isAsk)
    {
        int pastIndex = 0;
        int futureIndex = 0;
        int destinationIndex = 0;

        while (pastIndex < past.Length && futureIndex < future.Length)
        {
            int pastTicks = past[pastIndex].Ticks;
            int futureTicks = future[futureIndex].Ticks;

            if (pastTicks == futureTicks)
            {
                if (past[pastIndex].Quantity != future[futureIndex].Quantity)
                {
                    dst[destinationIndex] = future[futureIndex];
                    destinationIndex++;
                }

                pastIndex++;
                futureIndex++;
            }
            else
            {
                bool isPastMissingInFuture = isAsk ? (pastTicks < futureTicks) : (pastTicks > futureTicks);

                if (isPastMissingInFuture)
                {
                    dst[destinationIndex] = new Level(pastTicks, 0);
                    destinationIndex++;
                    pastIndex++;
                }
                else
                {
                    dst[destinationIndex] = future[futureIndex];
                    destinationIndex++;
                    futureIndex++;
                }
            }
        }

        while (futureIndex < future.Length)
        {
            dst[destinationIndex] = future[futureIndex];
            destinationIndex++;
            futureIndex++;
        }

        while (pastIndex < past.Length)
        {
            dst[destinationIndex] = new Level(past[pastIndex].Ticks, 0);
            destinationIndex++;
            pastIndex++;
        }

        return destinationIndex;
    }
}
//END_FILE HFT/Data/Tick.cs
*/