using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    public Side Side => _fill.Side;
    public ulong OrderId => _fill.OrderHeader.OrderId;
    public int InstrumentId => _fill.OrderHeader.OrderId.InstrumentId;
    public ulong FillId => _fill.FillId;
    public FillType FillType => _fill.FillType;
    public double Price => _fill.Price;
    public int Quantity => _fill.Quantity;
    public Timestamp ExchangeTimestamp => _fill.OrderHeader.ExchangeTimestamp;
    public Timestamp NicTimestamp => _fill.OrderHeader.NicTimestamp;

    public WidgetFill(string symbol, string shortSymbol, Fill fill)
    {
        Symbol = symbol;
        ShortSymbol = shortSymbol;
        _fill = fill;
    }
}

/// A context's fills, newest first: live fills arrive at the top, older pages load as the grid scrolls near its end.
/// UI thread; history pages parse on the thread pool. Cold path.
public sealed partial class FillsWidget : UserControl, IWidget, IDisposable
{
    private readonly Context _context;
    public AvaloniaList<WidgetFill> Rows { get; } = new AvaloniaList<WidgetFill>();

    private LogReader? _logReader;
    private bool _isLoadingHistory = false;
    private const int BatchSize = 100;
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
        Title = $"Fills ({_context.DirectoryName})";

        InitializeComponent();
        DataContext = this;
        FillsGrid.ItemsSource = Rows;

        _logReader = new LogReader(_context.FillsDirectoryPath.Path, "*.fill");
        _logReader.LiveLines += OnLiveLines;
        _logReader.Start();

        LoadHistoryAsync();
    }

    /// Pages in older fills once the grid is scrolled near its end.
    private void OnScrollChanged(object? sender, ScrollEventArgs e)
    {
        if (FillsGrid.IsScrolledNearEnd(Rows.Count))
            LoadHistoryAsync();
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
            if (fill.OrderHeader.OrderId == 0 && fill.FillId == 0 && fill.Quantity == 0) return false;

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

    /// The grid's menu: a checkbox per column, toggling whether it shows.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();
        contextMenu.AddColumnVisibilityMenuItems(FillsGrid);
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }
    public void Dispose()
    {
        _logReader?.Dispose();
    }
}