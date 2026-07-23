using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tools;

namespace Data;

[RegisterJson]
public enum ExpiryType : byte // 1 byte to match C++ `enum ExpiryType : char`; values are positive ASCII so (char)cast still yields the code letter
{
    Day = (byte)'D',
    Week = (byte)'W',
    Month = (byte)'M',
    Quarter = (byte)'Q',
    Year = (byte)'Y'
}

[RegisterJson]
public enum InstrumentType : byte
{
    Instrument = 50,
    Future = 51,
    Option = 52,
    Swap = 53,
    Stock = 54,
    Spread = 55,
    Forex = 56,
}

[RegisterJson]
public class Symbology
{
    public InstrumentType InstrumentType { get; }
    public string Exchange { get; }
    public string Root { get; }
    public string Ticker { get; }
    public string Symbol { get; } // this is unique and should be used for lookups
    public string Product { get; }
    public virtual string ShortSymbol { get; } // this is not unique

    protected Symbology(InstrumentType instrumentType, string exchange, string root, string ticker)
    {
        if (string.IsNullOrWhiteSpace(exchange)) throw new ArgumentException("exchange is required", nameof(exchange));
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root is required", nameof(root));
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("ticker is required", nameof(ticker));

        InstrumentType = instrumentType;
        Exchange = exchange;
        Root = root;
        Ticker = ticker;
        Product = $"{InstrumentType} {Exchange} {Root}";
        Symbol = $"{InstrumentType} {Exchange} {Ticker}";
        ShortSymbol = ticker;
    }

    public static Symbology FromString(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("symbol is required", nameof(symbol));

        // Expected: "<InstrumentType> <Exchange> <Ticker...>"
        int firstSpace = symbol.IndexOf(' ');
        int secondSpace = (firstSpace < 0) ? -1 : symbol.IndexOf(' ', firstSpace + 1);
        if (firstSpace < 0 || secondSpace < 0)
            throw new FormatException("Expected format: \"InstrumentType Exchange Ticker\".");

        string instrumentTypeText = symbol.Substring(0, firstSpace);
        string exchange = symbol.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
        string ticker = symbol.Substring(secondSpace + 1);

        InstrumentType instrumentType =
            (InstrumentType)Enum.Parse(typeof(InstrumentType), instrumentTypeText, true);

        // Ticker formats emitted:
        // Future: "<Root> <E><Date>"        e.g., "ES M20251215"
        // Spread: "<Root> <E><Date> - <E><Date>"
        int spaceAfterRoot = ticker.IndexOf(' ');
        if (spaceAfterRoot < 0)
            throw new FormatException("Ticker must contain root and an expiry part.");

        string root = ticker.Substring(0, spaceAfterRoot);
        string remainder = ticker.Substring(spaceAfterRoot + 1);

        if (instrumentType == InstrumentType.Future)
        {
            ExpiryType expiryType;
            Timestamp expiryDate;
            ParseExpiryTokenUsingFromDateString(remainder, out expiryType, out expiryDate);
            return new FutureSymbology(exchange, root, expiryType, expiryDate);
        }
        else if (instrumentType == InstrumentType.Spread)
        {
            string[] legs = remainder.Split(" - ", StringSplitOptions.TrimEntries);
            if (legs.Length != 2)
                throw new FormatException("Spread ticker must be in the form \"<E><Date> - <E><Date>\".");

            // Long leg
            ExpiryType longExpiryType;
            Timestamp longExpiryDate;
            ParseExpiryTokenUsingFromDateString(legs[0], out longExpiryType, out longExpiryDate);

            // Short leg
            ExpiryType shortExpiryType;
            Timestamp shortExpiryDate;
            ParseExpiryTokenUsingFromDateString(legs[1], out shortExpiryType, out shortExpiryDate);

            return new SpreadSymbology(exchange, root, longExpiryType, longExpiryDate, shortExpiryType, shortExpiryDate);
        }

        throw new NotSupportedException($"FromString does not yet support InstrumentType {instrumentType}.");
    }

    // Helper: token is like "M20251215" where 'M' is the ExpiryType code char.
    private static void ParseExpiryTokenUsingFromDateString(string token,
                                                            out ExpiryType expiryType,
                                                            out Timestamp expiryDate)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
            throw new FormatException("Expiry token must start with a letter and include a date, e.g., M20251215.");

        char typeChar = token[0];
        expiryType = (ExpiryType)typeChar; // enum values defined as 'D','W','M','Q','Y'

        string dateText = token.Substring(1);

        try
        {
            // Your requested form:
            // Timestamp longExpiryDate = Timestamp.FromDateString(longExpiryDateToken);
            expiryDate = Timestamp.FromString(dateText, "yyyy-MM-dd");
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid expiry date: \"{dateText}\".", ex);
        }
    }

    public override string ToString() => Symbol;
}


[RegisterJson]
public class FutureSymbology : Symbology
{
    public ExpiryType ExpiryType { get; protected set; }
    public Timestamp ExpiryDate { get; protected set; }
    public override string ShortSymbol { get; }


    // Normal ctor for outright futures: fixes InstrumentType.Future and auto-builds ticker.
    public FutureSymbology(string exchange, string root, ExpiryType expiryType, Timestamp expiryDate)
        : this(InstrumentType.Future, exchange, root, $"{root} {(char)expiryType}{expiryDate.ToDateString()}", expiryType, expiryDate)
    {

    }

    // Protected flex ctor for subclasses (e.g., Spread) to set InstrumentType and custom ticker.
    protected FutureSymbology(InstrumentType instrumentType, string exchange, string root, string ticker, ExpiryType expiryType, Timestamp expiryDate)
        : base(instrumentType, exchange, root, ticker)
    {
        ExpiryType = expiryType;
        ExpiryDate = expiryDate;
        string shortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(expiryDate.Month);
        ShortSymbol = $"{root} {shortMonthName} {expiryDate.Year}";
    }
}

[RegisterJson]
public class SpreadSymbology : FutureSymbology
{
    public FutureSymbology LongSymbology { get; }
    public FutureSymbology ShortSymbology { get; }
    public override string ShortSymbol { get; }
    public SpreadSymbology(string exchange, string root, ExpiryType longExpiryType, Timestamp longExpiryDate, ExpiryType shortExpiryType, Timestamp shortExpiryDate)
        : base(InstrumentType.Spread, exchange, root, $"{root} {(char)longExpiryType}{longExpiryDate.ToDateString()} - {(char)shortExpiryType}{shortExpiryDate.ToDateString()}", (longExpiryDate <= shortExpiryDate) ? longExpiryType : shortExpiryType, (longExpiryDate <= shortExpiryDate) ? longExpiryDate : shortExpiryDate)
    {
        LongSymbology = new FutureSymbology(exchange, root, longExpiryType, longExpiryDate);
        ShortSymbology = new FutureSymbology(exchange, root, shortExpiryType, shortExpiryDate);
        string longShortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(longExpiryDate.Month);
        string shortShortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(shortExpiryDate.Month);
        ShortSymbol = $"{root} {longShortMonthName} {longExpiryDate.Year} - {shortShortMonthName} {shortExpiryDate.Year}";
    }
}
