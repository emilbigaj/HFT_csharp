using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Provider;
using Data;
using Tools;
using System.Collections.Generic;
using System;
using System.Linq;

namespace Widget;

public sealed class WidgetInstrumentHeader : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private InstrumentHeader128 _header128;
    private ref readonly InstrumentHeader Header => ref _header128.AsInstrumentHeader();

    public int InstrumentHeaderId => Header.InstrumentHeaderId;
    public InstrumentType InstrumentType => Header.InstrumentType;
    public string InstrumentIdStr => Header.InstrumentId == -1 ? "" : Header.InstrumentId.ToString();

    public int ExchangeInstrumentId => Header.ExchangeInstrumentId;

    public int CoreGroupId => Header.CoreGroupId;
    public string Exchange => Header.Exchange.ToString();
    public string Root => Header.Root.ToString();
    public decimal TickSize => (decimal)Header.TickSize;
    public double InverseTickSize => Header.InverseTickSize;
    public TradingStatus TradingStatus => Header.TradingStatus;


    public string Symbol => _header128.Symbology.Symbol;
    public string ShortSymbol => _header128.Symbology.ShortSymbol;

    public double Multiplier => InstrumentType switch
    {
        InstrumentType.Future => _header128.AsFuture().Multiplier,
        InstrumentType.Spread => _header128.AsSpread().Multiplier,
        _ => 1.0
    };

    public string? ExpiryDateStr => InstrumentType == InstrumentType.Future && _header128.AsFuture().ExpiryDate.NanosSinceEpoch > 0 ? _header128.AsFuture().ExpiryDate.ToDateString() : "";
    public string? ExpiryType => InstrumentType == InstrumentType.Future ? _header128.AsFuture().ExpiryType.ToString() : "";

    public string? LongExpiryDateStr => InstrumentType == InstrumentType.Spread && _header128.AsSpread().LongExpiryDate.NanosSinceEpoch > 0 ? _header128.AsSpread().LongExpiryDate.ToDateString() : "";
    public string? LongExpiryType => InstrumentType == InstrumentType.Spread ? _header128.AsSpread().LongExpiryType.ToString() : "";
    public string? ShortExpiryDateStr => InstrumentType == InstrumentType.Spread && _header128.AsSpread().ShortExpiryDate.NanosSinceEpoch > 0 ? _header128.AsSpread().ShortExpiryDate.ToDateString() : "";
    public string? ShortExpiryType => InstrumentType == InstrumentType.Spread ? _header128.AsSpread().ShortExpiryType.ToString() : "";

    public string? LongInstrumentId => InstrumentType == InstrumentType.Spread ? (_header128.AsSpread().LongInstrumentId == -1 ? "" : _header128.AsSpread().LongInstrumentId.ToString()) : "";
    public string? ShortInstrumentId => InstrumentType == InstrumentType.Spread ? (_header128.AsSpread().ShortInstrumentId == -1 ? "" : _header128.AsSpread().ShortInstrumentId.ToString()) : "";

    public string? BaseCurrency => InstrumentType == InstrumentType.Forex ? _header128.AsForex().BaseCurrency.ToString() : "";
    public string? QuoteCurrency => InstrumentType == InstrumentType.Forex ? _header128.AsForex().QuoteCurrency.ToString() : "";

    public WidgetInstrumentHeader(int headerId, in InstrumentHeader128 header128)
    {
        _header128 = header128;
    }

    public void Update(in InstrumentHeader128 header128)
    {
        _header128 = header128;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // Refresh all bindings
    }
}

[RegisterJson]
public class InstrumentHeadersColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }
}

[RegisterJson]
public class InstrumentHeadersWidgetState
{
    public List<InstrumentHeadersColumnState> Columns { get; set; } = new();
    public List<int> VisibleTypes { get; set; } = new();
}

