using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// One active order in the Amend tab's list: the order, its instrument and the line the list shows. Updated in place on each
/// refresh so the ComboBox keeps its selection. UI thread, cold path.
public sealed class AmendOrderWrapper : INotifyPropertyChanged
{
    private OrderState _orderState;
    private string _displayString = null!;

    public OrderState OrderState => _orderState;
    public Instrument Instrument { get; private set; }

    public string DisplayString
    {
        get => _displayString;
        private set
        {
            if (_displayString != value)
            {
                _displayString = value;
                OnPropertyChanged();
            }
        }
    }

    public Side Side => _orderState.OrderProfile.Side;
    public ulong ClientOrderId => _orderState.OrderHeader.OrderId;
    public bool IsAmendable => !_orderState.OrderHeader.OrderId.IsAlgoOrder();   // the algo owns its orders' targets

    public AmendOrderWrapper(OrderState orderState, Instrument instrument)
    {
        Instrument = instrument;
        Update(orderState, instrument);
    }

    /// Takes the order's latest state; the list's line, "Symbol | Px: price  Qty: working quantity", notifies only when its text changes.
    public void Update(OrderState orderState, Instrument instrument)
    {
        _orderState = orderState;
        Instrument = instrument;
        DisplayString = $"{instrument.ShortSymbol} | Px: {instrument.TicksToPriceString(orderState.OrderProfile.Ticks)}  Qty: {orderState.WorkingQuantity}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

/// The order-entry widget's state: the mode (the Create or Amend tab), the instrument or order chosen, the price and quantity
/// typed (each tab keeps its own), and the send buttons' look. UI thread, cold path.
public sealed class SendOrderViewModel : INotifyPropertyChanged
{
    /// One tab's typed price (in ticks) and quantity, kept while the other tab is shown.
    private readonly record struct OrderInputs(int? TicksInput, int? QuantityInput);

    // --- Backing Fields (Source of Truth is INT) ---
    private int? _ticksInput = null;
    private int? _quantityInput = null;
    private readonly OrderInputs[] _savedOrderInputsByTabIndex = new OrderInputs[2];   // indexed by SelectedTabIndex: Create 0, Amend 1
    private string? _revertingInputPropertyName;   // the input string being shown again after a refused edit; null when none

    // --- Tab / Mode State ---
    private bool _isCreateMode = true;
    private bool _isAutoCenter = false;

    public int SelectedTabIndex
    {
        get => _isCreateMode ? 0 : 1;
        set => IsCreateMode = (value == 0);
    }

    // --- Collections ---
    public ObservableCollection<Instrument> SubscribedInstruments { get; } = new();
    public ObservableCollection<AmendOrderWrapper> ActiveOrders { get; } = new();

    // --- Selections ---
    private Instrument? _selectedCreateOrderInstrument;
    private AmendOrderWrapper? _selectedOrderToAmend;

    // --- Visual Properties ---
    private string _createButtonText = "Create Buy";
    private IBrush _createButtonBrush = Palette.BidActiveBlue;
    private IBrush _amendButtonBrush = Brushes.Gray;

    // --- Public Properties ---

    public int? TicksInput
    {
        get => _ticksInput;
        set
        {
            if (_ticksInput != value)
            {
                _ticksInput = value;
                OnPropertyChanged(nameof(TicksInputAsString)); // Refresh string view
            }
        }
    }

    public int? QuantityInput
    {
        get => _quantityInput;
        set
        {
            if (_quantityInput != value)
            {
                _quantityInput = value;
                OnPropertyChanged(nameof(QuantityInputAsString)); // Refresh string view
                UpdateButtonStates();
            }
        }
    }

    public void AdjustTicks(int delta)
    {
        int newTicks = (TicksInput ?? 0) + delta;
        TicksInput = newTicks;
    }

    /// Steps the quantity by delta; no effect when the step would flip an amended order's side.
    public void AdjustQuantity(int delta)
    {
        int quantity = (QuantityInput ?? 0) + delta;
        if (IsQuantityAllowed(quantity))
            QuantityInput = quantity;
    }

    /// False when an amend would flip the order's side: a buy's quantity stays at or above 0, a sell's at or below 0.
    private bool IsQuantityAllowed(int quantity)
    {
        if (IsCreateMode || SelectedOrderToAmend == null)
            return true;
        Side side = SelectedOrderToAmend.Side;
        bool isSideFlipped = (side == Side.Buy && quantity < 0) || (side == Side.Sell && quantity > 0);
        return !isSideFlipped;
    }

    public bool IsAutoCenter
    {
        get => _isAutoCenter;
        set { _isAutoCenter = value; OnPropertyChanged(); }
    }

    /// Switches between the Create and Amend tabs, keeping the tab being left's inputs and restoring the other's.
    public bool IsCreateMode
    {
        get => _isCreateMode;
        set
        {
            if (_isCreateMode == value)
                return;

            // Step 1: keep the inputs of the tab being left
            _savedOrderInputsByTabIndex[SelectedTabIndex] = new OrderInputs(TicksInput, QuantityInput);

            // Step 2: switch mode
            _isCreateMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveLadderInstrument));

            // Step 3: restore the inputs of the tab being shown
            OrderInputs savedOrderInputs = _savedOrderInputsByTabIndex[SelectedTabIndex];
            TicksInput = savedOrderInputs.TicksInput;
            QuantityInput = savedOrderInputs.QuantityInput;

            // Step 4: the strings follow the new mode's instrument even when the numbers are unchanged
            RefreshInputViews();
        }
    }

