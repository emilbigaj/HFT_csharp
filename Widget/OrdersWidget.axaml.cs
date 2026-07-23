using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Provider;
using Execution;
using Tools;
using Data;
using Avalonia;

namespace Widget;

public sealed class WidgetOrder : INotifyPropertyChanged
{
    public ulong ClientOrderId => _orderState.OrderHeader.OrderId;
    public Side Side => _orderState.OrderProfile.Side;
    public OrderStateStatus StateStatus => _orderState.OrderStateStatus;
    public int StateSeq => _orderState.OrderHeader.Seq;
    public int StateTicks => _orderState.OrderProfile.Ticks;
    public string StatePriceStr => _instrument.TicksToPrice(StateTicks).ToString(_priceFormat);
    public int StateQuantityAhead => _orderState.QuantityAhead;

    public int StateQuantity => _orderState.OrderProfile.Quantity;
    public int StateFilled => _orderState.QuantityFilled;
    public int StateWorking => _orderState.OrderProfile.Quantity - _orderState.QuantityFilled;

    public OrderTargetAction TargetAction => _orderTarget.OrderTargetAction;
    public OrderStateStatus TargetStatus => _orderTarget.OrderTargetStatus;
    public int TargetSeq => _orderTarget.OrderHeader.Seq;
    public int TargetTicks => _orderTarget.OrderProfile.Ticks;
    public string TargetPriceStr => _instrument.TicksToPrice(TargetTicks).ToString(_priceFormat);


    public int TargetQuantity => _orderTarget.OrderProfile.Quantity;

    public int InstrumentId => _instrument.InstrumentId;
    public string Symbol => _instrument.Symbol;
    public string ShortSymbol => _instrument.ShortSymbol;

    public int Ticks => _orderState.OrderProfile.Ticks;
    public int Quantity => _orderState.OrderProfile.Quantity;

    private OrderTarget _orderTarget;
    private OrderState _orderState;
    private readonly Instrument _instrument;

    public ref readonly OrderState OrderState => ref _orderState;
    public ref readonly OrderTarget OrderTarget => ref _orderTarget;

    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly string _priceFormat;

    public WidgetOrder(Instrument instrument, in OrderState orderState, in OrderTarget orderTarget)
    {
        _instrument = instrument;
        _orderState = orderState;
        _orderTarget = orderTarget;
        _priceFormat = $"N{instrument.TicKDecimals}";
    }

    public void Update(in OrderState state, in OrderTarget target)
    {
        _orderState = state;
        _orderTarget = target;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}

[RegisterJson]
public class OrdersWidgetColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }
}

[RegisterJson]
public class OrdersWidgetState
{
    public List<OrdersWidgetColumnState> Columns { get; set; } = new();
}

