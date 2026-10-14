using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;


/// One CoreGroup's rate-limit grid row: its window, its limit and the messages counted in the window. Refreshed on the
/// widget's refresh timer thread; cold path.
public sealed class WidgetRateLimit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private RollingRateLimit _rollingRateLimit;
    private string _coreGroupName = "";
    private int _count;

    // The row's id is a CoreGroupId; the name comes from the server's CoreGroups array.
    public string CoreGroup => _coreGroupName;
    public string Duration => _rollingRateLimit.RateLimit.Duration.TotalSeconds.ToString("0.###") + " s";
    public string Limit => _rollingRateLimit.RateLimit.Limit.ToString("N0");
    public string Count => _count.ToString("N0");

    /// Takes the CoreGroup's latest rate limit from shared memory, and the count the caller computed at its own clock (so it decays between sends), and re-evaluates every binding.
    public void Refresh(in RollingRateLimit rollingRateLimit, string coreGroupName, int count)
    {
        _rollingRateLimit = rollingRateLimit;
        _coreGroupName = coreGroupName;
        _count = count;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));   // null: every property
    }
}



/// Lists the server's rate limits, one per CoreGroup, refreshed every 100 ms. The right-click menu toggles columns on a header.
/// UI thread and the refresh timer's thread; cold path.
public sealed partial class RateLimitWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;

    private readonly Dictionary<int, WidgetRateLimit> _rowsByRateLimitId = new();   // refresh timer thread only
    public ObservableCollection<WidgetRateLimit> Rows { get; } = new();

    public string Title { get; private set; } = "Rate Limits";
    public double DefaultWidth => 420;
    public double DefaultHeight => 200;

    private Avalonia.Point _lastPointerPosition;

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

        RateLimitGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(RateLimitGrid);

        _refreshTimer = new WidgetRefreshTimer(nameof(RateLimitWidget), 100, Refresh);
    }

    /// Refreshes each CoreGroup's row in place and posts the active rows to the grid.
    private void Refresh()
    {
        // Step 1: one row per CoreGroup the server has written. Why the count is computed here, at this process's clock, from the ring the server shares: a published number would freeze between sends
        Timestamp now = Clock.Now;
        List<WidgetRateLimit> activeRows = new(_rowsByRateLimitId.Count);
        foreach (RollingRateLimit rollingRateLimit in _context.Primary.EnumerateRateLimits())
        {
            int rateLimitId = rollingRateLimit.RateLimit.RateLimitId;
            int count = rollingRateLimit.GetCount(now);
            string coreGroupName = _context.Primary.GetCoreGroup(rateLimitId).Read().CoreGroupName.ToString();
            if (!_rowsByRateLimitId.TryGetValue(rateLimitId, out WidgetRateLimit? widgetRateLimit))
            {
                widgetRateLimit = new WidgetRateLimit();
                _rowsByRateLimitId[rateLimitId] = widgetRateLimit;
            }
            widgetRateLimit.Refresh(in rollingRateLimit, coreGroupName, count);
            activeRows.Add(widgetRateLimit);
        }

        // Step 2: the grid shows exactly the active rows
        Dispatcher.UIThread.Post(() =>
        {
            if (!_refreshTimer.IsDisposed)
                Rows.ReconcileRows(activeRows);
        });
    }

    /// Fills the grid's right-click menu: column toggles on a header, nothing elsewhere.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();
        if (RateLimitGrid.GetDataGridHit(_lastPointerPosition).IsColumnHeader)
            contextMenu.AddColumnVisibilityMenuItems(RateLimitGrid);
    }

    public string? SaveStateJson() => DataGridWidgetState.NewStateJson(RateLimitGrid);

    public void LoadStateJson(string? json) => DataGridWidgetState.LoadStateJson(RateLimitGrid, json);

    public void Dispose() => _refreshTimer?.Dispose();
}
