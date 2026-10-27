//BEGIN_FILE HFT/Widget/PositionsWidget.axaml.cs
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
using Tools;

namespace Widget;

/// One instrument's row in the Positions grid: its profit, refreshed in place by the widget's timer thread. Cold path.
public sealed class WidgetPosition : INotifyPropertyChanged
{
    public Instrument Instrument => _position.Instrument;
    public string Symbol => _position.Instrument.Symbol;
    public string ShortSymbol => _position.Instrument.ShortSymbol;
    public int InstrumentId => _position.Instrument.InstrumentId;

    public int StrategyId => _position.PositionHeader.GetReadonlyRef().OrderHeader.OrderId.StrategyId;
    public AlgoStatus AlgoStatus => _position.AlgoStatus;
    public string AvgPriceStr => _profit.AvgPrice.ToString(_avgPriceFormat);
    public double AvgPrice => _profit.AvgPrice;   // the AvgPrice column's sort key: its text would sort 10,000 before 9,000
    public double Profit => _profit.Total;
    public double Floating => _profit.Floating;
    public Side Side => (Side)Math.Sign(_profit.Quantity);
    public double Realized => _profit.Realized;
    public int Quantity => _profit.Quantity;
    public int QuantityTraded => _position.PositionHeader.GetReadonlyRef().QuantityTraded;
    public double ProfitPerSide => QuantityTraded == 0 ? 0 : Profit / QuantityTraded;

    private readonly Position _position;
    private Profit _profit;
    private readonly string _avgPriceFormat;   // Why two more decimals than the instrument's PriceFormat: an average of fills falls between ticks

    public event PropertyChangedEventHandler? PropertyChanged;

    public WidgetPosition(Position position)
    {
        _position = position;
        _profit = _position.Profit;
        _avgPriceFormat = $"N{position.Instrument.TickDecimals + 2}";
    }

    /// Takes the position's latest profit and tells the grid every column changed.
    public void Refresh()
    {
        _profit = _position.Profit;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}

/// Lists the primary client's position on each allocated instrument, with algo pause and resume, order entry and a
/// ladder on right-click, and Pause/Resume All in the title bar. The rows are refreshed on a timer thread and applied
/// on the UI thread. Cold path (GUI).
public sealed partial class PositionsWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;
    private readonly Dictionary<int, WidgetPosition> _widgetPositionsByInstrumentId = new();   // timer thread only
    private readonly List<WidgetPosition> _activeWidgetPositions = new();   // timer thread writes; the UI post reads it before the next pass

    public string Title { get; private set; }
    public ObservableCollection<WidgetPosition> Rows { get; } = new();

    public double DefaultWidth => 715;
    public double DefaultHeight => 185;

    public object? TitleBarContent => Resources["TitleBarButtons"];

    private Avalonia.Point _lastPointerPosition;   // the pointer over the grid, where a right-click menu was asked for

