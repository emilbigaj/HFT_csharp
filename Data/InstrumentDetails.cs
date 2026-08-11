using System;
using System.Collections.Generic;
using System.IO;
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

[RegisterJson]
public struct InstrumentDetail
{
    public Timestamp Timestamp { get; init; }
    public string Field { get; init; }
    public string Value { get; init; }
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

        results.Sort((InstrumentDetails a, InstrumentDetails b) => string.CompareOrdinal(a.Symbol, b.Symbol));
        return results;
    }

}

// calendar spread looks like , 1, -1
// butterfly looks like , 1, -2, 1
// strip might look like , 1, -1, -1
[RegisterJson]
public sealed class Leg
{
    public int Weight { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string ExchangeInstrumentId { get; init; } = string.Empty;

}


// ======================= The InstrumentDetails model =======================
[RegisterJson]
public sealed class InstrumentDetails
{
    public string FileName => Symbol + ".json";

    public string GetFilePath(string directoryPath) => Path.Combine(directoryPath, FileName);

    // --------- Core schema (round-trippable) ---------
    public InstrumentType InstrumentType { get; init; }
    public string Exchange { get; init; } = string.Empty;
    public string Root { get; init; } = string.Empty;
    public string Ticker => Symbology.Ticker;
    public string Symbol => Symbology.Symbol;
    public string Currency { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    public string DatabentoSymbol { get; init; } = string.Empty;

    // The venue's own instrument id, as a string so every exchange's format fits. Reference only:
    // it is unique within a dataset on a given day, but is reused across years, so it must never
    // key a catalog-wide lookup.
    public string ExchangeInstrumentId { get; init; } = string.Empty;
    public string RicRoot { get; init; } = string.Empty;
    public string RicSymbol { get; init; } = string.Empty;
    public string RicExchange { get; init; } = string.Empty;

    public double QuantitySize { get; init; } = 1.0;
    public double Multiplier { get; init; } = 1.0;

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

    public void OnInstrumentDetail(InstrumentDetail detail)
    {
        switch (detail.Field)
        {
            case "TickSize":
                TickSize = double.Parse(detail.Value);
                break;
            case "MatchType":
                MatchType = (MatchType)Enum.Parse(typeof(MatchType), detail.Value, true);
                break;
            default:
                break;
        }
    }
    // financially, cash, physical etc
    public string? DeliveryMethod { get; init; } = null;
    // Dollars, barrels, bushels, pounds, etc
    public string? Units { get; init; } = null;

    //when did trading start?
    public Timestamp? FirstTradeTimestamp { get; init; } = null;
    //when dooes trading end? this is the date usually mislabelled "expiry"
    public Timestamp? LastTradeTimestamp { get; init; } = null;

    // Does this belong to a maturity schedule (e.g. monthly, quarterly, etc.)?
    // Some instruments have both say a monthly and daily maturity schedule and share a maturity date, so the schedule is needed to disambiguate.
    public MaturityType? MaturityType { get; init; } = null;

    //when does contract mature, this is the moment spot = future
    public Timestamp? MaturityDate { get; init; } = null;

    //defines spread, strips, butterfly legs, etc.
    public List<Leg> Legs { get; set; } = new List<Leg>();
    // hook up to find other InstrumentDetails
    public static Func<Leg, InstrumentDetails>? GetLeg { get; set; }

    //For forex the base and quote currency.
    public string? BaseCurrency { get; init; } = null;
    public string? QuoteCurrency { get; init; } = null;

    // ======================= JSON helpers =======================

    public static InstrumentDetails FromFile(string filePath)
    {
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        InstrumentDetails details = Json.Deserialize<InstrumentDetails>(json);
        return details;
    }

    public void ToDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        string json = Json.Serialize(this);
        File.WriteAllText(GetFilePath(directoryPath), json, Encoding.UTF8);
    }

    private Symbology? _symbology;
    public Symbology Symbology => _symbology ??= BuildSymbology();


    private Symbology BuildSymbology()
    {
        switch (InstrumentType)
        {
            case InstrumentType.Future:
                return new FutureSymbology(Exchange, Root, MaturityType!.Value, MaturityDate!.Value);

            case InstrumentType.Spread:
            {
                InstrumentDetails @long = GetLeg!(Legs[0]);
                InstrumentDetails @short = GetLeg!(Legs[1]);
                return new SpreadSymbology(
                    Exchange,
                    Root,
                    @long.MaturityType!.Value,
                    @long.MaturityDate!.Value,
                    @short.MaturityType!.Value,
                    @short.MaturityDate!.Value);
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
                MaturityType = MaturityType!.Value,
                Multiplier = Multiplier,
            };
        }
    }

    [JsonIgnore]
    public SpreadHeader SpreadHeader
    {
        get
        {
            InstrumentDetails @long = GetLeg!(Legs[0]);
            InstrumentDetails @short = GetLeg!(Legs[1]);
            return new SpreadHeader()
            {
                InstrumentHeader = InstrumentHeader,
                LongMaturityDate = @long.MaturityDate!.Value,
                LongMaturityType = @long.MaturityType!.Value,
                Multiplier = Multiplier,
                ShortMaturityDate = @short.MaturityDate!.Value,
                ShortMaturityType = @short.MaturityType!.Value,
                LongInstrumentId = 0,
                ShortInstrumentId = 0
            };
        }
    }

}
