using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;


public sealed class WidgetRiskLimit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Instrument _instrument;
    private RiskLimit _riskLimit;
    private int _positionQuantity;

    public string Symbol => _instrument.Symbol;
    public string ShortSymbol => _instrument.ShortSymbol;
    public int InstrumentId => _instrument.InstrumentId;
    public string RiskLimitFileName => _instrument.Symbol + ".risklimit";

    public int PositionQuantity => _positionQuantity;
    public int MaxOrderQuantity => _riskLimit.MaxOrderQuantity;
    public int MaxPositionQuantity => _riskLimit.MaxPositionQuantity;

    public int LongQuantityAllowance => _riskLimit.GetLongQuantityAllowance(_positionQuantity);
    public int ShortQuantityAllowance => _riskLimit.GetShortQuantityAllowance(_positionQuantity);

    public int WorstLongWorkingQuantity => _riskLimit.WorstLongWorkingQuantity;
    public int WorstShortWorkingQuantity => _riskLimit.WorstShortWorkingQuantity;

    public string StrategyId => _riskLimit.StrategyId < 0 ? "Server" : _riskLimit.StrategyId.ToString();
    public string Timestamp => _riskLimit.Timestamp == Tools.Timestamp.MinValue ? "—" : _riskLimit.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    public WidgetRiskLimit(Instrument instrument, int positionQuantity, RiskLimit riskLimit)
    {
        _instrument = instrument;
        _riskLimit = riskLimit;
        _positionQuantity = positionQuantity;
    }

    /// <summary>
    /// Pull current values from shared memory + position.
    /// Returns true if anything changed and bindings should be re-evaluated.
    /// </summary>
    public bool Refresh(in RiskLimit newRiskLimit, int newPositionQuantity)
    {
        bool changed =
            !RiskLimitEquals(_riskLimit, newRiskLimit) ||
            _positionQuantity != newPositionQuantity;

        _riskLimit = newRiskLimit;
        _positionQuantity = newPositionQuantity;

        if (changed)
        {
            // Refresh all bindings
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        return changed;
    }

    private static bool RiskLimitEquals(in RiskLimit a, in RiskLimit b)
    {
        return a.MaxOrderQuantity == b.MaxOrderQuantity
            && a.MaxPositionQuantity == b.MaxPositionQuantity
            && a.StrategyId == b.StrategyId
            && a.Timestamp == b.Timestamp
            && a.WorstLongWorkingQuantity == b.WorstLongWorkingQuantity
            && a.WorstShortWorkingQuantity == b.WorstShortWorkingQuantity;
    }

    private static string FormatDuration(Duration d)
    {
        double seconds = d.TotalSeconds;
        if (seconds <= 0) return "—";
        if (seconds < 1) return $"{d.TotalMilliseconds:N0}ms";
        if (seconds < 60) return $"{seconds:0.##}s";
        if (seconds < 3600) return $"{seconds / 60:0.##}m";
        return $"{seconds / 3600:0.##}h";
    }
}


[RegisterJson]
public class RiskLimitsColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }
}

[RegisterJson]
public class RiskLimitsWidgetState
{
    public List<RiskLimitsColumnState> Columns { get; set; } = new();
}


