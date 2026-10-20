using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Socket;
using Tools;

namespace Widget;


/// One product group's message-efficiency grid row: raw and weighted messages, quantity traded, efficiency against the tier
/// benchmarks. Refreshed on the widget's refresh timer thread; cold path.
public sealed class WidgetMessageEfficiency : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private MessageEfficiency _messageEfficiency;

    public string ProductGroupRoot => _messageEfficiency.ProductGroupRoot.ToString();
    public string RawMessages => _messageEfficiency.RawMessages.ToString("N0");
    public string WeightedMessages => _messageEfficiency.WeightedMessages.ToString("N2");
    public string QuantityTraded => _messageEfficiency.QuantityTraded.ToString("N0");
    public string Efficiency => _messageEfficiency.Efficiency.ToString("N2");
    public string EMTWeightedMessages => _messageEfficiency.EMTWeightedMessages.ToString("N2");
    public string EMTEfficiency => _messageEfficiency.EMTEfficiency.ToString("N2");

    public string TierEMT => _messageEfficiency.TierEMT.DailyRawMessages.ToString("N0");
    public string EMTBenchmark => _messageEfficiency.TierEMT.Benchmark.ToString("N0");
    public string Tier1 => _messageEfficiency.Tier1.DailyRawMessages.ToString("N0");
    public string Tier1Benchmark => _messageEfficiency.Tier1.Benchmark.ToString("N0");
    public string Tier2 => _messageEfficiency.Tier2.DailyRawMessages.ToString("N0");
    public string Tier2Benchmark => _messageEfficiency.Tier2.Benchmark.ToString("N0");
    public string Tier3 => _messageEfficiency.Tier3.DailyRawMessages.ToString("N0");
    public string Tier3Benchmark => _messageEfficiency.Tier3.Benchmark.ToString("N0");

    public string TradeDate => _messageEfficiency.TradeDate.ToString("yyyy-MM-dd");

    public string TradeTime => _messageEfficiency.TradeTime.ToString(@"hh\:mm\:ss");

    // Efficiency cell highlight, by the nearer to its benchmark of the EMT and the active MEP tier: green well under, yellow within
    // 25%, orange within 10%, red at/over it. Below every applied tier's raw-message threshold it's unconstrained (green).
    public IBrush EfficiencyBrush => EfficiencyStatusBrush(in _messageEfficiency);

    private static readonly IBrush s_safeBrush     = new ImmutableSolidColorBrush(Palette.Lighten(Palette.SafeGreen, 0.6));
    private static readonly IBrush s_warningBrush  = new ImmutableSolidColorBrush(Palette.Lighten(Palette.WarningYellow, 0.6));
    private static readonly IBrush s_cautionBrush  = new ImmutableSolidColorBrush(Palette.Lighten(Palette.CautionOrange, 0.6));
    private static readonly IBrush s_criticalBrush = new ImmutableSolidColorBrush(Palette.Lighten(Palette.CriticalRed, 0.6));

    private static IBrush EfficiencyStatusBrush(in MessageEfficiency messageEfficiency)
    {
        // Step 1: the nearer to its benchmark of the EMT and the MEP tier the day is in; 0 while neither applies
        int rawMessages = messageEfficiency.RawMessages;
        double emtRatio = messageEfficiency.TierEMT.GetBenchmarkRatio(rawMessages, messageEfficiency.EMTEfficiency);
        double mepRatio = messageEfficiency.GetMEPTier(rawMessages).GetBenchmarkRatio(rawMessages, messageEfficiency.Efficiency);
        double ratio = Math.Max(emtRatio, mepRatio);

        // Step 2: colour by how near
        if (ratio >= 1.00) return s_criticalBrush;   // at or over the benchmark
        if (ratio >= 0.90) return s_cautionBrush;    // within 10% of the benchmark
        if (ratio >= 0.75) return s_warningBrush;    // within 25% of the benchmark
        return s_safeBrush;
    }

    /// Takes the product group's latest values from shared memory and re-evaluates every binding.
    public void Refresh(in MessageEfficiency messageEfficiency)
    {
        _messageEfficiency = messageEfficiency;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));   // null: every property
    }
}



