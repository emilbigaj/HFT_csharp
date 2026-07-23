using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Tools;

// ======================= Supporting enums/types =======================

namespace Data;

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
        init
        {
            _tickSize = value;
            _inverseTickSize = (double)(1.0m / (decimal)value);
        }
    }
    public double InverseTickSize => _inverseTickSize;
    public Session[] Sessions { get; set; } = Array.Empty<Session>();
    public string? DeliveryMethod { get; init; } = null;
    public string? Units { get; init; } = null;
    public Timestamp? ListingDate { get; init; } = null;
    public ExpiryType? ExpiryType { get; init; } = null;

    public Timestamp? ExpiryDate { get; init; } = null;

    public ExpiryType? LongExpiryType { get; init; } = null;

    public Timestamp? LongExpiryDate { get; init; } = null;

    public ExpiryType? ShortExpiryType { get; init; } = null;

    public Timestamp? ShortExpiryDate { get; init; } = null;

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
        return InstrumentType switch
        {
            InstrumentType.Future => new FutureSymbology(Exchange, Root, ExpiryType!.Value, ExpiryDate!.Value),
            InstrumentType.Spread => new SpreadSymbology(Exchange, Root, LongExpiryType!.Value, LongExpiryDate!.Value, ShortExpiryType!.Value, ShortExpiryDate!.Value),
            _ => throw new NotImplementedException()
        };
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
                ExpiryDate = ExpiryDate!.Value,
                ExpiryType = ExpiryType!.Value,
                Multiplier = Multiplier,
            };
        }
    }

    [JsonIgnore]
    public SpreadHeader SpreadHeader
    {
        get
        {
            return new SpreadHeader()
            {
                InstrumentHeader = InstrumentHeader,
                LongExpiryDate = LongExpiryDate!.Value,
                LongExpiryType = LongExpiryType!.Value,
                Multiplier = Multiplier,
                ShortExpiryDate = ShortExpiryDate!.Value,
                ShortExpiryType = ShortExpiryType!.Value,
                LongInstrumentId = 0,
                ShortInstrumentId = 0
            };
        }
    }

}