    public PositionsWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Positions (Design)";
    }

    public PositionsWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Title = "Positions";
        InitializeComponent();
        DataContext = this;
        PositionsGrid.ItemsSource = Rows;
        PositionsGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(PositionsGrid);
        _refreshTimer = new WidgetRefreshTimer(nameof(PositionsWidget), 100, Refresh);
    }

    /// Fills the grid's right-click menu: the all-algo items and column toggles on a header, the position's items on a row,
    /// the all-algo items elsewhere.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        DataGridHit dataGridHit = PositionsGrid.GetDataGridHit(_lastPointerPosition);
        if (dataGridHit.IsColumnHeader)
        {
            AddAllAlgoStatusMenuItems(contextMenu);
            contextMenu.Items.Add(new Separator());
            contextMenu.AddColumnVisibilityMenuItems(PositionsGrid);
        }
        else if (dataGridHit.RowItem is WidgetPosition widgetPosition)
            AddPositionMenuItems(contextMenu, widgetPosition);
        else
            AddAllAlgoStatusMenuItems(contextMenu);
    }

    /// Pause All Algos and Resume All Algos.
    private void AddAllAlgoStatusMenuItems(ContextMenu contextMenu)
    {
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Pause All Algos", "⏸", () => PerformChangeAllAlgoStatus(AlgoStatus.Paused)));
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Resume All Algos", "▶", () => PerformChangeAllAlgoStatus(AlgoStatus.Live)));
    }

    /// The position's title, its algo's pause or resume, order entry and the ladder.
    private void AddPositionMenuItems(ContextMenu contextMenu, WidgetPosition widgetPosition)
    {
        // Step 1: title and the algo's pause or resume, whichever changes it
        contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem(widgetPosition.ShortSymbol));
        contextMenu.Items.Add(new Separator());
        bool isAlgoLive = widgetPosition.AlgoStatus == AlgoStatus.Live;
        contextMenu.Items.Add(WidgetMenus.NewMenuItem(isAlgoLive ? "Pause Algo" : "Resume Algo", isAlgoLive ? "⏸" : "▶",
            () => PerformChangeAlgoStatus(widgetPosition, isAlgoLive ? AlgoStatus.Paused : AlgoStatus.Live)));

        // Step 2: order entry
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Create Order", "📝", () => this.AddToHost(SendOrderWidget.NewCreateOrder(_context, widgetPosition.Instrument))));
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Create Close Order", "❌", () => this.AddToHost(SendOrderWidget.NewCreateOrder(_context, widgetPosition.Instrument, quantity: -widgetPosition.Quantity))));

        // Step 3: the ladder
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(WidgetMenus.NewMenuItem("Open Ladder", "🪜", () => this.AddToHost(LadderWidget.NewLadder(_context, widgetPosition.InstrumentId))));
    }

    /// Sends the control that pauses or resumes the workspace's switch on this position's instrument (the master switch from a server workspace).
    private void PerformChangeAlgoStatus(WidgetPosition widgetPosition, AlgoStatus targetAlgoStatus)
    {
        // Step 1: the server polls the control's execution channel only once the manual client holds the instrument
        _context.AllocateToManualClient(widgetPosition.InstrumentId);

        // Step 2: send the control
        ControlAlgoStatus controlAlgoStatus = new ControlAlgoStatus()
        {
            AlgoStatus = targetAlgoStatus,
            InstrumentId = widgetPosition.InstrumentId,
            StrategyId = _context.Primary.StrategyId,
            ClientId = _context.Manual.Context.ClientId
        };
        _context.Manual.OnControlAlgoStatus(controlAlgoStatus);
    }

    /// Pauses or resumes the algo of every position the grid shows. Why Rows: only the UI thread writes it, so a click mid-refresh sees a whole list
    private void PerformChangeAllAlgoStatus(AlgoStatus targetAlgoStatus)
    {
        foreach (WidgetPosition widgetPosition in Rows)
            PerformChangeAlgoStatus(widgetPosition, targetAlgoStatus);
    }

    public void OnPauseAllClick(object? sender, RoutedEventArgs e) => PerformChangeAllAlgoStatus(AlgoStatus.Paused);

    public void OnResumeAllClick(object? sender, RoutedEventArgs e) => PerformChangeAllAlgoStatus(AlgoStatus.Live);

    /// Adds a row for each instrument newly allocated to the primary client, refreshes every row and posts them to the grid with
    /// their total profit in the title bar. Timer thread.
    private void Refresh()
    {
        // Step 1: a row for each newly allocated instrument
        Bitset64 instrumentIds = _context.Primary.InstrumentIds;
        foreach (int instrumentId in instrumentIds)
        {
            if (!_widgetPositionsByInstrumentId.ContainsKey(instrumentId))
                _widgetPositionsByInstrumentId.Add(instrumentId, new WidgetPosition(_context.Primary.GetPosition(instrumentId)));
        }

        // Step 2: refresh every row and add up their profit
        _activeWidgetPositions.Clear();
        double _totalProfit = 0;
        foreach (WidgetPosition widgetPosition in _widgetPositionsByInstrumentId.Values)
        {
            widgetPosition.Refresh();
            _activeWidgetPositions.Add(widgetPosition);
            _totalProfit += widgetPosition.Profit;
        }

        // Step 3: post the rows to the grid and the total to the title bar, coloured by its sign as the ladder's PnL
        double totalProfit = _totalProfit;
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshTimer.IsDisposed)
                return;
            Rows.ReconcileRows(_activeWidgetPositions);
            TotalProfitValue.Text = $"{totalProfit:N2}";
            TotalProfitValue.Foreground = Palette.GetSignBrush(totalProfit, Palette.ProfitGreen, Palette.LossRed);
        });
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }

    public void Dispose() => _refreshTimer?.Dispose();
}
//END_FILE HFT/Widget/PositionsWidget.axaml.cs
