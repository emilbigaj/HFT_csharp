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
    public int OrdersCapacity => OrdersPerClient * ClientIds.Length;
    public int LocalPositionsCapacity => InstrumentIds.Length * ClientIds.Length;

    public override string ToString()
    {
        return Json.Serialize(this);
    }

}