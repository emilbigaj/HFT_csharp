using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Provider;
using Data;
using Tools;
using Execution;

namespace Widget;


public record struct OrderQuantity(ulong OrderId, int Quantity, int Ahead);

[RegisterJson]
public sealed class LadderWidgetState
{
    public int InstrumentId { get; set; }
    public string Symbol { get; set; } = "";
    public bool ShowMyOrders { get; set; } = true;
}

public sealed partial class LadderWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly System.Timers.Timer _refreshTimer;
    private bool _disposed;

    // State
    public Instrument Instrument { get; private set; } = null!;
    public Position Position { get; private set; } = null!;
    private int _rowDepth = 64;
    private string? _priceFormat = null!;
    private bool _showMyOrders = true;

    // Auto-scroll logic
    private bool _autoScrollPending = false;

    // Lock-on-midprice: when engaged, the ladder re-centers on every refresh instead of just once.
    private bool _lockCenter = false;

    // HFT Allocation optimizations
    private readonly List<RenderedLadderRow> _dataBuffer = new();

    private readonly List<RenderedLadderRow>[] _rowDataCaches = new[]
    {
        new List<RenderedLadderRow>(),
        new List<RenderedLadderRow>()
    };
    private int _writeCacheIndex = 0;

    private readonly HashMap<int, ArrayList<OrderQuantity>> _myOrders = new();

    // Buffers for sorting orders by side and priority
    private readonly List<OrderQuantity> _buyBuffer = new();
    private readonly List<OrderQuantity> _sellBuffer = new();

    public string TypeKey => "LadderWidget";

    private string _title = "Ladder";
    public string Title
    {
        get => _title;
        private set
        {
            if (_title != value)
            {
                _title = value;
                TitleChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? TitleChanged;

    public double DefaultWidth => 275;
    public double DefaultHeight => 575;

    public LadderWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
    }

    public LadderWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();

        _refreshTimer = new System.Timers.Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;

        LadderControl.RightClick += OnLadderRightClick;
    }

    public void SetInstrument(int instrumentId)
    {
        if (_context.Primary.InstrumentIds[instrumentId])
        {
            // The book is always read from the server's authoritative order book; positions/orders stay per-client.
            Instrument = ContextManager.ServerContext.GetInstrument(instrumentId);
            Position = _context.Primary.GetPosition(instrumentId);
            _priceFormat = $"N{Instrument.TicKDecimals}";

            Title = $"{Instrument.ShortSymbol}";

            _autoScrollPending = true;

            if (!_refreshTimer.Enabled)
            {
                _refreshTimer.Start();
            }
            OnRefresh(this, null);
        }
    }

    public IEnumerable<MenuItem> GetTitleBarMenuItems()
    {
        MenuItem item = new MenuItem { Header = "Set Instrument" };
        item.Click += (_, _) => PromptForInstrument();
        yield return item;

        MenuItem ordersItem = new MenuItem
        {
            Header = "Show My Orders",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _showMyOrders
        };

        ordersItem.Click += (_, _) =>
        {
            _showMyOrders = !_showMyOrders;
            OnRefresh(this, null);
        };
        yield return ordersItem;
    }

    public void ScrollToMiddle()
    {
        _autoScrollPending = true;
    }

    private void OnLockCenterChanged(object? sender, RoutedEventArgs e)
    {
        _lockCenter = (sender as ToggleButton)?.IsChecked == true;
        if (_lockCenter)
        {
            _autoScrollPending = true;   // snap to center immediately when the lock is engaged
        }
    }

    private async void PromptForInstrument()
    {
        if (VisualRoot is not Window window)
        {
            return;
        }

        SearchWidget search = new SearchWidget(_context.Primary);
        Instrument? result = await search.ShowDialog<Instrument?>(window);

        if (result != null)
        {
            SetInstrument(result.InstrumentId);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ContextMenu menu = new ContextMenu();
            foreach (MenuItem item in GetTitleBarMenuItems())
            {
                menu.Items.Add(item);
            }

            if (sender is Control control)
            {
                control.ContextMenu = menu;
                menu.Open(control);
            }
            e.Handled = true;
        }
    }

    private void OnLadderRightClick(object? sender, LadderRightClickEventArgs e)
    {
        if (e.IsHeader)
        {
            return;
        }

        if (Instrument != null)
        {
            ContextMenu menu = new ContextMenu();

            MenuItem headerItem = new MenuItem { Header = $"{Instrument.ShortSymbol}", FontWeight = FontWeight.Bold };
            menu.Items.Add(headerItem);

            menu.Items.Add(new Separator());

            string px = Instrument.TicksToPrice(e.Tick).ToString(_priceFormat);
            MenuItem createItem = new MenuItem { Header = $"Create Order @ {px}", Icon = new TextBlock { Text = "📝" } };
            createItem.Click += (_, _) => PerformCreate(e.Tick, 0);
            menu.Items.Add(createItem);

            ulong clickedOrderId = 0;
            if (e.Row != null)
            {
                if (e.ColumnIndex == 0)
                {
                    clickedOrderId = e.Row.BuyOrderId;
                }
                else if (e.ColumnIndex == 4)
                {
                    clickedOrderId = e.Row.SellOrderId;
                }
            }

            if (clickedOrderId != 0)
            {
                try
                {
                    int globalOrderIndex = OrderIdAllocator.GetGlobalIndex(clickedOrderId);
                    OrderState orderState = ContextManager.ServerContext.GetOrderState(globalOrderIndex).Read();

                    if (orderState.OrderHeader.OrderId == clickedOrderId && orderState.OrderStateStatus == OrderStateStatus.Active)
                    {
                        menu.Items.Add(new Separator());

                        string sideStr = orderState.OrderProfile.Side == Side.Buy ? "Buy" : "Sell";
                        string qtyStr = $"{orderState.WorkingQuantity}";
                        string pxStr = Instrument.TicksToPrice(orderState.OrderProfile.Ticks).ToString(_priceFormat);

                        MenuItem orderInfo = new MenuItem { Header = $"Order | Px: {pxStr} Qty: {qtyStr}", IsEnabled = false, FontWeight = FontWeight.Bold };
                        menu.Items.Add(orderInfo);

                        MenuItem cancelItem = new MenuItem { Header = "Cancel Order", Icon = new TextBlock { Text = "❌" } };
                        cancelItem.Click += (_, _) => PerformCancel(clickedOrderId, orderState);
                        menu.Items.Add(cancelItem);

                        MenuItem amendItem = new MenuItem { Header = "Amend Order", Icon = new TextBlock { Text = "📝" } };
                        amendItem.Click += (_, _) => PerformAmend(clickedOrderId);
                        menu.Items.Add(amendItem);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error retrieving order state: {ex.Message}");
                }
            }

            LadderControl.ContextMenu = menu;
            menu.Open(LadderControl);
        }
    }

    private void PerformCancel(ulong orderId, in OrderState state)
    {
        OrderTarget target = new OrderTarget()
        {
            OrderHeader = state.OrderHeader,
            OrderProfile = state.OrderProfile,
            OrderTargetAction = OrderTargetAction.Cancel,
        };
        target.OrderHeader.Seq = state.OrderHeader.Seq + 1_000_000;
        _context.Manual.OnOrderTarget(ref target);
    }

    private void PerformAmend(ulong orderId)
    {
        IWidgetHost? host = this.FindAncestorOfType<Window>() as IWidgetHost;
        if (host != null)
        {
            SendOrderWidget sendOrder = new SendOrderWidget(_context);
            sendOrder.SetOrderToAmend(orderId);
            host.AddWidget(sendOrder);
        }
    }

    private void PerformCreate(int tick, int quantity)
    {
        if (Instrument == null)
        {
            return;
        }

        IWidgetHost? host = this.FindAncestorOfType<Window>() as IWidgetHost;
        if (host != null)
        {
            SendOrderWidget sendOrder = new SendOrderWidget(_context);
            sendOrder.SetCreateOrder(Instrument, tick, quantity);
            host.AddWidget(sendOrder);
        }
    }

    private void OnRefresh(object? sender, System.Timers.ElapsedEventArgs? e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _writeCacheIndex = (_writeCacheIndex + 1) % 2;
            List<RenderedLadderRow> writeCache = _rowDataCaches[_writeCacheIndex];

            MarketByPrice64 mbp = ContextManager.ServerContext.GetMarketByPrice64(Instrument.InstrumentId).Read();

            using (ArrayList<ArrayList<OrderQuantity>> myOrdersCopy = _myOrders.CopyValues())
            {
                foreach (ArrayList<OrderQuantity> list in myOrdersCopy)
                {
                    list.Dispose();
                }
            }
            _myOrders.Clear();

            foreach ((OrderState state, OrderTarget target) in _context.EnumerateOrders())
            {
                if (state.OrderStateStatus != OrderStateStatus.Active)
                {
                    continue;
                }

                if (state.OrderHeader.OrderId.InstrumentId != Instrument.InstrumentId)
                {
                    continue;
                }

                int ticks = state.OrderProfile.Ticks;
                int workingQty = state.OrderProfile.Quantity - state.QuantityFilled;
                int ahead = Math.Abs(state.QuantityAhead);

                ArrayList<OrderQuantity> myOrders = _myOrders.GetOrAdd(ticks, ticks => new ArrayList<OrderQuantity>());
                myOrders.Add(new OrderQuantity(state.OrderHeader.OrderId, workingQty, ahead));
            }

            (int startTick, int endTick) = DetermineTickRange(in mbp);

            _dataBuffer.Clear();

            int rowIndex = 0;
            for (int t = startTick; t >= endTick; t--)
            {
                int bidQ = mbp.GetBidQuantity(t);
                int askQ = mbp.GetAskQuantity(t);
                double priceVal = Instrument.TicksToPrice(t);

                string priceStr = (t == 0 && mbp.IsEmpty) ? 0.0.ToString(_priceFormat) : priceVal.ToString(_priceFormat);
                string bidStr = bidQ > 0 ? bidQ.ToString() : "";
                string askStr = askQ > 0 ? askQ.ToString() : "";

                _buyBuffer.Clear();
                _sellBuffer.Clear();

                if (_showMyOrders && _myOrders.TryGetValue(t, out ArrayList<OrderQuantity> myOrdersList) && myOrdersList.Count > 0)
                {
                    foreach (OrderQuantity o in myOrdersList)
                    {
                        if (o.Quantity > 0)
                        {
                            _buyBuffer.Add(o);
                        }
                        else if (o.Quantity < 0)
                        {
                            _sellBuffer.Add(o);
                        }
                    }

                    _buyBuffer.Sort((a, b) => a.Ahead.CompareTo(b.Ahead));
                    _sellBuffer.Sort((a, b) => a.Ahead.CompareTo(b.Ahead));
                }

                if (_showMyOrders)
                {
                    for (int i = _sellBuffer.Count - 1; i > 0; i--)
                    {
                        AddRow(writeCache, rowIndex, t, "", "", "", "", FormatSell(_sellBuffer[i]), 0, _sellBuffer[i].OrderId);
                        rowIndex++;
                    }
                }

                string myBuyMain = "";
                ulong mainBuyId = 0;
                if (_showMyOrders && _buyBuffer.Count > 0)
                {
                    myBuyMain = FormatBuy(_buyBuffer[0]);
                    mainBuyId = _buyBuffer[0].OrderId;
                }

                string mySellMain = "";
                ulong mainSellId = 0;
                if (_showMyOrders && _sellBuffer.Count > 0)
                {
                    mySellMain = FormatSell(_sellBuffer[0]);
                    mainSellId = _sellBuffer[0].OrderId;
                }

                AddRow(writeCache, rowIndex, t, priceStr, bidStr, askStr, myBuyMain, mySellMain, mainBuyId, mainSellId);
                rowIndex++;

                if (_showMyOrders)
                {
                    for (int i = 1; i < _buyBuffer.Count; i++)
                    {
                        AddRow(writeCache, rowIndex, t, "", "", "", FormatBuy(_buyBuffer[i]), "", _buyBuffer[i].OrderId, 0);
                        rowIndex++;
                    }
                }
            }

            Profit profit = Position.Profit;
            int headerQty = profit.Quantity;
            double headerPnl = profit.Total;

            IBrush qtyBrush = Brushes.Black;
            if (headerQty > 0.0000)
            {
                qtyBrush = Palette.BidActiveBlue;
            }
            else if (headerQty < -0.000)
            {
                qtyBrush = Palette.AskActivePurple;
            }

            IBrush pnlBrush = Brushes.Black;
            if (headerPnl > 0.0000)
            {
                pnlBrush = Palette.ProfitGreen;
            }
            else if (headerPnl < -0.000)
            {
                pnlBrush = Palette.LossRed;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                PositionValue.Text = $"{headerQty}";
                PositionValue.Foreground = qtyBrush;
                PnLValue.Text = $"{headerPnl:N2}";
                PnLValue.Foreground = pnlBrush;

                LadderControl.IsCompactMode = !_showMyOrders;
                LadderControl.UpdateRows(_dataBuffer);

                if (_autoScrollPending || _lockCenter)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || (!_autoScrollPending && !_lockCenter))
                        {
                            return;
                        }

                        if (LadderScroller.Extent.Height > LadderScroller.Bounds.Height)
                        {
                            double scrollCenter = (LadderControl.Bounds.Height - LadderScroller.Bounds.Height) / 2.0;
                            LadderScroller.Offset = new Vector(0, Math.Max(0, scrollCenter));
                            _autoScrollPending = false;   // one-time flag cleared; the lock keeps re-centering each refresh
                        }
                    }, DispatcherPriority.Input);
                }

                if (!_disposed)
                {
                    _refreshTimer.Start();
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LadderWidget Refresh Error: {ex.Message}");
            if (!_disposed)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && _refreshTimer != null && !_refreshTimer.Enabled)
                    {
                        _refreshTimer.Start();
                    }
                });
            }
        }
    }

    private void AddRow(List<RenderedLadderRow> cache, int rowIndex, int ticks, string price, string bid, string ask, string myBuy, string mySell, ulong buyOrderId = 0, ulong sellOrderId = 0)
    {
        while (rowIndex >= cache.Count)
        {
            cache.Add(new RenderedLadderRow());
        }

        RenderedLadderRow row = cache[rowIndex];

        row.Update(ticks, price, bid, ask, myBuy, mySell, buyOrderId, sellOrderId);

        _dataBuffer.Add(row);
    }

    private string FormatBuy(OrderQuantity o)
    {
        return $"{o.Quantity} | {o.Ahead}";
    }

    private string FormatSell(OrderQuantity o)
    {
        return $"{o.Ahead} | {-o.Quantity}";
    }

    private (int startTick, int endTick) DetermineTickRange(in MarketByPrice64 mbp)
    {
        if (mbp.IsEmpty)
        {
            return (_rowDepth, -_rowDepth);
        }

        if (mbp.Bids.IsEmpty)
        {
            return (mbp.Asks.BestTicks + _rowDepth, mbp.Asks.BestTicks - _rowDepth);
        }

        if (mbp.Asks.IsEmpty)
        {
            return (mbp.Bids.BestTicks + _rowDepth, mbp.Bids.BestTicks - _rowDepth);
        }

        return (mbp.Asks.BestTicks + _rowDepth, mbp.Bids.BestTicks - _rowDepth);
    }

    public string? SaveStateJson()
    {
        if (Instrument == null)
        {
            return null;
        }

        return Json.Serialize(new LadderWidgetState
        {
            InstrumentId = Instrument.InstrumentId,
            Symbol = Instrument.Symbol,
            ShowMyOrders = _showMyOrders
        });
    }

    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            LadderWidgetState state = Tools.Json.Deserialize<LadderWidgetState>(json);
            if (state != null && state.InstrumentId >= 0 && _context != null)
            {
                try
                {
                    SetInstrument(state.InstrumentId);
                    _showMyOrders = state.ShowMyOrders;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        LadderControl.RightClick -= OnLadderRightClick;
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
        _dataBuffer.Clear();

        foreach (List<RenderedLadderRow> cache in _rowDataCaches)
        {
            cache.Clear();
        }

        _buyBuffer.Clear();
        _sellBuffer.Clear();
    }
}