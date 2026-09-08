using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Tools;

// ======================= Supporting enums/types =======================

namespace Data;

// How a venue allocates fills among resting orders at the same price. Only these five occur on CME
// (GLBX.MDP3 definition field match_algorithm); anything else the venue reports maps to Unknown.
[RegisterJson]
public enum MatchType : byte
{
    Unknown,
    Fifo,                   // 'F' — pure time priority; every liquid product (ES, NQ, CL, ZN)
    Configurable,           // 'K' — grains and softs (ZC, ZS, ZM, ZL, LBR, dairy)
    FifoLmm,                // 'T' — FIFO after a lead-market-maker allocation
    Allocation,             // 'A' — SOFR and rates (SR3, ESR, TBF3)
    ThresholdProRataLmm,    // 'Q' — AW, AWT, DRT, GDT, GIT
}

// "ValueType" discriminator makes Schedule entries round-trip as their leaf types under AOT source-gen.
[RegisterJson]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "ValueType")]
[JsonDerivedType(typeof(InstrumentDetailDouble), "double")]
[JsonDerivedType(typeof(InstrumentDetailString), "string")]
[JsonDerivedType(typeof(InstrumentDetailMatchType), "MatchType")]

public abstract class InstrumentDetail
{
    public Timestamp Timestamp { get; init; }
    public string Field { get; set; } = string.Empty;
    [JsonIgnore]
    public object Value { get; set; } = null!;   // boxed view; the typed leaf property is the JSON one
}

// Deliberately unregistered: open generics can't be source-gen registered; only the closed leaves are.
public abstract class InstrumentDetail<T> : InstrumentDetail
{
    public new T Value { get => (T)base.Value!; set => base.Value = value!; }
}

[RegisterJson]
public sealed class InstrumentDetailDouble : InstrumentDetail<double>
{
}

[RegisterJson]
public sealed class InstrumentDetailString : InstrumentDetail<string>
{
}

[RegisterJson]
public sealed class InstrumentDetailMatchType : InstrumentDetail<MatchType>
{
}

[RegisterJson]
public class InstrumentDetailsSearch
{
    public InstrumentType? InstrumentType { get; set; }
    public string? Exchange { get; set; }
    public string? Root { get; set; }
    public string? Ticker { get; set; }
    public FileSystemPath DirectoryPath { get; set; } = @"Z:\InstrumentDetails";


    public static ArrayList<InstrumentDetails> Search(InstrumentDetailsSearch search)
    {
        if (search is null)
            throw new ArgumentNullException(nameof(search));

        Console.WriteLine($"InstrumentDetailsSearch::Search({Json.SerializeToLine(search)})");

        ArrayList<InstrumentDetails> results = new ArrayList<InstrumentDetails>(16);

        if (string.IsNullOrWhiteSpace(search.DirectoryPath) || !Directory.Exists(search.DirectoryPath))
            return results;

        string[] files = Directory.GetFiles(search.DirectoryPath, "*.json");

        // Bound concurrency: IO-bound, so > CPU count helps, but cap to avoid storage saturation
        ParallelOptions parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(16, LowLatency.HouseKeepingCores.Length)
        };

        object gate = new object();

