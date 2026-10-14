using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Provider;
using Tools;

namespace Widget;

/// One instrument header's grid row. All values are cached: identity (symbology strings) once via ServerContext.GetSymbolStrings,
/// state per Update. Nothing here constructs a Symbology or formats a string inside a binding getter: that was the multi-second
/// freeze on grids with thousands of spreads. Typed properties (int?) sort correctly and feed the column filters; a null renders
/// as a blank cell. UI thread, cold path.
public sealed class WidgetInstrumentHeader : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private InstrumentHeader128 _header128;

    public int InstrumentHeaderId { get; }
    public string Symbol { get; }
    public string ShortSymbol { get; }

    // Strings throughout except where lexical sort would lie: ids stay int? (numeric sort),
    // sizes stay numeric. Enum names sort alphabetically, ISO dates sort chronologically.
    public string InstrumentType { get; private set; } = "";
    public int? InstrumentId { get; private set; }
    public int ExchangeInstrumentId { get; private set; }
    public int CoreGroupId { get; private set; }
    public string Exchange { get; private set; } = "";
    public string Root { get; private set; } = "";
    public decimal TickSize { get; private set; }
    public double InverseTickSize { get; private set; }
    public string TradingStatus { get; private set; } = "";
    public double Multiplier { get; private set; }

    public string MaturityDate { get; private set; } = "";

    // One display string per leg slot ("+ES Dec2025"); blank when the row has fewer legs.
    public string Leg0 { get; private set; } = "";
    public string Leg1 { get; private set; } = "";
    public string Leg2 { get; private set; } = "";
    public string Leg3 { get; private set; } = "";
    public string Leg4 { get; private set; } = "";
    public string Leg5 { get; private set; } = "";

    public string BaseCurrency { get; private set; } = "";
    public string QuoteCurrency { get; private set; } = "";

    public WidgetInstrumentHeader(int headerId, in InstrumentHeader128 header128)
    {
        InstrumentHeaderId = headerId;
        SymbolStrings symbols = ContextManager.ServerContext.GetSymbolStrings(headerId);
        Symbol = symbols.Symbol;
        ShortSymbol = symbols.ShortSymbol;
        Update(in header128);
    }

    /// Recomputes every cached value from the header and re-evaluates every binding.
    public void Update(in InstrumentHeader128 header128)
    {
        _header128 = header128;
        ref readonly InstrumentHeader header = ref _header128.AsInstrumentHeader();
        Data.InstrumentType instrumentType = header.InstrumentType;

        InstrumentType = instrumentType.ToString();
        InstrumentId = header.InstrumentId == -1 ? null : header.InstrumentId;
        ExchangeInstrumentId = header.ExchangeInstrumentId;
        CoreGroupId = header.CoreGroupId;
        Exchange = header.Exchange.ToString();
        Root = header.Root.ToString();
        TickSize = (decimal)header.TickSize;
        InverseTickSize = header.InverseTickSize;
        TradingStatus = header.TradingStatus.ToString();

        Multiplier = 1.0;
        MaturityDate = "";
        Leg0 = ""; Leg1 = ""; Leg2 = ""; Leg3 = ""; Leg4 = ""; Leg5 = "";
        BaseCurrency = ""; QuoteCurrency = "";

        if (instrumentType == Data.InstrumentType.Future)
        {
            ref readonly FutureHeader future = ref _header128.AsFuture();
            Multiplier = future.Multiplier;
            MaturityDate = future.MaturityDate.NanosSinceEpoch > 0 ? future.MaturityDate.ToDateString() : "";
        }
        else if (instrumentType == Data.InstrumentType.Spread)
        {
            ref LeggedHeader leggedHeader = ref _header128.AsLegged();
            Multiplier = leggedHeader.Multiplier;
            string[] legTexts = new string[] { "", "", "", "", "", "" };
            int legIndex = 0;
            foreach (ref readonly LegHeader legHeader in leggedHeader.Legs)
            {
                legTexts[legIndex++] = LeggedSymbology.GetSignedWeight(legHeader.Weight) + ContextManager.ServerContext.GetSymbolStrings(legHeader.InstrumentHeaderId).ShortSymbol;
            }
            Leg0 = legTexts[0]; Leg1 = legTexts[1]; Leg2 = legTexts[2]; Leg3 = legTexts[3]; Leg4 = legTexts[4]; Leg5 = legTexts[5];
        }
        else if (instrumentType == Data.InstrumentType.Forex)
        {
            ref readonly ForexHeader forex = ref _header128.AsForex();
            BaseCurrency = forex.BaseCurrency.ToString();
            QuoteCurrency = forex.QuoteCurrency.ToString();
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));   // null: every property
    }
}

