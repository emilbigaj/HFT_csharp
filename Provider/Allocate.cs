using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;
using Execution;
using Data;

namespace Provider;


[RegisterJson]
public enum AllocateType : byte
{
    Client = 100,
    Instrument = 101,
}


[RegisterJson]
public enum ControlType : byte
{
    AlgoStatus = 200,
    RiskLimit = 201,
    TransferPosition = 202,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct AllocateClient()
{
    public readonly Header<AllocateType> Header = new(AllocateType.Client);
    public int ClientId;
    public String256 ClientName;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct AllocateInstrument()
{
    public readonly Header<AllocateType> Header = new(AllocateType.Instrument);
    public int ClientId = -1;
    public int InstrumentHeaderId = -1;
    public int InstrumentId = -1;
    public int ExchangeInstrumentId = -1;
    public String64 Symbol;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ControlAlgoStatus()
{
    public readonly Header<ControlType> Header = new(ControlType.AlgoStatus);
    public int ClientId = -1;
    public int StrategyId = -1;
    public int InstrumentId = -1;
    public AlgoStatus AlgoStatus = AlgoStatus.Paused;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

// Client -> server request: config fields only. The server owns every limit row, the firm's (strategy 0) and each strategy's, and a client never sends one (see Spec.md).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ControlRiskLimit()
{
    public readonly Header<ControlType> Header = new(ControlType.RiskLimit);
    public int ClientId = -1;
    public int StrategyId = -1;   // the row edited: 0 the firm's, otherwise that strategy's
    public int InstrumentId = -1;
    public int MaxOrderQuantity = 0;
    public int MaxPositionQuantity = 0;
    public int MaxFillsPerSession = 0;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

// Server workspace -> server request: moves Quantity of the from book's position into the to book at Price, as two Transfer fills;
// the firm's row does not move (see Spec.md "Position transfer").
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ControlTransferPosition()
{
    public readonly Header<ControlType> Header = new(ControlType.TransferPosition);
    public int ClientId = -1;          // the requesting GUI's: each Transfer fill's order id carries it, so neither counts as an algo's
    public int InstrumentId = -1;
    public int FromStrategyId = -1;    // 0 the house book
    public int ToStrategyId = -1;
    public int Quantity = 0;           // the part of the from book's position that moves, signed as that position
    public double Price = double.NaN;

    /// The Transfer fill that books quantity to this strategy at the transfer's price; both fills of a transfer share its FillId.
    public readonly Fill NewTransferFill(int strategyId, int quantity, Timestamp timestamp) => new()
    {
        OrderHeader = new()
        {
            OrderId = new() { ClientId = ClientId, StrategyId = strategyId, InstrumentId = InstrumentId },
            ExchangeTimestamp = timestamp,
            NicTimestamp = timestamp,
        },
        FillId = (ulong)timestamp.NanosSinceEpoch,
        Price = Price,
        Quantity = quantity,
        FillType = FillType.Transfer,
    };

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct CoreGroup()
{
    public String16 CoreGroupName;
    public int CoreGroupId = -1;
    public int ServerCoreId = -1;
    public int MarketDataCoreId = -1;
    public int StrategyCoreId = -1;
    public int ReservedCoreId = -1; // could be a second server, strategy or market data core - depends on circumstances.
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ServerHeader()
{
    public String256 ServerName;
    public Timestamp Timestamp;   // simulation only: the simulation clock, written by ServerSimulator, which a simulated Workspace or Mirror follows
    public int InstrumentsCapacity = 4096;
    public int InstrumentsCount = 0;

    public Bitset64 InstrumentIds = new Bitset64();
    public Bitset64 ClientIds = new Bitset64();
    public Bitset64 CoreGroupIds = new Bitset64();

    public int OrdersPerClient = 64;

    // Client sockets outlive their client process: a dropped client goes Detached instead of being
    // disposed, so the server keeps writing into its ring and the audit tap keeps reading.
    // WIRE FIELD — mirrors Provider/Allocate.hpp byte-for-byte: offset 300, sizeof(ServerHeader) 305 (ServerName is 256 bytes since 2026-10-20).
    // MarshalAs(U1) pins it to 1 byte: the managed layout Unsafe.SizeOf/MemoryMarshal use already
    // treats bool as 1, but interop marshalling would otherwise default it to a 4-byte BOOL.
    [MarshalAs(UnmanagedType.U1)]
    public bool Persistance = true;

    // WIRE FIELD, offset 301: the publishing server's process id, stamped by ServerContext.Connect; a new value means the server restarted.
    public int ServerProcessId;

    public int OrdersCapacity => OrdersPerClient * ClientIds.Length;
    public int LocalPositionsCapacity => InstrumentIds.Length * ClientIds.Length;

    static ServerHeader()
    {
        // The C++ server publishes this struct into shared memory. A silent size drift here means
        // every field after the drift is read from the wrong offset.
        int size = Unsafe.SizeOf<ServerHeader>();
        if (size != 305)
            throw new InvalidOperationException($"ServerHeader must be 305 bytes to match Provider/Allocate.hpp, was {size}.");
    }

    public override string ToString()
    {
        return Json.Serialize(this);
    }

}