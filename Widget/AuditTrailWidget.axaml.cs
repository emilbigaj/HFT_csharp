using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Widget;

/// <summary>
/// Optimized DTO for Audit Trail.
/// All display strings are pre-calculated to ensure O(1) binding performance during scrolling.
/// </summary>
public sealed class WidgetOrderAudit
{
    // --- Pre-calculated Display Properties (Fast Bindings) ---
    public Timestamp Timestamp { get; }
    public string OrderTypeName { get; }
    public string ActionName { get; }
    public string PriceStr { get; }
    public Side Side { get; }
    public bool IsVisible { get; set; } = true; // For potential future binding, though we filter collection now.

    // --- Raw Data ---
    public OrderHeader OrderHeader { get; }
    public int? WorkingQuantity { get; }
    public int? QuantityFilled { get; }
    public int OrderType { get; }
    public int Action { get; }
    public Instrument Instrument { get; }
    public string Reason { get; }
    public string Symbol { get; }
    public string ShortSymbol => Instrument.ShortSymbol;


    public int? Ticks { get; }
    public int Quantity { get; }
    public double Price { get; }

    // Pass-throughs for bindings if needed
    public ulong ClientOrderId => OrderHeader.OrderId;
    public string Source => OrderHeader.OrderId.IsAlgoOrder() ? "Algo" : "Manual";
    public int Seq => OrderHeader.Seq;
    public Timestamp ExchangeTimestamp => OrderHeader.ExchangeTimestamp;
    public Timestamp NicTimestamp => OrderHeader.NicTimestamp;
    public int InstrumentId => OrderHeader.OrderId.InstrumentId;

    public WidgetOrderAudit(
        Instrument instrument,
        OrderHeader header,
        int orderType,
        int action,
        int quantity,
        int? ticks = null,
        double price = double.NaN,
        int? quantityFilled = null,
        int? workingQuantity = null,
        string reason = "")
    {
        Instrument = instrument;
        OrderHeader = header;
        OrderType = orderType;
        Action = action;
        Quantity = quantity;
        Ticks = ticks;
        Price = double.IsNaN(price) ? (ticks.HasValue ? instrument.TicksToPrice(ticks.Value) : double.NaN) : price;
        QuantityFilled = quantityFilled;
        WorkingQuantity = workingQuantity;
        Reason = reason;
        Symbol = instrument.Symbol;

        // --- Perform heavy lifting ONCE here ---

        Side = (Side)Math.Sign(Quantity);
        PriceStr = Price.ToString(Instrument.PriceFormat);
        OrderTypeName = ((Execution.OrderType)OrderType).ToString();

        ActionName = (Execution.OrderType)OrderType switch
        {
            Execution.OrderType.OrderState => ((OrderStateStatus)Action).ToString(),
            Execution.OrderType.OrderTarget => ((OrderTargetAction)Action).ToString(),
            Execution.OrderType.OrderRejected => ((OrderTargetAction)Action).ToString(),
            Execution.OrderType.Fill => ((FillType)Action).ToString(),
            Execution.OrderType.Position => "Position",
            _ => Action.ToString()
        };

        // One clock for every row: the NicTimestamp, which is also the logging server's sort key.
        Timestamp = OrderHeader.NicTimestamp;
    }
}

