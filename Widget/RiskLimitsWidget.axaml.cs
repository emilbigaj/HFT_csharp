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


/// One instrument's risk-limit grid row: the principal's limits, working risk and position for it. Refreshed on the widget's
/// refresh timer thread; cold path.
public sealed class WidgetRiskLimit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Instrument _instrument;
    private RiskLimit _riskLimit;
    private WorkingRisk _workingRisk;
    private int _positionQuantity;

    public string Symbol => _instrument.Symbol;
    public string ShortSymbol => _instrument.ShortSymbol;
    public int InstrumentId => _instrument.InstrumentId;
    public string RiskLimitFileName => _instrument.Symbol + ".risklimit";

    public int PositionQuantity => _positionQuantity;
    public int MaxOrderQuantity => _riskLimit.MaxOrderQuantity;
    public int MaxPositionQuantity => _riskLimit.MaxPositionQuantity;
    public int MaxFillsPerSession => _riskLimit.MaxFillsPerSession;

    public int LongQuantityAllowance => _riskLimit.GetLongQuantityAllowance(in _workingRisk);
    public int ShortQuantityAllowance => _riskLimit.GetShortQuantityAllowance(in _workingRisk);

    public int FillsThisSession => _workingRisk.FillsThisSession;
    public int WorstWorkingLongQuantity => _workingRisk.WorstWorkingLongQuantity;
    public int WorstWorkingLongPosition => _positionQuantity + _workingRisk.WorstWorkingLongQuantity;     // the position if every working buy filled
    public int WorstWorkingShortQuantity => _workingRisk.WorstWorkingShortQuantity;
    public int WorstWorkingShortPosition => _positionQuantity + _workingRisk.WorstWorkingShortQuantity;   // the position if every working sell filled
    public int WorstWorkingFills => _workingRisk.WorstWorkingFills;
    public int WorstWorkingFillsThisSession => _workingRisk.FillsThisSession + _workingRisk.WorstWorkingFills;   // the session's fills if every working algo lot filled

    public string Timestamp => _riskLimit.Timestamp == Tools.Timestamp.MinValue ? "—" : _riskLimit.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    public WidgetRiskLimit(Instrument instrument) => _instrument = instrument;

    /// Takes the instrument's latest limits, working risk and position and re-evaluates every binding.
    public void Refresh(in RiskLimit riskLimit, in WorkingRisk workingRisk, int positionQuantity)
    {
        _riskLimit = riskLimit;
        _workingRisk = workingRisk;
        _positionQuantity = positionQuantity;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));   // null: every property
    }
}



