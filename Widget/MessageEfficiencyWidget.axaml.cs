using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Data;
using Execution;
using Provider;
using Socket;
using Tools;

namespace Widget;


public sealed class WidgetMessageEfficiency : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private MessageEfficiency _messageEfficiency;

    public string ProductGroup => _messageEfficiency.ProductGroup.ToString();
    public string RawMessages => _messageEfficiency.RawMessages.ToString("N0");
    public string WeightedMessages => _messageEfficiency.WeightedMessages.ToString("N2");
    public string QuantityTraded => _messageEfficiency.QuantityTraded.ToString("N0");
    public string Efficiency => _messageEfficiency.Efficiency.ToString("N2");

    public string Tier0 => _messageEfficiency.Tier0.DailyRawMessages.ToString();
    public string Tier0Benchmark => _messageEfficiency.Tier0.Benchmark.ToString();
    public string Tier1 => _messageEfficiency.Tier1.DailyRawMessages.ToString();
    public string Tier1Benchmark => _messageEfficiency.Tier1.Benchmark.ToString();
    public string Tier2 => _messageEfficiency.Tier2.DailyRawMessages.ToString();
    public string Tier2Benchmark => _messageEfficiency.Tier2.Benchmark.ToString();
    public string Tier3 => _messageEfficiency.Tier3.DailyRawMessages.ToString();
    public string Tier3Benchmark => _messageEfficiency.Tier3.Benchmark.ToString();

    public string TradeDate => _messageEfficiency.TradeDate.ToString("yyyy-MM-dd");

    public string TradeTime => _messageEfficiency.TradeTime.ToString(@"hh\:mm\:ss");

    // Efficiency cell highlight: green well under the active tier's benchmark, yellow within 25%, orange
    // within 10%, red at/over it. Below the lowest tier's raw-message threshold it's unconstrained (green).
    public IBrush EfficiencyBrush => EfficiencyStatusBrush(in _messageEfficiency);

    private static readonly IBrush s_safeBrush     = new ImmutableSolidColorBrush(Palette.Lighten(Palette.SafeGreen, 0.6));
    private static readonly IBrush s_warningBrush  = new ImmutableSolidColorBrush(Palette.Lighten(Palette.WarningYellow, 0.6));
    private static readonly IBrush s_cautionBrush  = new ImmutableSolidColorBrush(Palette.Lighten(Palette.CautionOrange, 0.6));
    private static readonly IBrush s_criticalBrush = new ImmutableSolidColorBrush(Palette.Lighten(Palette.CriticalRed, 0.6));

    private static IBrush EfficiencyStatusBrush(in MessageEfficiency messageEfficiency)
    {
        // Mirror MessageEfficiency.Send tier selection: Tier1 has the highest raw-message threshold.
        MessageEfficiencyTier tier;
        if (messageEfficiency.RawMessages > messageEfficiency.Tier1.DailyRawMessages) tier = messageEfficiency.Tier1;
        else if (messageEfficiency.RawMessages > messageEfficiency.Tier2.DailyRawMessages) tier = messageEfficiency.Tier2;
        else if (messageEfficiency.RawMessages > messageEfficiency.Tier3.DailyRawMessages) tier = messageEfficiency.Tier3;
        else return s_safeBrush;   // below the lowest tier => no benchmark applies yet

        if (tier.Benchmark <= 0) return s_criticalBrush;   // zero benchmark => always breached

        double ratio = messageEfficiency.Efficiency / tier.Benchmark;
        if (ratio >= 1.00) return s_criticalBrush;   // at or over the benchmark
        if (ratio >= 0.90) return s_cautionBrush;    // within 10% of the benchmark
        if (ratio >= 0.75) return s_warningBrush;    // within 25% of the benchmark
        return s_safeBrush;
    }

    public WidgetMessageEfficiency(MessageEfficiency messageEfficiency)
    {
        _messageEfficiency = messageEfficiency;
    }

    /// <summary>
    /// Pull current values from shared memory.
    /// Returns true if anything changed and bindings should be re-evaluated.
    /// </summary>
    public bool Refresh(in MessageEfficiency newMessageEfficiency)
    {
        bool changed = !MessageEfficiencyEquals(_messageEfficiency, newMessageEfficiency);

        _messageEfficiency = newMessageEfficiency;

        if (changed)
        {
            // Refresh all bindings
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        return changed;
    }

    private static bool MessageEfficiencyEquals(in MessageEfficiency left, in MessageEfficiency right)
    {
        return left.RawMessages == right.RawMessages
            && left.WeightedMessages == right.WeightedMessages
            && left.QuantityTraded == right.QuantityTraded
            && left.InverseQuantityTraded == right.InverseQuantityTraded
            && left.TradeDate == right.TradeDate
            && left.TradeTime == right.TradeTime
            && left.Tier1.DailyRawMessages == right.Tier1.DailyRawMessages && left.Tier1.Benchmark == right.Tier1.Benchmark
            && left.Tier2.DailyRawMessages == right.Tier2.DailyRawMessages && left.Tier2.Benchmark == right.Tier2.Benchmark
            && left.Tier3.DailyRawMessages == right.Tier3.DailyRawMessages && left.Tier3.Benchmark == right.Tier3.Benchmark;
    }
}