    public Instrument? SelectedCreateOrderInstrument
    {
        get => _selectedCreateOrderInstrument;
        set
        {
            if (_selectedCreateOrderInstrument == value)
                return;
            _selectedCreateOrderInstrument = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveLadderInstrument));

            // Step 1: a new instrument starts at quantity 0 and the book's centre (even one-sided); no instrument clears both
            if (value != null && IsCreateMode)
            {
                QuantityInput = 0;
                int centreTicks = ContextManager.ServerContext.TryReadMarketByPrice64(value.InstrumentId, out MarketByPrice64 marketByPrice64) ? GetCentreTicks(in marketByPrice64) : 0;
                if (centreTicks != 0)
                    TicksInput = centreTicks;
            }
            else if (value == null)
            {
                QuantityInput = null;
                TicksInput = null;
            }

            // Step 2: the price string follows the new instrument's format even when the ticks are unchanged
            RefreshInputViews();
        }
    }

    public AmendOrderWrapper? SelectedOrderToAmend
    {
        get => _selectedOrderToAmend;
        set
        {
            if (value == _selectedOrderToAmend)
                return;
            _selectedOrderToAmend = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveLadderInstrument));

            // Step 1: the inputs start at the order's working quantity and price, or clear with no order
            QuantityInput = value?.OrderState.WorkingQuantity;
            TicksInput = value?.OrderState.OrderProfile.Ticks;

            // Step 2: the price string follows the order's instrument's format even when the ticks are unchanged
            RefreshInputViews();
        }
    }

    public Instrument? ActiveLadderInstrument => IsCreateMode ? SelectedCreateOrderInstrument : SelectedOrderToAmend?.Instrument;

    public string QuantityInputAsString
    {
        get => ToShownInputString(nameof(QuantityInputAsString), QuantityInput?.ToString() ?? string.Empty);
        set
        {
            // Step 1: a lone minus starts a sell quantity
            if (value == "-")
            {
                QuantityInput = 0;
                return;
            }
            if (string.IsNullOrEmpty(value))
                QuantityInput = null;

            // Step 2: take a quantity the order's side allows, else show the kept one again
            if (int.TryParse(value, out int quantity) && IsQuantityAllowed(quantity))
                QuantityInput = quantity;
            else
                ForceRevertInputString(nameof(QuantityInputAsString));
        }
    }

    public string TicksInputAsString
    {
        get => ToShownInputString(nameof(TicksInputAsString), ActiveLadderInstrument != null && TicksInput.HasValue ? ActiveLadderInstrument.TicksToPriceString(TicksInput.Value) : string.Empty);
        set
        {
            if (!double.TryParse(value, out double price))
                ForceRevertInputString(nameof(TicksInputAsString));
            else if (ActiveLadderInstrument != null)
                TicksInput = ActiveLadderInstrument.RoundToTicks(price);
        }
    }

    /// The input string the TextBox gets: with a leading space while ForceRevertInputString re-sends it.
    private string ToShownInputString(string propertyName, string inputString) =>
        propertyName == _revertingInputPropertyName ? " " + inputString : inputString;

    /// Puts the kept value back over a refused edit: it is re-sent once with a leading space, so the TextBox replaces its text even when the kept string is the same.
    private void ForceRevertInputString(string propertyName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _revertingInputPropertyName = propertyName;
            OnPropertyChanged(propertyName);
            _revertingInputPropertyName = null;
        }, DispatcherPriority.Default);
    }

    /// Re-reads both input strings and the buttons, for a change of mode or instrument that alters them without changing the numbers.
    private void RefreshInputViews()
    {
        OnPropertyChanged(nameof(TicksInputAsString));
        OnPropertyChanged(nameof(QuantityInputAsString));
        UpdateButtonStates();
    }

    // --- Visual Properties ---

    public string CreateButtonText
    {
        get => _createButtonText;
        set { _createButtonText = value; OnPropertyChanged(); }
    }

    public IBrush CreateButtonBrush
    {
        get => _createButtonBrush;
        set { _createButtonBrush = value; OnPropertyChanged(); }
    }

    public IBrush AmendButtonBrush
    {
        get => _amendButtonBrush;
        set { _amendButtonBrush = value; OnPropertyChanged(); }
    }

    // --- Methods ---

    public void UpdateButtonStates()
    {
        if (IsCreateMode)
        {
            if (SelectedCreateOrderInstrument == null)
            {
                CreateButtonText = "Select Instrument";
                CreateButtonBrush = Brushes.Gray;
                return;
            }

            if (_quantityInput >= 0)
            {
                CreateButtonText = $"Create Buy {_quantityInput}";
                CreateButtonBrush = Palette.BidActiveBlue;
            }
            else
            {
                CreateButtonText = $"Create Sell {-_quantityInput}";
                CreateButtonBrush = Palette.AskActivePurple;
            }
        }
        else if (SelectedOrderToAmend != null)
        {
            Side side = SelectedOrderToAmend.Side;
            AmendButtonBrush = side == Side.Buy ? Palette.BidActiveBlue : Palette.AskActivePurple;
        }
        else
        {
            AmendButtonBrush = Brushes.Gray;
        }
    }

    /// The book's centre in ticks: the mid when both sides show, else the one best price, else 0. Centres the ladder and a new order's price.
    public static int GetCentreTicks(in MarketByPrice64 marketByPrice64)
    {
        if (marketByPrice64.BidsCount > 0 && marketByPrice64.AsksCount > 0)
            return (int)((marketByPrice64.BestBid.Ticks + marketByPrice64.BestAsk.Ticks) * 0.5);
        if (marketByPrice64.BidsCount > 0)
            return marketByPrice64.BestBid.Ticks;
        if (marketByPrice64.AsksCount > 0)
            return marketByPrice64.BestAsk.Ticks;
        return 0;
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

/// The order-entry widget: creates an order on an instrument, or amends or cancels an active one, beside a compact ladder of
/// the instrument's book. The ladder rows are built on the refresh timer's thread; everything else runs on the UI thread.
/// Cold path (GUI).
public partial class SendOrderWidget : UserControl, IWidget, IDisposable
{
    public string Title => "Send Order";

    public double DefaultWidth => 600;
    public double DefaultHeight => 277;

    private readonly WorkspaceContext _context;
    private readonly SendOrderViewModel _viewModel;
    private readonly WidgetRefreshTimer _refreshTimer;

    private int _ladderScrollOffset = 0;   // ticks the ladder's centre sits off the book's centre; the wheel writes it, the next refresh pass reads it

    private readonly List<RenderedLadderRow> _renderedLadderRows = new();       // this pass's rows, handed to the ladder (timer thread, read by the UI post)
    private readonly List<RenderedLadderRow> _renderedLadderRowCache = new();   // every row built so far, reused so unchanged text keeps its layout

    public SendOrderWidget()
    {
        _context = null!; _viewModel = null!; _refreshTimer = null!;
        InitializeComponent();
    }

    public SendOrderWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _viewModel = new SendOrderViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Manual Wiring for Events
        LadderControl.TickSelected += OnLadderTickSelected;
        LadderControl.PointerWheelChanged += OnLadderPointerWheelChanged;
        LadderControl.PointerPressed += OnLadderPointerPressed;

        // Why tunnelling: the filter sees typed text before the TextBox takes it
        PriceTextBox.AddHandler(TextInputEvent, (_, textInputEventArgs) => FilterTextInput(textInputEventArgs, isDecimalAllowed: true), RoutingStrategies.Tunnel);
        QtyTextBox.AddHandler(TextInputEvent, (_, textInputEventArgs) => FilterTextInput(textInputEventArgs, isDecimalAllowed: false), RoutingStrategies.Tunnel);

        PopulateInstruments();

        _refreshTimer = new WidgetRefreshTimer(nameof(SendOrderWidget), intervalMilliseconds: 100, Refresh);
    }

    /// Swallows typed text holding anything but digits and a minus, or a decimal point where allowed.
    private static void FilterTextInput(TextInputEventArgs textInputEventArgs, bool isDecimalAllowed)
    {
        if (string.IsNullOrEmpty(textInputEventArgs.Text))
            return;
        foreach (char typedCharacter in textInputEventArgs.Text)
        {
            bool isAllowed = char.IsDigit(typedCharacter) || typedCharacter == '-' || (isDecimalAllowed && typedCharacter == '.');
            if (!isAllowed)
            {
                textInputEventArgs.Handled = true;
                return;
            }
        }
    }

    private void OnInstrumentComboDropDownOpened(object? sender, EventArgs e) => CalculateMaxDropDownHeight(InstrumentCombo);

    private void OnOrderComboDropDownOpened(object? sender, EventArgs e)
    {
        RefreshActiveOrders();
        CalculateMaxDropDownHeight(OrderCombo);
    }

    private void CalculateMaxDropDownHeight(ComboBox cb)
    {
        var point = cb.TranslatePoint(new Avalonia.Point(0, 0), this);
        if (point.HasValue)
        {
            double bottomOfCombo = point.Value.Y + cb.Bounds.Height;
            double availableHeight = this.Bounds.Height - bottomOfCombo;
            // Subtracting a small margin (e.g. 5px) ensures it doesn't touch the very edge
            // or get clipped by the widget's own bottom border/padding.
            cb.MaxDropDownHeight = Math.Max(0, availableHeight - 5);
        }
    }

    /// An order-entry widget set to create an order on this instrument, at this price and quantity when given.
    public static SendOrderWidget NewCreateOrder(WorkspaceContext context, Instrument instrument, int? tick = null, int? quantity = null)
    {
        SendOrderWidget sendOrderWidget = new(context);
        sendOrderWidget.SetCreateOrder(instrument, tick, quantity);
        return sendOrderWidget;
    }

    /// An order-entry widget set to amend this order.
    public static SendOrderWidget NewAmendOrder(WorkspaceContext context, ulong clientOrderId)
    {
        SendOrderWidget sendOrderWidget = new(context);
        sendOrderWidget.SetOrderToAmend(clientOrderId);
        return sendOrderWidget;
    }

    public void SetOrderToAmend(ulong clientOrderId)
    {
        // Must run on dispatcher to modify Bound Properties and Collections
        Dispatcher.UIThread.Post(() =>
        {
            _viewModel.IsCreateMode = false;
            RefreshActiveOrders(clientOrderId);
        });
    }

    public void SetCreateOrder(Instrument instrument, int? tick = null, int? quantity = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _viewModel.IsCreateMode = true;
            // Why the subscribed instance: the ComboBox selects by reference
            Instrument? subscribedInstrument = _viewModel.SubscribedInstruments.FirstOrDefault(candidateInstrument => candidateInstrument.InstrumentId == instrument.InstrumentId);
            _viewModel.SelectedCreateOrderInstrument = subscribedInstrument ?? instrument;

            if (tick.HasValue)
                _viewModel.TicksInput = tick.Value;

            if (quantity.HasValue)
                _viewModel.QuantityInput = quantity.Value;
        });
    }

    private void PopulateInstruments()
    {
        foreach (Instrument instrument in _context.Primary.EnumerateInstruments())
        {
            // Subscription set comes from the client, but the book is read from the server's authoritative order book.
            _viewModel.SubscribedInstruments.Add(ContextManager.ServerContext.GetInstrument(instrument.InstrumentId));
        }
        _viewModel.SelectedCreateOrderInstrument = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SendOrderViewModel.ActiveLadderInstrument))
        {
            ScrollToMiddle();
        }
    }

    private void ScrollToMiddle()
    {
        _ladderScrollOffset = 0;
    }

    /// One refresh pass, on the timer thread: builds the ladder rows, then posts them and the Amend list's refresh to the UI thread.
    private void Refresh()
    {
        // Step 1: the ladder rows of the instrument being entered, or none; its empty book skips the pass, as in LadderWidget
        Instrument? activeLadderInstrument = _viewModel.ActiveLadderInstrument;
        MarketByPrice64 marketByPrice64 = default;
        if (activeLadderInstrument != null && !ContextManager.ServerContext.TryReadMarketByPrice64(activeLadderInstrument.InstrumentId, out marketByPrice64))
            return;
        _renderedLadderRows.Clear();
        if (activeLadderInstrument != null)
            AddLadderRows(activeLadderInstrument, in marketByPrice64);

        // Step 2: the Amend list and the ladder, on the UI thread
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshTimer.IsDisposed)
                return;
            if (!_viewModel.IsCreateMode && (_viewModel.SelectedOrderToAmend != null || OrderCombo.IsDropDownOpen))
                RefreshActiveOrders(_viewModel.SelectedOrderToAmend?.ClientOrderId ?? 0);
            LadderControl.UpdateRows(_renderedLadderRows);
        });
    }

    /// Adds one row per tick the ladder's height shows, around the book's centre moved by the scroll offset. Timer thread.
    private void AddLadderRows(Instrument instrument, in MarketByPrice64 marketByPrice64)
    {
        // Step 1: the centre, followed when locked, else scrolled
        if (_viewModel.IsAutoCenter)
            _ladderScrollOffset = 0;
        int centreTicks = SendOrderViewModel.GetCentreTicks(in marketByPrice64) + _ladderScrollOffset;

        // Step 2: the ticks the ladder's height shows (24 px rows; 400 px until it is laid out)
        double availableHeight = LadderControl.Bounds.Height;
        if (availableHeight < 50) availableHeight = 400;
        int visibleRowCount = (int)(availableHeight / 24.0);
        int startTicks = centreTicks + visibleRowCount / 2;
        int endTicks = startTicks - visibleRowCount - 1;

        // Step 3: one cached row per tick; Update rebuilds only the text layouts whose text changed
        while (_renderedLadderRowCache.Count < startTicks - endTicks + 1) _renderedLadderRowCache.Add(new RenderedLadderRow());
        int rowIndex = 0;
        for (int ticks = startTicks; ticks >= endTicks; ticks--)
        {
            int bidQuantity = marketByPrice64.GetBidQuantity(ticks);
            int askQuantity = marketByPrice64.GetAskQuantity(ticks);
            RenderedLadderRow renderedLadderRow = _renderedLadderRowCache[rowIndex++];
            renderedLadderRow.Update(ticks, instrument.TicksToPriceString(ticks), bidQuantity > 0 ? bidQuantity.ToString() : "", askQuantity > 0 ? askQuantity.ToString() : "",
                myBuyQty: "", myBuyAhead: "", mySellQty: "", mySellAhead: "", buyOrderId: 0, sellOrderId: 0);
            _renderedLadderRows.Add(renderedLadderRow);
        }
    }

    /// Brings the Amend list in step with the active orders, in the order lists' order, and selects this order (none when 0 or gone). UI thread.
    private void RefreshActiveOrders(ulong selectedClientOrderId = 0)
    {
        // Step 1: a row per active order, reusing its existing row so the ComboBox keeps its selection
        Dictionary<ulong, AmendOrderWrapper> amendOrderWrappersByClientOrderId = _viewModel.ActiveOrders.ToDictionary(amendOrderWrapper => amendOrderWrapper.ClientOrderId);
        List<AmendOrderWrapper> activeAmendOrderWrappers = new();
        foreach ((OrderState orderState, OrderTarget _) in _context.EnumerateOrders())
        {
            if (orderState.OrderStateStatus != OrderStateStatus.Active)
                continue;
            Instrument instrument = _context.Primary.GetInstrument(orderState.OrderHeader.OrderId.InstrumentId);
            ulong clientOrderId = orderState.OrderHeader.OrderId;
            if (amendOrderWrappersByClientOrderId.TryGetValue(clientOrderId, out AmendOrderWrapper? amendOrderWrapper))
                amendOrderWrapper.Update(orderState, instrument);
            else
                amendOrderWrapper = new AmendOrderWrapper(orderState, instrument);
            activeAmendOrderWrappers.Add(amendOrderWrapper);
        }

        // Step 2: sorted as the order lists are, then reconciled into the bound collection
        activeAmendOrderWrappers.Sort((leftAmendOrderWrapper, rightAmendOrderWrapper) => DataGridRows.CompareOrders(
            leftAmendOrderWrapper.Instrument.Symbol, leftAmendOrderWrapper.OrderState.OrderProfile.Ticks,
            rightAmendOrderWrapper.Instrument.Symbol, rightAmendOrderWrapper.OrderState.OrderProfile.Ticks));
        _viewModel.ActiveOrders.ReconcileRows(activeAmendOrderWrappers);

        // Step 3: the asked selection; the same row again changes nothing
        _viewModel.SelectedOrderToAmend = _viewModel.ActiveOrders.FirstOrDefault(amendOrderWrapper => amendOrderWrapper.ClientOrderId == selectedClientOrderId);
    }

    private void OnLadderTickSelected(int ticks)
    {
        _viewModel.TicksInput = ticks;
    }

    /// Scrolls the ladder unless its centre is locked; the next refresh pass (within 100 ms) draws it.
    private void OnLadderPointerWheelChanged(object? sender, PointerWheelEventArgs pointerWheelEventArgs)
    {
        if (!_viewModel.IsAutoCenter)
            _ladderScrollOffset += (int)pointerWheelEventArgs.Delta.Y;
        pointerWheelEventArgs.Handled = true;
    }

    /// A middle click centres the ladder on the book; the next refresh pass (within 100 ms) draws it.
    private void OnLadderPointerPressed(object? sender, PointerPressedEventArgs pointerPressedEventArgs)
    {
        if (!pointerPressedEventArgs.GetCurrentPoint(LadderControl).Properties.IsMiddleButtonPressed)
            return;
        ScrollToMiddle();
        pointerPressedEventArgs.Handled = true;
    }

    // --- Buttons ---
    private void OnPriceUp(object? sender, RoutedEventArgs e) => _viewModel.AdjustTicks(1);
    private void OnPriceDown(object? sender, RoutedEventArgs e) => _viewModel.AdjustTicks(-1);
    private void OnQtyUp(object? sender, RoutedEventArgs e) => _viewModel.AdjustQuantity(1);
    private void OnQtyDown(object? sender, RoutedEventArgs e) => _viewModel.AdjustQuantity(-1);

    private void OnSendCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCreateOrderInstrument == null || _viewModel.TicksInput == null || _viewModel.QuantityInput == null || _viewModel.QuantityInput == 0)
            return;

        OrderTarget target = new OrderTarget()
        {
            OrderHeader = new()
            {
                // template: Client.Create stamps ClientId/StrategyId authoritatively before allocation
                OrderId = new OrderId
                {
                    ClientId = _context.Manual.Context.ClientId,
                    InstrumentId = _viewModel.SelectedCreateOrderInstrument.InstrumentId,
                },
                Seq = 1,
            },
            OrderProfile = new(_viewModel.TicksInput.Value, _viewModel.QuantityInput.Value),
            OrderTargetAction = OrderTargetAction.Create,
        };

        _context.Manual.OnOrderTarget(ref target);
    }

    private void OnSendAmendClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedOrderToAmend == null || !_viewModel.SelectedOrderToAmend.IsAmendable || _viewModel.TicksInput == null || _viewModel.QuantityInput == null)
            return;

        var state = _viewModel.SelectedOrderToAmend.OrderState;

        int workingQuantity = _viewModel.QuantityInput.Value;
        int newQuantity = state.QuantityFilled + workingQuantity;

        OrderProfile orderProfile = new OrderProfile(_viewModel.TicksInput.Value, newQuantity);
        OrderTarget target = new OrderTarget
        {
            OrderHeader = state.OrderHeader,
            OrderProfile = orderProfile,
            OrderTargetAction = orderProfile.IsReduceOf(in state.OrderProfile) ? OrderTargetAction.Reduce : OrderTargetAction.Replace
        };
        _context.Manual.OnOrderTarget(ref target);
    }

    private void OnSendCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedOrderToAmend == null) return;
        OrderTarget cancelOrderTarget = _viewModel.SelectedOrderToAmend.OrderState.NewCancel();
        _context.Manual.OnOrderTarget(ref cancelOrderTarget);
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }

    public void Dispose() => _refreshTimer?.Dispose();
}
