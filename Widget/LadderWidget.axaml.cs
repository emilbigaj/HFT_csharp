using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// One of my working orders at a price: working quantity (negative for a sell) and quantity ahead; OrderId 0 is an empty cell.
public record struct OrderQuantity(ulong OrderId, int Quantity, int Ahead)
{
    /// The working quantity as the ladder shows it, unsigned; empty for no order.
    public readonly string QuantityText => OrderId == 0 ? "" : Math.Abs(Quantity).ToString();

    /// The quantity ahead as the ladder shows it; empty for no order.
    public readonly string AheadText => OrderId == 0 ? "" : Ahead.ToString();

    /// The ladder's order within a price and side: fewest ahead first.
    public static int CompareByAhead(OrderQuantity leftOrderQuantity, OrderQuantity rightOrderQuantity) => leftOrderQuantity.Ahead.CompareTo(rightOrderQuantity.Ahead);
}

/// A ladder's saved state. Files saved with the former Symbol key still load: unknown keys are ignored.
[RegisterJson]
public sealed class LadderWidgetState
{
    public int InstrumentId { get; set; }
    public bool ShowMyOrders { get; set; } = true;
}

/// A price ladder on one instrument: the server's book with my working orders beside their prices, my position and PnL in
/// the header, and order entry, cancel and amend on right-click. Rows are built on a timer thread into one of two row
/// caches and applied on the UI thread. Cold path (GUI).
public sealed partial class LadderWidget : UserControl, IWidget, IDisposable
{
    private const int s_rowDepth = 64;   // rows shown above the best ask and below the best bid

    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;

    public Instrument Instrument { get; private set; } = null!;
    public Position Position { get; private set; } = null!;
    private bool _isShowingMyOrders = true;
    private bool _isAutoScrollPending;   // re-center once the rows overflow the view, then clear
    private bool _isCenterLocked;   // re-center on every refresh instead of once

    private readonly List<RenderedLadderRow> _renderedLadderRows = new();   // this pass's rows, handed to LadderControl by the UI post
    // Why two caches: LadderControl keeps drawing the last pass's rows while this pass rewrites the other cache
    private readonly List<RenderedLadderRow>[] _renderedLadderRowCaches = new[]
    {
        new List<RenderedLadderRow>(),
        new List<RenderedLadderRow>()
    };
    private int _writeCacheIndex = 0;

    private readonly HashMap<int, ArrayList<OrderQuantity>> _myOrderQuantitiesByTicks = new();

    // One price's orders by side, fewest ahead first
    private readonly List<OrderQuantity> _buyOrderQuantities = new();
    private readonly List<OrderQuantity> _sellOrderQuantities = new();