/// The Instrument Headers widget's saved state: its columns' layout and its column filters. Cold path: a workspace's save and load.
[RegisterJson]
public sealed class InstrumentHeadersWidgetState
{
    public List<DataGridColumnState> Columns { get; set; } = new();
    public List<ColumnRegexFilterState> Filters { get; set; } = new();
}

/// Lists every instrument header in the workspace's principal, regex-filterable per column, and re-reads a header when the server
/// allocates its instrument. The right-click menu filters and toggles columns on a header, and allocates, opens order entry or
/// opens a ladder on a row. UI thread and the refresh timer's thread; cold path.
public sealed partial class InstrumentHeadersWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;
    private Bitset64 _previousInstrumentIds;   // the server's allocated instruments at the last pass; refresh timer thread only

    public string Title { get; private set; } = "Instrument Headers";
    public double DefaultWidth => 900;
    public double DefaultHeight => 400;

    // The grid binds a DataGridCollectionView over _allHeaders: its Filter drives row visibility
    // (Refresh() on change, never rebuild a collection) and native header-click sorting comes free.
    private DataGridCollectionView? _view;
    private readonly List<WidgetInstrumentHeader> _allHeaders = new();
    private readonly Dictionary<int, WidgetInstrumentHeader> _headersById = new();

    // Regex filtering (shared machinery in ColumnRegexFilters): rows must pass every active column
    // filter (AND). Enum columns filter on their display text like any other — "Future|Spread".
    private readonly ColumnRegexFilters<WidgetInstrumentHeader> _columnRegexFilters;

    // Filterable text per column — cached row fields only, no symbology or formatting work.
    private static readonly Dictionary<string, Func<WidgetInstrumentHeader, string>> s_columnText = new()
    {
        ["ShortSymbol"] = r => r.ShortSymbol,
        ["Symbol"] = r => r.Symbol,
        ["InstrumentType"] = r => r.InstrumentType,
        ["CoreGroupId"] = r => r.CoreGroupId.ToString(),
        ["InstrumentHeaderId"] = r => r.InstrumentHeaderId.ToString(),
        ["InstrumentId"] = r => r.InstrumentId?.ToString() ?? "",
        ["ExchangeInstrumentId"] = r => r.ExchangeInstrumentId.ToString(),
        ["Exchange"] = r => r.Exchange,
        ["Root"] = r => r.Root,
        ["TradingStatus"] = r => r.TradingStatus,
        ["TickSize"] = r => r.TickSize.ToString(),
        ["InverseTickSize"] = r => r.InverseTickSize.ToString(),
        ["Multiplier"] = r => r.Multiplier.ToString(),
        ["MaturityDate"] = r => r.MaturityDate,
        ["Leg0"] = r => r.Leg0,
        ["Leg1"] = r => r.Leg1,
        ["Leg2"] = r => r.Leg2,
        ["Leg3"] = r => r.Leg3,
        ["Leg4"] = r => r.Leg4,
        ["Leg5"] = r => r.Leg5,
        ["BaseCurrency"] = r => r.BaseCurrency,
        ["QuoteCurrency"] = r => r.QuoteCurrency,
    };

    private Avalonia.Point _lastPointerPosition;

    public InstrumentHeadersWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        _columnRegexFilters = new ColumnRegexFilters<WidgetInstrumentHeader>(HeadersGrid, s_columnText, () => _view?.Refresh());
        Title = "Instrument Headers (Design)";
    }

    public InstrumentHeadersWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;

        LoadAllHeaders();

        // Before the view: setting Filter refreshes synchronously, which runs FilterRow.
        _columnRegexFilters = new ColumnRegexFilters<WidgetInstrumentHeader>(HeadersGrid, s_columnText, () => _view?.Refresh());

        _view = new DataGridCollectionView(_allHeaders)
        {
            Filter = FilterRow
        };
        HeadersGrid.ItemsSource = _view;

        HeadersGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(HeadersGrid);

        _refreshTimer = new WidgetRefreshTimer(nameof(InstrumentHeadersWidget), 100, Refresh);
    }

    /// Builds one row per instrument header in the principal, and records the server's allocated instruments as already read.
    private void LoadAllHeaders()
    {
        _previousInstrumentIds = ContextManager.ServerContext.InstrumentIds;
        foreach (InstrumentHeader128 header128 in _context.Primary.EnumerateInstrumentHeaders())
        {
            int instrumentHeaderId = header128.AsInstrumentHeader().InstrumentHeaderId;
            WidgetInstrumentHeader widgetInstrumentHeader = new(instrumentHeaderId, in header128);
            _allHeaders.Add(widgetInstrumentHeader);
            _headersById[instrumentHeaderId] = widgetInstrumentHeader;
        }
    }

    /// Re-reads the header of each instrument the server allocated since the last pass and posts the row updates to the UI thread.
    private void Refresh()
    {
        // Step 1: the newly allocated instruments. Why the server's: every instrument it allocated, not just the principal's, so a GUI's own allocation (on a remote desk too) shows here
        Bitset64 instrumentIds = ContextManager.ServerContext.InstrumentIds;
        Bitset64 newInstrumentIds = new Bitset64(instrumentIds.Raw & ~_previousInstrumentIds.Raw);

        // Step 2: read each one's header here, apply it on the UI thread
        List<Action> rowUpdates = new();
        foreach (int instrumentId in newInstrumentIds)
        {
            // Why the catch: an instrument this principal cannot resolve must not stop the others updating
            try
            {
                int instrumentHeaderId = _context.Primary.GetInstrumentHeaderIdByInstrumentId(instrumentId).GetReadonlyRef();
                if (_headersById.TryGetValue(instrumentHeaderId, out WidgetInstrumentHeader? widgetInstrumentHeader))
                {
                    InstrumentHeader128 header128 = _context.Primary.GetInstrumentHeader(instrumentHeaderId).Read();
                    rowUpdates.Add(() => widgetInstrumentHeader.Update(in header128));
                }
            }
            catch { }
        }
        if (rowUpdates.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_refreshTimer.IsDisposed)
                    return;
                foreach (Action rowUpdate in rowUpdates)
                    rowUpdate();
            });
        }

        _previousInstrumentIds = instrumentIds;
    }

    /// Fills the grid's right-click menu: filters and column toggles on a header; allocate, order entry and ladder on a row.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        // Step 1: a header filters its column (a regex over its display text, enum columns included: "Future|Spread") and lists the columns to toggle
        DataGridHit dataGridHit = HeadersGrid.GetDataGridHit(_lastPointerPosition);
        if (dataGridHit.IsColumnHeader)
        {
            _columnRegexFilters.AddMenuItems(contextMenu, dataGridHit.ColumnHeaderText);
            contextMenu.AddColumnVisibilityMenuItems(HeadersGrid);
            return;
        }

        // Step 2: allocate. Why: allocating to the GUI client makes the server open the instrument's data feed, so any header can be lit up and laddered from here; idempotent
        WidgetInstrumentHeader? widgetInstrumentHeader = dataGridHit.RowItem as WidgetInstrumentHeader ?? HeadersGrid.SelectedItem as WidgetInstrumentHeader;
        contextMenu.Items.Add(WidgetMenus.NewMenuItem(widgetInstrumentHeader != null ? $"Allocate {widgetInstrumentHeader.Symbol}" : "Allocate Instrument", iconText: "",
            () => _context.Manual.OnAllocateInstrument(widgetInstrumentHeader!.InstrumentHeaderId), isEnabled: widgetInstrumentHeader != null));

        // Step 3: order entry and the ladder. Why only when allocated: they need the instrument's id in this workspace's principal
        int instrumentId = -1;
        bool isAllocated = widgetInstrumentHeader != null && _context.Primary.TryGetInstrumentId(widgetInstrumentHeader.InstrumentHeaderId, out instrumentId) && _context.Primary.InstrumentIds[instrumentId];
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Create Order", "📝", () => this.AddToHost(SendOrderWidget.NewCreateOrder(_context, _context.Primary.GetInstrument(instrumentId))), isAllocated));
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Open Ladder", "🪜", () => this.AddToHost(LadderWidget.NewLadder(_context, instrumentId)), isAllocated));
    }

    private bool FilterRow(object item) => item is WidgetInstrumentHeader widgetInstrumentHeader && _columnRegexFilters.Matches(widgetInstrumentHeader);

    /// The columns' layout, under their base names (a filter active at save time must not change a column's saved identity), and the filters.
    public string? SaveStateJson() => Json.Serialize(new InstrumentHeadersWidgetState
    {
        Columns = DataGridColumnState.NewDataGridColumnStates(HeadersGrid, _columnRegexFilters.GetBaseName),
        Filters = _columnRegexFilters.NewColumnRegexFilterStates(),
    });

    /// Applies a saved state's column layout and filters. Why the catch: a layout file that no longer parses must not stop the widget opening; it keeps its defaults
    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            InstrumentHeadersWidgetState? instrumentHeadersWidgetState = Json.Deserialize<InstrumentHeadersWidgetState>(json);
            if (instrumentHeadersWidgetState == null)
                return;
            DataGridColumnState.ApplyDataGridColumnStates(HeadersGrid, instrumentHeadersWidgetState.Columns, _columnRegexFilters.GetBaseName);
            _columnRegexFilters.ApplyColumnRegexFilterStates(instrumentHeadersWidgetState.Filters ?? new());
        }
        catch { }
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
