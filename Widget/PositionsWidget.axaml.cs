//BEGIN_FILE HFT/Widget/PositionsWidget.axaml.cs
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Provider;
using Tools;


using System.ComponentModel;
using System.Runtime.CompilerServices;
using Data;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia;
using Execution;

namespace Widget;


public sealed class WidgetPosition : INotifyPropertyChanged
{
    public string Symbol => _position.Instrument.Symbol;
    public string ShortSymbol => _position.Instrument.ShortSymbol;
    public int InstrumentId => _position.Instrument.InstrumentId;

    public int StrategyId => _position.Header.OrderHeader.OrderId.StrategyId;
    public readonly Position _position;
    private Profit _profit;
    public AlgoStatus AlgoStatus => _position.AlgoStatus;
    public double AvgPrice => _profit.AvgPrice;
    public string AvgPriceStr => _profit.AvgPrice.ToString(_avgPriceFormat);
    public double Profit => _profit.Total;
    public double Floating => _profit.Floating;
    public Side Side => (Side)Math.Sign(_profit.Quantity);
    public double Realized => _profit.Realized;
    public int Quantity => _profit.Quantity;
    public int QuantityTraded => _position.Header.QuantityTraded;

    public Timestamp Timestamp => _profit.Timestamp;

    public event PropertyChangedEventHandler? PropertyChanged;
    private string _avgPriceFormat;
    public WidgetPosition(Position position)
    {
        _position = position;
        _profit = _position.Profit;
        _avgPriceFormat = $"N{position.Instrument.TicKDecimals + 2}";
    }

    public void Refresh()
    {
        var newProfit = _position.Profit;
        _profit = newProfit;
        OnPropertyChanged("");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


public sealed partial class PositionsWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;
    private Dictionary<int, WidgetPosition> _positions = new Dictionary<int, WidgetPosition>();
    private List<WidgetPosition> _active = new List<WidgetPosition>();

    public string TypeKey => "PositionsWidget";
    public string Title { get; private set; }
    public ObservableCollection<WidgetPosition> Rows { get; } = new();

    public double DefaultWidth => 715;
    public double DefaultHeight => 185;

    // --- EXPOSE XAML RESOURCE TO WIDGET CONTAINER ---
    public object? TitleBarContent { get; private set; }

    // Cached pointer position for hit testing context menu
    private Avalonia.Point _lastPointerPos;

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

        // Grab the buttons we designed in AXAML
        if (this.Resources.TryGetValue("TitleBarButtons", out var buttons))
        {
            TitleBarContent = buttons;
        }

        // Pointer tracking for Context Menu Hit Testing
        PositionsGrid.PointerMoved += OnPositionsGridPointerMoved;

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;

        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    private void OnPositionsGridPointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerPos = e.GetPosition(PositionsGrid);
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || PositionsGrid == null) return;

        menu.Items.Clear();

        var visual = PositionsGrid.InputHitTest(_lastPointerPos) as Visual;

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
            MenuItem pauseAllItem = new MenuItem { Header = "Pause All Algos", Icon = new TextBlock { Text = "⏸" } };
            pauseAllItem.Click += (_, _) => PerformChangeAllAlgoStatus(AlgoStatus.Paused);
            menu.Items.Add(pauseAllItem);

            MenuItem resumeAllItem = new MenuItem { Header = "Resume All Algos", Icon = new TextBlock { Text = "▶" } };
            resumeAllItem.Click += (_, _) => PerformChangeAllAlgoStatus(AlgoStatus.Live);
            menu.Items.Add(resumeAllItem);

            menu.Items.Add(new Separator());

