using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Timers;
using Avalonia.Collections;
using Avalonia.Layout;
using Avalonia.Media;
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

// Process-wide cache of symbology-derived display strings, indexed by instrumentHeaderId.
// Header identity is immutable and slots are never reused (see Spec.md), so each entry is computed
// exactly once for the process lifetime; Symbology construction never runs in a binding getter.
public static class SymbolCache
{
    public sealed record SymbolStrings(string Symbol, string ShortSymbol);

    private static readonly SymbolStrings s_unknown = new("?", "?");
    private static SymbolStrings?[] s_strings = Array.Empty<SymbolStrings?>();
    private static ServerContext? s_context;

    // Idempotent; call at workspace startup. Sizes the slot array and warms it off-thread so the
    // headers widget opens against a hot cache.
    public static void Initialize(ServerContext context)
    {
        if (s_context != null)
            return;
        s_context = context;
        s_strings = new SymbolStrings?[context.ServerHeader.GetReadonlyRef().InstrumentsCapacity];
        System.Threading.Tasks.Task.Run(Warm);
    }

    public static void Warm()
    {
        ServerContext? context = s_context;
        if (context == null)
            return;
        int count = context.ServerHeader.GetReadonlyRef().InstrumentsCount;
        for (int instrumentHeaderId = 0; instrumentHeaderId < count; instrumentHeaderId++)
            _ = Get(instrumentHeaderId);
    }

    // Lazy: covers headers written after startup and widgets opening before the warm completes.
    // Benign race: concurrent fills compute identical values.
    public static SymbolStrings Get(int instrumentHeaderId)
    {
        SymbolStrings?[] strings = s_strings;
        if (s_context == null || (uint)instrumentHeaderId >= (uint)strings.Length)
            return Compute(instrumentHeaderId);
        return strings[instrumentHeaderId] ??= Compute(instrumentHeaderId);
    }

    private static SymbolStrings Compute(int instrumentHeaderId)
    {
        ServerContext? context = s_context;
        if (context == null)
            return s_unknown;
        try
        {
            InstrumentHeader128 header128 = context.GetInstrumentHeader(instrumentHeaderId).Read();
            Symbology symbology = header128.Symbology;
            return new SymbolStrings(symbology.Symbol, symbology.ShortSymbol);
        }
        catch
        {
            // Unknown instrument type (Symbology getter throws) — mark, never take down a widget.
            return s_unknown;
        }
    }
}

// All values are cached: identity (symbology strings) once via SymbolCache, state per Update().
// Nothing here constructs a Symbology or formats a string inside a binding getter — that was the
// multi-second freeze on grids with thousands of spreads. Typed properties (int?, enum?, Timestamp?)
// sort correctly and feed the column filters; nullable renders as a blank cell.
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
    public string MaturityType { get; private set; } = "";

    public string LongMaturityDate { get; private set; } = "";
    public string LongMaturityType { get; private set; } = "";
    public string ShortMaturityDate { get; private set; } = "";
    public string ShortMaturityType { get; private set; } = "";

    public int? LongInstrumentId { get; private set; }
    public int? ShortInstrumentId { get; private set; }

    public string BaseCurrency { get; private set; } = "";
    public string QuoteCurrency { get; private set; } = "";

    public WidgetInstrumentHeader(int headerId, in InstrumentHeader128 header128)
    {
        InstrumentHeaderId = headerId;
        SymbolCache.SymbolStrings symbols = SymbolCache.Get(headerId);
        Symbol = symbols.Symbol;
        ShortSymbol = symbols.ShortSymbol;
        Recompute(in header128);
    }

    public void Update(in InstrumentHeader128 header128)
    {
        Recompute(in header128);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // Refresh all bindings
    }

    private void Recompute(in InstrumentHeader128 header128)
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
        MaturityDate = ""; MaturityType = "";
        LongMaturityDate = ""; LongMaturityType = "";
        ShortMaturityDate = ""; ShortMaturityType = "";
        LongInstrumentId = null; ShortInstrumentId = null;
        BaseCurrency = ""; QuoteCurrency = "";

        if (instrumentType == Data.InstrumentType.Future)
        {
            ref readonly FutureHeader future = ref _header128.AsFuture();
            Multiplier = future.Multiplier;
            MaturityType = future.MaturityType.ToString();
            MaturityDate = future.MaturityDate.NanosSinceEpoch > 0 ? future.MaturityDate.ToDateString() : "";
        }
        else if (instrumentType == Data.InstrumentType.Spread)
        {
            ref readonly SpreadHeader spread = ref _header128.AsSpread();
            Multiplier = spread.Multiplier;
            LongMaturityType = spread.LongMaturityType.ToString();
            ShortMaturityType = spread.ShortMaturityType.ToString();
            LongMaturityDate = spread.LongMaturityDate.NanosSinceEpoch > 0 ? spread.LongMaturityDate.ToDateString() : "";
            ShortMaturityDate = spread.ShortMaturityDate.NanosSinceEpoch > 0 ? spread.ShortMaturityDate.ToDateString() : "";
            LongInstrumentId = spread.LongInstrumentId == -1 ? null : spread.LongInstrumentId;
            ShortInstrumentId = spread.ShortInstrumentId == -1 ? null : spread.ShortInstrumentId;
        }
        else if (instrumentType == Data.InstrumentType.Forex)
        {
            ref readonly ForexHeader forex = ref _header128.AsForex();
            BaseCurrency = forex.BaseCurrency.ToString();
            QuoteCurrency = forex.QuoteCurrency.ToString();
        }
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
public class InstrumentHeadersFilterState
{
    public string Column { get; set; } = "";
    public string? Pattern { get; set; }
}

