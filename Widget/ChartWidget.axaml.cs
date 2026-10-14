using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chart;
using Data;
using Execution;
using Provider;
using Tools;

using ChartPanel = Chart.Chart;

namespace Widget;

[RegisterJson]
public sealed class ChartWidgetState { public List<ChartPanelState> Panels { get; set; } = new(); }
[RegisterJson]
public sealed class ChartPanelState { public string Name { get; set; } = ""; public List<ChartSeriesState> Series { get; set; } = new(); }
[RegisterJson]
public sealed class ChartSeriesState { public string Name { get; set; } = ""; public string FilePath { get; set; } = ""; }

/// A stack of chart panels, each plotting series streamed from chart files (.point, .candle, .fill, .histogram) as they grow.
/// UI thread, except each series' file streamer, which parses its lines on its own thread and posts them to the UI. Cold path.
public partial class ChartWidget : UserControl, IWidget, IDisposable
{
    private readonly Context _context = null!;

    private readonly List<ChartPanel> _chartPanels = new();
    private readonly List<SeriesBinding> _seriesBindings = new();
    private ChartStack? _chartStack;
    private ChartPanel? _contextMenuTargetPanel;   // the panel last right-clicked; null when none, or once it is removed

    private static readonly IBrush[] s_seriesColors = Palette.ChartSeriesColors;
    private readonly Dictionary<ChartPanel, int> _panelColorIndices = new();   // per panel, the colour index of its next point series

    public ChartWidget()
    {
        InitializeComponent();
    }

    public ChartWidget(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        InitializeComponent();

        ChartControlHost.PointerPressed += OnChartPointerPressed;

        AddChartPanel();
    }

    public string Title => "Chart";

    public double DefaultWidth => 1000;
    public double DefaultHeight => 600;

    private void UpdateEmptyState() => EmptyLabel.IsVisible = _chartPanels.Count == 0;

    /// Adds an empty panel after insertAfterPanel (at the end when it is null or last) and shows it in the stack.
    /// Returns the new panel.
    private ChartPanel AddChartPanel(ChartPanel? insertAfterPanel = null)
    {
        // Step 1: the panel, in the widget's list
        ChartPanel chartPanel = new ChartPanel($"Chart {_chartPanels.Count + 1}");
        int insertAfterIndex = insertAfterPanel == null ? -1 : _chartPanels.IndexOf(insertAfterPanel);
        if (insertAfterIndex >= 0 && insertAfterIndex < _chartPanels.Count - 1)
            _chartPanels.Insert(insertAfterIndex + 1, chartPanel);
        else
            _chartPanels.Add(chartPanel);
        _panelColorIndices[chartPanel] = 0;

        // Step 2: the panel, in the stack the chart control draws (a new stack already holds it)
        EnsureStackExists();
        if (!_chartStack!.Stack.Contains(chartPanel))
        {
            int stackInsertAfterIndex = -1;
            for (int stackIndex = 0; stackIndex < _chartStack.Stack.Count; stackIndex++)
            {
                if (ReferenceEquals(_chartStack.Stack[stackIndex], insertAfterPanel))
                {
                    stackInsertAfterIndex = stackIndex;
                    break;
                }
            }
            if (stackInsertAfterIndex >= 0)
                _chartStack.InsertPanel(stackInsertAfterIndex + 1, chartPanel);
            else
                _chartStack.AddPanel(chartPanel);
        }

        UpdateEmptyState();
        return chartPanel;
    }

