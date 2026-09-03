using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        // Future: "<Root> <E><Date>"                e.g., "ES M2025-12-15"
        // Spread: "<Root> +<E><Date> -<E><Date>"    signed leg tokens, weight folded into the sign
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
            // Signed leg tokens "±[n]<E><Date>": root appears once, legs maturity-ascending.
            List<Symbology> symbologies = new List<Symbology>();
            List<int> weights = new List<int>();
            foreach (string legToken in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int sign = legToken[0] == '+' ? 1 : legToken[0] == '-' ? -1 : throw new FormatException($"Spread leg \"{legToken}\" must start with '+' or '-'.");
                int index = 1;
                int magnitude = 0;
                while (index < legToken.Length && char.IsAsciiDigit(legToken[index]))
                {
                    magnitude = magnitude * 10 + (legToken[index] - '0');
                    index++;
                }
                ParseMaturityTokenUsingFromDateString(legToken.Substring(index), out MaturityType maturityType, out Timestamp maturityDate);
                symbologies.Add(new FutureSymbology(exchange, root, maturityType, maturityDate));
                weights.Add(sign * Math.Max(magnitude, 1));
            }
            return new SpreadSymbology(exchange, root, symbologies, weights);
        }

        throw new NotSupportedException($"FromString does not yet support InstrumentType {instrumentType}.");
    }

    // Helper: token is like "M2025-12-15" where 'M' is the MaturityType code char.
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
        ShortSymbol = $"{root} {shortMonthName}{maturityDate.Year%100}";
    }
}

public record struct SymbolLeg(string Symbol, int Weight);

public class LeggedSymbology : Symbology
{
    public List<Symbology> Symbologies { get; }
    public List<int> Weights { get; }

    public override string ShortSymbol { get; }

    // The root appears once, up front; leg tokens carry only sign+maturity ("+M2025-12-19").
    public LeggedSymbology(InstrumentType instrumentType, string exchange, string root, List<Symbology> symbologies, List<int> weights)
        : base(instrumentType, exchange, root, GetLegsTicker(root, symbologies.Select((l, i) => new SymbolLeg(l.Ticker.Replace(l.Root + " ", ""), weights[i])).ToList()))
    {
        Symbologies = symbologies;
        Weights = weights;
        ShortSymbol = GetLegsTicker(root, symbologies.Select((l, i) => new SymbolLeg(l.ShortSymbol.Replace(l.Root + " ", ""), weights[i])).ToList());
    }

    // Weight rendered as the leg-token sign: "+", "-", "+2"; negative weights carry their own '-'.
    public static string GetSignedWeight(int weight) => weight switch
    {
        1 => "+",
        -1 => "-",
        > 1 => $"+{weight}",
        _ => weight.ToString(),   // 0 renders as "0"
    };

    public static string GetLegsTicker(string root, List<SymbolLeg> legs)
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(root + " ");
        int i = 0;
        while (i < legs.Count)
        {
            string symbol = legs[i].Symbol;
            int weight = legs[i].Weight;
            stringBuilder.Append(GetSignedWeight(weight) + symbol);
            i++;
            if (i < legs.Count)
            {
                stringBuilder.Append(" ");
            }
        }
        return stringBuilder.ToString();
    }
}




[RegisterJson]
public class SpreadSymbology : LeggedSymbology
{
    public SpreadSymbology(string exchange, string root, List<Symbology> symbologies, List<int> weights)
        : base(InstrumentType.Spread, exchange, root, symbologies, weights)
    {
    }
}