/// The workspace's audit trail (the primary context's tap and its manual client's), newest first, filtered by order type,
/// source and column regexes; older pages load as the grid scrolls near its end. UI thread; history pages parse on the
/// thread pool. Cold path.
public sealed partial class AuditTrailWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;

    // Master list of all loaded audits
    private readonly List<WidgetOrderAudit> _allAudits = new List<WidgetOrderAudit>();

    // Filtered view bound to the DataGrid
    public AvaloniaList<WidgetOrderAudit> Rows { get; } = new AvaloniaList<WidgetOrderAudit>();

    // Filter state
    private readonly HashSet<int> _visibleOrderTypes = new HashSet<int>();

    // Source filter ("Algo"/"Manual"); both visible by default.
    private static readonly string[] s_sources = new[] { "Algo", "Manual" };
    private readonly HashSet<string> _visibleSources = new HashSet<string>(s_sources);

    // Regex filtering (shared machinery in ColumnRegexFilters): rows must pass every active column
    // filter (AND).
    private readonly ColumnRegexFilters<WidgetOrderAudit> _columnRegexFilters;

    // Filterable text per column — cached row fields only, no formatting work.
    private static readonly Dictionary<string, Func<WidgetOrderAudit, string>> s_columnText = new()
    {
        ["ShortSymbol"] = r => r.ShortSymbol,
        ["Symbol"] = r => r.Symbol,
        ["ClientOrderId"] = r => r.ClientOrderId.ToString(),
    };

    // One reader per audit directory (primary tap + manual _GUI client tap), merged by timestamp.
    private readonly List<LogReader> _logReaders = new List<LogReader>();
    private readonly List<System.Collections.Generic.Queue<WidgetOrderAudit>> _historyQueues = new List<System.Collections.Generic.Queue<WidgetOrderAudit>>();
    private bool _isLoadingHistory = false;
    private const int BatchSize = 100;
    public string Title { get; private set; }
    public double DefaultWidth => 800;
    public double DefaultHeight => 500;

    private readonly ConcurrentDictionary<string, Instrument> _instrumentBySymbol = new ConcurrentDictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);

    // Cached pointer position for context menu hit testing
    private Avalonia.Point _lastPointerPos;

    public AuditTrailWidget()
    {
        _context = null!;
        InitializeComponent();
        DataContext = this;
        _columnRegexFilters = new ColumnRegexFilters<WidgetOrderAudit>(AuditGrid, s_columnText, ApplyFilter);
        Title = "Audit Trail (Design)";
    }

    public AuditTrailWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Title = $"Audit Trail ({_context.Primary.DirectoryName})";

        InitializeComponent();
        DataContext = this;
        AuditGrid.ItemsSource = Rows;

        _columnRegexFilters = new ColumnRegexFilters<WidgetOrderAudit>(AuditGrid, s_columnText, ApplyFilter);

        // Track pointer for context menu
        AuditGrid.PointerMoved += (s, e) => _lastPointerPos = e.GetPosition(AuditGrid);

        // Initialize Filter (Show All by default)
        foreach (OrderType orderType in Enum.GetValues<OrderType>())
        {
            _visibleOrderTypes.Add((int)orderType);
        }

        // Primary carries the algo/server tap; the manual client's order traffic is tapped into its
        // own <name>_GUI/Audit directory, so read both (they coincide when the workspace IS the _GUI client).
        string primaryAuditDirectoryPath = _context.Primary.AuditDirectoryPath.Path;
        string manualAuditDirectoryPath = Provider.Context.GetAuditDirectoryPath(_context.Manual.Context.ClientName);
        AddLogReader(primaryAuditDirectoryPath);
        if (!string.Equals(manualAuditDirectoryPath, primaryAuditDirectoryPath, StringComparison.OrdinalIgnoreCase))
            AddLogReader(manualAuditDirectoryPath);

        LoadHistoryAsync();
    }

    private void AddLogReader(string auditDirectoryPath)
    {
        LogReader logReader = new LogReader(auditDirectoryPath, "*.audit");
        logReader.LiveLines += OnLiveLines;
        logReader.Start();
        _logReaders.Add(logReader);
        _historyQueues.Add(new System.Collections.Generic.Queue<WidgetOrderAudit>());
    }

    /// Pages in older audit rows once the grid is scrolled near its end.
    private void OnScrollChanged(object? sender, ScrollEventArgs e)
    {
        if (AuditGrid.IsScrolledNearEnd(Rows.Count))
            LoadHistoryAsync();
    }

    private async void LoadHistoryAsync()
    {
        if (_isLoadingHistory || _logReaders.Count == 0) return;
        _isLoadingHistory = true;

        await Task.Run(() =>
        {
            List<WidgetOrderAudit> audits = MergeHistory(BatchSize);

            if (audits.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Add to master list
                    _allAudits.AddRange(audits);

                    // Add to visible list if type matches
                    var filteredToAdd = audits.Where(IsRowVisible).ToList();
                    Rows.AddRange(filteredToAdd);

                    _isLoadingHistory = false;
                });
            }
            else
            {
                _isLoadingHistory = false;
            }
        });
    }

    // K-way merge across the readers: emit the newest head first, refilling each queue from its
    // reader as it empties, so paging stays newest-to-oldest across both directories.
    private List<WidgetOrderAudit> MergeHistory(int count)
    {
        List<WidgetOrderAudit> merged = new List<WidgetOrderAudit>(count);

        while (merged.Count < count)
        {
            System.Collections.Generic.Queue<WidgetOrderAudit>? newestQueue = null;

            for (int readerIndex = 0; readerIndex < _logReaders.Count; readerIndex++)
            {
                System.Collections.Generic.Queue<WidgetOrderAudit> historyQueue = _historyQueues[readerIndex];

                // Refill until we have a head or the reader is exhausted (a batch can parse to zero rows).
                while (historyQueue.Count == 0)
                {
                    List<string> lines = _logReaders[readerIndex].LoadHistory(BatchSize);
                    if (lines.Count == 0) break;
                    foreach (WidgetOrderAudit audit in ParseLines(lines)) historyQueue.Enqueue(audit);
                }

                if (historyQueue.Count == 0) continue;
                if (newestQueue == null || historyQueue.Peek().Timestamp.CompareTo(newestQueue.Peek().Timestamp) > 0) newestQueue = historyQueue;
            }

            if (newestQueue == null) break;
            merged.Add(newestQueue.Dequeue());
        }

        return merged;
    }

    private void OnLiveLines(List<string> lines)
    {
        // The tail delivers oldest first, history pages newest first: reverse so ties sort the same way in both.
        List<string> newestFirstLines = new List<string>(lines);
        newestFirstLines.Reverse();
        List<WidgetOrderAudit> audits = ParseLines(newestFirstLines);
        Dispatcher.UIThread.Post(() =>
        {
            // Insert at top of master list (assuming new lines are newer)
            _allAudits.InsertRange(0, audits);

            // Filter
            var filteredToAdd = audits.Where(IsRowVisible).ToList();
            Rows.InsertRange(0, filteredToAdd);
        });
    }

    private List<WidgetOrderAudit> ParseLines(List<string> lines)
    {
        List<WidgetOrderAudit> audits = new List<WidgetOrderAudit>(lines.Count);
        foreach (string line in lines)
        {
            if (TryCreateAudit(line, out WidgetOrderAudit wa)) audits.Add(wa);
        }
        // Stable: rows sharing a NicTimestamp (a target and its trigger, a fill and its position) keep file order.
        audits = audits.OrderByDescending(audit => audit.Timestamp).ToList();

        return audits;
    }

    private bool TryGetInstrument(string symbol, out Instrument? instrument)
    {
        if (string.IsNullOrWhiteSpace(symbol)) { instrument = null!; return false; }
        if (_instrumentBySymbol.TryGetValue(symbol, out instrument)) return true;
        foreach (Instrument candidate in _context.Primary.EnumerateInstruments())
        {
            if (candidate.Symbol == symbol)
            {
                _instrumentBySymbol[symbol] = candidate;
                instrument = candidate;
                return true;
            }
        }
        return false;
    }

    private bool TryCreateAudit(string json, out WidgetOrderAudit widgetAudit)
    {
        widgetAudit = null!;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("OrderHeader", out JsonElement headerProp) || !headerProp.TryGetString("Symbol", out string symbol) || string.IsNullOrEmpty(symbol)) return false;
            if (!TryGetInstrument(symbol, out Instrument? instrument)) return false;
            if (!root.TryGetProperty("Header", out JsonElement header) || !header.TryGetString("Type", out string typeStr) || !Enum.TryParse(typeStr, true, out OrderType orderType)) return false;

            switch (orderType)
            {
                case OrderType.OrderState:
                    var state = Json.Deserialize<OrderState>(json);
                    widgetAudit = new WidgetOrderAudit(instrument!, state.OrderHeader, (int)OrderType.OrderState, (int)state.OrderStateStatus, state.OrderProfile.Quantity, ticks: state.OrderProfile.Ticks, quantityFilled: state.QuantityFilled, workingQuantity: state.WorkingQuantity, reason: state.OrderStateReason.ToString() );
                    return true;
                case OrderType.OrderTarget:
                    var target = Json.Deserialize<OrderTarget>(json);
                    widgetAudit = new WidgetOrderAudit(instrument!, target.OrderHeader, (int)OrderType.OrderTarget, (int)target.OrderTargetAction, target.OrderProfile.Quantity, ticks: target.OrderProfile.Ticks);
                    return true;
                case OrderType.OrderRejected:
                    var rejected = Json.Deserialize<OrderRejected>(json);
                    widgetAudit = new WidgetOrderAudit(instrument!, rejected.OrderHeader, (int)OrderType.OrderRejected, (int)rejected.OrderTargetAction, rejected.OrderProfile.Quantity, ticks: rejected.OrderProfile.Ticks, reason: rejected.OrderRejectedReasonsString);
                    return true;
                case OrderType.Fill:
                    var fill = Json.Deserialize<Fill>(json);
                    widgetAudit = new WidgetOrderAudit(instrument!, fill.OrderHeader, (int)OrderType.Fill, (int)fill.FillType, fill.Quantity, price: fill.Price);
                    return true;
                case OrderType.Position:
                    var pos = Json.Deserialize<PositionHeader>(json);
                    widgetAudit = new WidgetOrderAudit(instrument!, pos.OrderHeader, (int)OrderType.Position, (int)OrderType.Fill, quantity: pos.Quantity, ticks: null, price: pos.AvgPrice);
                    return true;
                default: return false;
            }
        }
        catch { return false; }
    }

    /// A header's menu: its regex filter and the column toggles. A row's menu: the order type and source filters.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        DataGridHit dataGridHit = AuditGrid.GetDataGridHit(_lastPointerPos);
        if (dataGridHit.IsColumnHeader)
        {
            // A header: the right-clicked column's regex filter (all active filters AND together), then the column toggles
            _columnRegexFilters.AddMenuItems(contextMenu, dataGridHit.ColumnHeaderText);
            contextMenu.AddColumnVisibilityMenuItems(AuditGrid);
            return;
        }

        // A row: the order type filters, then the source filters
        contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem("Filter Order Types"));
        contextMenu.Items.Add(new Separator());
        foreach (OrderType orderType in Enum.GetValues<OrderType>())
            contextMenu.Items.Add(NewFilterMenuItem(orderType.ToString(), _visibleOrderTypes, (int)orderType));

        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem("Filter Source"));
        contextMenu.Items.Add(new Separator());
        foreach (string source in s_sources)
            contextMenu.Items.Add(NewFilterMenuItem(source, _visibleSources, source));
    }

    /// A checkbox item, ticked while value is in visibleValues, that toggles it there and refilters the rows.
    private MenuItem NewFilterMenuItem<T>(string header, HashSet<T> visibleValues, T value)
    {
        MenuItem menuItem = new() { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = visibleValues.Contains(value) };
        menuItem.Click += (_, _) =>
        {
            if (!visibleValues.Remove(value))
                visibleValues.Add(value);
            ApplyFilter();
        };
        return menuItem;
    }

    // Row must pass the OrderType mask, the Source mask, and every active column regex (AND).
    private bool IsRowVisible(WidgetOrderAudit audit)
    {
        if (!_visibleOrderTypes.Contains(audit.OrderType)) return false;
        if (!_visibleSources.Contains(audit.Source)) return false;
        return _columnRegexFilters.Matches(audit);
    }

    private void ApplyFilter()
    {
        // Rebuild Rows from _allAudits based on current mask
        // This is efficient enough for typical UI lists (< 100k items)
        // For extremely large lists, we'd use a virtualizing filtered view, 
        // but rebuilding the AvaloniaList is the robust filtered way here.

        // 1. Snapshot current selection if needed (not implemented here)

        // 2. Clear visual rows
        Rows.Clear();

        // 3. Add matching rows
        var matches = _allAudits.Where(IsRowVisible);
        Rows.AddRange(matches);
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }
    public void Dispose()
    {
        foreach (LogReader logReader in _logReaders) logReader.Dispose();
    }
}