public sealed partial class RiskLimitsWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    private readonly Dictionary<int, WidgetRiskLimit> _rowsByInstrumentId = new();
    public ObservableCollection<WidgetRiskLimit> Rows { get; } = new();

    public string TypeKey => "RiskLimitsWidget";
    public string Title { get; private set; } = "Risk Limits";
    public double DefaultWidth => 820;
    public double DefaultHeight => 320;

    private Avalonia.Point _lastPointerPos;

    public RiskLimitsWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Risk Limits (Design)";
    }

    public RiskLimitsWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        RiskLimitsGrid.ItemsSource = Rows;

        RiskLimitsGrid.PointerMoved += (_, e) => _lastPointerPos = e.GetPosition(RiskLimitsGrid);

        _refreshTimer = new Timer(250);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    // Both read the server row, never Primary: limits are server-wide and RiskLayer gates them on the server position.
    private static RiskLimit GetRiskLimit(int instrumentId) => ContextManager.ServerContext.GetRiskLimit(instrumentId).Read();

    private static int GetPositionQuantity(int instrumentId) => ContextManager.ServerContext.GetPosition(instrumentId).Profit.Quantity;

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            Bitset64 subscribed = _context.Primary.InstrumentIds;

            // Add brand-new instruments to the dict (timer thread only owns the dict).
            foreach (int instrumentId in subscribed)
            {
                if (!_rowsByInstrumentId.ContainsKey(instrumentId))
                {
                    try
                    {
                        Instrument instrument = _context.Primary.GetInstrument(instrumentId);
                        int positionQuantity = GetPositionQuantity(instrumentId);
                        RiskLimit riskLimit = GetRiskLimit(instrumentId);
                        _rowsByInstrumentId[instrumentId] = new WidgetRiskLimit(instrument, positionQuantity, riskLimit);
                    }
                    catch
                    {
                        // Instrument not fully ready yet; try next tick.
                    }
                }
            }

            // Snapshot the active set + refresh in-place (PropertyChanged is safe to fire from any thread for ObservableCollection items).
            List<WidgetRiskLimit> active = new List<WidgetRiskLimit>(_rowsByInstrumentId.Count);
            foreach (var kvp in _rowsByInstrumentId)
            {
                int instrumentId = kvp.Key;
                WidgetRiskLimit row = kvp.Value;
                if (!subscribed[instrumentId]) continue;

                try
                {
                    RiskLimit current = GetRiskLimit(instrumentId);
                    int positionQty = GetPositionQuantity(instrumentId);
                    row.Refresh(in current, positionQty);
                }
                catch
                {
                    // Skip refresh of this row this tick.
                }

                active.Add(row);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                // Remove rows for instruments that are no longer subscribed.
                for (int i = Rows.Count - 1; i >= 0; i--)
                {
                    if (!active.Contains(Rows[i]))
                    {
                        Rows.RemoveAt(i);
                    }
                }

                // Add newly active rows.
                foreach (var r in active)
                {
                    if (!Rows.Contains(r))
                    {
                        Rows.Add(r);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RiskLimitsWidget Error: {ex.Message}");
        }
        finally
        {
            if (!_disposed) _refreshTimer.Start();
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || RiskLimitsGrid == null) return;

        menu.Items.Clear();

        // Header cell hit-test → show column-toggle list.
        var visual = RiskLimitsGrid.InputHitTest(_lastPointerPos) as Visual;
        bool isHeader = false;
        DataGridRow? hitRow = null;

        var v = visual;
        while (v != null)
        {
            if (v is DataGridColumnHeader)
            {
                isHeader = true;
                break;
            }
            if (v is DataGridRow row)
            {
                hitRow = row;
            }
            v = v.GetVisualParent() as Visual;
        }
        ServerContext serverContext = ContextManager.ServerContext;

        if (isHeader)
        {
            foreach (DataGridColumn col in RiskLimitsGrid.Columns)
            {
                string header = col.Header?.ToString() ?? "Column";
                MenuItem item = new MenuItem
                {
                    Header = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = col.IsVisible
                };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
        else if (hitRow != null && hitRow.DataContext is WidgetRiskLimit riskLimit)
        {
            var headerItem = new MenuItem { Header = riskLimit.ShortSymbol, FontWeight = Avalonia.Media.FontWeight.Bold, IsEnabled = false };
            menu.Items.Add(headerItem);
            menu.Items.Add(new Separator());

            var editItem = new MenuItem { Header = "Edit RiskLimit", Icon = new TextBlock { Text = "✏" } };
            editItem.Click += async (_, _) => await EditRiskLimit(riskLimit);
            menu.Items.Add(editItem);
            menu.Items.Add(new Separator());

            FileSystemPath filePath = Context.GetRiskLimitsFilePath(serverContext.RiskLimitsDirectoryPath, riskLimit.Symbol);
            var copyPathItem = new MenuItem { Header = "Copy Server .risklimit File Path", Icon = new TextBlock { Text = "📋" } };
            copyPathItem.Click += async (_, _) =>
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(filePath);
                }
                catch { }
            };
            menu.Items.Add(copyPathItem);

            var openFolderItem = new MenuItem { Header = "Open Server RiskLimits Folder", Icon = new TextBlock { Text = "📁" } };
            openFolderItem.Click += (_, _) => OpenInExplorer(serverContext.RiskLimitsDirectoryPath.ToString());
            menu.Items.Add(openFolderItem);
        }
        else
        {
            var openFolderItem = new MenuItem { Header = "Open Server RiskLimits Folder", Icon = new TextBlock { Text = "📁" } };
            openFolderItem.Click += (_, _) => OpenInExplorer(serverContext.RiskLimitsDirectoryPath.ToString());
            menu.Items.Add(openFolderItem);
        }
    }

    private async System.Threading.Tasks.Task EditRiskLimit(WidgetRiskLimit row)
    {
        try
        {
            Window? window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) return;

            // Read a fresh copy rather than using the row's cached value, which can be up to one
            // refresh tick stale. The whole struct goes back to the server, so every field we are
            // not editing has to be current or we would silently revert it.
            RiskLimit current = GetRiskLimit(row.InstrumentId);

            RiskLimitEditDialog dialog = new RiskLimitEditDialog(row.ShortSymbol, current);
            RiskLimit? edited = await dialog.ShowDialog<RiskLimit?>(window);
            if (edited == null) return;

            // The server owns _riskLimits and the .risklimit file; it stamps the timestamp, writes
            // shared memory and appends the line. The grid picks the change up on its next refresh.
            RiskLimit riskLimit = edited.Value;
            _context.Manual.OnRiskLimit(in riskLimit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RiskLimitsWidget.EditRiskLimit Error: {ex.Message}");
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return;
            }

            string opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = opener,
                ArgumentList = { path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RiskLimitsWidget.OpenInExplorer Error: {ex.Message}");
        }
    }

    public string? SaveStateJson()
    {
        var state = new RiskLimitsWidgetState();
        foreach (var col in RiskLimitsGrid.Columns)
        {
            state.Columns.Add(new RiskLimitsColumnState
            {
                Header = col.Header?.ToString() ?? "",
                Width = col.Width.Value,
                DisplayIndex = col.DisplayIndex,
                IsVisible = col.IsVisible
            });
        }
        return Json.Serialize(state);
    }

    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var state = Json.Deserialize<RiskLimitsWidgetState>(json);
            if (state == null) return;

            foreach (var colState in state.Columns)
            {
                var col = RiskLimitsGrid.Columns.FirstOrDefault(c => (c.Header?.ToString() ?? "") == colState.Header);
                if (col != null)
                {
                    col.Width = new DataGridLength(colState.Width);
                    col.DisplayIndex = colState.DisplayIndex;
                    col.IsVisible = colState.IsVisible;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
    }
}