/// Lists the principal's risk limits for each instrument allocated in the workspace's principal, refreshed every 250 ms: a server
/// workspace's are the firm's, a strategy workspace's the strategy's own. The right-click menu toggles columns on a header, and
/// edits the limits and offers the principal's .risklimit file and folder on a row. UI thread and the refresh timer's thread; cold path.
public sealed partial class RiskLimitsWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;

    private readonly Dictionary<int, WidgetRiskLimit> _rowsByInstrumentId = new();   // refresh timer thread only
    public ObservableCollection<WidgetRiskLimit> Rows { get; } = new();

    public string Title { get; private set; } = "Risk Limits";
    public double DefaultWidth => 820;
    public double DefaultHeight => 320;

    private Avalonia.Point _lastPointerPosition;

    public RiskLimitsWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Risk Limits (Design)";
    }

    public RiskLimitsWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        RiskLimitsGrid.ItemsSource = Rows;

        RiskLimitsGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(RiskLimitsGrid);

        _refreshTimer = new WidgetRefreshTimer(nameof(RiskLimitsWidget), 250, Refresh);
    }

    // The principal's own rows, as its RiskLayer checks them: the firm's limit against the firm's exposure, or a strategy's against its own.
    private RiskLimit GetRiskLimit(int instrumentId) => _context.Primary.GetRiskLimit(instrumentId).Read();

    private WorkingRisk GetWorkingRisk(int instrumentId) => _context.Primary.GetWorkingRisk(instrumentId).Read();

    private int GetPositionQuantity(int instrumentId) => _context.Primary.GetPosition(instrumentId).Profit.Quantity;

    /// Refreshes each allocated instrument's row in place, creating it on first sight, and posts the active rows to the grid.
    private void Refresh()
    {
        // Step 1: each allocated instrument's row
        List<WidgetRiskLimit> activeRows = new(_rowsByInstrumentId.Count);
        foreach (int instrumentId in _context.Primary.InstrumentIds)
        {
            _rowsByInstrumentId.TryGetValue(instrumentId, out WidgetRiskLimit? widgetRiskLimit);
            // Why the catch: an instrument not fully ready yet gets its row, or its row's new values, on a later pass
            try
            {
                RiskLimit riskLimit = GetRiskLimit(instrumentId);
                WorkingRisk workingRisk = GetWorkingRisk(instrumentId);
                int positionQuantity = GetPositionQuantity(instrumentId);
                if (widgetRiskLimit == null)
                {
                    widgetRiskLimit = new WidgetRiskLimit(_context.Primary.GetInstrument(instrumentId));
                    _rowsByInstrumentId[instrumentId] = widgetRiskLimit;
                }
                widgetRiskLimit.Refresh(in riskLimit, in workingRisk, positionQuantity);
            }
            catch { }
            if (widgetRiskLimit != null)
                activeRows.Add(widgetRiskLimit);
        }

        // Step 2: the grid shows exactly the active rows
        Dispatcher.UIThread.Post(() =>
        {
            if (!_refreshTimer.IsDisposed)
                Rows.ReconcileRows(activeRows);
        });
    }

    /// Fills the grid's right-click menu: column toggles on a header; edit, the instrument's file path and the folder on a row, the folder elsewhere.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        // Step 1: a header lists the columns to toggle
        DataGridHit dataGridHit = RiskLimitsGrid.GetDataGridHit(_lastPointerPosition);
        if (dataGridHit.IsColumnHeader)
        {
            contextMenu.AddColumnVisibilityMenuItems(RiskLimitsGrid);
            return;
        }

        // Step 2: a row names its instrument, edits its limits and copies its file path
        Context primary = _context.Primary;
        if (dataGridHit.RowItem is WidgetRiskLimit widgetRiskLimit)
        {
            contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem(widgetRiskLimit.ShortSymbol));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(WidgetMenus.NewMenuItem("Edit RiskLimit", "✏", async () => await EditRiskLimit(widgetRiskLimit)));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(WidgetMenus.NewCopyPathMenuItem(this, "Copy .risklimit File Path", Context.GetRiskLimitsFilePath(primary.DirectoryPath, widgetRiskLimit.Symbol)));
        }

        // Step 3: the folder, from anywhere off the header
        contextMenu.Items.Add(WidgetMenus.NewOpenFolderMenuItem("Open RiskLimits Folder", primary.RiskLimitsDirectoryPath.ToString()));
    }

    /// Opens the edit dialog on the instrument's current limits and sends the edit to the server as a ControlRiskLimit; logs a failure.
    private async System.Threading.Tasks.Task EditRiskLimit(WidgetRiskLimit widgetRiskLimit)
    {
        // Why the catch: a failed edit must not take down the GUI; it is logged and the limits stay as they were
        try
        {
            // Step 1: the dialog, on a fresh read rather than the row's cached limits, which can be one refresh stale
            if (TopLevel.GetTopLevel(this) is not Window window)
                return;
            RiskLimit riskLimit = GetRiskLimit(widgetRiskLimit.InstrumentId);
            RiskLimitEditDialog riskLimitEditDialog = new(widgetRiskLimit.ShortSymbol, riskLimit);
            ControlRiskLimit? editedControlRiskLimit = await riskLimitEditDialog.ShowDialog<ControlRiskLimit?>(window);
            if (editedControlRiskLimit == null)
                return;

            // Step 2: send the request on the instrument's execution channel; the server stamps and applies it, the logging server appends the .risklimit line, the grid shows it on its next refresh
            _context.AllocateToManualClient(widgetRiskLimit.InstrumentId);
            ControlRiskLimit controlRiskLimit = editedControlRiskLimit.Value;
            controlRiskLimit.ClientId = _context.Manual.Context.ClientId;
            controlRiskLimit.StrategyId = _context.Primary.StrategyId;
            _context.Manual.OnControlRiskLimit(in controlRiskLimit);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"RiskLimitsWidget.EditRiskLimit: {exception.Message}");
        }
    }

    public string? SaveStateJson() => DataGridWidgetState.NewStateJson(RiskLimitsGrid);

    public void LoadStateJson(string? json) => DataGridWidgetState.LoadStateJson(RiskLimitsGrid, json);

    public void Dispose() => _refreshTimer?.Dispose();
}
