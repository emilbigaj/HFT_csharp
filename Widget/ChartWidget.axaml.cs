using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chart;
using Data;
using Provider;
using Tools;

using Point = Avalonia.Point;
using ChartPanel = Chart.Chart;
using Execution;

namespace Widget;

[RegisterJson]
public sealed class ChartWidgetState { public List<ChartPanelState> Panels { get; set; } = new(); }
[RegisterJson]
public sealed class ChartPanelState { public string Name { get; set; } = ""; public List<ChartSeriesState> Series { get; set; } = new(); }
[RegisterJson]
public sealed class ChartSeriesState { public string Name { get; set; } = ""; public string FilePath { get; set; } = ""; }

public partial class ChartWidget : UserControl, IWidget, IDisposable
{
    public const string TypeKeyStatic = "ChartWidget";
    private readonly Context _context = null!;

    private readonly List<ChartPanel> _panels = new();
    private readonly List<SeriesBinding> _bindings = new();
    private ChartStack? _stack;
    private ChartPanel? _contextMenuTargetPanel;

    private static readonly IBrush[] s_seriesColors = Palette.ChartSeriesColors;
    private readonly Dictionary<ChartPanel, int> _panelColorIndices = new();

    public ChartWidget()
    {
        InitializeComponent();
    }

    public ChartWidget(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        InitializeComponent();
        Title = "Chart";

        ChartControlHost.PointerPressed += OnChartPointerPressed;

        AddChartPanel();
    }

    public string TypeKey => TypeKeyStatic;
    public string Title { get; set; } = "Chart";

    public double DefaultWidth => 1000;
    public double DefaultHeight => 600;

    private void UpdateEmptyState()
    {
        if (EmptyLabel != null)
        {
            EmptyLabel.IsVisible = _panels.Count == 0;
        }
    }

    private ChartPanel AddChartPanel(ChartPanel? insertAfter = null)
    {
        ChartPanel panel = new ChartPanel($"Chart {_panels.Count + 1}");
        if (insertAfter != null)
        {
            int idx = _panels.IndexOf(insertAfter);
            if (idx >= 0 && idx < _panels.Count - 1)
            {
                _panels.Insert(idx + 1, panel);
            }
            else
            {
                _panels.Add(panel);
            }
        }
        else
        {
            _panels.Add(panel);
        }

        _panelColorIndices[panel] = 0;

        EnsureStackExists();
        if (!_stack!.Stack.Contains(panel))
        {
            if (insertAfter != null)
            {
                int stackIdx = -1;
                for (int i = 0; i < _stack.Stack.Count; i++)
                {
                    if (ReferenceEquals(_stack.Stack[i], insertAfter))
                    {
                        stackIdx = i;
                        break;
                    }
                }

                if (stackIdx >= 0)
                {
                    _stack.InsertPanel(stackIdx + 1, panel);
                }
                else
                {
                    _stack.AddPanel(panel);
                }
            }
            else
            {
                _stack.AddPanel(panel);
            }
        }

        UpdateEmptyState();
        return panel;
    }

