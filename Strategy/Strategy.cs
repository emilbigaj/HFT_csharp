
using Data;
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

    private string[] _subDirectories = new string[] { "Factors", "Series", "Fills", "Positions" };
    private void InitDirectories()
    {
        foreach (string subDirectory in _subDirectories)
        {
            string subDirectoryPath = Path.Combine(DirectoryPath, subDirectory);
            Directory.CreateDirectory(subDirectoryPath);
            if (Mode == ClockMode.Realtime)
                continue;

            foreach (string filePath in Directory.EnumerateFiles(subDirectoryPath))
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

    public Position GetPosition(Instrument instrument)
    {
        Position position = Client.GetPosition(instrument.InstrumentId);
        Positions.TryAdd(position.Instrument.Symbol, position);
        return position;
    }

    public Series<Point> NewSeries(string name, ref Action<Timestamp> @event, Func<double> getValue)
    {
        Series<Point> series = NewSeries<Point>(name);
        @event += timestamp =>
        {
            double lastValue = series.Value.Value;
            double value = getValue();
            if (!double.IsNaN(value) && value != lastValue)
                series.Append(new Point(timestamp, value));
        };
        return series;
    }

    public virtual Factor NewFactor(string name, int meanHalfLife, int stdDevHalfLife)
        => AttachSeries<Factor, FactorPoint>(new Factor(name, meanHalfLife, stdDevHalfLife));

    public virtual Mean NewMean(string name, int halfLife, double seed = 0)
    {
        Mean mean = AttachSeries<Mean, MeanPoint>(new Mean(name, halfLife));
        if (mean.Point.Count == 0)
            mean.Point = new MeanPoint(double.NaN, 0, seed);
        return mean;
    }
    public virtual StdDev NewStdDev(string name, int halfLife, double seed = 0)
    {
        StdDev stdDev = AttachSeries<StdDev, StdDevPoint>(new StdDev(name, halfLife));
        if (stdDev.Point.Count == 0)
            stdDev.Point = new StdDevPoint(double.NaN, 0, seed * seed, seed);
        return stdDev;
    }

    private TFactor AttachSeries<TFactor, TPoint>(TFactor factor)
        where TFactor : Factor<TPoint>
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