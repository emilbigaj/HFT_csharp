
using Data;
using Execution;
using Provider;
using Socket;
using System;
using System.IO;
using Tools;
using Workspace;

namespace Strategy;

public abstract class Loggable
{
    public int Length { get; }
    public string FilePath { get; }
    public ClientSocket ClientSocket { get; private set; }
    private static SocketHeader s_socketHeader;

    public Loggable(FileSystemPath filePath, FileSystemPath loggingServerName, int length)
    {
        unsafe
        {
            if (filePath.Length > s_socketHeader.ClientName.Capacity)
                throw new ArgumentException($"Loggable FilePath: {filePath} is too long, must be <= {s_socketHeader.ClientName.Capacity} chars");
        }


        FilePath = filePath;
        Length = length;
        // Logging only needs to write out to Server, so ClientToServer > 0, ServerToClient = 0
        int[] logClientToServerLengths = new int[] { length };
        int[] logServerToClientLengths = new int[] { };

        ClientSocket = new ClientSocket(FilePath, loggingServerName, logClientToServerLengths, logServerToClientLengths);
    }
    public void Connect()
    {
        ClientSocket.Connect();
    }
}

public class LoggableManager
{
    public HashMap<string, Loggable> _loggables = new HashMap<string, Loggable>();
    public void OnLogging(Loggable logging)
    {
        if (!_loggables.TryAdd(logging.FilePath, logging))
            throw new ArgumentException($"Duplicate filePath {logging.FilePath}");
        logging.Connect();
    }
}

public class Series<T> : Loggable where T : unmanaged
{
    public T Value = default;
    public Series(FileSystemPath filePath, string loggingServerName) : base(filePath, loggingServerName, Memory.HugePageLength)
    {
    }
    public void Append(in T value)
    {
        Value = value;
        ClientSocket.Write(SocketChannel.Admin, in value);
    }
}

public class Strategy
{
    public void OpenWorkspace(string workspaceName)
    {
        string workspaceFilePath = Context.GetWorkspaceFilePath(DirectoryPath, workspaceName);

        FileSystemPath directoryPath = Path.GetDirectoryName(workspaceFilePath)!;
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }


        WorkspaceRunner.RunOnBackgroundThread(Client.ServerName, Client.ClientName, Clock.Mode, workspaceFilePath);
    }

    public static FileSystemPath GetFactorFilePath<T>(FileSystemPath factorsDirectoryPath, string factorName)
    {
        return Path.Combine(factorsDirectoryPath, factorName + "." + typeof(T).Name.ToLower());
    }
    public static FileSystemPath GetSeriesFilePath<T>(FileSystemPath seriesDirectoryPath, string seriesName)
    {
        return Path.Combine(seriesDirectoryPath, seriesName + "." + typeof(T).Name.ToLower());
    }


    public LoggableManager LoggableManager { get; } = new LoggableManager();

    public HashMap<string, Position> Positions = new HashMap<string, Position>(32);
    public HashMap<Position, Markouts> MarkoutsByPosition = new HashMap<Position, Markouts>(32);   // one recorder per position, made when the position is first got

    /// The markout durations of this strategy's positions, strictly ascending; set it before the first GetPosition to change them.
    /// A negative one is before the fill: 20 microseconds is the tick-to-execution time at CME, so that book could still be acted on.
    public Duration[] MarkoutDurations { get; set; } =
    [
        Duration.FromMicroseconds(-20), Duration.Zero, Duration.FromMilliseconds(100), Duration.FromSeconds(1), Duration.FromSeconds(10),
        Duration.FromMinutes(1), Duration.FromMinutes(5), Duration.FromMinutes(10), Duration.FromMinutes(15),
    ];
    public FileSystemPath DirectoryPath => Client.Context.DirectoryPath;
    public FileSystemPath SeriesDirectoryPath => Client.Context.SeriesDirectoryPath;
    public FileSystemPath FactorsDirectoryPath => Path.Combine(Client.Context.DirectoryPath, "Factors");

    public string WorkspaceName { get; set; } = "default";
    public Client Client => Scenario.Client;
    public Scenario Scenario { get; }

    public ClockMode Mode => Clock.Mode;

    public Strategy(Scenario scenario)
    {
        Scenario = scenario;
        InitDirectories();

    }

    private string[] _subDirectories = new string[] { "Factors", "Series", "Fills", "Positions", "Markouts" };
    private void InitDirectories()
    {
        foreach (string subDirectory in _subDirectories)
        {
            string subDirectoryPath = Path.Combine(DirectoryPath, subDirectory);
            Directory.CreateDirectory(subDirectoryPath);
            if (Mode == ClockMode.Realtime)
                continue;

            // Why AllDirectories: markout files sit one folder down, in Markouts/<symbol>
            foreach (string filePath in Directory.EnumerateFiles(subDirectoryPath, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(filePath); }
                catch { }
            }
        }
    }

    public virtual void Build()
    {

    }

    public virtual Series<T> NewSeries<T>(string name) where T : unmanaged
    {
        FileSystemPath filePath = GetSeriesFilePath<T>(SeriesDirectoryPath, name);
        Series<T> series = new Series<T>(filePath, Client.Context.LoggingServerName);
        if (File.Exists(filePath) && Mode == ClockMode.Realtime)
        {
            string? json = Tools.Tools.ReadLastLine(filePath);
            if (json != null)
            {
                series.Value = Json.Deserialize<T>(json);
            }
        }
        LoggableManager.OnLogging(series);
        return series;
    }

    /// Makes the recorder of the markouts of every algo fill on this position, at this strategy's MarkoutDurations,
    /// writing Markouts/<symbol>/<today, UTC>.markout.
    protected virtual Markouts NewMarkouts(Position position)
    {
        FileSystemPath filePath = Client.Context.GetMarkoutsFilePath(position.Instrument.Symbol, Clock.Now.ToDateTime);
        Series<Markout> markoutSeries = new Series<Markout>(filePath, Client.Context.LoggingServerName);
        LoggableManager.OnLogging(markoutSeries);
        return new Markouts(Client, position, markoutSeries, MarkoutDurations);
    }

    /// The strategy's position in this instrument; the first time it is got, its markouts start recording.
    public Position GetPosition(Instrument instrument)
    {
        Position position = Client.GetPosition(instrument.InstrumentId);
        if (Positions.TryAdd(position.Instrument.Symbol, position))
            MarkoutsByPosition.TryAdd(position, NewMarkouts(position));
        return position;
    }

    /// The recorder of this position's markouts, to subscribe to its events and to set its FairValuePrice.
    public Markouts GetMarkouts(Position position) => MarkoutsByPosition[position];

    public Series<Point> NewSeries(string name, ref Action<Timestamp> @event, Func<double> getValue)
    {
        Series<Point> series = NewSeries<Point>(name);
        @event += timestamp =>
        {
            double lastValue = series.Value.Value;
            double value = getValue();
            if (double.IsFinite(value) && value != lastValue)
                series.Append(new Point(timestamp, value));
        };
        return series;
    }

    public virtual Factor NewFactor(string name, int meanHalfLife, int stdDevHalfLife)
        => AttachSeries<Factor, FactorPoint>(new Factor(name, meanHalfLife, stdDevHalfLife));

    /// A mean saved in Factors/<name>.mean. A seed other than 0 starts it there, counted as one half-life of
    /// observations, so it is IsInitialized at once; with no seed it is the plain average of its first values.
    public virtual Mean NewMean(string name, int halfLife, double seed = 0)
    {
        Mean mean = AttachSeries<Mean, MeanPoint>(new Mean(name, halfLife));
        if (mean.Point.Count == 0 && seed != 0)
            mean.Point = new MeanPoint(double.NaN, mean.HalfLife, seed);
        return mean;
    }

    /// A standard deviation saved in Factors/<name>.stddev, seeded as NewMean is.
    public virtual StdDev NewStdDev(string name, int halfLife, double seed = 0)
    {
        StdDev stdDev = AttachSeries<StdDev, StdDevPoint>(new StdDev(name, halfLife));
        if (stdDev.Point.Count == 0 && seed != 0)
            stdDev.Point = new StdDevPoint(double.NaN, stdDev.HalfLife, seed * seed, seed);
        return stdDev;
    }

    /// A correlation saved in Factors/<name>.correl; in Realtime it carries on from the file's last point.
    public virtual Correl NewCorrel(string name, int halfLife)
        => AttachSeries<Correl, CorrelPoint>(new Correl(name, halfLife));

    /// A regression saved as one Factors/<name> <input>-<input>.correl per pair of its series; in Realtime each
    /// pair carries on from its file's last point, and the betas are solved from them at once.
    public virtual Regression NewRegression(string name, string[] inputNames, int halfLife, double ridge)
        => new Regression(name, inputNames, halfLife, ridge, NewCorrel);

    private TFactor AttachSeries<TFactor, TPoint>(TFactor factor)
        where TFactor : Statistic<TPoint>
        where TPoint : unmanaged
    {
        FileSystemPath filePath = GetFactorFilePath<TFactor>(FactorsDirectoryPath, factor.Name);
        Series<TPoint> series = new Series<TPoint>(filePath, Client.Context.LoggingServerName);
        if (File.Exists(filePath) && Mode == ClockMode.Realtime)
        {
            string? json = Tools.Tools.ReadLastLine(filePath);
            if (json != null)
            {
                factor.Point = Json.Deserialize<TPoint>(json);
            }
        }
        factor.Value += () => series.Append(factor.Point);
        LoggableManager.OnLogging(series);
        return factor;
    }
}