using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tools;

namespace Data;


/// <summary>
/// Identifies the logical type object flowing over a socket.
/// </summary>
/// 

[RegisterJson]
public enum FileType : byte
{
    Log = 0,
    Audit = 1,
    Fill = 2,
    Position = 3,
    MarketByPrice = 4,
    Trade = 5,
    Point = 6,
    Pair = 7,
    Candle = 8,
    Histogram = 9,
    Alert = 10,
    Factor = 11,
    Mean = 12,
    StdDev = 13,
}

[RegisterJson]
public struct Point(Timestamp timestamp, double value)
{
    public Timestamp Timestamp = timestamp;
    public double Value = value;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[RegisterJson]
public struct Pair(Timestamp timestamp, double first, double second)
{
    public Timestamp Timestamp = timestamp;
    public double First = first;
    public double Second = second;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[RegisterJson]
public enum FillType : byte
{
    Maker = 0,
    Taker = 1,
    Auction = 2,
}


[RegisterJson]
public struct Filld(Timestamp timestamp, double price, double quantity, FillType fillType)
{
    public Timestamp Timestamp = timestamp;
    public double Price = price;
    public double Quantity = quantity;
    public FillType FillType = fillType;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[RegisterJson]
public struct Histogram(Timestamp opened, Timestamp closed, double value)
{
    public Timestamp Opened = opened;
    public Timestamp Closed = closed;

    public double Value = value;
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[RegisterJson]
public struct Candle(Timestamp opened, double price)
{
    public Timestamp Opened = opened;
    public Timestamp Closed = opened;
    public double Open = price;
    public double High = price;
    public double Low = price;
    public double Close = price;
    public double Volume = 0;
    public void OnTrade(double price, double quantity)
    {
        High = Math.Max(High, price);
        Low = Math.Min(Low, price);
        Close = price;
        Volume += quantity;
    }
    public void OnClose(Timestamp closed)
    {
        Closed = closed;
    }
    public override string ToString()
    {
        return Json.Serialize(this);
    }
}
