using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Mirror;
using Provider;
using Tools;
using Widget;

namespace Phone;

/// The phone's one view: the desk in one process. Starts the Remote Mirror's loop on a thread over in-process memory (it dials the
/// relay desk in MirrorConfig.json), opens the GUI's client off the UI thread, then shows one existing widget at a time over the
/// WorkspaceContext a desk Workspace builds, with the clock and the link's latency in a bar; rebuilds on a server restart as a desk
/// Workspace does (see Spec.md "The phone"). A server workspace: every order and position; the ladder and Send Order open from the
/// grids' menus as on a desk (the view is their IWidgetHost). UI thread, plus the Mirror thread and the client thread; cold path.
public partial class PhoneView : UserControl, IWidgetHost
{
    private const int s_passIntervalMilliseconds = 20;   // Why: the Mirror and client loops pass less often than a desk's 1 ms, for the battery
    private static readonly string[] s_widgetTypeKeys = [nameof(OrdersWidget), nameof(PositionsWidget), nameof(LadderWidget), nameof(InstrumentHeadersWidget), nameof(RiskLimitsWidget), nameof(MessageEfficiencyWidget), nameof(RateLimitWidget)];   // the row-fed widgets (no files) and the ladder, which picks its instrument; Send Order opens from their menus

    private readonly System.Timers.Timer _refreshTimer = new(100);
    private readonly LatencyText _latencyText = new();
    private FileSystemPath _serverName = "";
    private string _connectAddress = "";
    private IManualClient? _manualClient;                // open while connected; null before, and between a session's end and the rebuild
    private WorkspaceContext? _workspaceContext;
    private Thread? _clientThread;
    private volatile bool _isClientStopping;
    private int _guiNumber;                              // this view's GUI number; a rebuild asks for the same one first
    private bool _isRebuilding;                          // a rebuild's connect runs off the UI thread; no second one meanwhile (UI thread)

    public PhoneView()
    {
        InitializeComponent();
        TopBar.Background = Palette.WorkspaceRealtimeGreen;
        WidgetPicker.ItemsSource = s_widgetTypeKeys;
        WidgetPicker.SelectedIndex = 0;
        WidgetPicker.SelectionChanged += (_, _) => ShowWidget();
        _refreshTimer.Elapsed += OnRefresh;
        _refreshTimer.AutoReset = true;
        Loaded += (_, _) => Start();
    }

    /// Reads the config, starts the Mirror thread over in-process memory, and connects the GUI off the UI thread.
    private void Start()
    {
        try
        {
            // Step 1: this process's memory is its own; its name is the device's (Android reports "localhost")
            Memory.IsInProcess = true;
            Clock.Mode = ClockMode.Realtime;
            Platform.Name = Android.OS.Build.Model?.Replace(' ', '-') ?? "phone";

            // Step 2: the config built into the app (Phone/MirrorConfig.json), and the data folder under the app's files folder
            using StreamReader mirrorConfigReader = new(Android.App.Application.Context.Assets!.Open(MirrorConfig.s_fileName));
            MirrorConfig mirrorConfig = Json.Deserialize<MirrorConfig>(mirrorConfigReader.ReadToEnd());
            mirrorConfig.ThrowIfInvalid(MirrorConfig.s_fileName);
            string filesRoot = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? throw new InvalidOperationException("PhoneView.Start: no external files folder");
            Context.DirectoriesRoot = Path.Combine(filesRoot, "Data");
            _serverName = ServerContext.GetDirectoryPath(mirrorConfig.ServerName);
            _connectAddress = string.Join(", ", mirrorConfig.ConnectAddresses);

            // Step 3: the Mirror's loop, as Mirror.exe runs it on a desk
            TcpLinkConnector tcpLinkConnector = new(mirrorConfig.ListenPort, mirrorConfig.IPWhitelist, mirrorConfig.ConnectAddresses);
            LowLatency.StartBackgroundThread("Mirror", () => MirrorRunner.RunRemote(_serverName, tcpLinkConnector, mirrorConfig, s_passIntervalMilliseconds));

            // Step 4: the GUI's client, once the copy is up
            StatusText.Text = $"connecting to {_connectAddress}…";
            ConnectAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.GetBaseException().Message;
        }
    }