public sealed partial class OrdersWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    private readonly List<WidgetOrder> _active = new(64);
    private readonly List<ulong> _done = new(16);
    private readonly Dictionary<ulong, WidgetOrder> _orders = new(64);

    public string TypeKey => "OrdersWidget";
    public string Title { get; private set; } = "Orders";
    public ObservableCollection<WidgetOrder> Rows { get; } = new();

    public double DefaultWidth => 900;
    public double DefaultHeight => 300;

    // --- EXPOSE XAML RESOURCE TO WIDGET CONTAINER ---
    public object? TitleBarContent { get; private set; }

    private Avalonia.Point _lastPointerPos;

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

        // Grab the buttons designed in AXAML
        if (this.Resources.TryGetValue("TitleBarButtons", out var buttons))
        {
            TitleBarContent = buttons;
        }

        OrdersGrid.PointerMoved += OnOrdersGridPointerMoved;

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    private void OnOrdersGridPointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerPos = e.GetPosition(OrdersGrid);
    }

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            _active.Clear();
            _done.Clear();

            foreach (var (state, target) in _context.EnumerateOrders())
            {
                ulong id = state.OrderHeader.OrderId;

                if (_orders.TryGetValue(id, out var existingRow))
                {
                    existingRow.Update(in state, in target);
                    _active.Add(existingRow);
                }
                else
                {
                    Instrument instrument = _context.Primary.GetInstrument(state.OrderHeader.OrderId.InstrumentId);
                    var newRow = new WidgetOrder(instrument, in state, in target);
                    _orders[id] = newRow;
                    _active.Add(newRow);
                }
            }

            foreach (var kvp in _orders)
            {
                ulong cachedId = kvp.Key;
                bool isAlive = false;
                for (int i = 0; i < _active.Count; i++)
                {
                    if (_active[i].ClientOrderId == cachedId)
                    {
                        isAlive = true;
                        break;
                    }
                }
                if (!isAlive) _done.Add(cachedId);
            }

            for (int i = 0; i < _done.Count; i++) _orders.Remove(_done[i]);

            _active.Sort((a, b) =>
            {
                int sym = string.CompareOrdinal(a.Symbol, b.Symbol);
                if (sym != 0) return sym;
                return b.Ticks.CompareTo(a.Ticks);
            });

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                for (int i = Rows.Count - 1; i >= 0; i--)
                {
                    if (!_orders.ContainsKey(Rows[i].ClientOrderId) || !_active.Contains(Rows[i]))
                    {
                        Rows.RemoveAt(i);
                    }
                }

                for (int i = 0; i < _active.Count; i++)
                {
                    var order = _active[i];
                    if (i < Rows.Count)
                    {
                        if (Rows[i] != order)
                        {
                            int oldIndex = Rows.IndexOf(order);
                            if (oldIndex >= 0)
                            {
                                Rows.Move(oldIndex, i);
                            }
                            else
                            {
                                Rows.Insert(i, order);
                            }
                        }
                    }
                    else
                    {
                        Rows.Add(order);
                    }
                }

                _refreshTimer.Start();
            });
        }
        catch (Exception)
        {
            if (!_disposed) _refreshTimer.Start();
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || OrdersGrid == null) return;

        menu.Items.Clear();

        var visual = OrdersGrid.InputHitTest(_lastPointerPos) as Visual;

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

        if (isHeader)
        {
            foreach (var col in OrdersGrid.Columns)
            {
                var header = col.Header?.ToString() ?? "Column";
                var item = new MenuItem
                {
                    Header = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = col.IsVisible
                };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
        else if (hitRow != null && hitRow.DataContext is WidgetOrder order)
        {

            var headerItem = new MenuItem { Header = $"{order.ShortSymbol} | Px: {order.StatePriceStr} Qty: {order.StateQuantity}" };
            menu.Items.Add(headerItem);
            menu.Items.Add(new Separator());

            var cancelItem = new MenuItem { Header = "Cancel Order", Icon = new TextBlock { Text = "❌" } };
            cancelItem.Click += (_, _) => PerformCancel(order);
            menu.Items.Add(cancelItem);

            var amendItem = new MenuItem { Header = "Amend Order", Icon = new TextBlock { Text = "📝" } };
            amendItem.Click += (_, _) => PerformAmend(order);
            menu.Items.Add(amendItem);
        }
        else
        {
            menu.Items.Add(new MenuItem { Header = "No Selection", IsEnabled = false });
        }
    }

    public void OnCancelAllClick(object? sender, RoutedEventArgs e)
    {
        PerformCancelAll();
    }

    private void PerformCancelAll()
    {
        foreach (var order in _active.ToArray())
        {
            PerformCancel(order);
        }
    }

    private void PerformCancel(WidgetOrder order)
    {
        ref readonly OrderState orderState = ref order.OrderState;
        OrderTarget target = new OrderTarget()
        {
            OrderHeader = order.OrderState.OrderHeader,
            OrderProfile = orderState.OrderProfile,
            OrderTargetAction = OrderTargetAction.Cancel,
        };
        Duration duration = Clock.Now - orderState.OrderHeader.NicTimestamp;
        target.OrderHeader.Seq = orderState.OrderHeader.Seq + 10_000 + (int)duration.TotalSeconds;
        _context.Manual.OnOrderTarget(ref target);
    }

    private void PerformAmend(WidgetOrder order)
    {
        var host = this.FindAncestorOfType<Window>() as IWidgetHost;

        if (host == null)
        {
            Console.WriteLine("OrdersWidget: Could not find IWidgetHost parent window.");
            return;
        }

        var sendOrder = new SendOrderWidget(_context);
        sendOrder.SetOrderToAmend(order.ClientOrderId);
        host.AddWidget(sendOrder);
    }

    public string? SaveStateJson()
    {
        var state = new OrdersWidgetState();
        foreach (var col in OrdersGrid.Columns)
        {
            state.Columns.Add(new OrdersWidgetColumnState
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
            var state = Tools.Json.Deserialize<OrdersWidgetState>(json);
            if (state == null || state.Columns == null) return;

            foreach (var colState in state.Columns)
            {
                var col = OrdersGrid.Columns.FirstOrDefault(c => (c.Header?.ToString() ?? "") == colState.Header);
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

        if (OrdersGrid != null)
            OrdersGrid.PointerMoved -= OnOrdersGridPointerMoved;

        _orders.Clear();
        _active.Clear();
        _done.Clear();

        Dispatcher.UIThread.Post(() => Rows.Clear());
    }
}