[RegisterJson]
public class MessageEfficiencyColumnState
{
    public string Header { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool IsVisible { get; set; }
}

[RegisterJson]
public class MessageEfficiencyWidgetState
{
    public List<MessageEfficiencyColumnState> Columns { get; set; } = new();
}


public sealed partial class MessageEfficiencyWidget : UserControl, IWidget, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    private readonly Dictionary<int, WidgetMessageEfficiency> _rowsByProductGroupId = new();
    public ObservableCollection<WidgetMessageEfficiency> Rows { get; } = new();

    public string TypeKey => "MessageEfficiencyWidget";
    public string Title { get; private set; } = "Message Efficiency";
    public double DefaultWidth => 820;
    public double DefaultHeight => 320;

    private Avalonia.Point _lastPointerPos;

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

        MessageEfficiencyGrid.PointerMoved += (_, e) => _lastPointerPos = e.GetPosition(MessageEfficiencyGrid);

        _refreshTimer = new Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = false;
        _refreshTimer.Start();
    }

    private void OnRefresh(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            // ProductGroups are allocated densely from 0; EnumerateMessageEfficiency yields each allocated
            // slot and stops at the first empty one. Key rows by ProductGroupId and refresh in place.
            List<WidgetMessageEfficiency> active = new List<WidgetMessageEfficiency>(_rowsByProductGroupId.Count);
            foreach (MessageEfficiency messageEfficiency in _context.Primary.EnumerateMessageEfficiency())
            {
                if (_rowsByProductGroupId.TryGetValue(messageEfficiency.ProductGroupId, out WidgetMessageEfficiency? row))
                {
                    row.Refresh(in messageEfficiency);
                }
                else
                {
                    row = new WidgetMessageEfficiency(messageEfficiency);
                    _rowsByProductGroupId[messageEfficiency.ProductGroupId] = row;
                }
                active.Add(row);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                // Remove rows for instruments that are no longer subscribed.
                for (int i = Rows.Count - 1; i >= 0; i--)
                {
                    if (!active.Contains(Rows[i]))
                    {
                        Rows.RemoveAt(i);
                    }
                }

                // Add newly active rows.
                foreach (var r in active)
                {
                    if (!Rows.Contains(r))
                    {
                        Rows.Add(r);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MessageEfficiencyWidget Error: {ex.Message}");
        }
        finally
        {
            if (!_disposed) _refreshTimer.Start();
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || MessageEfficiencyGrid == null) return;

        menu.Items.Clear();

        // Header cell hit-test → show column-toggle list.
        var visual = MessageEfficiencyGrid.InputHitTest(_lastPointerPos) as Visual;
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
        ServerContext serverContext = ContextManager.ServerContext;

        if (isHeader)
        {
            foreach (DataGridColumn col in MessageEfficiencyGrid.Columns)
            {
                string header = col.Header?.ToString() ?? "Column";
                MenuItem item = new MenuItem
                {
                    Header = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = col.IsVisible
                };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
        }
        else if (hitRow != null && hitRow.DataContext is WidgetMessageEfficiency messageEfficiency)
        {
            var headerItem = new MenuItem { Header = messageEfficiency.ProductGroup, FontWeight = Avalonia.Media.FontWeight.Bold, IsEnabled = false };
            menu.Items.Add(headerItem);
            menu.Items.Add(new Separator());

            FileSystemPath filePath = Context.GetMessageEfficiencyFilePath(serverContext.MessageEfficiencyDirectoryPath, messageEfficiency.ProductGroup);
            var copyPathItem = new MenuItem { Header = "Copy Server .messageefficiency File Path", Icon = new TextBlock { Text = "📋" } };
            copyPathItem.Click += async (_, _) =>
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(filePath);
                }
                catch { }
            };
            menu.Items.Add(copyPathItem);

            var openFolderItem = new MenuItem { Header = "Open Server MessageEfficiency Folder", Icon = new TextBlock { Text = "📁" } };
            openFolderItem.Click += (_, _) => OpenInExplorer(serverContext.MessageEfficiencyDirectoryPath.ToString());
            menu.Items.Add(openFolderItem);
        }
        else
        {
            var openFolderItem = new MenuItem { Header = "Open Server MessageEfficiency Folder", Icon = new TextBlock { Text = "📁" } };
            openFolderItem.Click += (_, _) => OpenInExplorer(serverContext.MessageEfficiencyDirectoryPath.ToString());
            menu.Items.Add(openFolderItem);
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return;
            }

            string opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = opener,
                ArgumentList = { path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MessageEfficiencyWidget.OpenInExplorer Error: {ex.Message}");
        }
    }

    public string? SaveStateJson()
    {
        var state = new MessageEfficiencyWidgetState();
        foreach (var col in MessageEfficiencyGrid.Columns)
        {
            state.Columns.Add(new MessageEfficiencyColumnState
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
            var state = Json.Deserialize<MessageEfficiencyWidgetState>(json);
            if (state == null) return;

            foreach (var colState in state.Columns)
            {
                var col = MessageEfficiencyGrid.Columns.FirstOrDefault(c => (c.Header?.ToString() ?? "") == colState.Header);
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
    }
}