    private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(ChartControlHost);
        if (pt.Properties.IsRightButtonPressed)
        {
            _contextMenuTargetPanel = ChartControlHost.GetPanelAt(e.GetPosition(ChartControlHost));
        }
    }

    private void OnAddChartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddChartPanel(_contextMenuTargetPanel);
    }

    private void OnRemoveChartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChartPanel target = _contextMenuTargetPanel!;
        if (target == null)
        {
            return;
        }

        List<SeriesBinding> toRemove = _bindings.Where(b => ReferenceEquals(b.Panel, target)).ToList();
        foreach (SeriesBinding binding in toRemove)
        {
            binding.Dispose();
            _bindings.Remove(binding);
        }

        _stack?.RemovePanel(target);
        _panels.Remove(target);
        _panelColorIndices.Remove(target);

        if (ReferenceEquals(_contextMenuTargetPanel, target))
        {
            _contextMenuTargetPanel = null;
        }

        UpdateEmptyState();
    }

    private async void OnAddSeriesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            ChartPanel? panel = _contextMenuTargetPanel ?? _panels.LastOrDefault();
            if (panel == null)
            {
                panel = AddChartPanel();
            }

            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            IStorageProvider storageProvider = topLevel.StorageProvider;
            IStorageFolder? startLocation = await storageProvider.TryGetFolderFromPathAsync(_context.SeriesDirectoryPath);

            IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add Series from Chart File",
                AllowMultiple = true,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Chart files") { Patterns = new[] { "*.point", "*.candle", "*.fill", "*.histogram", "*.txt", "*.csv", "*.*" } }
                }
            });

            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (IStorageFile file in files)
            {
                CreateAndBindSeries(panel, file.Path.LocalPath);
            }

            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding series: {ex.Message}");
        }
    }

    private void CreateAndBindSeries(ChartPanel panel, string filePath)
    {
        try
        {
            string fileName = Path.GetFileName(filePath);
            if (panel.Series.Any(s => s.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string ext = Path.GetExtension(fileName).TrimStart('.');

            if (!Enum.TryParse(typeof(FileType), ext, true, out object? typeObj) || typeObj == null)
            {
                Console.WriteLine($"CreateAndBindSeries: Unsupported file extension '{ext}'");
                return;
            }

            FileType type = (FileType)typeObj;

            ISeries series;
            SeriesBinding binding;

            switch (type)
            {
                case FileType.Candle:
                    CandleSeries candleSeries = new CandleSeries(fileName, Palette.ProfitGreen, Palette.LossRed, YAxisSide.Right, 16 * 1024);
                    series = candleSeries;
                    binding = new CandleBinding(this, panel, candleSeries, filePath);
                    break;

                case FileType.Fill:
                    FillSeries fillSeries = new FillSeries(fileName, Palette.BidActiveBlue, Palette.BidEmptyDarkBlue, Palette.AskActivePurple, Palette.AskEmptyDarkPurple, YAxisSide.Right, 1024);
                    series = fillSeries;
                    binding = new FillBinding(this, panel, fillSeries, filePath);
                    break;

                case FileType.Histogram:
                    HistogramSeries histSeries = new HistogramSeries(fileName, Palette.ProfitGreen, Palette.LossRed, YAxisSide.Right, 16 * 1024);
                    series = histSeries;
                    binding = new HistogramBinding(this, panel, histSeries, filePath);
                    break;

                case FileType.Point:
                default:
                    if (!_panelColorIndices.TryGetValue(panel, out int colorIndex))
                    {
                        colorIndex = 0;
                    }
                    IBrush color = s_seriesColors[colorIndex % s_seriesColors.Length];
                    _panelColorIndices[panel] = colorIndex + 1;

                    PointSeries pointSeries = new PointSeries(fileName, color, 1.0, YAxisSide.Left, 16 * 1024);
                    series = pointSeries;
                    binding = new PointBinding(this, panel, pointSeries, filePath);
                    break;
            }

            panel.AddSeries(series);
            _bindings.Add(binding);

            EnsureStackExists();
            if (!_stack!.Stack.Contains(panel))
            {
                _stack.AddPanel(panel);
            }

            ChartControlHost.AttachSeries(series);
            binding.IsAttachedToChartControl = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CreateAndBindSeries Error: {ex}");
        }
    }

    private async void OnRemoveSeriesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            ChartPanel? panel = _contextMenuTargetPanel;
            if (panel == null || panel.Series.Count == 0)
            {
                return;
            }

            Window? window = VisualRoot as Window;
            if (window == null)
            {
                return;
            }

            RemoveSeriesDialog dlg = new RemoveSeriesDialog(panel.Series);
            List<ISeries> selectedSeries = await dlg.ShowDialog<List<ISeries>>(window);

            if (selectedSeries != null && selectedSeries.Count > 0)
            {
                foreach (ISeries s in selectedSeries)
                {
                    panel.RemoveSeries(s);
                    SeriesBinding? binding = _bindings.FirstOrDefault(b => ReferenceEquals(b.Series, s));
                    if (binding != null)
                    {
                        binding.Dispose();
                        _bindings.Remove(binding);
                    }
                }
                ChartControlHost.InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing series: {ex.Message}");
        }
    }

    private void EnsureStackExists()
    {
        if (_stack != null)
        {
            return;
        }

        _stack = new ChartStack();

        foreach (ChartPanel p in _panels)
        {
            if (!_stack.Stack.Contains(p))
            {
                _stack.AddPanel(p);
            }
        }

        ChartControlHost.Stack = _stack;
    }

    private void EnsurePanelInStack(ChartPanel panel)
    {
        EnsureStackExists();
        if (!_stack!.Stack.Contains(panel))
        {
            _stack.AddPanel(panel);
        }
    }

    internal void OnFileStreamerError(Exception ex, string filePath) =>
        System.Diagnostics.Debug.WriteLine($"FileStreamer error for '{filePath}': {ex.Message}");

    public string? SaveStateJson()
    {
        ChartWidgetState state = new ChartWidgetState();
        foreach (ChartPanel panel in _panels)
        {
            ChartPanelState ps = new ChartPanelState { Name = panel.Name };
            foreach (SeriesBinding b in _bindings.Where(b => ReferenceEquals(b.Panel, panel)))
            {
                ps.Series.Add(new ChartSeriesState { Name = b.Series.Name, FilePath = b.FilePath });
            }
            state.Panels.Add(ps);
        }
        return state.Panels.Count == 0 ? null : Json.Serialize(state);
    }

    public void LoadStateJson(string? json)
    {
        foreach (SeriesBinding b in _bindings)
        {
            b.Dispose();
        }
        _bindings.Clear();
        _panels.Clear();
        _panelColorIndices.Clear();
        _stack = null;
        ChartControlHost.Stack = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            AddChartPanel();
            return;
        }

        ChartWidgetState? state;
        try
        {
            state = Tools.Json.Deserialize<ChartWidgetState>(json);
        }
        catch
        {
            AddChartPanel();
            return;
        }

        if (state == null || state.Panels.Count == 0)
        {
            AddChartPanel();
            return;
        }

        foreach (ChartPanelState ps in state.Panels)
        {
            ChartPanel panel = new ChartPanel(ps.Name);
            _panels.Add(panel);
            _panelColorIndices[panel] = 0;

            foreach (ChartSeriesState ss in ps.Series)
            {
                if (string.IsNullOrWhiteSpace(ss.FilePath) || !File.Exists(ss.FilePath))
                {
                    continue;
                }
                CreateAndBindSeries(panel, ss.FilePath);
            }
        }

        EnsureStackExists();
        UpdateEmptyState();
    }

    public void Dispose()
    {
        foreach (SeriesBinding b in _bindings)
        {
            b.Dispose();
        }
        _bindings.Clear();
    }

    private abstract class SeriesBinding : IDisposable
    {
        protected readonly ChartWidget Owner;
        protected int Scheduled;

        public ChartPanel Panel { get; }
        public ISeries Series { get; }
        public string FilePath { get; }
        public bool IsAttachedToChartControl { get; set; }
        public FileStreamer Streamer { get; }

        protected SeriesBinding(ChartWidget owner, ChartPanel panel, ISeries series, string file)
        {
            Owner = owner;
            Panel = panel;
            Series = series;
            FilePath = file;
            Streamer = new FileStreamer(file);
            Streamer.Line += OnLine;
            Streamer.Error += OnError;
            Streamer.Connect();
        }

        protected abstract void OnLine(string line);

        private void OnError(Exception ex) => Owner.OnFileStreamerError(ex, FilePath);

        public virtual void Dispose()
        {
            Streamer.Line -= OnLine;
            Streamer.Error -= OnError;
            Streamer.Dispose();
        }

        protected void CheckAttach()
        {
            if (!IsAttachedToChartControl)
            {
                Owner.EnsurePanelInStack(Panel);
                Owner.ChartControlHost.AttachSeries(Series);
                IsAttachedToChartControl = true;
            }
        }
    }

    private abstract class SeriesBinding<T> : SeriesBinding
    {
        private readonly ConcurrentQueue<T> _pending = new();

        protected SeriesBinding(ChartWidget owner, ChartPanel panel, ISeries series, string file)
            : base(owner, panel, series, file)
        {
        }

        protected abstract bool TryParse(string line, out T result);
        protected abstract void Append(T item);

        protected override void OnLine(string line)
        {
            if (!TryParse(line, out T item))
            {
                return;
            }
            _pending.Enqueue(item);

            if (Interlocked.CompareExchange(ref Scheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(FlushOnUi);
            }
        }

        private void FlushOnUi()
        {
            CheckAttach();
            bool added = false;
            while (_pending.TryDequeue(out T? item))
            {
                Append(item);
                added = true;
            }

            if (added)
            {
                Owner.EnsurePanelInStack(Panel);
            }

            Interlocked.Exchange(ref Scheduled, 0);
            if (!_pending.IsEmpty && Interlocked.CompareExchange(ref Scheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(FlushOnUi);
            }
        }
    }

    private sealed class PointBinding : SeriesBinding<Data.Point>
    {
        private readonly PointSeries _pointSeries;

        public PointBinding(ChartWidget owner, ChartPanel panel, PointSeries series, string file)
            : base(owner, panel, series, file)
        {
            _pointSeries = series;
        }

        protected override bool TryParse(string line, out Data.Point result)
        {
            try
            {
                result = Tools.Json.Deserialize<Data.Point>(line);
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        protected override void Append(Data.Point item)
        {
            _pointSeries.Append(item.Timestamp, item.Value);
        }
    }

    private sealed class CandleBinding : SeriesBinding<Candle>
    {
        private readonly CandleSeries _candleSeries;

        public CandleBinding(ChartWidget owner, ChartPanel panel, CandleSeries series, string file)
            : base(owner, panel, series, file)
        {
            _candleSeries = series;
        }

        protected override bool TryParse(string line, out Candle result)
        {
            try
            {
                result = Json.Deserialize<Candle>(line);
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        protected override void Append(Candle item)
        {
            _candleSeries.Append(item);
        }
    }

    private sealed class FillBinding : SeriesBinding<Filld>
    {
        private readonly FillSeries _fillSeries;

        public FillBinding(ChartWidget owner, ChartPanel panel, FillSeries series, string file)
            : base(owner, panel, series, file)
        {
            _fillSeries = series;
        }

        protected override bool TryParse(string line, out Filld filld)
        {
            try
            {
                Fill fill = Tools.Json.Deserialize<Fill>(line);
                filld = new Filld(fill.OrderHeader.ExchangeTimestamp, fill.OrderProfile.Ticks, fill.OrderProfile.Quantity, fill.FillType);
                return true;
            }
            catch
            {
                filld = default;
                return false;
            }
        }

        protected override void Append(Filld item)
        {
            _fillSeries.Append(item);
        }
    }

    private sealed class HistogramBinding : SeriesBinding<Histogram>
    {
        private readonly HistogramSeries _histSeries;

        public HistogramBinding(ChartWidget owner, ChartPanel panel, HistogramSeries series, string file)
            : base(owner, panel, series, file)
        {
            _histSeries = series;
        }

        protected override bool TryParse(string line, out Histogram result)
        {
            try
            {
                result = Tools.Json.Deserialize<Histogram>(line);
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        protected override void Append(Histogram item)
        {
            _histSeries.Append(item);
        }
    }
}