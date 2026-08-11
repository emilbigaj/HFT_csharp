using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tools;

namespace Data;

// The C++ side still declares this type under its old ExpiryType name -- rename it there before
// relying on the two lining up. Layout is unaffected either way: 1 byte, values are positive ASCII
// so a (char) cast still yields the code letter.
[RegisterJson]
public enum MaturityType : byte
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
            throw new FormatException("Ticker must contain root and a maturity part.");

        string root = ticker.Substring(0, spaceAfterRoot);
        string remainder = ticker.Substring(spaceAfterRoot + 1);

        if (instrumentType == InstrumentType.Future)
        {
            MaturityType maturityType;
            Timestamp maturityDate;
            ParseMaturityTokenUsingFromDateString(remainder, out maturityType, out maturityDate);
            return new FutureSymbology(exchange, root, maturityType, maturityDate);
        }
        else if (instrumentType == InstrumentType.Spread)
        {
            string[] legs = remainder.Split(" - ", StringSplitOptions.TrimEntries);
            if (legs.Length != 2)
                throw new FormatException("Spread ticker must be in the form \"<E><Date> - <E><Date>\".");

            // Long leg
            MaturityType longMaturityType;
            Timestamp longMaturityDate;
            ParseMaturityTokenUsingFromDateString(legs[0], out longMaturityType, out longMaturityDate);

            // Short leg
            MaturityType shortMaturityType;
            Timestamp shortMaturityDate;
            ParseMaturityTokenUsingFromDateString(legs[1], out shortMaturityType, out shortMaturityDate);

            return new SpreadSymbology(exchange, root, longMaturityType, longMaturityDate, shortMaturityType, shortMaturityDate);
        }

        throw new NotSupportedException($"FromString does not yet support InstrumentType {instrumentType}.");
    }

    // Helper: token is like "M20251215" where 'M' is the MaturityType code char.
    private static void ParseMaturityTokenUsingFromDateString(string token,
                                                            out MaturityType maturityType,
                                                            out Timestamp maturityDate)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
            throw new FormatException("Maturity token must start with a letter and include a date, e.g., M20251215.");

        char typeChar = token[0];
        maturityType = (MaturityType)typeChar; // enum values defined as 'D','W','M','Q','Y'

        string dateText = token.Substring(1);

        try
        {
            // Your requested form:
            // Timestamp longMaturityDate = Timestamp.FromDateString(longMaturityDateToken);
            maturityDate = Timestamp.FromString(dateText, "yyyy-MM-dd");
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid maturity date: \"{dateText}\".", ex);
        }
    }

    public override string ToString() => Symbol;
}


[RegisterJson]
public class FutureSymbology : Symbology
{
    public MaturityType MaturityType { get; protected set; }
    public Timestamp MaturityDate { get; protected set; }
    public override string ShortSymbol { get; }


    // Normal ctor for outright futures: fixes InstrumentType.Future and auto-builds ticker.
    public FutureSymbology(string exchange, string root, MaturityType maturityType, Timestamp maturityDate)
        : this(InstrumentType.Future, exchange, root, $"{root} {(char)maturityType}{maturityDate.ToDateString()}", maturityType, maturityDate)
    {

    }

    // Protected flex ctor for subclasses (e.g., Spread) to set InstrumentType and custom ticker.
    protected FutureSymbology(InstrumentType instrumentType, string exchange, string root, string ticker, MaturityType maturityType, Timestamp maturityDate)
        : base(instrumentType, exchange, root, ticker)
    {
        MaturityType = maturityType;
        MaturityDate = maturityDate;
        string shortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(maturityDate.Month);
        ShortSymbol = $"{root} {shortMonthName} {maturityDate.Year}";
    }
}

[RegisterJson]
public class SpreadSymbology : FutureSymbology
{
    public FutureSymbology LongSymbology { get; }
    public FutureSymbology ShortSymbology { get; }
    public override string ShortSymbol { get; }
    public SpreadSymbology(string exchange, string root, MaturityType longMaturityType, Timestamp longMaturityDate, MaturityType shortMaturityType, Timestamp shortMaturityDate)
        : base(InstrumentType.Spread, exchange, root, $"{root} {(char)longMaturityType}{longMaturityDate.ToDateString()} - {(char)shortMaturityType}{shortMaturityDate.ToDateString()}", (longMaturityDate <= shortMaturityDate) ? longMaturityType : shortMaturityType, (longMaturityDate <= shortMaturityDate) ? longMaturityDate : shortMaturityDate)
    {
        LongSymbology = new FutureSymbology(exchange, root, longMaturityType, longMaturityDate);
        ShortSymbology = new FutureSymbology(exchange, root, shortMaturityType, shortMaturityDate);
        string longShortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(longMaturityDate.Month);
        string shortShortMonthName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(shortMaturityDate.Month);
        ShortSymbol = $"{root} {longShortMonthName} {longMaturityDate.Year} - {shortShortMonthName} {shortMaturityDate.Year}";
    }
}
