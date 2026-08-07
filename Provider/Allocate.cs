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
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct AllocateClient()
{
    public readonly Header<AllocateType> Header = new(AllocateType.Client);
    public int ClientId;
    public String128 ClientName;
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



[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct ServerHeader()
{
    public String128 ServerName;
    public Timestamp Timestamp;
    public int InstrumentsCapacity = 4096;
    public int InstrumentsCount = 0;

    public Bitset64 InstrumentIds = new Bitset64();
    public Bitset64 ClientIds = new Bitset64();
    public Bitset64 CoreGroupIds = new Bitset64();

    public int OrdersPerClient = 64;

    // Client sockets outlive their client process: a dropped client goes Detached instead of being
    // disposed, so the server keeps writing into its ring and the audit tap keeps reading.
    // WIRE FIELD — mirrors Provider/Allocate.hpp byte-for-byte: offset 172, sizeof(ServerHeader) 173.
    // MarshalAs(U1) pins it to 1 byte: the managed layout Unsafe.SizeOf/MemoryMarshal use already
    // treats bool as 1, but interop marshalling would otherwise default it to a 4-byte BOOL.
    [MarshalAs(UnmanagedType.U1)]
    public bool Persistance = true;

    public int OrdersCapacity => OrdersPerClient * ClientIds.Length;
    public int LocalPositionsCapacity => InstrumentIds.Length * ClientIds.Length;

    static ServerHeader()
    {
        // The C++ server publishes this struct into shared memory. A silent size drift here means
        // every field after the drift is read from the wrong offset.
        int size = Unsafe.SizeOf<ServerHeader>();
        if (size != 173)
            throw new InvalidOperationException($"ServerHeader must be 173 bytes to match Provider/Allocate.hpp, was {size}.");
    }

    public override string ToString()
    {
        return Json.Serialize(this);
    }

}