/// Lists the server's message-efficiency rows, one per product group, refreshed every 100 ms. The right-click menu toggles
/// columns on a header and offers the server's .messageefficiency file and folder on a row. UI thread and the refresh timer's
/// thread; cold path.
public sealed partial class MessageEfficiencyWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly WidgetRefreshTimer _refreshTimer;

    private readonly Dictionary<int, WidgetMessageEfficiency> _rowsByProductGroupId = new();   // refresh timer thread only
    public ObservableCollection<WidgetMessageEfficiency> Rows { get; } = new();

    public string Title { get; private set; } = "Message Efficiency";
    public double DefaultWidth => 820;
    public double DefaultHeight => 320;

    private Avalonia.Point _lastPointerPosition;

    public MessageEfficiencyWidget()
    {
        _context = null!;
        _refreshTimer = null!;
        InitializeComponent();
        DataContext = this;
        Title = "Message Efficiency (Design)";
    }

    public MessageEfficiencyWidget(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        InitializeComponent();
        DataContext = this;
        MessageEfficiencyGrid.ItemsSource = Rows;

        MessageEfficiencyGrid.PointerMoved += (_, pointerEventArgs) => _lastPointerPosition = pointerEventArgs.GetPosition(MessageEfficiencyGrid);

        _refreshTimer = new WidgetRefreshTimer(nameof(MessageEfficiencyWidget), 100, Refresh);
    }

    /// Refreshes each product group's row in place and posts the active rows to the grid.
    private void Refresh()
    {
        // Step 1: each allocated product group's row, refreshed in place (allocated densely from 0; the enumeration stops at the first empty slot)
        List<WidgetMessageEfficiency> activeRows = new(_rowsByProductGroupId.Count);
        foreach (MessageEfficiency messageEfficiency in _context.Primary.EnumerateMessageEfficiency())
        {
            if (!_rowsByProductGroupId.TryGetValue(messageEfficiency.ProductGroupId, out WidgetMessageEfficiency? widgetMessageEfficiency))
            {
                widgetMessageEfficiency = new WidgetMessageEfficiency();
                _rowsByProductGroupId[messageEfficiency.ProductGroupId] = widgetMessageEfficiency;
            }
            widgetMessageEfficiency.Refresh(in messageEfficiency);
            activeRows.Add(widgetMessageEfficiency);
        }

        // Step 2: the grid shows exactly the active rows
        Dispatcher.UIThread.Post(() =>
        {
            if (!_refreshTimer.IsDisposed)
                Rows.ReconcileRows(activeRows);
        });
    }

    /// Fills the grid's right-click menu: column toggles on a header; the product group's file path and the folder on a row, the folder elsewhere.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();

        // Step 1: a header lists the columns to toggle
        DataGridHit dataGridHit = MessageEfficiencyGrid.GetDataGridHit(_lastPointerPosition);
        if (dataGridHit.IsColumnHeader)
        {
            contextMenu.AddColumnVisibilityMenuItems(MessageEfficiencyGrid);
            return;
        }

        // Step 2: a row names its product group and copies its file path
        ServerContext serverContext = ContextManager.ServerContext;
        if (dataGridHit.RowItem is WidgetMessageEfficiency widgetMessageEfficiency)
        {
            contextMenu.Items.Add(WidgetMenus.NewTitleMenuItem(widgetMessageEfficiency.ProductGroupRoot));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(WidgetMenus.NewCopyPathMenuItem(this, "Copy Server .messageefficiency File Path", Context.GetMessageEfficiencyFilePath(serverContext.DirectoryPath, widgetMessageEfficiency.ProductGroupRoot)));
        }

        // Step 3: the folder, from anywhere off the header
        contextMenu.Items.Add(WidgetMenus.NewOpenFolderMenuItem("Open Server MessageEfficiency Folder", serverContext.MessageEfficiencyDirectoryPath.ToString()));
    }

    public string? SaveStateJson() => DataGridWidgetState.NewStateJson(MessageEfficiencyGrid);

    public void LoadStateJson(string? json) => DataGridWidgetState.LoadStateJson(MessageEfficiencyGrid, json);

    public void Dispose() => _refreshTimer?.Dispose();
}