    public string Title { get; private set; } = "Ladder";

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
        _refreshTimer = new WidgetRefreshTimer(nameof(LadderWidget), 100, Refresh, isStarted: false);
        LadderControl.RightClick += OnLadderRightClick;
    }

    /// A ladder on this instrument (shown only if it is allocated to the workspace's principal).
    public static LadderWidget NewLadder(WorkspaceContext context, int instrumentId)
    {
        LadderWidget ladderWidget = new(context);
        ladderWidget.SetInstrument(instrumentId);
        return ladderWidget;
    }

    /// Shows this instrument and starts refreshing, if it is allocated to the workspace's primary client; otherwise does nothing.
    public void SetInstrument(int instrumentId)
    {
        if (!_context.Primary.InstrumentIds[instrumentId])
            return;

        // Why the server's instrument: the book is read from the server's authoritative order book; positions and orders stay per-client
        Instrument = ContextManager.ServerContext.GetInstrument(instrumentId);
        Position = _context.Primary.GetPosition(instrumentId);
        Title = Instrument.ShortSymbol;
        _isAutoScrollPending = true;
        _refreshTimer.Start();
    }

    /// The title bar's menu: the Show My Orders toggle, applied by the next refresh.
    public IEnumerable<MenuItem> GetTitleBarMenuItems()
    {
        MenuItem showMyOrdersMenuItem = new() { Header = "Show My Orders", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _isShowingMyOrders };
        showMyOrdersMenuItem.Click += (_, _) => _isShowingMyOrders = !_isShowingMyOrders;
        yield return showMyOrdersMenuItem;
    }

    private void OnLockCenterChanged(object? sender, RoutedEventArgs e)
    {
        _isCenterLocked = (sender as ToggleButton)?.IsChecked == true;
        if (_isCenterLocked)
            _isAutoScrollPending = true;   // snap to center immediately when the lock is engaged
    }

    /// Opens the title bar's menu on a right-click on the header.
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;
        if (sender is Control control)
            control.OpenMenu(GetTitleBarMenuItems());
        e.Handled = true;
    }

    /// Opens the right-click menu on a price: create an order there, and cancel or amend my active order in the clicked cell.
    private void OnLadderRightClick(object? sender, LadderRightClickEventArgs e)
    {
        if (e.IsHeader || Instrument == null)
            return;

        // Step 1: title and order entry at the clicked price
        ContextMenu contextMenu = new();
        contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem(Instrument.ShortSymbol));
        contextMenu.Items.Add(new Separator());
        int clickedTicks = e.Tick;
        contextMenu.Items.Add(WidgetMenus.NewMenuItem($"Create Order @ {Instrument.TicksToPriceString(clickedTicks)}", "📝",
            () => this.AddToHost(SendOrderWidget.NewCreateOrder(_context, Instrument, tick: clickedTicks, quantity: 0))));

        // Step 2: cancel and amend for my active order in the clicked My Buy (column 0) or My Sell (column 4) cell
        ulong clickedOrderId = e.ColumnIndex switch
        {
            0 => e.Row?.BuyOrderId ?? 0,
            4 => e.Row?.SellOrderId ?? 0,
            _ => 0,
        };
        if (clickedOrderId != 0)
        {
            try
            {
                OrderState orderState = ContextManager.ServerContext.GetOrderState(clickedOrderId).Read();
                bool isClickedOrderActive = orderState.OrderHeader.OrderId == clickedOrderId && orderState.OrderStateStatus == OrderStateStatus.Active;
                if (isClickedOrderActive)
                {
                    contextMenu.Items.Add(new Separator());
                    contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem($"Order | Px: {Instrument.TicksToPriceString(orderState.OrderProfile.Ticks)} Qty: {orderState.WorkingQuantity}"));
                    contextMenu.AddOrderMenuItems(this, _context, in orderState);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving order state: {exception.Message}");
            }
        }

        // Why not OpenMenu: the menu holds Separators, which OpenMenu's MenuItem list cannot carry
        LadderControl.ContextMenu = contextMenu;
        contextMenu.Open(LadderControl);
    }

    /// Builds the rows from the server's book and my active orders on this instrument, and posts them with my position and
    /// PnL to the UI thread. Timer thread.
    private void Refresh()
    {
        // Step 1: the book, then the next row cache. Why the book first: an empty one skips the pass, and must not flip the cache the screen shows
        if (!ContextManager.ServerContext.TryReadMarketByPrice64(Instrument.InstrumentId, out MarketByPrice64 marketByPrice))
            return;
        _writeCacheIndex = (_writeCacheIndex + 1) % 2;
        List<RenderedLadderRow> writeCache = _renderedLadderRowCaches[_writeCacheIndex];

        // Step 2: my active orders on this instrument, by price
        using (ArrayList<ArrayList<OrderQuantity>> myOrderQuantityLists = _myOrderQuantitiesByTicks.CopyValues())
        {
            foreach (ArrayList<OrderQuantity> myOrderQuantities in myOrderQuantityLists)
                myOrderQuantities.Dispose();
        }
        _myOrderQuantitiesByTicks.Clear();
        foreach ((OrderState orderState, OrderTarget orderTarget) in _context.EnumerateOrders(Instrument.InstrumentId))
        {
            if (orderState.OrderStateStatus != OrderStateStatus.Active)
                continue;
            ArrayList<OrderQuantity> myOrderQuantities = _myOrderQuantitiesByTicks.GetOrAdd(orderState.OrderProfile.Ticks, _ => new ArrayList<OrderQuantity>());
            myOrderQuantities.Add(new OrderQuantity(orderState.OrderHeader.OrderId, orderState.WorkingQuantity, Math.Abs(orderState.QuantityAhead)));
        }

        // Step 3: each price's rows, top to bottom
        (int startTicks, int endTicks) = DetermineTickRange(in marketByPrice);
        _renderedLadderRows.Clear();
        for (int ticks = startTicks; ticks >= endTicks; ticks--)
            AddPriceRows(writeCache, in marketByPrice, ticks);

        // Step 4: my position and PnL for the header
        Profit profit = Position.Profit;
        int positionQuantity = profit.Quantity;
        double profitTotal = profit.Total;
        IBrush positionQuantityBrush = Palette.GetSignBrush(positionQuantity, Palette.BidActiveBlue, Palette.AskActivePurple);
        IBrush profitTotalBrush = Palette.GetSignBrush(profitTotal, Palette.ProfitGreen, Palette.LossRed);

        // Step 5: apply on the UI thread, re-centering after layout when asked
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshTimer.IsDisposed)
                return;

            PositionValue.Text = $"{positionQuantity}";
            PositionValue.Foreground = positionQuantityBrush;
            PnLValue.Text = $"{profitTotal:N2}";
            PnLValue.Foreground = profitTotalBrush;

            LadderControl.IsCompactMode = !_isShowingMyOrders;
            LadderControl.UpdateRows(_renderedLadderRows);

            if (_isAutoScrollPending || _isCenterLocked)
                Dispatcher.UIThread.Post(CenterLadder, DispatcherPriority.Input);
        });
    }

    /// Scrolls the ladder to its middle once its rows overflow the view, clearing the one-time request when it does.
    private void CenterLadder()
    {
        if (_refreshTimer.IsDisposed || (!_isAutoScrollPending && !_isCenterLocked))
            return;

        if (LadderScroller.Extent.Height > LadderScroller.Bounds.Height)
        {
            double scrollCenter = (LadderControl.Bounds.Height - LadderScroller.Bounds.Height) / 2.0;
            LadderScroller.Offset = new Vector(0, Math.Max(0, scrollCenter));
            _isAutoScrollPending = false;   // one-time flag cleared; the lock keeps re-centering each refresh
        }
    }

    /// Adds one price's rows: my further sells above it, the price row with the book's quantities and my first buy and sell, my further buys below it.
    private void AddPriceRows(List<RenderedLadderRow> writeCache, in MarketByPrice64 marketByPrice, int ticks)
    {
        // Step 1: my orders at this price by side, fewest ahead first
        _buyOrderQuantities.Clear();
        _sellOrderQuantities.Clear();
        if (_isShowingMyOrders && _myOrderQuantitiesByTicks.TryGetValue(ticks, out ArrayList<OrderQuantity> myOrderQuantities))
        {
            foreach (OrderQuantity orderQuantity in myOrderQuantities)
            {
                if (orderQuantity.Quantity > 0)
                    _buyOrderQuantities.Add(orderQuantity);
                else if (orderQuantity.Quantity < 0)
                    _sellOrderQuantities.Add(orderQuantity);
            }
            _buyOrderQuantities.Sort(OrderQuantity.CompareByAhead);
            _sellOrderQuantities.Sort(OrderQuantity.CompareByAhead);
        }

        // Step 2: my further sells above the price row, the furthest back on top
        for (int sellOrderIndex = _sellOrderQuantities.Count - 1; sellOrderIndex > 0; sellOrderIndex--)
            AddRow(writeCache, ticks, "", "", "", default, _sellOrderQuantities[sellOrderIndex]);

        // Step 3: the price row
        int bidQuantity = marketByPrice.GetBidQuantity(ticks);
        int askQuantity = marketByPrice.GetAskQuantity(ticks);
        OrderQuantity firstBuyOrderQuantity = _buyOrderQuantities.Count > 0 ? _buyOrderQuantities[0] : default;
        OrderQuantity firstSellOrderQuantity = _sellOrderQuantities.Count > 0 ? _sellOrderQuantities[0] : default;
        AddRow(writeCache, ticks, Instrument.TicksToPriceString(ticks), bidQuantity > 0 ? bidQuantity.ToString() : "", askQuantity > 0 ? askQuantity.ToString() : "", firstBuyOrderQuantity, firstSellOrderQuantity);

        // Step 4: my further buys below it
        for (int buyOrderIndex = 1; buyOrderIndex < _buyOrderQuantities.Count; buyOrderIndex++)
            AddRow(writeCache, ticks, "", "", "", _buyOrderQuantities[buyOrderIndex], default);
    }

    /// Writes one row into the write cache's next slot (adding the slot the first time the ladder grows this long) and lists it for the UI post.
    private void AddRow(List<RenderedLadderRow> writeCache, int ticks, string priceText, string bidQuantityText, string askQuantityText, OrderQuantity myBuyOrderQuantity, OrderQuantity mySellOrderQuantity)
    {
        int rowIndex = _renderedLadderRows.Count;
        if (rowIndex == writeCache.Count)
            writeCache.Add(new RenderedLadderRow());
        RenderedLadderRow renderedLadderRow = writeCache[rowIndex];

        // Why quantity and ahead apart: FastLadderControl draws them either side of a fixed chevron
        renderedLadderRow.Update(ticks, priceText, bidQuantityText, askQuantityText, myBuyOrderQuantity.QuantityText, myBuyOrderQuantity.AheadText,
            mySellOrderQuantity.QuantityText, mySellOrderQuantity.AheadText, myBuyOrderQuantity.OrderId, mySellOrderQuantity.OrderId);
        _renderedLadderRows.Add(renderedLadderRow);
    }

    /// The ticks shown, top to bottom: s_rowDepth above the best ask to s_rowDepth below the best bid, around the one side
    /// there is, or around 0 for an empty book.
    private static (int StartTicks, int EndTicks) DetermineTickRange(in MarketByPrice64 marketByPrice)
    {
        if (marketByPrice.IsEmpty)
            return (s_rowDepth, -s_rowDepth);
        if (marketByPrice.Bids.IsEmpty)
            return (marketByPrice.Asks.BestTicks + s_rowDepth, marketByPrice.Asks.BestTicks - s_rowDepth);
        if (marketByPrice.Asks.IsEmpty)
            return (marketByPrice.Bids.BestTicks + s_rowDepth, marketByPrice.Bids.BestTicks - s_rowDepth);
        return (marketByPrice.Asks.BestTicks + s_rowDepth, marketByPrice.Bids.BestTicks - s_rowDepth);
    }

    public string? SaveStateJson() =>
        Instrument == null ? null : Json.Serialize(new LadderWidgetState { InstrumentId = Instrument.InstrumentId, ShowMyOrders = _isShowingMyOrders });

    /// Restores the instrument and the Show My Orders toggle. Why the catch: a layout that no longer parses or names an instrument no longer allocated must not stop the widget opening
    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            LadderWidgetState? ladderWidgetState = Json.Deserialize<LadderWidgetState>(json);
            if (ladderWidgetState != null && ladderWidgetState.InstrumentId >= 0 && _context != null)
            {
                SetInstrument(ladderWidgetState.InstrumentId);
                _isShowingMyOrders = ladderWidgetState.ShowMyOrders;
            }
        }
        catch { }
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
