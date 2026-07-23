using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Dialogs;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Provider;
using Tools;

namespace Workspace;

/// <summary>
/// Custom provider to inject favorite folder shortcuts into the left-hand 
/// sidebar of the Avalonia Managed File Dialog, while bypassing native DBus calls.
/// </summary>
public sealed class CustomVolumeInfoProvider : IMountedVolumeInfoProvider
{
    public IDisposable Listen(ObservableCollection<MountedVolumeInfo> items)
    {
        items.Clear();

        // --- ADD YOUR CUSTOM SHORTCUTS HERE ---
        items.Add(new MountedVolumeInfo { VolumeLabel = "Servers", VolumePath = ServerContext.DirectoriesPath, VolumeSizeBytes = 0 });
        items.Add(new MountedVolumeInfo { VolumeLabel = "Strategies", VolumePath = ClientContext.DirectoriesPath, VolumeSizeBytes = 0 });

        // --- RETAIN STANDARD DRIVES ---
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                    items.Add(new MountedVolumeInfo { VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel, VolumePath = drive.RootDirectory.FullName, VolumeSizeBytes = (ulong)Math.Max(0, drive.TotalSize) });
            }
        }
        catch
        {
        }

        return new Disposable();
    }

    private sealed class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public static class WorkspaceRunner
{
    private record WorkspaceRequest(string ServerName, string ClientName, ClockMode Mode, string? WorkspacePath);

    private static WorkspaceRequest? s_startupRequest;
    private static IClassicDesktopStyleApplicationLifetime? s_desktopLifetime;
    private static Thread? s_uiThread;
    private static readonly object s_lock = new object();
    private static readonly System.Collections.Generic.Queue<WorkspaceRequest> s_pendingQueue = new System.Collections.Generic.Queue<WorkspaceRequest>();
    private static bool s_ownsContextManager = false;