    private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ChartControlHost).Properties.IsRightButtonPressed)
            _contextMenuTargetPanel = ChartControlHost.GetPanelAt(e.GetPosition(ChartControlHost));
    }

    private void OnAddChartClick(object? sender, RoutedEventArgs e)
    {
        AddChartPanel(_contextMenuTargetPanel);
    }

    /// Removes the right-clicked panel with its series; nothing when no panel was right-clicked.
    private void OnRemoveChartClick(object? sender, RoutedEventArgs e)
    {
        ChartPanel? targetPanel = _contextMenuTargetPanel;
        if (targetPanel == null)
            return;

        RemoveSeriesBindings(seriesBinding => ReferenceEquals(seriesBinding.Panel, targetPanel));
        _chartStack?.RemovePanel(targetPanel);
        _chartPanels.Remove(targetPanel);
        _panelColorIndices.Remove(targetPanel);
        _contextMenuTargetPanel = null;

        UpdateEmptyState();
    }

    /// Asks for chart files and adds a series for each to the right-clicked panel (else the last, else a new one); logs a failure.
    private async void OnAddSeriesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Step 1: the panel the series go to
            ChartPanel chartPanel = _contextMenuTargetPanel ?? _chartPanels.LastOrDefault() ?? AddChartPanel();

            // Step 2: the files, picked from the context's series directory
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return;
            IStorageProvider storageProvider = topLevel.StorageProvider;
            IStorageFolder? startStorageFolder = await storageProvider.TryGetFolderFromPathAsync(_context.SeriesDirectoryPath);
            IReadOnlyList<IStorageFile> storageFiles = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add Series from Chart File",
                AllowMultiple = true,
                SuggestedStartLocation = startStorageFolder,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Chart files") { Patterns = new[] { "*.point", "*.candle", "*.fill", "*.histogram", "*.txt", "*.csv", "*.*" } }
                }
            });

            // Step 3: a series per file
            foreach (IStorageFile storageFile in storageFiles)
                CreateAndBindSeries(chartPanel, storageFile.Path.LocalPath);
            UpdateEmptyState();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ChartWidget.OnAddSeriesClick: {exception.Message}");
        }
    }

    /// Adds a series streamed from this chart file to the panel, typed by the file's extension (anything not Candle, Fill or
    /// Histogram plots as points); nothing when the panel already has a series of that file name, logs an unknown extension.
    private void CreateAndBindSeries(ChartPanel chartPanel, string filePath)
    {
        try
        {
            // Step 1: one series per file name in a panel, of a type its extension names
            string fileName = Path.GetFileName(filePath);
            if (chartPanel.Series.Any(series => series.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                return;
            string extension = Path.GetExtension(fileName).TrimStart('.');
            if (!Enum.TryParse(extension, ignoreCase: true, out FileType fileType))
            {
                Console.WriteLine($"ChartWidget.CreateAndBindSeries: unsupported file extension '{extension}'");
                return;
            }

            // Step 2: the series, and the binding that streams the file into it
            SeriesBinding seriesBinding;
            switch (fileType)
            {
                case FileType.Candle:
                    CandleSeries candleSeries = new CandleSeries(fileName, Palette.ProfitGreen, Palette.LossRed, YAxisSide.Right, 16 * 1024);
                    seriesBinding = new SeriesBinding<Candle>(this, chartPanel, candleSeries, filePath, Json.Deserialize<Candle>, candleSeries.Append);
                    break;

                case FileType.Fill:
                    FillSeries fillSeries = new FillSeries(fileName, Palette.BidActiveBlue, Palette.BidEmptyDarkBlue, Palette.AskActivePurple, Palette.AskEmptyDarkPurple, YAxisSide.Right, 1024);
                    seriesBinding = new SeriesBinding<Filld>(this, chartPanel, fillSeries, filePath, NewFilld, fillSeries.Append);
                    break;

                case FileType.Histogram:
                    HistogramSeries histogramSeries = new HistogramSeries(fileName, Palette.ProfitGreen, Palette.LossRed, YAxisSide.Right, 16 * 1024);
                    seriesBinding = new SeriesBinding<Histogram>(this, chartPanel, histogramSeries, filePath, Json.Deserialize<Histogram>, histogramSeries.Append);
                    break;

                default:
                    int colorIndex = _panelColorIndices.GetValueOrDefault(chartPanel);
                    _panelColorIndices[chartPanel] = colorIndex + 1;
                    PointSeries pointSeries = new PointSeries(fileName, s_seriesColors[colorIndex % s_seriesColors.Length], 1.0, YAxisSide.Left, 16 * 1024);
                    seriesBinding = new SeriesBinding<Data.Point>(this, chartPanel, pointSeries, filePath, Json.Deserialize<Data.Point>, point => pointSeries.Append(point.Timestamp, point.Value));
                    break;
            }

            // Step 3: the series in its panel, the panel in the stack, the series drawn
            chartPanel.AddSeries(seriesBinding.Series);
            _seriesBindings.Add(seriesBinding);
            EnsurePanelInStack(chartPanel);
            ChartControlHost.AttachSeries(seriesBinding.Series);
            seriesBinding.IsAttachedToChartControl = true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ChartWidget.CreateAndBindSeries: {exception}");
        }
    }

    /// A fill file's line as the chart's fill marker: its exchange time, price, quantity and type. Throws on a line that does not parse.
    private static Filld NewFilld(string fillLine)
    {
        Fill fill = Json.Deserialize<Fill>(fillLine);
        return new Filld(fill.OrderHeader.ExchangeTimestamp, fill.Price, fill.Quantity, fill.FillType);
    }

    /// Asks which of the right-clicked panel's series to remove and removes them; logs a failure.
    private async void OnRemoveSeriesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ChartPanel? chartPanel = _contextMenuTargetPanel;
            if (chartPanel == null || chartPanel.Series.Count == 0 || VisualRoot is not Window window)
                return;

            RemoveSeriesDialog removeSeriesDialog = new RemoveSeriesDialog(chartPanel.Series);
            List<ISeries>? selectedSeries = await removeSeriesDialog.ShowDialog<List<ISeries>?>(window);
            if (selectedSeries == null || selectedSeries.Count == 0)
                return;

            foreach (ISeries series in selectedSeries)
            {
                chartPanel.RemoveSeries(series);
                RemoveSeriesBindings(seriesBinding => ReferenceEquals(seriesBinding.Series, series));
            }
            ChartControlHost.InvalidateVisual();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ChartWidget.OnRemoveSeriesClick: {exception.Message}");
        }
    }

    /// Disposes and drops the bindings that match: a removed panel's, a removed series', or all of them.
    private void RemoveSeriesBindings(Predicate<SeriesBinding> isRemoved)
    {
        foreach (SeriesBinding seriesBinding in _seriesBindings)
        {
            if (isRemoved(seriesBinding))
                seriesBinding.Dispose();
        }
        _seriesBindings.RemoveAll(isRemoved);
    }

    /// Gives the chart control a stack holding every panel, the first time one is needed.
    private void EnsureStackExists()
    {
        if (_chartStack != null)
            return;

        _chartStack = new ChartStack();
        foreach (ChartPanel chartPanel in _chartPanels)
            _chartStack.AddPanel(chartPanel);
        ChartControlHost.Stack = _chartStack;
    }

    private void EnsurePanelInStack(ChartPanel chartPanel)
    {
        EnsureStackExists();
        if (!_chartStack!.Stack.Contains(chartPanel))
            _chartStack.AddPanel(chartPanel);
    }

    private void OnFileStreamerError(Exception exception, string filePath) =>
        System.Diagnostics.Debug.WriteLine($"ChartWidget: file streamer error for '{filePath}': {exception.Message}");

    public string? SaveStateJson()
    {
        ChartWidgetState chartWidgetState = new ChartWidgetState();
        foreach (ChartPanel chartPanel in _chartPanels)
        {
            ChartPanelState chartPanelState = new ChartPanelState { Name = chartPanel.Name };
            foreach (SeriesBinding seriesBinding in _seriesBindings)
            {
                if (ReferenceEquals(seriesBinding.Panel, chartPanel))
                    chartPanelState.Series.Add(new ChartSeriesState { Name = seriesBinding.Series.Name, FilePath = seriesBinding.FilePath });
            }
            chartWidgetState.Panels.Add(chartPanelState);
        }
        return chartWidgetState.Panels.Count == 0 ? null : Json.Serialize(chartWidgetState);
    }

    /// Replaces every panel and series with the saved ones; one empty panel when there are none or the JSON does not parse.
    public void LoadStateJson(string? json)
    {
        // Step 1: drop every panel and series
        RemoveSeriesBindings(_ => true);
        _chartPanels.Clear();
        _panelColorIndices.Clear();
        _chartStack = null;
        ChartControlHost.Stack = null;

        // Step 2: the saved state. Why the catch: a layout file that no longer parses must not stop the widget opening
        ChartWidgetState? chartWidgetState = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                chartWidgetState = Json.Deserialize<ChartWidgetState>(json);
        }
        catch { }
        if (chartWidgetState == null || chartWidgetState.Panels.Count == 0)
        {
            AddChartPanel();
            return;
        }

        // Step 3: each panel, with the series whose files still exist
        foreach (ChartPanelState chartPanelState in chartWidgetState.Panels)
        {
            ChartPanel chartPanel = new ChartPanel(chartPanelState.Name);
            _chartPanels.Add(chartPanel);
            _panelColorIndices[chartPanel] = 0;
            foreach (ChartSeriesState chartSeriesState in chartPanelState.Series)
            {
                if (!string.IsNullOrWhiteSpace(chartSeriesState.FilePath) && File.Exists(chartSeriesState.FilePath))
                    CreateAndBindSeries(chartPanel, chartSeriesState.FilePath);
            }
        }

        EnsureStackExists();
        UpdateEmptyState();
    }

    public void Dispose() => RemoveSeriesBindings(_ => true);

    /// One series fed from its chart file, as the file grows. The list of a widget's bindings holds them whatever their item type.
    private abstract class SeriesBinding : IDisposable
    {
        protected readonly ChartWidget OwnerChartWidget;
        protected readonly FileStreamer SeriesFileStreamer;   // connected by the derived constructor, once it can take lines

        public ChartPanel Panel { get; }
        public ISeries Series { get; }
        public string FilePath { get; }
        public bool IsAttachedToChartControl { get; set; }

        protected SeriesBinding(ChartWidget ownerChartWidget, ChartPanel panel, ISeries series, string filePath)
        {
            OwnerChartWidget = ownerChartWidget;
            Panel = panel;
            Series = series;
            FilePath = filePath;
            SeriesFileStreamer = new FileStreamer(filePath);
            SeriesFileStreamer.Line += OnLine;
            SeriesFileStreamer.Error += OnError;
        }

        protected abstract void OnLine(string line);

        private void OnError(Exception exception) => OwnerChartWidget.OnFileStreamerError(exception, FilePath);

        public void Dispose()
        {
            SeriesFileStreamer.Line -= OnLine;
            SeriesFileStreamer.Error -= OnError;
            SeriesFileStreamer.Dispose();
        }
    }

    /// A binding whose file holds one T per line: parseLine turns a line into a T on the streamer's thread (throwing on one that
    /// does not parse), and append adds it to the series on the UI thread, in batches of whatever arrived since the last post.
    private sealed class SeriesBinding<T> : SeriesBinding
    {
        private readonly ConcurrentQueue<T> _pendingItems = new();   // parsed on the streamer's thread, not yet appended on the UI thread
        private readonly Func<string, T> _parseLine;
        private readonly Action<T> _append;
        private int _isFlushPosted;                                   // 1 while a FlushOnUiThread is posted and has not yet run (Interlocked)

        public SeriesBinding(ChartWidget ownerChartWidget, ChartPanel panel, ISeries series, string filePath, Func<string, T> parseLine, Action<T> append)
            : base(ownerChartWidget, panel, series, filePath)
        {
            _parseLine = parseLine;
            _append = append;
            // Why last: the streamer calls OnLine on its own thread as soon as it connects, and OnLine needs parseLine
            SeriesFileStreamer.Connect();
        }

        protected override void OnLine(string line)
        {
            // Why the catch: a partial or malformed line is skipped; the next one may parse
            T item;
            try
            {
                item = _parseLine(line);
            }
            catch
            {
                return;
            }
            _pendingItems.Enqueue(item);

            if (Interlocked.CompareExchange(ref _isFlushPosted, 1, 0) == 0)
                Dispatcher.UIThread.Post(FlushOnUiThread);
        }

        /// Appends every pending item to the series, then posts itself again if more arrived meanwhile.
        private void FlushOnUiThread()
        {
            // Step 1: the series drawn, once
            if (!IsAttachedToChartControl)
            {
                OwnerChartWidget.EnsurePanelInStack(Panel);
                OwnerChartWidget.ChartControlHost.AttachSeries(Series);
                IsAttachedToChartControl = true;
            }

            // Step 2: the pending items into the series
            bool isAppended = false;
            while (_pendingItems.TryDequeue(out T item))
            {
                _append(item);
                isAppended = true;
            }
            if (isAppended)
                OwnerChartWidget.EnsurePanelInStack(Panel);

            // Step 3: another pass for items that arrived after the queue emptied
            Interlocked.Exchange(ref _isFlushPosted, 0);
            if (!_pendingItems.IsEmpty && Interlocked.CompareExchange(ref _isFlushPosted, 1, 0) == 0)
                Dispatcher.UIThread.Post(FlushOnUiThread);
        }
    }
}
