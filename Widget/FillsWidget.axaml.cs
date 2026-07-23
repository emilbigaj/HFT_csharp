using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;

public sealed class WidgetFill
{
    public string Symbol { get; }
    public string ShortSymbol { get; }
    public readonly Fill _fill;
    public Side Side => (Side)Math.Sign(_fill.OrderProfile.Quantity);
    public ulong OrderId => _fill.OrderHeader.OrderId;
    public int InstrumentId => _fill.OrderHeader.OrderId.InstrumentId;
    public ulong FillId => _fill.FillId;
    public FillType FillType => _fill.FillType;
    public int Ticks => _fill.OrderProfile.Ticks;
    public int Quantity => _fill.OrderProfile.Quantity;
    public Timestamp ExchangeTimestamp => _fill.OrderHeader.ExchangeTimestamp;
    public Timestamp NicTimestamp => _fill.OrderHeader.NicTimestamp;

    public WidgetFill(string symbol, string shortSymbol, Fill fill)
    {
        Symbol = symbol;
        ShortSymbol = shortSymbol;
        _fill = fill;
    }
}

public sealed partial class FillsWidget : UserControl, IWidget, IDisposable
{
    private readonly Context _context;
    public AvaloniaList<WidgetFill> Rows { get; } = new AvaloniaList<WidgetFill>();

    private LogReader? _logReader;
    private bool _isLoadingHistory = false;
    private const int BatchSize = 100;
    public string TypeKey => "FillsWidget";
    public string Title { get; private set; }
    public double DefaultWidth => 700;
    public double DefaultHeight => 500;

    public FillsWidget()
    {
        _context = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Fills (Design)";
    }

    public FillsWidget(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        string label;
        try
        {
            string trimmed = _context.DirectoryPath.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            label = string.IsNullOrWhiteSpace(name) ? _context.DirectoryPath.Path : name;
        }
        catch { label = _context.DirectoryPath.Path; }
        Title = $"Fills ({label})";

        InitializeComponent();
        DataContext = this;
        FillsGrid.ItemsSource = Rows;

        _logReader = new LogReader(_context.FillsDirectoryPath.Path, "*.fill");
        _logReader.LiveLines += OnLiveLines;
        _logReader.Start();

        LoadHistoryAsync();
    }

    private void OnScrollChanged(object? sender, ScrollEventArgs e)
    {
        if (sender is DataGrid auditGrid)
        {
            var scrollbar = auditGrid.GetVisualDescendants()
                                     .OfType<ScrollBar>()
                                     .FirstOrDefault(s => s.Orientation == Orientation.Vertical);

            if (scrollbar != null)
            {
                double current = scrollbar.Value;
                double max = scrollbar.Maximum;
                double count = Rows.Count;
                double scollPerCount = max / count;

                if (max <= 0) return;

                double threshold = (max - scollPerCount * 5);

                if (current >= threshold)
                {
                    LoadHistoryAsync();
                }
            }
        }
    }

    private async void LoadHistoryAsync()
    {
        if (_isLoadingHistory || _logReader == null) return;
        _isLoadingHistory = true;

        await Task.Run(() =>
        {
            List<string> lines = _logReader.LoadHistory(BatchSize);
            if (lines.Count > 0)
            {
                List<WidgetFill> fills = ParseLines(lines);
                Dispatcher.UIThread.Post(() =>
                {
                    Rows.AddRange(fills);
                    _isLoadingHistory = false;
                });
            }
            else
            {
                _isLoadingHistory = false;
            }
        });
    }

    private void OnLiveLines(List<string> lines)
    {
        List<WidgetFill> fills = ParseLines(lines);
        Dispatcher.UIThread.Post(() => Rows.InsertRange(0, fills));
    }

    private List<WidgetFill> ParseLines(List<string> lines)
    {
        List<WidgetFill> fills = new List<WidgetFill>(lines.Count);
        foreach (string line in lines)
        {
            if (TryCreateFill(line, out WidgetFill wf)) fills.Add(wf);
        }
        fills.Sort((a, b) => b.ExchangeTimestamp.CompareTo(a.ExchangeTimestamp));

        return fills;
    }

    private bool TryCreateFill(string line, out WidgetFill widgetFill)
    {
        widgetFill = null!;
        try
        {
            Fill fill = Tools.Json.Deserialize<Fill>(line);
            if (fill.OrderHeader.OrderId == 0 && fill.FillId == 0 && fill.OrderProfile.Quantity == 0) return false;

            string symbol = "???";
            string shortSymbol = "???";
            try
            {
                Instrument inst = _context.GetInstrument(fill.OrderHeader.OrderId.InstrumentId);
                symbol = inst.Symbol;
                shortSymbol = inst.ShortSymbol;
            }
            catch { }

            widgetFill = new WidgetFill(symbol, shortSymbol, fill);
            return true;
        }
        catch { return false; }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && FillsGrid != null)
        {
            menu.Items.Clear();
            foreach (DataGridColumn col in FillsGrid.Columns)
            {
                string header = col.Header?.ToString() ?? "Column";
                MenuItem item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = col.IsVisible };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }
    public void Dispose()
    {
        _logReader?.Dispose();
    }
}