    /// Opens the GUI's client off the UI thread (the copy's header and the AllocatedClientId can take seconds over a mobile link),
    /// then shows the picked widget and starts the refresh.
    private void ConnectAsync()
    {
        Task.Run(() =>
        {
            try
            {
                ContextManager.Initialize(_serverName);
                Connect();
                Dispatcher.UIThread.Post(() =>
                {
                    _isRebuilding = false;
                    ShowWidget();
                    _refreshTimer.Start();
                });
            }
            catch (Exception exception)
            {
                Dispatcher.UIThread.Post(() => { _isRebuilding = false; StatusText.Text = $"connect failed: {exception.GetBaseException().Message}"; });
            }
        });
    }

    // The GUI's client under the server as principal (a server workspace) and its thread, as Workspace.Connect; not the UI thread.
    private void Connect()
    {
        _manualClient = ManualClient.Open(ContextManager.ServerContext.ServerName, ContextManager.ServerContext.ServerName, _guiNumber);
        ContextManager.ServerContext.WarmSymbolStrings();   // Why after Open: the copy is complete only once Open returns
        ManualClient.TryParseRemoteGuiName(_manualClient.Context.ClientName.ToString(), out _, out _guiNumber);
        _isClientStopping = false;
        IManualClient manualClient = _manualClient;
        _clientThread = LowLatency.StartBackgroundThread(manualClient.Context.ClientName, () =>
        {
            while (!Tools.Application.IsExiting && !_isClientStopping)
            {
                try
                {
                    manualClient.WriteSocket();
                    manualClient.ReadSocket();
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"PhoneView client thread: {exception}");
                }
                Thread.Sleep(s_passIntervalMilliseconds);
            }
        });
        _workspaceContext = new WorkspaceContext(ContextManager.ServerContext, manualClient);
    }

    // Stops the client thread and disposes the widget and the client: before a rebuild connects anew. UI thread.
    private void Disconnect()
    {
        _isClientStopping = true;
        while (_clientThread is { IsAlive: true })
            Thread.Sleep(1);
        Show(widget: null);
        _manualClient?.Dispose();
        _manualClient = null;
        _workspaceContext = null;
    }

    /// Shows the picked widget in place of the one shown.
    private void ShowWidget()
    {
        if (_workspaceContext == null || WidgetPicker.SelectedItem is not string typeKey)
            return;
        Show(Widgets.NewWidget(typeKey, _workspaceContext));
    }

    /// A widget another widget's menu opens (a ladder from Instrument Headers, Send Order from either), as a desk Window adds one:
    /// shown in place of the one shown, with the picker cleared.
    public void AddWidget(IWidget widget)
    {
        WidgetPicker.SelectedItem = null;
        Show(widget);
    }

    /// Shows this widget in the host, disposing the one shown, with its title-bar content (if any) in the strip under the bar.
    private void Show(IWidget? widget)
    {
        (WidgetHost.Content as IDisposable)?.Dispose();
        WidgetHost.Content = widget;
        TitleBarHost.Content = widget?.TitleBarContent;
        TitleBarStrip.IsVisible = TitleBarHost.Content != null;
    }

    // Every 100 ms: the clock and the latency in the bar, and a rebuild once the server or the Remote Mirror this GUI was opened on is replaced.
    private void OnRefresh(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_manualClient is not RemoteClient remoteClient || _isRebuilding)
                return;
            // Why: a restarted server is a new run (client ids, instrument ids), so the view rebuilds on it (see Spec.md "Remote workspaces")
            if (remoteClient.IsSessionEnded)
            {
                _isRebuilding = true;
                StatusText.Text = "server restarted: rebuilding…";
                ContextManager.Renew();
                Disconnect();
                ConnectAsync();
                return;
            }
            StatusText.Text = _latencyText.GetText(ContextManager.ServerContext, remoteClient) + "    " + ContextManager.ServerContext.GetLatestMarketDataTimestamp().ToString("yyyy-MM-dd HH:mm:ss.fff");
        });
    }
}