        // Thread-local aggregation → no shared writes inside the hot loop
        Parallel.ForEach<string, ArrayList<InstrumentDetails>>(files, parallelOptions, () => new ArrayList<InstrumentDetails>(64), (string filePath, ParallelLoopState loopState, ArrayList<InstrumentDetails> local) =>
        {
            InstrumentDetails instrumentDetails;
            try
            {
                instrumentDetails = InstrumentDetails.FromFile(filePath);
                Console.WriteLine(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InstrumentDetailsSearch.Search() Failed to parse: {filePath}");
                Console.WriteLine(ex.ToString());
                throw; // preserve existing behavior: fail hard on a bad file
            }

            // --- Apply filters (only when specified) ---
            if (search.InstrumentType.HasValue &&
                instrumentDetails.InstrumentType != search.InstrumentType.Value)
                return local;

            if (!string.IsNullOrWhiteSpace(search.Exchange) &&
                !instrumentDetails.Exchange.Equals(search.Exchange, StringComparison.OrdinalIgnoreCase))
                return local;

            if (!string.IsNullOrWhiteSpace(search.Ticker) &&
                !instrumentDetails.Ticker.Equals(search.Ticker, StringComparison.OrdinalIgnoreCase))
                return local;

            if (!string.IsNullOrWhiteSpace(search.Root) &&
                !instrumentDetails.Root.Equals(search.Root, StringComparison.OrdinalIgnoreCase))
                return local;

            local.Add(instrumentDetails);
            return local;
        },
        (ArrayList<InstrumentDetails> local) =>
        {
            if (local.Count == 0) return;
            lock (gate)
            {
                foreach(InstrumentDetails details in local)
                    results.Add(details);
            }
        });

        results.Sort((InstrumentDetails a, InstrumentDetails b) => string.CompareOrdinal(a.FilePath, b.FilePath));
        return results;
    }

}

// calendar spread looks like , 1, -1
// butterfly looks like , 1, -2, 1
// strip might look like , 1, -1, -1
[RegisterJson]
public sealed class Leg
{
    public int Weight { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string ExchangeInstrumentId { get; set; } = string.Empty;

}


// ======================= The InstrumentDetails model =======================
[RegisterJson]
public sealed class InstrumentDetails
{
    public string FileName => Symbol + ".json";

    public string GetFilePath(string directoryPath) => Path.Combine(directoryPath, FileName);

