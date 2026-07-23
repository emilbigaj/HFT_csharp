using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;
using Widget;


namespace Workspace;


/// <summary>
/// Top-level workspace window. Hosts floating widget panels.
/// Implements IWidgetHost to allow widgets to request actions.
/// </summary>
public partial class Workspace : Window, IWidgetHost
{
    public Context Context { get; } = null!;
    public ManualClient ManualClient { get; } = null!;
    public AlertManager AlertManager { get; } = null!;
    public WorkspaceContext WorkspaceContext { get; } = null!;
    private readonly System.Timers.Timer _refreshTimer = null!;
    private string? _currentWorkspacePath;
    private Thread _clientThread = null!;
    private volatile bool _isClosing = false;
    public Workspace()
    {
        InitializeComponent();
    }

    public Workspace(Context context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        if (context is ClientContext clientContext)
        {
            ManualClient = new ManualClient(clientContext.ClientName, Context.ServerName);
        }
        else
        {
            throw new NotImplementedException();
            //Client = new ManualClient(Context.ServerName, Context.ServerName);
        }

        AlertManager = new AlertManager(ManualClient.Context);
        Clock.Exception += AlertManager.OnException;
        ManualClient.OrderRejected += OnClientOrderRejected;

        _clientThread = LowLatency.StartBackgroundThread($"{ManualClient.ClientName}", () =>
        {
            while (!Tools.Application.IsExiting && !_isClosing)
            {
                try
                {
                    ManualClient.WriteSocket();
                    ManualClient.ReadSocket();
                }
                catch(Exception exception)
                {
                    AlertManager.OnException(exception);
                }
                Thread.Sleep(1);
            }
        });




        WorkspaceContext = new WorkspaceContext(Context, ManualClient);

        InitializeComponent();

        Loaded += (_, _) =>
        {
            UpdateHeader();
            UpdateModeMenuHeaders();
        };

        Closed += OnWorkspaceClosed;

        _refreshTimer = new System.Timers.Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
    }

    private void OnClientOrderRejected(in OrderRejected orderRejected)
    {
        AlertManager.OnOrderRejected(orderRejected, "");
    }

    private void OnWorkspaceClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _clientThread.Join();