public sealed partial class InstrumentHeadersWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;
    private Bitset64 _lastSubs = new Bitset64();

    public string TypeKey => "InstrumentHeadersWidget";
    public string Title { get; private set; } = "Instrument Headers";
    public double DefaultWidth => 900;
    public double DefaultHeight => 400;

    public ObservableCollection<WidgetInstrumentHeader> Rows { get; } = new();
    private readonly List<WidgetInstrumentHeader> _allHeaders = new();
    private readonly Dictionary<int, WidgetInstrumentHeader> _headersById = new();
    private readonly HashSet<InstrumentType> _visibleTypes = new();

    private Avalonia.Point _lastPointerPos;

    public InstrumentHeadersWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Instrument Headers (Design)";
    }

    public InstrumentHeadersWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        HeadersGrid.ItemsSource = Rows;

        foreach (InstrumentType type in Enum.GetValues<InstrumentType>())
        {
            _visibleTypes.Add(type);
        }

        LoadAllHeaders();

        HeadersGrid.PointerMoved += (s, e) => _lastPointerPos = e.GetPosition(HeadersGrid);

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    private void LoadAllHeaders()
    {
        _lastSubs = _context.Primary.InstrumentIds;
        foreach (InstrumentHeader128 header128 in _context.Primary.EnumerateInstrumentHeaders())
        {
            int instrumentHeaderid = header128.AsInstrumentHeader().InstrumentHeaderId;
            var widgetHeader = new WidgetInstrumentHeader(instrumentHeaderid, in header128);
            _allHeaders.Add(widgetHeader);
            _headersById[instrumentHeaderid] = widgetHeader;
            if (_visibleTypes.Contains(widgetHeader.InstrumentType))
            {
                Rows.Add(widgetHeader);
            }
        }
    }

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            Bitset64 currentSubs = _context.Primary.InstrumentIds;
            Bitset64 diff = new Bitset64(currentSubs.Raw ^ _lastSubs.Raw);
            diff.Raw &= currentSubs.Raw;

            if (!diff.IsEmpty)
            {
                List<Action> uiUpdates = new List<Action>();
                foreach (int instrumentId in diff)
                {
                    try
                    {
                        int headerId = _context.Primary.GetInstrumentHeaderIdByInstrumentId(instrumentId).GetReadonlyRef();
                        if (_headersById.TryGetValue(headerId, out var widgetHeader))
                        {
                            var header128 = _context.Primary.GetInstrumentHeader(headerId).Read();
                            uiUpdates.Add(() => widgetHeader.Update(in header128));
                        }
                    }
                    catch { }
                }

                if (uiUpdates.Count > 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed) return;
                        foreach (var update in uiUpdates) update();
                    });
                }
            }

            _lastSubs = currentSubs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"InstrumentHeadersWidget Error: {ex.Message}");
        }
        finally
        {
            if (!_disposed) _refreshTimer.Start();
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || HeadersGrid == null) return;

        menu.Items.Clear();

        var visual = HeadersGrid.InputHitTest(_lastPointerPos) as Visual;
        bool isHeader = false;

        var v = visual;
        while (v != null)
        {
            if (v is DataGridColumnHeader)
            {
                isHeader = true;
                break;
            }
            v = v.GetVisualParent() as Visual;
        }

        if (isHeader)
        {
            foreach (DataGridColumn col in HeadersGrid.Columns)
            {
                string header = col.Header?.ToString() ?? "Column";
                MenuItem item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = col.IsVisible };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
        else
        {
            var filterHeader = new MenuItem { Header = "Filter Instrument Types", FontWeight = Avalonia.Media.FontWeight.Bold, IsEnabled = false };
            menu.Items.Add(filterHeader);
            menu.Items.Add(new Separator());

            foreach (InstrumentType type in Enum.GetValues<InstrumentType>())
            {
                InstrumentType currentType = type;
                var item = new MenuItem
                {
                    Header = currentType.ToString(),
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = _visibleTypes.Contains(currentType)
                };

                item.Click += (_, _) => ToggleFilter(currentType);
                menu.Items.Add(item);
            }
        }
    }

    private void ToggleFilter(InstrumentType type)
    {
        if (_visibleTypes.Contains(type))
            _visibleTypes.Remove(type);
        else
            _visibleTypes.Add(type);

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var matches = _allHeaders.Where(a => _visibleTypes.Contains(a.InstrumentType));
        foreach (var match in matches) Rows.Add(match);
    }

    public string? SaveStateJson()
    {
        var state = new InstrumentHeadersWidgetState();
        foreach (var col in HeadersGrid.Columns)
        {
            state.Columns.Add(new InstrumentHeadersColumnState
            {
                Header = col.Header?.ToString() ?? "",
                Width = col.Width.Value,
                DisplayIndex = col.DisplayIndex,
                IsVisible = col.IsVisible
            });
        }
        state.VisibleTypes = _visibleTypes.Select(t => (int)t).ToList();
        return Json.Serialize(state);
    }

    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var state = Tools.Json.Deserialize<InstrumentHeadersWidgetState>(json);
            if (state == null) return;

            foreach (var colState in state.Columns)
            {
                var col = HeadersGrid.Columns.FirstOrDefault(c => (c.Header?.ToString() ?? "") == colState.Header);
                if (col != null)
                {
                    col.Width = new DataGridLength(colState.Width);
                    col.DisplayIndex = colState.DisplayIndex;
                    col.IsVisible = colState.IsVisible;
                }
            }

            if (state.VisibleTypes != null && state.VisibleTypes.Count > 0)
            {
                _visibleTypes.Clear();
                foreach (int t in state.VisibleTypes)
                {
                    _visibleTypes.Add((InstrumentType)t);
                }
                ApplyFilter();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
    }
}