    // Starts UI programmatically on the current thread. Blocks thread until closed.
    public static void RunOnThisThread(string serverName, string clientName, ClockMode mode, string? workspacePath = null)
    {
        s_ownsContextManager = true;
        Thread.CurrentThread.Name = "Workspace-UI";
        s_startupRequest = new WorkspaceRequest(serverName, clientName, mode, workspacePath);
        StartAvalonia(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
    }

    // Starts UI seamlessly from a running Strategy. Spawns thread automatically if needed. Non-blocking.
    public static void RunOnBackgroundThread(string serverName, string clientName, ClockMode mode, string? workspacePath = null)
    {
        WorkspaceRequest request = new WorkspaceRequest(serverName, clientName, mode, workspacePath);

        lock (s_lock)
        {
            if (s_desktopLifetime != null)
            {
                IClassicDesktopStyleApplicationLifetime desktop = s_desktopLifetime;
                Dispatcher.UIThread.Post(() => ProcessRequestToStartWorkspace(request, desktop));
                return;
            }

            s_pendingQueue.Enqueue(request);

            if (s_uiThread == null || !s_uiThread.IsAlive)
            {
                using ManualResetEventSlim startedEvent = new ManualResetEventSlim(false);
                s_uiThread = new Thread(() =>
                {
                    LowLatency.PinCurrentThreadToCoreRange(LowLatency.HouseKeepingCores);
                    LowLatency.SetThreadPriorityNormal();
                    startedEvent.Set();
                    try
                    {
                        StartAvalonia(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[WorkspaceRunner] Fatal error: {ex}");
                    }
                });
                s_uiThread.Name = "Workspace-UI";
                s_uiThread.IsBackground = false;

                if (OperatingSystem.IsWindows())
                    s_uiThread.SetApartmentState(ApartmentState.STA);

                s_uiThread.Start();
                startedEvent.Wait();
            }
        }
    }

    // DRY Avalonia bootstrapper. Centralizes setup to prevent repeated code.

    private static void StartAvalonia(string[] args, ShutdownMode shutdownMode)
    {
        Console.WriteLine($"WorkspaceRunner::StartAvalonia");

        AppBuilder builder = AppBuilder.Configure<App>();

        // INJECT CUSTOM FOLDER SHORTCUTS
        builder = builder.With(new ManagedFileDialogOptions { CustomVolumeInfoProvider = new CustomVolumeInfoProvider() });

        builder = builder.UsePlatformDetect();
        builder = builder.WithInterFont();
        builder = builder.LogToTrace();

        // Prevent hardware acceleration GPU drivers from initiating cross-CPU interrupts
        // that stall on the isolated RT core.
        builder = builder.With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } }).With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });

        // Bypass native OS DBus/GTK file dialogs which can trigger compositor/DRM kernel hangs.
        // Instead, use Avalonia's own software-rendered managed dialogs.
        builder = builder.UseManagedSystemDialogs();

        // Globally intercept the ManagedFileChooserWindow to enforce 60% sizing
        //BEGIN_FILE HFT/Workspace/WorkspaceRunner.cs

        // Globally intercept the ManagedFileChooserWindow to enforce 60% sizing and auto-column width
        builder = builder.AfterSetup(_ =>
        {
            Window.WindowOpenedEvent.AddClassHandler<Window>((sender, e) =>
            {
                if (sender.GetType().Name.Contains("ManagedFileChooserWindow"))
                {
                    // 1. Handle Sizing and Centering
                    Screen? screen = sender.Screens.ScreenFromVisual(sender) ?? sender.Screens.Primary;
                    if (screen != null)
                    {
                        sender.Width = screen.WorkingArea.Width * 0.6;
                        sender.Height = screen.WorkingArea.Height * 0.6;
                        sender.Position = new PixelPoint(
                            screen.WorkingArea.X + (int)((screen.WorkingArea.Width - sender.Width) / 2),
                            screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - sender.Height) / 2));
                    }

                    // 2. Handle Column Widths
                    // We wait for the window to load its template to find the internal DataGrid
                    sender.TemplateApplied += (s, ev) =>
                    {
                        DataGrid? grid = sender.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();

                        if (grid != null)
                        {
                            foreach (DataGridColumn col in grid.Columns)
                            {
                                string? headerText = col.Header?.ToString();
                                if (headerText != null && headerText.Contains("Name", StringComparison.OrdinalIgnoreCase))
                                {
                                    col.Width = DataGridLength.Auto;
                                    break;
                                }
                            }
                        }
                    };
                }
            });
        });

        ClassicDesktopStyleApplicationLifetime lifetime = new ClassicDesktopStyleApplicationLifetime();
        lifetime.ShutdownMode = shutdownMode;

        builder.SetupWithLifetime(lifetime);
        lifetime.Start(args ?? Array.Empty<string>());
    }

    // Hook called by App.axaml.cs when Avalonia engine is fully booted.
    internal static void OnAppReady(IClassicDesktopStyleApplicationLifetime desktop)
    {
        s_desktopLifetime = desktop;

        if (s_startupRequest != null)
            ProcessRequestToStartWorkspace(s_startupRequest, desktop);

        FlushQueuedWorkspaces(desktop);
    }

    // Empties the queue of windows requested before the UI thread was ready.
    private static void FlushQueuedWorkspaces(IClassicDesktopStyleApplicationLifetime desktop)
    {
        List<WorkspaceRequest> pendingRequests;

        lock (s_lock)
        {
            if (s_pendingQueue.Count == 0)
                return;

            pendingRequests = new List<WorkspaceRequest>(s_pendingQueue);
            s_pendingQueue.Clear();
        }

        foreach (WorkspaceRequest request in pendingRequests)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ProcessRequestToStartWorkspace(request, desktop);
            });
        }
    }

    // Core window builder. Links UI to backend data context and renders it to screen.
    private static void ProcessRequestToStartWorkspace(WorkspaceRequest request, IClassicDesktopStyleApplicationLifetime desktop)
    {

        Context context = request.ClientName == request.ServerName ? ContextManager.ServerContext : ContextManager.GetClientContext(request.ClientName);
        Workspace window = new Workspace(context);

        if (desktop.MainWindow == null)
        {
            desktop.MainWindow = window;

            if (s_ownsContextManager)
            {
                desktop.Exit += delegate (object? sender, ControlledApplicationLifetimeExitEventArgs e)
                {
                    ContextManager.Dispose();
                };
            }
        }

        window.Show();

        if (!string.IsNullOrEmpty(request.WorkspacePath))
            _ =window.LoadWorkspaceAsync(request.WorkspacePath);
    }
}