using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Socket;
using Tools;
using Widget;


namespace Workspace;


/// A top-level workspace window: its context's client (and that client's thread), and the widgets floating on its canvas.
/// Widgets open others through IWidgetHost. UI thread, except the client thread. Cold path.
public partial class Workspace : Window, IWidgetHost
{
    private static readonly FilePickerFileType[] s_workspaceFileTypes =
    {
        new FilePickerFileType("Workspace files") { Patterns = new[] { "*.workspace" } },
        new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
    };

    public Context Context { get; private set; } = null!;
    public IManualClient ManualClient { get; private set; } = null!;
    public AlertManager? AlertManager { get; private set; }   // null on a remote desk: no Logging there; its stand-in alerts beside the server
    public WorkspaceContext WorkspaceContext { get; private set; } = null!;
    private readonly System.Timers.Timer _refreshTimer = null!;
    private string? _currentWorkspacePath;
    private Thread _clientThread = null!;
    private volatile bool _isClientStopping;                                  // tells the client thread to end (Disconnect)
    private bool _isConnected;                                                // a client is open and its thread runs (UI thread); Disconnect does nothing otherwise
    private ulong _latencySeq;                                                // the Latency[0] row's seq when last seen to change (UI thread)
    private Timestamp _latencyTimestamp = Timestamp.StopwatchNow;             // when it last changed; unchanged for the silence timeout is a hung Remote Mirror
    private int _guiNumber;                                                   // this window's GUI number on a remote desk; a rebuild asks for the same one first
    private static readonly List<Workspace> s_openWorkspaces = new();         // this process's open Workspace windows (UI thread). Why our own: Avalonia 12's desktop.Windows misses one opened before the lifetime starts

    /// This process's open Workspace windows, in the order they opened.
    public static IReadOnlyList<Workspace> OpenWorkspaces => s_openWorkspaces;

    public Workspace()
    {
        InitializeComponent();
    }

    public Workspace(Context context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Connect();

        InitializeComponent();

        Loaded += (_, _) =>
        {
            UpdateHeader();
            UpdateModeMenuHeaders();
            // A standalone Workspace process only follows the server's clock, so its own speed would change nothing.
            SimulationSpeedButton.IsVisible = Clock.Mode == ClockMode.Simulation && WorkspaceRunner.IsHostedByStrategy;
        };

        Closed += OnWorkspaceClosed;

        _refreshTimer = new System.Timers.Timer(100);
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
        s_openWorkspaces.Add(this);
    }

    // Opens this window's client under its principal and starts its thread: at construction, and again when a remote desk rebuilds
    // on a restarted server or Remote Mirror. Throws when the client cannot connect (ManualClient.Open's refusals).
    private void Connect()
    {
        // Step 1: the server's symbol strings, warmed off-thread
        ContextManager.ServerContext.WarmSymbolStrings();

        // Step 2: the client.
        // The server as principal selects server mode: the GUI still connects as its own "<server>_GUI<n>"
        // client, but shadows every client instead of one algo and books manual orders to the reserved house
        // strategy. Open takes the first free number, so any number of workspaces may share a principal.
        ManualClient = Provider.ManualClient.Open(Context is ClientContext clientContext ? clientContext.ClientName : Context.ServerName, Context.ServerName, _guiNumber);
        Provider.ManualClient.TryParseRemoteGuiName(ManualClient.Context.ClientName.ToString(), out _, out _guiNumber);
        if (ManualClient is Provider.ManualClient manualClient)
        {
            AlertManager = new AlertManager(ManualClient.Context);
            Clock.Exception += AlertManager.OnException;
            manualClient.OrderRejected += (in OrderRejected orderRejected) => AlertManager!.OnOrderRejected(orderRejected, "");
        }

        // Step 3: its thread, and the context its widgets are built with
        _isClientStopping = false;
        _clientThread = LowLatency.StartBackgroundThread($"{ManualClient.Context.ClientName}", () =>
        {
            while (!Tools.Application.IsExiting && !_isClientStopping)
            {
                try
                {
                    ManualClient.WriteSocket();
                    ManualClient.ReadSocket();
                }
                catch(Exception exception)
                {
                    // Why: the console too: a remote desk has no AlertManager
                    Console.WriteLine($"Workspace client thread: {exception}");
                    AlertManager?.OnException(exception);
                }
                Thread.Sleep(1);
            }
        });
        WorkspaceContext = new WorkspaceContext(Context, ManualClient);
        _isConnected = true;
    }