        if (AlertManager != null)
        {
            Clock.Exception -= AlertManager.OnException;
            ManualClient.OrderRejected -= OnClientOrderRejected;
            AlertManager.Dispose();
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool hasOtherWorkspaces = false;
            foreach (var window in desktop.Windows)
            {
                if (window is Workspace && window != this)
                {
                    hasOtherWorkspaces = true;
                    break;
                }
            }

            if (!hasOtherWorkspaces)
            {
                desktop.Shutdown();
            }
            ManualClient?.Dispose();
        }
    }

    private void OnRefresh(object? sender, System.Timers.ElapsedEventArgs? e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TimestampText.Text = Context.ServerHeader.GetReadonlyRef().Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        });
    }

    private void UpdateHeader()
    {
        string contextName = Context is ClientContext clientContext ? clientContext.ClientName : Context.ServerName;
        Title = $"Server: {Context.ServerName}, Context [{contextName}]";
        OpenDirectoryMenuItem.Header = Context is ClientContext ? "Open Strategy Directory" : "Open Server Directory";
    }

    private void UpdateModeMenuHeaders()
    {
        bool isSimulation = Clock.Mode == ClockMode.Simulation;
        bool isRealtime = Clock.Mode == ClockMode.Realtime;

        SimulationMenuItem.Header = isSimulation ? "✔ Simulation" : "Simulation";
        RealtimeMenuItem.Header = isRealtime ? "✔ Realtime" : "Realtime";

        SimulationMenuItem.IsEnabled = false;
        RealtimeMenuItem.IsEnabled = false;

        if (TopBar != null)
        {
            if (isRealtime)
            {
                TopBar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0xC3, 0x5B));
            }
            else
            {
                TopBar.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x99));
            }
        }
    }

    private void OnSimulationModeClick(object? sender, RoutedEventArgs e) { }
    private void OnRealtimeModeClick(object? sender, RoutedEventArgs e) { }

    private void OnExtendWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        ExtendWorkspace();
    }

    public void ExtendWorkspace()
    {
        var extended = new Workspace(Context);
        extended.Show();
    }

    private void OnNewGlobalWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ContextManager.ServerContext;
        var ws = new Workspace(ctx);
        ws.Show();
    }

    private async void OnNewStrategyWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var strategiesRoot = ClientContext.GetDirectoryPath("");
            List<string> allStrategies;
            try
            {
                allStrategies = Directory.Exists(strategiesRoot)
                    ? Directory.GetDirectories(strategiesRoot)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
            }
            catch { allStrategies = new List<string>(); }
            Dictionary<string, string> clientNameToDirectoryPath = new();
            foreach (string strategy in allStrategies)
                clientNameToDirectoryPath.TryAdd(Path.GetFileName(strategy), strategy);

            var dlg = new Window { Width = 360, Height = 320, Title = "New Strategy Workspace", WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock { Text = "Strategy name:", Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(label, 0);
            var textBox = new TextBox { MinWidth = 240, PlaceholderText = "Start typing..." };
            Grid.SetRow(textBox, 1);
            var suggestions = new ListBox { Margin = new Thickness(0, 6, 0, 0), MinHeight = 120, MaxHeight = 200 };
            Grid.SetRow(suggestions, 2);

            void ApplyFilter()
            {
                var text = textBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text)) suggestions.ItemsSource = clientNameToDirectoryPath.Keys.OrderBy(k => k).ToList();
                else suggestions.ItemsSource = allStrategies.Where(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (suggestions.ItemCount > 0 && suggestions.SelectedIndex < 0) suggestions.SelectedIndex = 0;
            }
            textBox.TextChanged += (_, _) => ApplyFilter();
            suggestions.DoubleTapped += (_, _) => { if (suggestions.SelectedItem is string name) dlg.Close(name); };

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 3);

            grid.Children.Add(label); grid.Children.Add(textBox); grid.Children.Add(suggestions); grid.Children.Add(buttonPanel);
            dlg.Content = grid;

            okButton.Click += (_, _) => { var text = textBox.Text; if (string.IsNullOrWhiteSpace(text) && suggestions.SelectedItem is string selected) text = selected; dlg.Close(text); };
            cancelButton.Click += (_, _) => dlg.Close(null);

            ApplyFilter();
            var result = await dlg.ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(result))
                return;
            string clientName = clientNameToDirectoryPath[result];
            var ctx = ContextManager.GetClientContext(clientName);
            var ws = new Workspace(ctx);
            ws.Show();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating strategy workspace: {ex.Message}");
        }
    }

    private async void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            string folder = Context.WorkspaceDirectoryPath;
            Directory.CreateDirectory(folder);

            var startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(folder);

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workspace",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Workspace files") { Patterns = new[] { "*.workspace" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });

            if (files == null || files.Count == 0) return;

            await LoadWorkspaceAsync(files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OnOpenWorkspaceClick Error: {ex}");
        }
    }

    private async void OnSaveWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentWorkspacePath))
                await SaveWorkspaceAsAsync();
            else
                await SaveWorkspaceAsync(_currentWorkspacePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving workspace: {ex.Message}");
        }
    }

    private async void OnSaveWorkspaceAsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveWorkspaceAsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving workspace as: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task SaveWorkspaceAsAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            string folder = Context.WorkspaceDirectoryPath;
            Directory.CreateDirectory(folder);

            var startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(folder);

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Workspace As",
                SuggestedFileName = "default.workspace",
                SuggestedStartLocation = startLocation,
                DefaultExtension = "workspace",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Workspace files") { Patterns = new[] { "*.workspace" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });

            if (file == null) return;

            await SaveWorkspaceAsync(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OnSaveWorkspaceAsClick Error: {ex}");
        }
    }

    private void OnOpenContextDirectoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WorkspaceContext.Primary.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OnOpenContextDirectoryClick Error: {ex}");
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    public void AddWidget(IWidget widget)
    {
        var container = new WidgetContainer(widget);

        container.Width = widget.DefaultWidth;
        container.Height = widget.DefaultHeight;

        DockHost.Children.Add(container);

        double x = (Bounds.Width - container.Width) / 2.0;
        double y = (Bounds.Height - container.Height) / 2.0;

        if (x < 0) x = 0;
        if (y < 0) y = 0;

        Canvas.SetLeft(container, x);
        Canvas.SetTop(container, y);
    }

    private IEnumerable<(WidgetContainer Container, IWidget Widget)> EnumerateWidgets()
    {
        foreach (var child in DockHost.Children)
        {
            if (child is WidgetContainer container && container.Widget is IWidget widget)
                yield return (container, widget);
        }
    }

    private WorkspaceLayout CaptureLayout()
    {
        var layout = new WorkspaceLayout
        {
            WindowWidth = Width,
            WindowHeight = Height,
            WindowX = Position.X,
            WindowY = Position.Y,
            WindowMaximized = WindowState == WindowState.Maximized,
        };

        foreach (var (container, widget) in EnumerateWidgets())
        {
            double x = Canvas.GetLeft(container);
            double y = Canvas.GetTop(container);
            if (double.IsNaN(x)) x = 0;
            if (double.IsNaN(y)) y = 0;

            double width = container.Bounds.Width > 0 ? container.Bounds.Width : container.Width;
            double height = container.Bounds.Height > 0 ? container.Bounds.Height : container.Height;

            var wl = new WidgetLayout
            {
                TypeKey = widget.TypeKey,
                Title = widget.Title,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                StateJson = widget.SaveStateJson()
            };
            layout.Widgets.Add(wl);
        }

        return layout;
    }

    private WorkspaceLayout CaptureAllLayouts()
    {
        WorkspaceLayout rootLayout = CaptureLayout();

        var allWindows = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : Enumerable.Empty<Window>();

        foreach (var w in allWindows)
        {
            if (w is Workspace ws && ws != this && ws.Context == this.Context)
            {
                rootLayout.ChildWindows.Add(ws.CaptureLayout());
            }
        }

        return rootLayout;
    }

    private void RestoreLayout(WorkspaceLayout layout)
    {
        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        Position = new PixelPoint((int)layout.WindowX, (int)layout.WindowY);
        WindowState = layout.WindowMaximized ? WindowState.Maximized : WindowState.Normal;

        DockHost.Children.Clear();

        foreach (var wl in layout.Widgets)
        {
            var widget = CreateWidgetFromLayout(wl);
            if (widget == null)
                continue;

            var container = new Widget.WidgetContainer(widget)
            {
                Width = wl.Width > 0 ? wl.Width : widget.DefaultWidth,
                Height = wl.Height > 0 ? wl.Height : widget.DefaultHeight
            };

            DockHost.Children.Add(container);
            Canvas.SetLeft(container, wl.X);
            Canvas.SetTop(container, wl.Y);
        }

        UpdateHeader();

        foreach (var childLayout in layout.ChildWindows)
        {
            var ws = new Workspace(Context);
            ws.RestoreLayout(childLayout);
            ws.Show();
        }
    }

    private IWidget? CreateWidgetFromLayout(WidgetLayout wl)
    {
        IWidget? widget = wl.TypeKey switch
        {
            "ChartWidget" => new ChartWidget(Context),
            "FillsWidget" => new FillsWidget(Context),
            "PositionsWidget" => new PositionsWidget(WorkspaceContext),
            "OrdersWidget" => new OrdersWidget(WorkspaceContext),
            "LadderWidget" => new LadderWidget(WorkspaceContext),
            "AuditTrailWidget" => new AuditTrailWidget(WorkspaceContext),
            "SendOrderWidget" => new SendOrderWidget(WorkspaceContext),
            "InstrumentHeadersWidget" => new InstrumentHeadersWidget(WorkspaceContext),
            "RiskLimitsWidget" => new RiskLimitsWidget(WorkspaceContext),
            "MessageEfficiencyWidget" => new MessageEfficiencyWidget(WorkspaceContext),
            _ => null
        };

        widget?.LoadStateJson(wl.StateJson);
        return widget;
    }

    private async System.Threading.Tasks.Task SaveWorkspaceAsync(string path)
    {
        try
        {
            var layout = CaptureAllLayouts();
            string json = Json.Serialize(layout);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, json);
            _currentWorkspacePath = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SaveWorkspaceAsync Error: {ex}");
        }
    }

    internal async System.Threading.Tasks.Task LoadWorkspaceAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            string json = await File.ReadAllTextAsync(path);
            WorkspaceLayout layout = Json.Deserialize<WorkspaceLayout>(json);
            if (layout == null)
                return;

            RestoreLayout(layout);
            _currentWorkspacePath = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading workspace '{path}': {ex.Message}");
        }
    }

    private async void OnAddLadderWidgetClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var searchDialog = new SearchWidget(Context);
            Instrument? selectedInstrument = await searchDialog.ShowDialog<Instrument?>(this);

            if (selectedInstrument != null)
            {
                var ladder = new LadderWidget(WorkspaceContext);
                ladder.SetInstrument(selectedInstrument.InstrumentId);
                AddWidget(ladder);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OnAddLadderWidgetClick: {ex}");
        }
    }

    private void OnAddChartWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new ChartWidget(Context));
    private void OnAddFillsWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new FillsWidget(Context));
    private void OnAddPositionsWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new PositionsWidget(WorkspaceContext));
    private void OnAddOrdersWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new OrdersWidget(WorkspaceContext));
    private void OnAddSendOrderWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new SendOrderWidget(WorkspaceContext));
    private void OnAddAuditTrailWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new AuditTrailWidget(WorkspaceContext));
    private void OnAddInstrumentHeadersWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new InstrumentHeadersWidget(WorkspaceContext));
    private void OnAddMessageEfficiencyWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new MessageEfficiencyWidget(WorkspaceContext));
    private void OnAddRiskLimitsWidgetClick(object? sender, RoutedEventArgs e) => AddWidget(new RiskLimitsWidget(WorkspaceContext));   // ← add this

}