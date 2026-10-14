//BEGIN_FILE HFT/Widget/Widget.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// <summary>
/// Interface for the container window hosting widgets.
/// Allows widgets to request actions (like adding new widgets) without referencing the Window class directly.
/// </summary>
public interface IWidgetHost
{
    void AddWidget(IWidget widget);
}

/// <summary>
/// Interface that all workspace widgets implement so hosts can manage them.
/// </summary>
public interface IWidget
{
    // The widget's type in a saved layout: its class name. Renaming a widget class orphans the layouts that saved it
    string TypeKey => GetType().Name;
    string Title { get; }

    // Static default dimensions for the widget type
    double DefaultWidth { get; }
    double DefaultHeight { get; }

    string? SaveStateJson();
    void LoadStateJson(string? json);

    /// <summary>
    /// Allows the widget to provide custom menu items for the container's title bar context menu.
    /// Default implementation returns empty list.
    /// </summary>
    IEnumerable<MenuItem> GetTitleBarMenuItems() => Enumerable.Empty<MenuItem>();

    /// <summary>
    /// Optional UI content to inject directly into the WidgetContainer's Title Bar.
    /// </summary>
    object? TitleBarContent => null;
}

/// <summary>
/// Serialized widget layout: type + geometry + custom JSON state.
/// </summary>
[RegisterJson]
public sealed class WidgetLayout
{
    public string TypeKey { get; set; } = "";
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? StateJson { get; set; }
}

/// Puts a widget into the Workspace window that holds a control: a widget opening another (order entry, a ladder).
public static class WidgetHost
{
    /// Adds the widget to the IWidgetHost window holding this control; logs and drops it when there is none.
    public static void AddToHost(this Control control, IWidget widget)
    {
        if (control.FindAncestorOfType<Window>() is IWidgetHost widgetHost)
            widgetHost.AddWidget(widget);
        else
            Console.WriteLine($"{control.GetType().Name}: not inside a Workspace window, {widget.TypeKey} not opened");
    }

    /// Opens a context menu of these items on the control (a title bar's right-click); nothing when there are none.
    public static void OpenMenu(this Control control, IEnumerable<MenuItem> menuItems)
    {
        ContextMenu contextMenu = new();
        foreach (MenuItem menuItem in menuItems)
            contextMenu.Items.Add(menuItem);
        if (contextMenu.Items.Count == 0)
            return;
        control.ContextMenu = contextMenu;
        contextMenu.Open(control);
    }
}

/// A widget's refresh loop: refresh runs on a timer thread every interval, one pass at a time, and the next pass waits until
/// the UI thread has run what the last one posted, so a pass never rewrites state a pending post still reads. Cold path.
public sealed class WidgetRefreshTimer : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Action _refresh;
    private readonly string _ownerName;   // names the widget in a logged failure
    private string? _lastFailureMessage;  // the last failure logged; the same one again is not logged until a pass succeeds (timer thread)

    public bool IsDisposed { get; private set; }

    public WidgetRefreshTimer(string ownerName, double intervalMilliseconds, Action refresh, bool isStarted = true)
    {
        _ownerName = ownerName;
        _refresh = refresh;
        _timer = new System.Timers.Timer(intervalMilliseconds) { AutoReset = false };
        _timer.Elapsed += OnElapsed;
        if (isStarted)
            _timer.Start();
    }

    /// Starts the loop (a widget that waits for its instrument starts it then); no effect once disposed.
    public void Start()
    {
        if (!IsDisposed)
            _timer.Start();
    }

    private void OnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (IsDisposed)
            return;
        // Why the catch: one failed pass must not stop the widget refreshing; the next pass retries
        try
        {
            _refresh();
            _lastFailureMessage = null;
        }
        catch (Exception exception)
        {
            // Why only a new message: a failure repeating every pass would flood the console ten times a second; it shows once, and again after a success
            if (exception.Message != _lastFailureMessage)
                Console.WriteLine($"{_ownerName}.Refresh: {exception.Message}");
            _lastFailureMessage = exception.Message;
        }
        // Why posted: posts run in order, so the next pass starts only after the UI thread has applied this one
        Dispatcher.UIThread.Post(Start);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}

/// What a point on a DataGrid is over: a column header (with its text), a row (with its item), or neither.
public readonly record struct DataGridHit(bool IsColumnHeader, string? ColumnHeaderText, object? RowItem);