    // Stops this window's client thread and disposes its widgets and client: when it closes, and before a rebuild connects anew.
    private void Disconnect()
    {
        if (!_isConnected)
            return;
        _isConnected = false;
        _isClientStopping = true;
        // Why a poll, not Join: a kernel wait on the UI thread pumps messages under NativeAOT, so another window's refresh could start a second rebuild inside this one (see Spec.md)
        while (_clientThread.IsAlive)
            Thread.Sleep(1);
        foreach ((_, IWidget widget) in EnumerateWidgets())
            (widget as IDisposable)?.Dispose();
        if (AlertManager != null)
        {
            Clock.Exception -= AlertManager.OnException;
            AlertManager.Dispose();
            AlertManager = null;
        }
        ManualClient.Dispose();
    }

    private void OnWorkspaceClosed(object? sender, EventArgs e)
    {
        // Why: a closed window's clock would otherwise keep refreshing
        _refreshTimer.Dispose();
        Disconnect();

        s_openWorkspaces.Remove(this);
        if (s_openWorkspaces.Count == 0 && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnRefresh(object? sender, System.Timers.ElapsedEventArgs? e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TimestampText.Text = Context.ServerHeader.GetReadonlyRef().Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            RemoteClient? remoteClient = ManualClient as RemoteClient;
            // Why: a restarted server is a new run (client ids, instrument ids) and a restarted Remote Mirror serves no old session, so a remote desk rebuilds every Workspace on either (see Spec.md "Remote workspaces")
            bool isSessionEnded = _isConnected && remoteClient != null && remoteClient.IsSessionEnded;
            if (isSessionEnded)
            {
                RestartWorkspaces();
                return;
            }
            // A remote desk shows its Mirror link's latency beside the clock, a Workspace beside the server whether the server's process has exited (see Spec.md "Remote workspaces")
            if (remoteClient != null)
                TimestampText.Text = GetLatencyText(remoteClient) + "    " + TimestampText.Text;
            else if (!ProcessId.IsAlive(Context.ServerHeader.GetReadonlyRef().ServerProcessId))
                TimestampText.Text = "Disconnected – server stopped, close this Workspace    " + TimestampText.Text;
            if (SimulationSpeedButton.IsVisible)
            {
                // The speed the clock thread has applied, not the one last requested.
                string simulationSpeedText = FormatSimulationSpeed(Clock.SimulationSpeed);
                if (!Equals(SimulationSpeedButton.Content, simulationSpeedText))
                    SimulationSpeedButton.Content = simulationSpeedText;
            }
        });
    }

    // Latency[0] exactly as the Remote Mirror writes it (below 0 while its link is down); offline once its process has exited, not
    // responding once it has stopped rewriting the row for the silence timeout (a hung Remote Mirror).
    private string GetLatencyText(RemoteClient remoteClient)
    {
        // Step 1: the Remote Mirror itself: alive, and still rewriting the row every pass
        if (!remoteClient.IsRemoteMirrorAlive)
            return "Remote Mirror offline";
        ref SharedArrayEntry<Duration> latencyEntry = ref Context.GetLatency(0);
        if (latencyEntry.GetSeq() != _latencySeq)
            (_latencySeq, _latencyTimestamp) = (latencyEntry.GetSeq(), Timestamp.StopwatchNow);
        if (Timestamp.StopwatchNow - _latencyTimestamp >= RemoteClient.s_silenceTimeout)
            return "Remote Mirror not responding";

        // Step 2: its number, to the microsecond so a steady link visibly ticks. Why a plain read: one aligned 8-byte Duration cannot tear, and Read would spin forever on a row a killed Remote Mirror left mid-write
        Duration latency = latencyEntry.GetReadonlyRef();
        return latency < Duration.Zero ? "Connection broken – reconnecting…" : $"{latency.TotalMilliseconds:0.000} ms";
    }

