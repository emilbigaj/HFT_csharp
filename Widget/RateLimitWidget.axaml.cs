using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;


public sealed class WidgetRateLimit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private RollingRateLimit _rollingRateLimit;
    private string _coreGroupName;
    private int _count;

    // The row's id is a CoreGroupId; the name comes from the server's CoreGroups array.
    public string CoreGroup => _coreGroupName;
    public string Duration => _rollingRateLimit.RateLimit.Duration.TotalSeconds.ToString("0.###") + " s";
    public string Limit => _rollingRateLimit.RateLimit.Limit.ToString("N0");
    public string Count => _count.ToString("N0");

    public WidgetRateLimit(in RollingRateLimit rollingRateLimit, string coreGroupName, int count)
    {
        _rollingRateLimit = rollingRateLimit;
        _coreGroupName = coreGroupName;
        _count = count;
    }

    /// <summary>
    /// Pull current values from shared memory. The count is computed by the caller at its own clock,
    /// so it decays between sends. Returns true if anything changed and bindings should be re-evaluated.
    /// </summary>
    public bool Refresh(in RollingRateLimit newRollingRateLimit, string newCoreGroupName, int newCount)
    {
        bool changed = newCount != _count || newRollingRateLimit.RateLimit != _rollingRateLimit.RateLimit || newCoreGroupName != _coreGroupName;

        _rollingRateLimit = newRollingRateLimit;
        _coreGroupName = newCoreGroupName;
        _count = newCount;

        if (changed)
        {
            // Refresh all bindings
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        return changed;
    }
}


[RegisterJson]
public class RateLimitColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }
}

[RegisterJson]
public class RateLimitWidgetState
{
    public List<RateLimitColumnState> Columns { get; set; } = new();
}


public sealed partial class RateLimitWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    private readonly Dictionary<int, WidgetRateLimit> _rowsByRateLimitId = new();
    public ObservableCollection<WidgetRateLimit> Rows { get; } = new();

    public string TypeKey => "RateLimitWidget";
    public string Title { get; private set; } = "Rate Limits";
    public double DefaultWidth => 420;
    public double DefaultHeight => 200;

    private Avalonia.Point _lastPointerPos;

    public RateLimitWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Rate Limits (Design)";
    }

    public RateLimitWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        RateLimitGrid.ItemsSource = Rows;

        RateLimitGrid.PointerMoved += (_, e) => _lastPointerPos = e.GetPosition(RateLimitGrid);

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            // One row per CoreGroup the server has written; EnumerateRateLimits walks the set bits. Key rows
            // by RateLimitId and refresh in place. Count is derived here, at this process's clock, from the
            // ring the server shares: it is never published as a number, or it would freeze between sends.
            Timestamp now = Clock.Now;
            List<WidgetRateLimit> active = new List<WidgetRateLimit>(_rowsByRateLimitId.Count);
            foreach (RollingRateLimit rollingRateLimit in _context.Primary.EnumerateRateLimits())
            {
                int count = rollingRateLimit.GetCount(now);
                string coreGroupName = _context.Primary.GetCoreGroup(rollingRateLimit.RateLimit.RateLimitId).Read().CoreGroupName.ToString();
                if (_rowsByRateLimitId.TryGetValue(rollingRateLimit.RateLimit.RateLimitId, out WidgetRateLimit? row))
                {
                    row.Refresh(in rollingRateLimit, coreGroupName, count);
                }
                else
                {
                    row = new WidgetRateLimit(in rollingRateLimit, coreGroupName, count);
                    _rowsByRateLimitId[rollingRateLimit.RateLimit.RateLimitId] = row;
                }
                active.Add(row);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                // Remove rows for CoreGroups that are no longer present.
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
            Console.WriteLine($"RateLimitWidget Error: {ex.Message}");
        }
        finally
        {
            if (!_disposed) _refreshTimer.Start();
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || RateLimitGrid == null) return;

        menu.Items.Clear();

        // Header cell hit-test → show column-toggle list.
        var visual = RateLimitGrid.InputHitTest(_lastPointerPos) as Visual;
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
            foreach (DataGridColumn col in RateLimitGrid.Columns)
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
    }

    public string? SaveStateJson()
    {
        var state = new RateLimitWidgetState();
        foreach (var col in RateLimitGrid.Columns)
        {
            state.Columns.Add(new RateLimitColumnState
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
            var state = Json.Deserialize<RateLimitWidgetState>(json);
            if (state == null) return;

            foreach (var colState in state.Columns)
            {
                var col = RateLimitGrid.Columns.FirstOrDefault(c => (c.Header?.ToString() ?? "") == colState.Header);
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
