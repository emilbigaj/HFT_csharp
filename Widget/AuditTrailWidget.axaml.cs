using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        PriceStr = Price.ToString($"N{Instrument.TicKDecimals}");
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
    public string TypeKey => "AuditTrailWidget";
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

        string label;
        try
        {
            string trimmed = _context.Primary.DirectoryPath.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            label = string.IsNullOrWhiteSpace(name) ? _context.Primary.DirectoryPath.Path : name;
        }
        catch
        {
            label = _context.Primary.DirectoryPath.Path;
        }

        Title = $"Audit Trail ({label})";

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
        string manualAuditDirectoryPath = Provider.Context.GetAuditDirectoryPath(_context.Manual.ClientName);
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

    private void OnScrollChanged(object? sender, ScrollEventArgs e)
    {
        // 1. Identify the DataGrid
        if (sender is DataGrid auditGrid)
        {
            // 2. Find the internal vertical ScrollBar by name
            var scrollbar = auditGrid.GetVisualDescendants()
                                     .OfType<ScrollBar>()
                                     .FirstOrDefault(s => s.Orientation == Orientation.Vertical);

            if (scrollbar != null)
            {
                double current = scrollbar.Value;
                double max = scrollbar.Maximum;
                double count = Rows.Count;
                double scollPerCount = max / Math.Max(1, count);

                if (max <= 0) return;

                // 3. Calculate if we are in the bottom 100 rows
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

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || AuditGrid == null) return;

        menu.Items.Clear();

        // Perform Hit Test
        var visual = AuditGrid.InputHitTest(_lastPointerPos) as Visual;
        bool isHeader = false;
        string? clickedColumn = null;

        // Walk up logic
        var v = visual;
        while (v != null)
        {
            if (v is DataGridColumnHeader columnHeader)
            {
                isHeader = true;
                clickedColumn = columnHeader.Content?.ToString();
                break;
            }
            v = v.GetVisualParent() as Visual;
        }

        if (isHeader)
        {
            // Filter section for the column that was right-clicked: a regex over its display text.
            // All active filters AND together.
            _columnRegexFilters.AddMenuItems(menu, clickedColumn);

            // --- HEADER: Column Toggles ---
            foreach (DataGridColumn col in AuditGrid.Columns)
            {
                string header = col.Header?.ToString() ?? "Column";
                MenuItem item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = col.IsVisible };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
        else
        {
            // --- CONTENT: Type Filters ---
            var filterHeader = new MenuItem { Header = "Filter Order Types", FontWeight = Avalonia.Media.FontWeight.Bold, IsEnabled = false };
            menu.Items.Add(filterHeader);
            menu.Items.Add(new Separator());

            foreach (OrderType type in Enum.GetValues<OrderType>())
            {
                int typeInt = (int)type;
                var item = new MenuItem
                {
                    Header = type.ToString(),
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = _visibleOrderTypes.Contains(typeInt)
                };

                item.Click += (_, _) => ToggleFilter(typeInt);
                menu.Items.Add(item);
            }

            // --- CONTENT: Source Filters ---
            menu.Items.Add(new Separator());
            var sourceHeader = new MenuItem { Header = "Filter Source", FontWeight = Avalonia.Media.FontWeight.Bold, IsEnabled = false };
            menu.Items.Add(sourceHeader);
            menu.Items.Add(new Separator());

            foreach (string source in s_sources)
            {
                var item = new MenuItem
                {
                    Header = source,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = _visibleSources.Contains(source)
                };

                item.Click += (_, _) => ToggleSource(source);
                menu.Items.Add(item);
            }
        }
    }

    private void ToggleFilter(int orderType)
    {
        if (_visibleOrderTypes.Contains(orderType))
            _visibleOrderTypes.Remove(orderType);
        else
            _visibleOrderTypes.Add(orderType);

        ApplyFilter();
    }

    private void ToggleSource(string source)
    {
        if (_visibleSources.Contains(source))
            _visibleSources.Remove(source);
        else
            _visibleSources.Add(source);

        ApplyFilter();
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