    private static string FormatSimulationSpeed(double simulationSpeed)
    {
        return simulationSpeed == double.MaxValue ? "Max" : simulationSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
    }

    private void OnRealTimeSpeedClick(object? sender, RoutedEventArgs e) => SetSimulationSpeed(1);

    private void OnMaxSpeedClick(object? sender, RoutedEventArgs e) => SetSimulationSpeed(double.MaxValue);

    private void OnCustomSpeedClick(object? sender, RoutedEventArgs e) => SetCustomSimulationSpeed();

    private void OnCustomSpeedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        SetCustomSimulationSpeed();
        e.Handled = true;
    }

    // Accepts "10" or "10x"; anything that is not a positive finite number leaves the speed alone and marks the box red.
    private void SetCustomSimulationSpeed()
    {
        string customSpeedText = (CustomSpeedTextBox.Text ?? "").Trim().TrimEnd('x', 'X');
        if (double.TryParse(customSpeedText, NumberStyles.Float, CultureInfo.InvariantCulture, out double simulationSpeed) && simulationSpeed > 0 && double.IsFinite(simulationSpeed))
            SetSimulationSpeed(simulationSpeed);
        else
            CustomSpeedTextBox.BorderBrush = Brushes.Red;
    }

    private void SetSimulationSpeed(double simulationSpeed)
    {
        Clock.SimulationSpeed = simulationSpeed;
        CustomSpeedTextBox.ClearValue(TextBox.BorderBrushProperty);
        SimulationSpeedButton.Flyout?.Hide();
    }

    private void UpdateHeader()
    {
        string contextName = Context is ClientContext clientContext ? clientContext.ClientName : Context.ServerName;
        Title = $"Server: {Context.ServerName}, Context [{contextName}]";
        OpenDirectoryMenuItem.Header = Context is ClientContext ? "Open Strategy Directory" : "Open Server Directory";
    }

    /// Ticks the clock's mode in the Mode menu (whose items only show it) and colours the top bar by it: green realtime, yellow simulation.
    private void UpdateModeMenuHeaders()
    {
        bool isSimulation = Clock.Mode == ClockMode.Simulation;
        bool isRealtime = Clock.Mode == ClockMode.Realtime;

        SimulationMenuItem.Header = isSimulation ? "✔ Simulation" : "Simulation";
        RealtimeMenuItem.Header = isRealtime ? "✔ Realtime" : "Realtime";
        TopBar.Background = isRealtime ? Palette.WorkspaceRealtimeGreen : Palette.WorkspaceSimulationYellow;
    }

    private void OnExtendWorkspaceClick(object? sender, RoutedEventArgs e) => new Workspace(Context).Show();

    private void OnNewServerWorkspaceClick(object? sender, RoutedEventArgs e) => new Workspace(ContextManager.ServerContext).Show();

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
            new Workspace(ContextManager.GetClientContext(clientName)).Show();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating strategy workspace: {ex.Message}");
        }
    }

    /// The context's workspace folder (created when missing), where the file dialogs open.
    private Task<IStorageFolder?> GetWorkspaceStorageFolderAsync()
    {
        string workspaceDirectoryPath = Context.WorkspaceDirectoryPath;
        Directory.CreateDirectory(workspaceDirectoryPath);
        return StorageProvider.TryGetFolderFromPathAsync(workspaceDirectoryPath);
    }

    /// Asks for a workspace file and loads it into this window; logs a failure.
    private async void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFile> storageFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workspace",
                AllowMultiple = false,
                SuggestedStartLocation = await GetWorkspaceStorageFolderAsync(),
                FileTypeFilter = s_workspaceFileTypes,
            });
            if (storageFiles.Count == 0) return;

            await LoadWorkspaceAsync(storageFiles[0].Path.LocalPath);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Workspace.OnOpenWorkspaceClick: {exception}");
        }
    }

    private async void OnSaveWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentWorkspacePath))
            await SaveWorkspaceAsAsync();
        else
            await SaveWorkspaceAsync(_currentWorkspacePath);
    }

    private async void OnSaveWorkspaceAsClick(object? sender, RoutedEventArgs e) => await SaveWorkspaceAsAsync();

    /// Asks where to save the workspace and saves it there; logs a failure.
    private async Task SaveWorkspaceAsAsync()
    {
        try
        {
            IStorageFile? storageFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Workspace As",
                SuggestedFileName = "default.workspace",
                SuggestedStartLocation = await GetWorkspaceStorageFolderAsync(),
                DefaultExtension = "workspace",
                FileTypeChoices = s_workspaceFileTypes,
            });
            if (storageFile == null) return;

            await SaveWorkspaceAsync(storageFile.Path.LocalPath);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Workspace.SaveWorkspaceAsAsync: {exception}");
        }
    }

    /// Asks where to save a PNG of this window and saves it there; logs a failure.
    private async void OnSaveScreenshotClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string workspaceName = string.IsNullOrEmpty(_currentWorkspacePath) ? "workspace" : Path.GetFileNameWithoutExtension(_currentWorkspacePath);
            IStorageFile? storageFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Screenshot",
                SuggestedFileName = $"{workspaceName}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                SuggestedStartLocation = await GetWorkspaceStorageFolderAsync(),
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG images") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
            if (storageFile == null) return;

            // This window, not the main one: several workspaces can be open at once.
            await WorkspaceRunner.CaptureScreenshotAsync(storageFile.Path.LocalPath, this);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Workspace.OnSaveScreenshotClick: {exception}");
        }
    }

    private void OnOpenContextDirectoryClick(object? sender, RoutedEventArgs e) => WidgetMenus.OpenInExplorer(WorkspaceContext.Primary.DirectoryPath);

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    /// Adds the widget at its default size, centred in the window (its top-left kept on the canvas).
    public void AddWidget(IWidget widget) =>
        AddWidgetContainer(widget, Math.Max(0, (Bounds.Width - widget.DefaultWidth) / 2.0), Math.Max(0, (Bounds.Height - widget.DefaultHeight) / 2.0), widget.DefaultWidth, widget.DefaultHeight);

    /// Puts the widget on the canvas in a container at this position and size.
    private void AddWidgetContainer(IWidget widget, double left, double top, double width, double height)
    {
        WidgetContainer widgetContainer = new WidgetContainer(widget) { Width = width, Height = height };
        DockHost.Children.Add(widgetContainer);
        Canvas.SetLeft(widgetContainer, left);
        Canvas.SetTop(widgetContainer, top);
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

        foreach ((WidgetContainer widgetContainer, IWidget widget) in EnumerateWidgets())
        {
            Rect layoutRect = widgetContainer.GetLayoutRect();
            layout.Widgets.Add(new WidgetLayout
            {
                TypeKey = widget.TypeKey,
                Title = widget.Title,
                X = layoutRect.X,
                Y = layoutRect.Y,
                Width = layoutRect.Width,
                Height = layoutRect.Height,
                StateJson = widget.SaveStateJson()
            });
        }

        return layout;
    }

    private WorkspaceLayout CaptureAllLayouts()
    {
        WorkspaceLayout rootLayout = CaptureLayout();
        foreach (Workspace workspace in s_openWorkspaces)
        {
            if (workspace != this && workspace.Context == Context)
                rootLayout.ChildWindows.Add(workspace.CaptureLayout());
        }
        return rootLayout;
    }

    private void RestoreLayout(WorkspaceLayout layout)
    {
        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        Position = new PixelPoint((int)layout.WindowX, (int)layout.WindowY);
        WindowState = layout.WindowMaximized ? WindowState.Maximized : WindowState.Normal;

        RestoreWidgets(layout);

        foreach (var childLayout in layout.ChildWindows)
        {
            Workspace childWorkspace = new Workspace(Context);
            childWorkspace.RestoreLayout(childLayout);
            childWorkspace.Show();
        }
    }

    // The layout's widgets in this window, replacing its own; its size, position and state are left alone (a rebuild keeps them).
    private void RestoreWidgets(WorkspaceLayout layout)
    {
        DockHost.Children.Clear();

        foreach (WidgetLayout widgetLayout in layout.Widgets)
        {
            IWidget? widget = NewWidget(widgetLayout.TypeKey);
            if (widget == null)
                continue;
            widget.LoadStateJson(widgetLayout.StateJson);
            AddWidgetContainer(widget, widgetLayout.X, widgetLayout.Y, widgetLayout.Width > 0 ? widgetLayout.Width : widget.DefaultWidth, widgetLayout.Height > 0 ? widgetLayout.Height : widget.DefaultHeight);
        }

        UpdateHeader();
    }

    // Rebuilds every Workspace of this process in place for a restarted server or Remote Mirror: each window keeps itself, its menus, size and
    // position, and swaps its context, client, connection and widgets (see Spec.md "Remote workspaces"). UI thread.
    private static void RestartWorkspaces()
    {
        // Step 1: fresh contexts over the same copy; the old stay mapped, so nothing still reading them touches freed memory
        ContextManager.Renew();

        // Step 2: every window's layout, then every window disconnected. Why all first: no window can then reconnect under a name another still holds
        List<(Workspace Workspace, WorkspaceLayout WorkspaceLayout)> workspaceLayouts = s_openWorkspaces.Select(workspace => (workspace, workspace.CaptureLayout())).ToList();
        foreach ((Workspace workspace, _) in workspaceLayouts)
            workspace.Disconnect();

        // Step 3: each window: its context anew, a client under its own GUI number, its widgets back; one that cannot reconnect closes, saying why
        foreach ((Workspace workspace, WorkspaceLayout workspaceLayout) in workspaceLayouts)
        {
            try
            {
                workspace.Context = workspace.Context is ClientContext clientContext ? ContextManager.GetClientContext(clientContext.ClientName) : ContextManager.ServerContext;
                workspace.Connect();
                workspace.RestoreWidgets(workspaceLayout);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Workspace.RestartWorkspaces: {workspace.Title} could not reconnect, closing it: {exception}");
                workspace.Close();
            }
        }
    }

    /// A new widget of this type key (its class name, as a saved layout and the Widgets menu name it), on this window's context;
    /// null for a key no widget has.
    private IWidget? NewWidget(string typeKey) => typeKey switch
    {
        nameof(ChartWidget) => new ChartWidget(Context),
        nameof(FillsWidget) => new FillsWidget(Context),
        nameof(PositionsWidget) => new PositionsWidget(WorkspaceContext),
        nameof(OrdersWidget) => new OrdersWidget(WorkspaceContext),
        nameof(LadderWidget) => new LadderWidget(WorkspaceContext),
        nameof(AuditTrailWidget) => new AuditTrailWidget(WorkspaceContext),
        nameof(SendOrderWidget) => new SendOrderWidget(WorkspaceContext),
        nameof(InstrumentHeadersWidget) => new InstrumentHeadersWidget(WorkspaceContext),
        nameof(RiskLimitsWidget) => new RiskLimitsWidget(WorkspaceContext),
        nameof(MessageEfficiencyWidget) => new MessageEfficiencyWidget(WorkspaceContext),
        nameof(RateLimitWidget) => new RateLimitWidget(WorkspaceContext),
        _ => null
    };

    /// Adds the widget the clicked Widgets menu item's Tag names.
    private void OnAddWidgetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string typeKey } && NewWidget(typeKey) is IWidget widget)
            AddWidget(widget);
    }

    private async Task SaveWorkspaceAsync(string path)
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

    internal async Task LoadWorkspaceAsync(string path)
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
}
