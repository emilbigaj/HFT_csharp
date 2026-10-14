using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;

namespace Widget;

/// One order's row in the Orders grid: its state and target, refreshed in place by the widget's timer thread. Cold path.
public sealed class WidgetOrder : INotifyPropertyChanged
{
    public ulong ClientOrderId => _orderState.OrderHeader.OrderId;
    public Side Side => _orderState.OrderProfile.Side;
    public OrderStateStatus StateStatus => _orderState.OrderStateStatus;
    public OrderStateReason StateReason => _orderState.OrderStateReason;
    public int StateSeq => _orderState.OrderHeader.Seq;
    public int StateTicks => _orderState.OrderProfile.Ticks;
    public string StatePriceStr => _instrument.TicksToPriceString(StateTicks);
    public int StateQuantityAhead => _orderState.QuantityAhead;
    public int StateQuantityBehind => _orderState.QuantityBehind;

    public int StateQuantity => _orderState.OrderProfile.Quantity;
    public int StateFilled => _orderState.QuantityFilled;
    public int StateWorking => _orderState.WorkingQuantity;

    public OrderTargetAction TargetAction => _orderTarget.OrderTargetAction;
    public OrderStateStatus TargetStatus => _orderTarget.OrderTargetStatus;
    public int TargetSeq => _orderTarget.OrderHeader.Seq;
    public int TargetTicks => _orderTarget.OrderProfile.Ticks;
    public string TargetPriceStr => _instrument.TicksToPriceString(TargetTicks);
    public int TargetQuantity => _orderTarget.OrderProfile.Quantity;

    public string Symbol => _instrument.Symbol;
    public string ShortSymbol => _instrument.ShortSymbol;

    private OrderTarget _orderTarget;
    private OrderState _orderState;
    private readonly Instrument _instrument;

    public ref readonly OrderState OrderState => ref _orderState;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WidgetOrder(Instrument instrument, in OrderState orderState, in OrderTarget orderTarget)
    {
        _instrument = instrument;
        _orderState = orderState;
        _orderTarget = orderTarget;
    }

    /// Takes the order's latest state and target and tells the grid every column changed.
    public void Update(in OrderState orderState, in OrderTarget orderTarget)
    {
        _orderState = orderState;
        _orderTarget = orderTarget;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}

/// Lists the workspace's live orders, sorted by symbol then price, with cancel and amend on right-click and Cancel All
/// in the title bar. The rows are rebuilt on a timer thread and applied on the UI thread. Cold path (GUI).
public sealed partial class OrdersWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;

    private readonly List<WidgetOrder> _activeWidgetOrders = new(64);   // timer thread writes; the UI post reads it before the next pass
    private readonly Dictionary<ulong, WidgetOrder> _widgetOrdersByOrderId = new(64);

    public string Title { get; private set; } = "Orders";
    public ObservableCollection<WidgetOrder> Rows { get; } = new();

    public double DefaultWidth => 900;
    public double DefaultHeight => 300;

    public object? TitleBarContent => Resources["TitleBarButtons"];

    private Avalonia.Point _lastPointerPosition;   // the pointer over the grid, where a right-click menu was asked for

    public OrdersWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Orders (Design)";
    }

    public OrdersWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        OrdersGrid.ItemsSource = Rows;
        OrdersGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(OrdersGrid);
        _refreshTimer = new WidgetRefreshTimer(nameof(OrdersWidget), 100, Refresh);
    }

    /// Updates or adds each live order's row, forgets the orders that are gone, and posts the sorted rows to the grid. Timer thread.
    private void Refresh()
    {
        // Step 1: update or add each live order's row
        _activeWidgetOrders.Clear();
        foreach ((OrderState orderState, OrderTarget orderTarget) in _context.EnumerateOrders())
        {
            ulong orderId = orderState.OrderHeader.OrderId;
            if (_widgetOrdersByOrderId.TryGetValue(orderId, out WidgetOrder? widgetOrder))
                widgetOrder.Update(in orderState, in orderTarget);
            else
            {
                widgetOrder = new WidgetOrder(_context.Primary.GetInstrument(orderState.OrderHeader.OrderId.InstrumentId), in orderState, in orderTarget);
                _widgetOrdersByOrderId[orderId] = widgetOrder;
            }
            _activeWidgetOrders.Add(widgetOrder);
        }

        // Step 2: forget the orders that are gone (Why no copy: Dictionary.Remove does not invalidate its enumerator since .NET Core 3.0)
        foreach (KeyValuePair<ulong, WidgetOrder> widgetOrderByOrderId in _widgetOrdersByOrderId)
        {
            if (!_activeWidgetOrders.Contains(widgetOrderByOrderId.Value))
                _widgetOrdersByOrderId.Remove(widgetOrderByOrderId.Key);
        }

        // Step 3: sort and post to the grid
        _activeWidgetOrders.Sort((leftWidgetOrder, rightWidgetOrder) => DataGridRows.CompareOrders(leftWidgetOrder.Symbol, leftWidgetOrder.StateTicks, rightWidgetOrder.Symbol, rightWidgetOrder.StateTicks));
        Dispatcher.UIThread.Post(() =>
        {
            if (!_refreshTimer.IsDisposed)
                Rows.ReconcileRows(_activeWidgetOrders);
        });
    }

    /// Fills the grid's right-click menu: column toggles on a header, the order's cancel and amend on a row.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        DataGridHit dataGridHit = OrdersGrid.GetDataGridHit(_lastPointerPosition);
        if (dataGridHit.IsColumnHeader)
            contextMenu.AddColumnVisibilityMenuItems(OrdersGrid);
        else if (dataGridHit.RowItem is WidgetOrder widgetOrder)
        {
            contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem($"{widgetOrder.ShortSymbol} | Px: {widgetOrder.StatePriceStr} Qty: {widgetOrder.StateQuantity}"));
            contextMenu.Items.Add(new Separator());
            contextMenu.AddOrderMenuItems(this, _context, in widgetOrder.OrderState);
        }
        else
            contextMenu.Items.Add(new MenuItem { Header = "No Selection", IsEnabled = false });
    }

    /// Cancels every order the grid shows. Why Rows: only the UI thread writes it, so a click mid-refresh sees a whole list
    public void OnCancelAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (WidgetOrder widgetOrder in Rows)
        {
            OrderTarget cancelOrderTarget = widgetOrder.OrderState.NewCancel();
            _context.Manual.OnOrderTarget(ref cancelOrderTarget);
        }
    }

    public string? SaveStateJson() => DataGridWidgetState.NewStateJson(OrdersGrid);

    public void LoadStateJson(string? json) => DataGridWidgetState.LoadStateJson(OrdersGrid, json);

    public void Dispose() => _refreshTimer?.Dispose();
}