    // --------- Core schema (round-trippable) ---------
    public InstrumentType InstrumentType { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string Root { get; set; } = string.Empty;
    public string Ticker => Symbology.Ticker;
    public string Symbol => Symbology.Symbol;
    public string Currency { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public string DatabentoSymbol { get; set; } = string.Empty;

    // The venue's own instrument id, as a string so every exchange's format fits. Reference only:
    // it is unique within a dataset on a given day, but is reused across years, so it must never
    // key a catalog-wide lookup.
    public string ExchangeInstrumentId { get; set; } = string.Empty;
    public string RicRoot { get; set; } = string.Empty;
    public string RicSymbol { get; set; } = string.Empty;
    public string RicExchange { get; set; } = string.Empty;

    public double QuantitySize { get; set; } = 1.0;
    public double Multiplier { get; set; } = 1.0;

    private double _tickSize;
    private double _inverseTickSize;

    public double TickSize
    {
        get => _tickSize;
        set
        {
            _tickSize = value;
            _inverseTickSize = (double)(1.0m / (decimal)value);
        }
    }
    public double InverseTickSize => _inverseTickSize;

    /// <summary>Fill allocation at a price level — not FIFO for ~3,100 CME contracts, which changes queue modelling.</summary>
    public MatchType MatchType { get; set; } = MatchType.Unknown;

    public Session[] Sessions { get; set; } = Array.Empty<Session>();
    
    // foreach(var detail in Schedule) Clock.AddReminder(new Reminder(detail.Timestamp, timestamp => OnInstrumentDetail(detail)));
    public List<InstrumentDetail> Schedule { get; set; } = new List<InstrumentDetail>();

    // Exactly the fields OnInstrumentDetail applies — the only changes that earn a Schedule entry;
    // every other changed field just adopts its latest value.
    public static readonly HashSet<string> Replayed = new HashSet<string> { nameof(TickSize), nameof(MatchType) };

    public void OnInstrumentDetail(InstrumentDetail detail)
    {
        switch (detail)
        {
            case InstrumentDetail<double> tickSize when detail.Field == nameof(TickSize):
                TickSize = tickSize.Value;
                break;
            case InstrumentDetail<MatchType> matchType when detail.Field == nameof(MatchType):
                MatchType = matchType.Value;
                break;
            default:
                throw new NotImplementedException($"InstrumentDetails.OnInstrumentDetail() unhandled field: {detail.Field}");
        }
    }
    // financially, cash, physical etc
    public string? DeliveryMethod { get; set; } = null;
    // Dollars, barrels, bushels, pounds, etc
    public string? Units { get; set; } = null;

    //when did trading start?
    public Timestamp? FirstTradeTimestamp { get; set; } = null;
    //when dooes trading end? this is the date usually mislabelled "expiry"
    public Timestamp? LastTradeTimestamp { get; set; } = null;

    //when does contract mature, this is the moment spot = future
    public Timestamp? MaturityDate { get; set; } = null;

    //defines spread, strips, butterfly legs, etc.
    public List<Leg> Legs { get; set; } = new List<Leg>();

    // hook up to find other InstrumentDetails
    public static Func<Leg, InstrumentDetails>? GetLeg { get; set; }

    //For forex the base and quote currency.
    public string? BaseCurrency { get; set; } = null;
    public string? QuoteCurrency { get; set; } = null;

    // ======================= JSON helpers =======================

    public static InstrumentDetails FromFile(string filePath)
    {
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        InstrumentDetails details = Json.Deserialize<InstrumentDetails>(json);
        details.FilePath = filePath;
        return details;
    }

    // the file this instance was loaded from; spreads can not build Symbol before their legs resolve
    [JsonIgnore]
    public FileSystemPath FilePath { get; private set; } = string.Empty;

    public void ToDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        string json = Json.Serialize(this);
        File.WriteAllText(GetFilePath(directoryPath), json, Encoding.UTF8);
    }

    private Symbology? _symbology;
    public Symbology Symbology => _symbology ??= BuildSymbology();

    // Legs resolved via GetLeg, maturity-ascending — the ticker invariant; file order is not trusted.
    public List<(Leg Leg, InstrumentDetails Details)> GetLegDetails()
    {
        List<(Leg Leg, InstrumentDetails Details)> legDetailsList = new List<(Leg, InstrumentDetails)>(Legs.Count);
        foreach (Leg leg in Legs)
            legDetailsList.Add((leg, GetLeg!(leg)));
        legDetailsList.Sort((a, b) => a.Details.MaturityDate!.Value.CompareTo(b.Details.MaturityDate!.Value));
        return legDetailsList;
    }

    private void GetSortedLegs(out List<Symbology> symbologies, out List<int> weights)
    {
        List<(Leg Leg, InstrumentDetails Details)> legDetailsList = GetLegDetails();
        weights = new List<int>(legDetailsList.Count);
        symbologies = new List<Symbology>(legDetailsList.Count);
        foreach ((Leg leg, InstrumentDetails details) in legDetailsList)
        {
            weights.Add(leg.Weight);
            symbologies.Add(details.Symbology);
        }
    }

    private Symbology BuildSymbology()
    {
        switch (InstrumentType)
        {
            case InstrumentType.Future:
                return new FutureSymbology(Exchange, Root, MaturityDate!.Value);

            case InstrumentType.Spread:
            {
                GetSortedLegs(out List<Symbology> symbologies, out List<int> weights);
                return new SpreadSymbology(Exchange, Root, symbologies, weights);
            }

            default:
                throw new NotImplementedException();
        }
    }

    [JsonIgnore]
    public InstrumentHeader InstrumentHeader
    {
        get
        {
            return new InstrumentHeader()
            {
                Exchange = new String8(Exchange),
                Root = new String8(Root),
                InstrumentType = InstrumentType,
                TickSize = TickSize,
                InverseTickSize = InverseTickSize,
            };
        }
    }
    [JsonIgnore]
    public FutureHeader FutureHeader
    {
        get
        {
            return new FutureHeader()
            {
                InstrumentHeader = InstrumentHeader,
                MaturityDate = MaturityDate!.Value,
                Multiplier = Multiplier,
            };
        }
    }

}