/// Grid helpers shared by the widgets: hit-testing, keeping rows in step, paging, ordering. UI thread, cold path.
public static class DataGridRows
{
    /// The header or row under this point (the nearest one up the visual tree), or neither.
    public static DataGridHit GetDataGridHit(this DataGrid dataGrid, Point point)
    {
        for (Visual? visual = dataGrid.InputHitTest(point) as Visual; visual != null; visual = visual.GetVisualParent() as Visual)
        {
            if (visual is DataGridColumnHeader dataGridColumnHeader)
                return new DataGridHit(IsColumnHeader: true, dataGridColumnHeader.Content?.ToString(), RowItem: null);
            if (visual is DataGridRow dataGridRow)
                return new DataGridHit(IsColumnHeader: false, ColumnHeaderText: null, dataGridRow.DataContext);
        }
        return default;
    }

    /// Makes the rows equal activeRows, in activeRows' order, removing, moving and inserting only what differs.
    public static void ReconcileRows<T>(this ObservableCollection<T> rows, IReadOnlyList<T> activeRows) where T : class
    {
        // Step 1: the rows that left
        for (int rowIndex = rows.Count - 1; rowIndex >= 0; rowIndex--)
        {
            if (!activeRows.Contains(rows[rowIndex]))
                rows.RemoveAt(rowIndex);
        }

        // Step 2: each active row into its place
        for (int activeRowIndex = 0; activeRowIndex < activeRows.Count; activeRowIndex++)
        {
            T activeRow = activeRows[activeRowIndex];
            if (activeRowIndex == rows.Count)
                rows.Add(activeRow);
            else if (!ReferenceEquals(rows[activeRowIndex], activeRow))
            {
                int previousRowIndex = rows.IndexOf(activeRow);
                if (previousRowIndex >= 0)
                    rows.Move(previousRowIndex, activeRowIndex);
                else
                    rows.Insert(activeRowIndex, activeRow);
            }
        }
    }

    /// True when the grid is scrolled to within rowsFromEnd rows of its end: time to page in more.
    public static bool IsScrolledNearEnd(this DataGrid dataGrid, int rowCount, int rowsFromEnd = 5)
    {
        ScrollBar? verticalScrollBar = dataGrid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(scrollBar => scrollBar.Orientation == Orientation.Vertical);
        if (verticalScrollBar == null || verticalScrollBar.Maximum <= 0)
            return false;
        double scrollPerRow = verticalScrollBar.Maximum / Math.Max(1, rowCount);
        return verticalScrollBar.Value >= verticalScrollBar.Maximum - scrollPerRow * rowsFromEnd;
    }

    /// The order lists' order: by symbol, then by price in ticks, highest first.
    public static int CompareOrders(string leftSymbol, int leftTicks, string rightSymbol, int rightTicks)
    {
        int symbolComparison = string.CompareOrdinal(leftSymbol, rightSymbol);
        return symbolComparison != 0 ? symbolComparison : rightTicks.CompareTo(leftTicks);
    }
}

/// Context-menu items the widgets share. UI thread, cold path.
public static class WidgetMenus
{
    /// An item with an icon glyph that runs onClick.
    public static MenuItem NewMenuItem(string header, string iconText, Action onClick, bool isEnabled = true)
    {
        MenuItem menuItem = new() { Header = header, Icon = new TextBlock { Text = iconText }, IsEnabled = isEnabled };
        menuItem.Click += (_, _) => onClick();
        return menuItem;
    }

    /// A menu's bold, unclickable first line naming what was right-clicked.
    public static MenuItem NewTitleMenuItem(string header) => new() { Header = header, FontWeight = FontWeight.Bold, IsEnabled = false };

    /// One checkbox item per column of the grid, toggling whether it shows.
    public static void AddColumnVisibilityMenuItems(this ContextMenu contextMenu, DataGrid dataGrid)
    {
        foreach (DataGridColumn dataGridColumn in dataGrid.Columns)
        {
            MenuItem menuItem = new() { Header = dataGridColumn.Header?.ToString() ?? "Column", ToggleType = MenuItemToggleType.CheckBox, IsChecked = dataGridColumn.IsVisible };
            menuItem.Click += (_, _) => dataGridColumn.IsVisible = !dataGridColumn.IsVisible;
            contextMenu.Items.Add(menuItem);
        }
    }

    /// Cancel Order, and Amend Order unless an algo owns the order (the GUI may only cancel an algo's orders).
    public static void AddOrderMenuItems(this ContextMenu contextMenu, Control control, WorkspaceContext workspaceContext, in OrderState orderState)
    {
        OrderState clickedOrderState = orderState;
        contextMenu.Items.Add(NewMenuItem("Cancel Order", "❌", () =>
        {
            OrderTarget cancelOrderTarget = clickedOrderState.NewCancel();
            workspaceContext.Manual.OnOrderTarget(ref cancelOrderTarget);
        }));
        if (!clickedOrderState.OrderHeader.OrderId.IsAlgoOrder())
            contextMenu.Items.Add(NewMenuItem("Amend Order", "📝", () => control.AddToHost(SendOrderWidget.NewAmendOrder(workspaceContext, clickedOrderState.OrderHeader.OrderId))));
    }