            foreach (var col in PositionsGrid.Columns)
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
        else if (hitRow != null && hitRow.DataContext is WidgetPosition pos)
        {
            var headerItem = new MenuItem { Header = $"{pos.ShortSymbol}" };
            menu.Items.Add(headerItem);

            menu.Items.Add(new Separator());

            if (pos.AlgoStatus == AlgoStatus.Live)
            {
                MenuItem algoItem = new MenuItem { Header = $"Pause Algo", Icon = new TextBlock { Text = "⏸" } };
                algoItem.Click += (_, _) => PerformChangeAlgoStatus(pos, AlgoStatus.Paused);
                menu.Items.Add(algoItem);
            }
            else
            {
                MenuItem algoItem = new MenuItem { Header = $"Resume Algo", Icon = new TextBlock { Text = "▶" } };
                algoItem.Click += (_, _) => PerformChangeAlgoStatus(pos, AlgoStatus.Live);
                menu.Items.Add(algoItem);
            }

            menu.Items.Add(new Separator());

            var createItem = new MenuItem { Header = $"Create Order", Icon = new TextBlock { Text = "📝" } };
            createItem.Click += (_, _) => PerformCreateOrder(pos);
            menu.Items.Add(createItem);

            var closeItem = new MenuItem { Header = "Create Close Order", Icon = new TextBlock { Text = "❌" } };
            closeItem.Click += (_, _) => PerformClosePosition(pos);
            menu.Items.Add(closeItem);

            menu.Items.Add(new Separator());

            var ladderItem = new MenuItem { Header = "Open Ladder", Icon = new TextBlock { Text = "🪜" } };
            ladderItem.Click += (_, _) => PerformOpenLadder(pos);
            menu.Items.Add(ladderItem);
        }
        else
        {
            MenuItem pauseAllItem = new MenuItem { Header = "Pause All Algos", Icon = new TextBlock { Text = "⏸" } };
            pauseAllItem.Click += (_, _) => PerformChangeAllAlgoStatus(AlgoStatus.Paused);
            menu.Items.Add(pauseAllItem);

            MenuItem resumeAllItem = new MenuItem { Header = "Resume All Algos", Icon = new TextBlock { Text = "▶" } };
            resumeAllItem.Click += (_, _) => PerformChangeAllAlgoStatus(AlgoStatus.Live);
            menu.Items.Add(resumeAllItem);
        }
    }

    private void PerformChangeAlgoStatus(WidgetPosition pos, AlgoStatus targetAlgoStatus)
    {
        ControlAlgoStatus controlAlgoStatus = new ControlAlgoStatus()
        {
            AlgoStatus = targetAlgoStatus,
            InstrumentId = pos.InstrumentId,
            StrategyId = pos.StrategyId,
            ClientId = _context.Manual.ClientId()
        };
        _context.Manual.OnControlAlgoStatus(controlAlgoStatus);
    }

    private void PerformChangeAllAlgoStatus(AlgoStatus targetAlgoStatus)
    {
        foreach (var pos in _positions.Values)
        {
            ControlAlgoStatus controlAlgoStatus = new ControlAlgoStatus()
            {
                AlgoStatus = targetAlgoStatus,
                InstrumentId = pos.InstrumentId,
                StrategyId = pos.StrategyId,
                ClientId = _context.Manual.ClientId()
            };
            _context.Manual.OnControlAlgoStatus(controlAlgoStatus);
        }
    }

    public void OnPauseAllClick(object? sender, RoutedEventArgs e)
    {
        PerformChangeAllAlgoStatus(AlgoStatus.Paused);
    }

    public void OnResumeAllClick(object? sender, RoutedEventArgs e)
    {
        PerformChangeAllAlgoStatus(AlgoStatus.Live);
    }

    private void PerformCreateOrder(WidgetPosition pos)
    {
        var host = this.FindAncestorOfType<Window>() as IWidgetHost;
        if (host != null)
        {
            var sendOrder = new SendOrderWidget(_context);
            sendOrder.SetCreateOrder(pos._position.Instrument);
            host.AddWidget(sendOrder);
        }
    }

    private void PerformClosePosition(WidgetPosition pos)
    {
        var host = this.FindAncestorOfType<Window>() as IWidgetHost;
        if (host != null)
        {
            int closingQty = -pos.Quantity;
            var sendOrder = new SendOrderWidget(_context);
            sendOrder.SetCreateOrder(pos._position.Instrument, null, closingQty);
            host.AddWidget(sendOrder);
        }
    }

    private void PerformOpenLadder(WidgetPosition pos)
    {
        var host = this.FindAncestorOfType<Window>() as IWidgetHost;
        if (host != null)
        {
            var ladder = new LadderWidget(_context);
            ladder.SetInstrument(pos.InstrumentId);
            host.AddWidget(ladder);
        }
    }

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            Bitset64 instrumentIds = _context.Primary.InstrumentIds;
            foreach (int instrumentId in instrumentIds)
            {
                if (!_positions.ContainsKey(instrumentId))
                {
                    _positions.Add(instrumentId, new WidgetPosition(_context.Primary.GetPosition(instrumentId)));
                }
            }

            _active.Clear();
            foreach (var pos in _positions.Values)
            {
                pos.Refresh();
                _active.Add(pos);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                    return;

                for (int i = Rows.Count - 1; i >= 0; i--)
                {
                    if (!_active.Contains(Rows[i]))
                    {
                        Rows.RemoveAt(i);
                    }
                }

                foreach (var p in _active)
                {
                    if (!Rows.Contains(p))
                    {
                        Rows.Add(p);
                    }
                }

                _refreshTimer.Start();
            });
        }
        catch (Exception)
        {
            if (!_disposed)
                _refreshTimer.Start();
        }
    }

    public string? SaveStateJson() => null;
    public void LoadStateJson(string? json) { }
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (PositionsGrid != null)
            PositionsGrid.PointerMoved -= OnPositionsGridPointerMoved;

        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
    }
}
//END_FILE HFT/Widget/PositionsWidget.axaml.cs