[RegisterJson]
public class InstrumentHeadersWidgetState
{
    public List<InstrumentHeadersColumnState> Columns { get; set; } = new();
    public List<InstrumentHeadersFilterState> Filters { get; set; } = new();
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

    // The grid binds a DataGridCollectionView over _allHeaders: its Filter drives row visibility
    // (Refresh() on change, never rebuild a collection) and native header-click sorting comes free.
    private DataGridCollectionView? _view;
    private readonly List<WidgetInstrumentHeader> _allHeaders = new();
    private readonly Dictionary<int, WidgetInstrumentHeader> _headersById = new();

    // One regex filter per column, keyed by the column's base name; rows must pass every active
    // filter (AND). Enum columns filter on their display text like any other — "Future|Spread".
    private sealed class ColumnFilter
    {
        public string? Pattern;
        public Regex? Regex;
        public bool IsEmpty => Regex == null;
    }

    private readonly Dictionary<string, ColumnFilter> _columnFilters = new();
    private readonly Dictionary<DataGridColumn, string> _columnBaseNames = new();

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
        ["MaturityType"] = r => r.MaturityType,
        ["LongMaturityDate"] = r => r.LongMaturityDate,
        ["LongMaturityType"] = r => r.LongMaturityType,
        ["ShortMaturityDate"] = r => r.ShortMaturityDate,
        ["ShortMaturityType"] = r => r.ShortMaturityType,
        ["LongInstrumentId"] = r => r.LongInstrumentId?.ToString() ?? "",
        ["ShortInstrumentId"] = r => r.ShortInstrumentId?.ToString() ?? "",
        ["BaseCurrency"] = r => r.BaseCurrency,
        ["QuoteCurrency"] = r => r.QuoteCurrency,
    };

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

        LoadAllHeaders();
        _view = new DataGridCollectionView(_allHeaders)
        {
            Filter = FilterRow
        };
        HeadersGrid.ItemsSource = _view;

        // Header text gets decorated with the active filter ("ShortSymbol (^es)"), so the stable
        // identity of each column is captured once here — persistence and the filter map key on it.
        foreach (DataGridColumn col in HeadersGrid.Columns)
            _columnBaseNames[col] = col.Header?.ToString() ?? "";

        HeadersGrid.PointerMoved += (s, e) => _lastPointerPos = e.GetPosition(HeadersGrid);

        // Targeted refresh for allocations made through this GUI. The timer diff below only watches
        // Primary.InstrumentIds — in a strategy workspace that is the algo's context, which a
        // GUI-client allocation never touches, so without this the InstrumentId column stays blank.
        _context.Manual.Instrument += OnManualInstrumentAllocated;

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    // Fires on the ManualClient's owner thread once the allocation echo lands — re-read the one
    // header (the server has stamped InstrumentId into shared memory by then) and update that row.
    private void OnManualInstrumentAllocated(Instrument instrument)
    {
        int instrumentHeaderId = instrument.Header.InstrumentHeaderId;
        InstrumentHeader128 header128 = _context.Primary.GetInstrumentHeader(instrumentHeaderId).Read();
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (_headersById.TryGetValue(instrumentHeaderId, out WidgetInstrumentHeader? row))
                row.Update(in header128);
        });
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
        string? clickedColumn = null;
        WidgetInstrumentHeader? row = null;

        var v = visual;
        while (v != null)
        {
            if (v is DataGridColumnHeader columnHeader)
            {
                isHeader = true;
                clickedColumn = columnHeader.Content?.ToString();
                break;
            }
            if (v is DataGridRow dataGridRow)
            {
                row = dataGridRow.DataContext as WidgetInstrumentHeader;
                break;
            }
            v = v.GetVisualParent() as Visual;
        }
        row ??= HeadersGrid.SelectedItem as WidgetInstrumentHeader;

        if (isHeader)
        {
            // Filter section for the column that was right-clicked: a regex over its display text,
            // enum columns included ("Future|Spread"). All active filters AND together.
            clickedColumn = ResolveBaseName(clickedColumn);
            if (clickedColumn != null && s_columnText.ContainsKey(clickedColumn))
            {
                string column = clickedColumn;
                _columnFilters.TryGetValue(column, out ColumnFilter? active);
                var regexItem = new MenuItem
                {
                    Header = active?.Pattern is { Length: > 0 } pattern ? $"Filter {column}: /{pattern}/ …" : $"Filter {column} (regex)…"
                };
                regexItem.Click += async (_, _) => await PromptForRegexFilter(column);
                menu.Items.Add(regexItem);

                if (_columnFilters.ContainsKey(column))
                {
                    var clearItem = new MenuItem { Header = $"Clear {column} filter" };
                    clearItem.Click += (_, _) => ClearColumnFilter(column);
                    menu.Items.Add(clearItem);
                }
                if (_columnFilters.Count > 0)
                {
                    var clearAllItem = new MenuItem { Header = "Clear all filters" };
                    clearAllItem.Click += (_, _) => ClearAllFilters();
                    menu.Items.Add(clearAllItem);
                }
                menu.Items.Add(new Separator());
            }

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
            // Allocating to the GUI client is what makes the server open the instrument's data
            // feed, so any header in this grid can be lit up and laddered from here — in both a
            // strategy workspace and a server workspace. Idempotent if already allocated.
            var allocateItem = new MenuItem
            {
                Header = row != null ? $"Allocate {row.Symbol}" : "Allocate Instrument",
                IsEnabled = row != null
            };
            if (row != null)
            {
                int instrumentHeaderId = row.InstrumentHeaderId;
                allocateItem.Click += (_, _) => _context.Manual.OnAllocateInstrument(instrumentHeaderId);
            }
            menu.Items.Add(allocateItem);
        }
    }

    private bool FilterRow(object item)
    {
        if (item is not WidgetInstrumentHeader row)
            return false;

        foreach (KeyValuePair<string, ColumnFilter> pair in _columnFilters)
        {
            if (!s_columnText.TryGetValue(pair.Key, out Func<WidgetInstrumentHeader, string>? text))
                continue;
            if (pair.Value.Regex != null && !pair.Value.Regex.IsMatch(text(row)))
                return false;
        }
        return true;
    }

    private void ApplyFilter()
    {
        _view?.Refresh();
        UpdateColumnHeaders();
    }

    private string GetBaseName(DataGridColumn col) =>
        _columnBaseNames.TryGetValue(col, out string? name) ? name : col.Header?.ToString() ?? "";

    // Headers show their active filter: "ShortSymbol (^es)", "InstrumentType (Future|Spread)".
    // Display-only — every lookup keys on the base name captured at construction.
    private void UpdateColumnHeaders()
    {
        if (HeadersGrid == null)
            return;
        foreach (DataGridColumn col in HeadersGrid.Columns)
        {
            string baseName = GetBaseName(col);
            string decorated = baseName;
            if (_columnFilters.TryGetValue(baseName, out ColumnFilter? filter))
            {
                if (filter.Pattern is { Length: > 0 })
                    decorated = $"{baseName} ({filter.Pattern})";
            }
            if (!string.Equals(col.Header?.ToString(), decorated, StringComparison.Ordinal))
                col.Header = decorated;
        }
    }

    // A right-clicked header carries the decorated text; resolve it back to the column's identity.
    private string? ResolveBaseName(string? headerText)
    {
        if (headerText == null || s_columnText.ContainsKey(headerText))
            return headerText;
        int suffix = headerText.IndexOf(" (", StringComparison.Ordinal);
        if (suffix > 0 && s_columnText.ContainsKey(headerText[..suffix]))
            return headerText[..suffix];
        return headerText;
    }

    private ColumnFilter GetOrAddFilter(string column)
    {
        if (!_columnFilters.TryGetValue(column, out ColumnFilter? filter))
        {
            filter = new ColumnFilter();
            _columnFilters[column] = filter;
        }
        return filter;
    }

    private void PruneFilter(string column)
    {
        if (_columnFilters.TryGetValue(column, out ColumnFilter? filter) && filter.IsEmpty)
            _columnFilters.Remove(column);
    }

    private void SetRegexFilter(string column, string pattern, Regex regex)
    {
        ColumnFilter filter = GetOrAddFilter(column);
        filter.Pattern = pattern;
        filter.Regex = regex;
        ApplyFilter();
    }

    private void ClearRegexFilter(string column)
    {
        if (_columnFilters.TryGetValue(column, out ColumnFilter? filter))
        {
            filter.Pattern = null;
            filter.Regex = null;
            PruneFilter(column);
        }
        ApplyFilter();
    }

    private void ClearColumnFilter(string column)
    {
        _columnFilters.Remove(column);
        ApplyFilter();
    }

    private void ClearAllFilters()
    {
        _columnFilters.Clear();
        ApplyFilter();
    }

    // Code-built prompt: regex applies on commit only; an invalid pattern shows its parse error and
    // keeps the previous filter untouched.
    private async System.Threading.Tasks.Task PromptForRegexFilter(string column)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        _columnFilters.TryGetValue(column, out ColumnFilter? existing);

        TextBox input = new TextBox { Text = existing?.Pattern ?? "", PlaceholderText = "regex, e.g. ^SR1|ES  (case-insensitive)" };
        TextBlock error = new TextBlock { Foreground = Brushes.Red, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button apply = new Button { Content = "Apply", IsDefault = true };
        Button clear = new Button { Content = "Clear", IsEnabled = existing?.Regex != null };
        Button cancel = new Button { Content = "Cancel", IsCancel = true };

        Window dialog = new Window
        {
            Title = $"Filter {column}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    input,
                    error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, clear, cancel } }
                }
            }
        };

        apply.Click += (_, _) =>
        {
            string pattern = input.Text ?? "";
            if (pattern.Length == 0)
            {
                ClearRegexFilter(column);
                dialog.Close();
                return;
            }
            try
            {
                Regex regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                SetRegexFilter(column, pattern, regex);
                dialog.Close();
            }
            catch (ArgumentException ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        };
        clear.Click += (_, _) => { ClearRegexFilter(column); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => input.Focus();

        await dialog.ShowDialog(owner);
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
        foreach (KeyValuePair<string, ColumnFilter> pair in _columnFilters)
        {
            state.Filters.Add(new InstrumentHeadersFilterState { Column = pair.Key, Pattern = pair.Value.Pattern });
        }
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

            _columnFilters.Clear();
            foreach (InstrumentHeadersFilterState filterState in state.Filters ?? new())
            {
                if (string.IsNullOrEmpty(filterState.Pattern) || !s_columnText.ContainsKey(filterState.Column))
                    continue;
                try
                {
                    SetRegexFilter(filterState.Column, filterState.Pattern,
                        new Regex(filterState.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                catch (ArgumentException) { }
            }
            ApplyFilter();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null)
            _context.Manual.Instrument -= OnManualInstrumentAllocated;
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
    }
}