    /// An item that copies a path to the clipboard.
    public static MenuItem NewCopyPathMenuItem(Control control, string header, string path) => NewMenuItem(header, "📋", async () =>
    {
        // Why the catch: a clipboard the OS refuses changes nothing; best-effort
        try
        {
            if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(path);
        }
        catch { }
    });

    /// An item that opens a folder in the file manager.
    public static MenuItem NewOpenFolderMenuItem(string header, string directoryPath) => NewMenuItem(header, "📁", () => OpenInExplorer(directoryPath));

    /// Opens a folder in the file manager (Explorer, Finder, or xdg-open); logs and does nothing when it fails or the folder does not exist.
    public static void OpenInExplorer(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
                return;
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = directoryPath, UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open", ArgumentList = { directoryPath }, UseShellExecute = false });
        }
        catch (Exception exception)
        {
            Console.WriteLine($"WidgetMenus.OpenInExplorer({directoryPath}): {exception.Message}");
        }
    }
}

/// One DataGrid column's saved layout: its width with the width's kind (Auto, Pixel, Star), its position and visibility,
/// matched by header on load. Cold path: a workspace's save and load.
[RegisterJson]
public sealed class DataGridColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public DataGridLengthUnitType? WidthUnit { get; set; }   // null in files saved before 2026-10-14, which kept only the number (see ApplyDataGridColumnStates)
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }

    /// The header a column is saved under unless its widget names it otherwise: its header text.
    public static string GetHeader(DataGridColumn dataGridColumn) => dataGridColumn.Header?.ToString() ?? "";

    /// Each column's layout, under the header getHeader names it by.
    public static List<DataGridColumnState> NewDataGridColumnStates(DataGrid dataGrid, Func<DataGridColumn, string> getHeader) =>
        dataGrid.Columns.Select(dataGridColumn => new DataGridColumnState
        {
            Header = getHeader(dataGridColumn),
            Width = dataGridColumn.Width.Value,
            WidthUnit = dataGridColumn.Width.UnitType,
            DisplayIndex = dataGridColumn.DisplayIndex,
            IsVisible = dataGridColumn.IsVisible,
        }).ToList();

    /// Gives each column whose header getHeader names a saved layout that layout; the others keep their defaults.
    public static void ApplyDataGridColumnStates(DataGrid dataGrid, IEnumerable<DataGridColumnState> dataGridColumnStates, Func<DataGridColumn, string> getHeader)
    {
        foreach (DataGridColumnState dataGridColumnState in dataGridColumnStates)
        {
            DataGridColumn? dataGridColumn = dataGrid.Columns.FirstOrDefault(column => getHeader(column) == dataGridColumnState.Header);
            if (dataGridColumn == null)
                continue;
            // Why: a file saved before 2026-10-14 has no WidthUnit; its Auto columns were saved as 1 and hand-sized ones as their pixels
            DataGridLengthUnitType widthUnit = dataGridColumnState.WidthUnit ?? (dataGridColumnState.Width > 1 ? DataGridLengthUnitType.Pixel : DataGridLengthUnitType.Auto);
            dataGridColumn.Width = new DataGridLength(dataGridColumnState.Width, widthUnit);
            dataGridColumn.DisplayIndex = dataGridColumnState.DisplayIndex;
            dataGridColumn.IsVisible = dataGridColumnState.IsVisible;
        }
    }
}

/// The saved state of a widget whose state is its grid's columns only (Orders, Message Efficiency, Rate Limit, Risk Limits).
[RegisterJson]
public sealed class DataGridWidgetState
{
    public List<DataGridColumnState> Columns { get; set; } = new();

    /// The grid's columns as a widget's state JSON.
    public static string NewStateJson(DataGrid dataGrid) =>
        Json.Serialize(new DataGridWidgetState { Columns = DataGridColumnState.NewDataGridColumnStates(dataGrid, DataGridColumnState.GetHeader) });

    /// Applies a widget's state JSON to its grid's columns. Why the catch: a layout file that no longer parses must not stop the widget opening; it keeps its defaults
    public static void LoadStateJson(DataGrid dataGrid, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            DataGridWidgetState? dataGridWidgetState = Json.Deserialize<DataGridWidgetState>(json);
            if (dataGridWidgetState?.Columns != null)
                DataGridColumnState.ApplyDataGridColumnStates(dataGrid, dataGridWidgetState.Columns, DataGridColumnState.GetHeader);
        }
        catch { }
    }
}
//END_FILE HFT/Widget/Widget.cs