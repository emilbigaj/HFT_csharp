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

// Wrapper to keep the selection stable in the ComboBox
public class AmendOrderWrapper : INotifyPropertyChanged
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

    public AmendOrderWrapper(OrderState state, Instrument inst)
    {
        Instrument = inst;
        Update(state, inst);
    }

    public void Update(OrderState state, Instrument inst)
    {
        _orderState = state;
        Instrument = inst;

        // Format: Symbol Side Qty @ Price
        double price = inst.TicksToPrice(state.OrderProfile.Ticks);
        string priceStr = price.ToString($"N{inst.TicKDecimals}");

        DisplayString = $"{inst.ShortSymbol} | Px: {priceStr}  Qty: {state.WorkingQuantity}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public sealed class SendOrderViewModel : INotifyPropertyChanged
{
    private readonly WorkspaceContext _context;

    // --- Backing Fields (Source of Truth is INT) ---
    private int? _ticksInput = null;
    private int? _quantityInput = null;

    // --- State Persistence for Tabs ---
    private int? _savedCreateTicksInput = null;
    private int? _savedCreateQuantityInput = null;

    private int? _savedAmendTicksInput = null;
    private int? _savedAmendQuantityInput = null;

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

    public SendOrderViewModel(WorkspaceContext context)
    {
        _context = context;
    }

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

    public void AdjustQuantity(int delta)
    {
        int newQty = (QuantityInput ?? 0) + delta;

        // Enforce Amend Side Constraints
        if (!IsCreateMode && SelectedOrderToAmend != null)
        {
            Side side = SelectedOrderToAmend.Side;
            // If Buy (Positive): New Quantity must be >= 0 (0 implies cancel/reduce to nothing, effectively)
            if (side == Side.Buy && newQty < 0)
                return;

            // If Sell (Negative): New Quantity must be <= 0
            if (side == Side.Sell && newQty > 0)
                return;
        }

        QuantityInput = newQty;
    }

    public bool IsAutoCenter
    {
        get => _isAutoCenter;
        set { _isAutoCenter = value; OnPropertyChanged(); }
    }

    public bool IsCreateMode
    {
        get => _isCreateMode;
        set
        {
            if (_isCreateMode != value)
            {
                // 1. Save current state before switching
                if (_isCreateMode) // Switch FROM Create TO Amend
                {
                    _savedCreateTicksInput = TicksInput;
                    _savedCreateQuantityInput = QuantityInput;

                    // Switch Mode
                    _isCreateMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveLadderInstrument));

                    // Restore Amend State using Properties
                    TicksInput = _savedAmendTicksInput;
                    QuantityInput = _savedAmendQuantityInput;
                }
                else // Switch FROM Amend TO Create
                {
                    _savedAmendTicksInput = TicksInput;
                    _savedAmendQuantityInput = QuantityInput;

                    // Switch Mode
                    _isCreateMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveLadderInstrument));

                    // Restore Create State using Properties
                    TicksInput = _savedCreateTicksInput;
                    QuantityInput = _savedCreateQuantityInput;
                }

                // Explicitly notify strings in case the integer values happened to be identical
                // between modes (e.g. 100 ticks -> 100 ticks) but the context changed.
                OnPropertyChanged(nameof(TicksInputAsString));
                OnPropertyChanged(nameof(QuantityInputAsString));

                UpdateButtonStates();
            }
        }
    }

    public Instrument? SelectedCreateOrderInstrument
    {
        get => _selectedCreateOrderInstrument;
        set
        {
            if (_selectedCreateOrderInstrument != value)
            {
                _selectedCreateOrderInstrument = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveLadderInstrument));

                // Only set defaults if we are actually switching TO an instrument
                if (value != null && IsCreateMode)
                {
                    QuantityInput = 0; // Use Property

                    // Use common static helper to initialize price even if one-sided
                    int center = GetCentreTIck(ContextManager.ServerContext.GetMarketByPrice64(value.InstrumentId).Read());
                    if (center != 0)
                    {
                        TicksInput = center;
                    }
                }
                else if (value == null)
                {
                    // Clear inputs if no instrument is selected
                    QuantityInput = null; // Use Property
                    TicksInput = null; // Use Property
                }

                // Explicitly notify strings because if TicksInput didn't change (e.g. same price),
                // the string format might still have changed due to the new Instrument.
                OnPropertyChanged(nameof(TicksInputAsString));
                OnPropertyChanged(nameof(QuantityInputAsString));

                UpdateButtonStates();
            }
        }
    }

    public AmendOrderWrapper? SelectedOrderToAmend
    {
        get => _selectedOrderToAmend;
        set
        {
            if (value != _selectedOrderToAmend)
            {
                _selectedOrderToAmend = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveLadderInstrument));

                if (value != null)
                {
                    QuantityInput = value.OrderState.WorkingQuantity; // Use Property
                    TicksInput = value.OrderState.OrderProfile.Ticks; // Use Property
                }
                else
                {
                    QuantityInput = null; // Use Property
                    TicksInput = null; // Use Property
                }

                // Explicitly notify strings because if TicksInput didn't change,
                // the string format might still have changed or needs refreshing.
                OnPropertyChanged(nameof(TicksInputAsString));
                OnPropertyChanged(nameof(QuantityInputAsString));

                UpdateButtonStates();
            }
        }
    }

    public Instrument? ActiveLadderInstrument => IsCreateMode ? SelectedCreateOrderInstrument : SelectedOrderToAmend?.Instrument;

    private string _quantityInputAsStringOverride = null!;
    public string QuantityInputAsString
    {
        get
        {
            if (string.IsNullOrEmpty(_quantityInputAsStringOverride))
            {
                return QuantityInput?.ToString() ?? string.Empty;
            }
            else
            {
                return _quantityInputAsStringOverride;
            }
        }
        set
        {
            if (value == "-")
            {
                QuantityInput = -0;
                return;
            }
            else if (string.IsNullOrEmpty(value))
            {
                QuantityInput = null;
            }

            if (int.TryParse(value, out int result))
            {
                if (!IsCreateMode && SelectedOrderToAmend != null)
                {
                    Side side = SelectedOrderToAmend.Side;
                    if (side == Side.Buy && result < 0)
                    {
                        ForceRevertQuantityInputAsString();
                        return;
                    }
                    if (side == Side.Sell && result > 0)
                    {
                        ForceRevertQuantityInputAsString();
                        return;
                    }
                }
                QuantityInput = result;
            }
            else
            {
                // Parse failed
                ForceRevertQuantityInputAsString();
            }
        }
    }

    private string _ticksInputAsStringOverride = null!;
    public string TicksInputAsString
    {
        get
        {
            if (string.IsNullOrEmpty(_ticksInputAsStringOverride))
            {
                if (ActiveLadderInstrument != null && TicksInput.HasValue)
                    return ActiveLadderInstrument.TicksToPrice(TicksInput.Value).ToString($"N{ActiveLadderInstrument.TicKDecimals}");
                return string.Empty;
            }
            else
            {
                return _ticksInputAsStringOverride;
            }
        }
        set
        {
            if (double.TryParse(value, out double price))
            {
                if (ActiveLadderInstrument != null)
                {
                    int ticks = ActiveLadderInstrument.RoundToTicks(price);
                    TicksInput = ticks;
                }
            }
            else
            {
                ForceRevertTicksInputAsString();
            }

        }
    }

    private void ForceRevertQuantityInputAsString()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _quantityInputAsStringOverride = " " + QuantityInputAsString;
            OnPropertyChanged(nameof(QuantityInputAsString));
            _quantityInputAsStringOverride = null!;
        }, DispatcherPriority.Default);
    }
    private void ForceRevertTicksInputAsString()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ticksInputAsStringOverride = " " + TicksInputAsString;
            OnPropertyChanged(nameof(TicksInputAsString));
            _ticksInputAsStringOverride = null!;
        }, DispatcherPriority.Default);
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

    /// <summary>
    /// Shared logic to determine the logical center of a book for UI alignment.
    /// </summary>
    public static int GetCentreTIck(in MarketByPrice64 mbp)
    {
        if (mbp.BidsCount > 0 && mbp.AsksCount > 0)
        {
            return (int)((mbp.BestBid.Ticks + mbp.BestAsk.Ticks) * 0.5);
        }
        else if (mbp.BidsCount > 0)
        {
            return mbp.BestBid.Ticks;
        }
        else if (mbp.AsksCount > 0)
        {
            return mbp.BestAsk.Ticks;
        }
        return 0;
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public partial class SendOrderWidget : UserControl, IWidget, IDisposable
{
    public const string TypeKeyStatic = "SendOrderWidget";
    public string TypeKey => TypeKeyStatic;

    private string _title = "Send Order";
    public string Title
    {
        get => _title;
        private set { _title = value; TitleChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? TitleChanged;

    public double DefaultWidth => 600;
    public double DefaultHeight => 277;

    private readonly WorkspaceContext _context;
    private readonly SendOrderViewModel _viewModel;
    private readonly System.Timers.Timer _refreshTimer;
    private bool _disposed;

    private int _ladderScrollOffset = 0;

    // Buffers for Ladder
    private readonly List<RenderedLadderRow> _dataBuffer = new();
    private readonly List<RenderedLadderRow> _rowDataCache = new();

    public SendOrderWidget()
    {
        _context = null!; _viewModel = null!; _refreshTimer = null!;
        InitializeComponent();
    }

    public SendOrderWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _viewModel = new SendOrderViewModel(_context);
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Manual Wiring for Events
        LadderControl.TickSelected += OnLadderTickSelected;
        LadderControl.PointerWheelChanged += OnLadderPointerWheelChanged;
        LadderControl.PointerPressed += OnLadderPointerPressed;

        // Manual Wiring for TextBox Input Filtering (Tunneling ensures checking before TextBox logic)
        PriceTextBox.AddHandler(TextInputEvent, OnPriceTextInput, RoutingStrategies.Tunnel);
        QtyTextBox.AddHandler(TextInputEvent, OnQtyTextInput, RoutingStrategies.Tunnel);

        PopulateInstruments();

        _refreshTimer = new System.Timers.Timer(100);
        _refreshTimer.Elapsed += OnTimerTick;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    // --- Input Filtering Handlers (View Level) ---
    private void OnPriceTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Allow digits, dot, minus
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c) && c != '.' && c != '-')
            {
                e.Handled = true; // Swallow input
                return;
            }
        }
    }

    private void OnQtyTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Allow digits, minus
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c) && c != '-')
            {
                e.Handled = true; // Swallow input
                return;
            }
        }
    }

    private void OnInstrumentComboDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox cb)
        {
            CalculateMaxDropDownHeight(cb);
        }
    }

    private void OnOrderComboDropDownOpened(object? sender, EventArgs e)
    {
        RefreshActiveOrders();

        if (sender is ComboBox cb)
        {
            CalculateMaxDropDownHeight(cb);
        }
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
            // Find reference in our subscribed list to match reference equality if possible
            var match = _viewModel.SubscribedInstruments.FirstOrDefault(i => i.InstrumentId == instrument.InstrumentId);
            _viewModel.SelectedCreateOrderInstrument = match ?? instrument;

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

    private void OnTimerTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            // --- BG Thread Calculation ---

            // 1. Prepare Ladder Data (Heavy lifting)
            List<RenderedLadderRow> ladderUpdate = new List<RenderedLadderRow>();
            if (_viewModel.ActiveLadderInstrument != null)
            {
                ladderUpdate = CalculateLadderRows(_viewModel.ActiveLadderInstrument);
            }

            // 2. Schedule UI Updates
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                // Update Active Orders if needed (UI Thread)
                if (!_viewModel.IsCreateMode && (_viewModel.SelectedOrderToAmend != null || OrderCombo.IsDropDownOpen))
                {
                    RefreshActiveOrders(_viewModel.SelectedOrderToAmend?.ClientOrderId ?? 0);
                }

                // Update Ladder (UI Thread)
                LadderControl.UpdateRows(ladderUpdate);

                // Restart Timer (UI Thread to avoid race conditions on disposal)
                if (!_disposed) _refreshTimer.Start();
            });
        }
        catch
        {
            // If anything fails, try to restart timer from UI thread to keep alive
            if (!_disposed)
                Dispatcher.UIThread.Post(() => { if (!_disposed) _refreshTimer.Start(); });
        }
    }

    // Refactored from RefreshLadder to return list instead of posting
    private List<RenderedLadderRow> CalculateLadderRows(Instrument instrument)
    {

        if (_viewModel.IsAutoCenter)
            _ladderScrollOffset = 0;

        MarketByPrice64 mbp = ContextManager.ServerContext.GetMarketByPrice64(instrument.InstrumentId).Read();

        int centerTick = SendOrderViewModel.GetCentreTIck(in mbp);
        centerTick += _ladderScrollOffset;

        double availableHeight = LadderControl.Bounds.Height;
        if (availableHeight < 50) availableHeight = 400;
        int rowsVisible = (int)(availableHeight / 24.0);

        int halfRows = rowsVisible / 2;
        int startTick = centerTick + halfRows;
        int endTick = startTick - rowsVisible - 1;

        _dataBuffer.Clear();
        int required = (startTick - endTick) + 1;

        // Cache expansion might be unsafe if shared across threads without locks. 
        // Since OnTimerTick is sequential (AutoReset=false), and we only run one calculation at a time, it's safer.
        while (_rowDataCache.Count < required) _rowDataCache.Add(new RenderedLadderRow());

        int rIdx = 0;
        string fmt = $"N{instrument.TicKDecimals}";

        for (int t = startTick; t >= endTick; t--)
        {
            var row = _rowDataCache[rIdx++];
            row.Ticks = t;

            int bid = mbp.GetBidQuantity(t);
            int ask = mbp.GetAskQuantity(t);

            row.Price = instrument.TicksToPrice(t).ToString(fmt);
            row.BidQty = bid > 0 ? bid.ToString() : "";
            row.AskQty = ask > 0 ? ask.ToString() : "";
            row.MyBuyQty = "";
            row.MySellQty = "";

            row.ResetCache();
            _dataBuffer.Add(row);
        }

        // Return a copy or the buffer itself if consumed immediately on UI thread
        return new List<RenderedLadderRow>(_dataBuffer);
    }

    // Maintained purely for legacy calls, now redirects to CalculateLadderRows + Update
    private void RefreshLadder(Instrument instrument)
    {
        var rows = CalculateLadderRows(instrument);
        Dispatcher.UIThread.Post(() => LadderControl.UpdateRows(rows));
    }

    private void RefreshActiveOrders(ulong selectedClientOrderId = 0)
    {
        // --- THREAD SAFETY GUARD ---
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RefreshActiveOrders(selectedClientOrderId));
            return;
        }

        List<(OrderState State, Instrument Inst)> sorted = new();
        foreach ((OrderState state, OrderTarget target) in _context.EnumerateOrders())
        {
            if (state.OrderStateStatus == OrderStateStatus.Active)
            {
                Instrument inst = _context.Primary.GetInstrument(state.OrderHeader.OrderId.InstrumentId);
                sorted.Add((state, inst));
            }
        }

        sorted.Sort((a, b) =>
        {
            int sym = string.CompareOrdinal(a.Inst.Symbol, b.Inst.Symbol);
            if (sym != 0) return sym;
            return b.State.OrderProfile.Ticks.CompareTo(a.State.OrderProfile.Ticks);
        });

        Dictionary<ulong, AmendOrderWrapper> map = new Dictionary<ulong, AmendOrderWrapper>();
        ObservableCollection<AmendOrderWrapper> actives = _viewModel.ActiveOrders;
        foreach (AmendOrderWrapper amend in actives)
        {
            map[amend.ClientOrderId] = amend;
        }

        int newIndex = 0, activeIndex = 0;
        while (newIndex < sorted.Count && activeIndex < actives.Count)
        {
            if (sorted[newIndex].State.OrderHeader.OrderId == actives[activeIndex].ClientOrderId)
            {
                // Match: Update in place
                actives[activeIndex].Update(sorted[newIndex].State, sorted[newIndex].Inst);
                newIndex++;
                activeIndex++;
            }
            else if (map.TryGetValue(sorted[newIndex].State.OrderHeader.OrderId, out AmendOrderWrapper? amend))
            {
                // Mismatch, but item exists elsewhere. Move it here.
                // We use Move() to preserve the object and selection state.
                int oldIndex = actives.IndexOf(amend!);
                if (oldIndex >= 0)
                {
                    actives.Move(oldIndex, activeIndex);
                    actives[activeIndex].Update(sorted[newIndex].State, sorted[newIndex].Inst);
                }
                newIndex++;
                activeIndex++;
            }
            else
            {
                // New item. Insert here.
                actives.Insert(activeIndex, new AmendOrderWrapper(sorted[newIndex].State, sorted[newIndex].Inst));
                newIndex++;
                activeIndex++;
            }
        }

        // Add any remaining items from sorted
        while (newIndex < sorted.Count)
        {
            actives.Add(new AmendOrderWrapper(sorted[newIndex].State, sorted[newIndex].Inst));
            newIndex++;
        }

        // Remove any extra items from actives
        while (actives.Count > sorted.Count)
        {
            actives.RemoveAt(actives.Count - 1);
        }
        var wrapper = _viewModel.ActiveOrders.FirstOrDefault(x => x?.ClientOrderId == selectedClientOrderId, null);
        _viewModel.SelectedOrderToAmend = wrapper;

    }

    private void OnLadderTickSelected(int ticks)
    {
        _viewModel.TicksInput = ticks;
    }

    private void OnLadderPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel.IsAutoCenter)
        {
            e.Handled = true;
            return;
        }
        _ladderScrollOffset += (int)e.Delta.Y;
        if (_viewModel.ActiveLadderInstrument != null)
            RefreshLadder(_viewModel.ActiveLadderInstrument);
        e.Handled = true;
    }

    private void OnLadderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(LadderControl).Properties.IsMiddleButtonPressed)
        {
            ScrollToMiddle();
            if (_viewModel.ActiveLadderInstrument != null)
                RefreshLadder(_viewModel.ActiveLadderInstrument);
            e.Handled = true;
        }
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
                // template: ClientId is needed for TCPServer routing on the remote path;
                // Client.Create stamps ClientId/StrategyId authoritatively before allocation
                OrderId = new OrderId
                {
                    ClientId = _context.Manual.ClientId(),
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
        if (_viewModel.SelectedOrderToAmend == null || _viewModel.TicksInput == null || _viewModel.QuantityInput == null)
            return;

        var state = _viewModel.SelectedOrderToAmend.OrderState;

        int workingQuantity = _viewModel.QuantityInput.Value;
        int newQuantity = state.QuantityFilled + workingQuantity;

        OrderTarget target = new OrderTarget
        {
            OrderHeader = state.OrderHeader,
            OrderProfile = new OrderProfile(_viewModel.TicksInput.Value, newQuantity),
            OrderTargetAction = OrderTargetAction.Amend
        };
        target.OrderHeader.Seq += 1;
        _context.Manual.OnOrderTarget(ref target);
    }

    private void OnSendCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedOrderToAmend == null) return;
        var state = _viewModel.SelectedOrderToAmend.OrderState;
        OrderTarget target = new OrderTarget
        {
            OrderHeader = state.OrderHeader,
            OrderProfile = state.OrderProfile,
            OrderTargetAction = OrderTargetAction.Cancel
        };
        target.OrderHeader.Seq += 1000; // make sure algo cant overwrite the seq with an amend
        _context.Manual.OnOrderTarget(ref target);
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refreshTimer?.Stop();
    }
}