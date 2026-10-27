using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.App.Shell;

/// <summary>
/// The desktop shell: main window (placement, close-to-tray, start minimized), tray icon, theme, activation by a
/// second launch and quitting. The window is only ever hidden when a tray host exists to bring it back; otherwise
/// it is minimized, so it can never be lost.
/// </summary>
public sealed class DesktopShell : IShellHost, IDisposable
{
    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly ShellContext _ctx;
    private readonly AppHost _host;
    private readonly AppSettingsStore _store;
    private readonly AppLog _log;
    private readonly IConnectionStatus[] _connections;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _toggleItem;
    private readonly List<(IConnectionStatus Status, NativeMenuItem Item)> _statusItems = [];
    private bool _trayAvailable;
    private bool _quitting;
    private bool _wasMaximized;
    private Size _normalSize = new(WindowPlacementLogic.DefaultWidth, WindowPlacementLogic.DefaultHeight);
    private PixelPoint? _normalPosition;

    public DesktopShell(Application app, IClassicDesktopStyleApplicationLifetime desktop, ShellContext ctx, AppHost host)
    {
        _app = app;
        _desktop = desktop;
        _ctx = ctx;
        _host = host;
        _store = host.Settings;
        _log = host.Get<AppLog>();
        _connections = host.GetAll<IConnectionStatus>().ToArray();
        _trayAvailable = ctx.TrayProbe.IsCompletedSuccessfully ? ctx.TrayProbe.Result : OperatingSystem.IsWindows();
    }

    public MainWindow Window => _window ?? throw new InvalidOperationException("Not started");

    public bool IsTrayAvailable => _trayAvailable;

    public event Action? TrayAvailabilityChanged;

    public bool CanRestart => true;

    /// <summary>Positions positions are meaningless on Wayland (the compositor places windows).</summary>
    private static bool CanPosition => Program.BackendName != nameof(Platform.DisplayBackend.Wayland);

    private GeneralSettings General => _store.Settings.General;

    public void Start()
    {
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(_app, General.Theme);

        _window = new MainWindow { DataContext = new MainWindowViewModel(_host, this) };
        ApplyPlacement(_window);
        TrackPlacement(_window);
        _window.Closing += OnClosing;

        if (General.ShowTrayIcon) CreateTray();

        var minimized = _ctx.Options.Minimized || General.StartMinimized;
        if (minimized && General.ShowTrayIcon && WaitForTrayProbe())
        {
            _log.Info("Cuebot", "Started minimized to the tray");
            Dispatcher.UIThread.Post(_ctx.MarkUiReady, DispatcherPriority.Background);   // the UI thread is running
        }
        else
        {
            if (minimized) _window.WindowState = WindowState.Minimized;
            _window.Opened += (_, _) => _ctx.MarkUiReady();
            // The lifetime shows its MainWindow when it starts, so it is only assigned once the window should be seen.
            _desktop.MainWindow = _window;
            _window.Show();
        }

        if (_ctx.Instance is { } instance)
        {
            instance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            if (instance.ListenError is { } error)
                _log.Warn("Cuebot", $"Later launches cannot bring this window forward ({error})");
        }

        // The probe may still be running; act on its answer when it arrives.
        _ = _ctx.TrayProbe.ContinueWith(t => Dispatcher.UIThread.Post(() => SetTrayAvailable(t.IsCompletedSuccessfully && t.Result)),
            TaskScheduler.Default);
    }

    private bool WaitForTrayProbe()
    {
        try
        {
            if (_ctx.TrayProbe.Wait(TimeSpan.FromSeconds(2))) _trayAvailable = _ctx.TrayProbe.Result;
        }
        catch (AggregateException)
        {
            _trayAvailable = false;
        }
        return _trayAvailable;
    }

    private void SetTrayAvailable(bool available)
    {
        if (_trayAvailable == available) return;
        _trayAvailable = available;
        _log.Info("Cuebot", available ? "System tray found" : "No system tray found; the window minimizes instead of hiding");
        // Never stay hidden without a way back.
        if (!available && _window is { IsVisible: false } && !_quitting)
        {
            _desktop.MainWindow ??= _window;
            _window.WindowState = WindowState.Minimized;
            _window.Show();
        }
        TrayAvailabilityChanged?.Invoke();
    }

    // ---- Window ----

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
        e.Cancel = true;
        if (General.CloseToTray && General.ShowTrayIcon) HideToTray();
        else Quit();
    }

    /// <summary>Hides to the tray when there is one; otherwise minimizes.</summary>
    public void HideToTray()
    {
        if (_window is null) return;
        SavePlacement();
        if (_trayAvailable && _tray is not null) _window.Hide();
        else _window.WindowState = WindowState.Minimized;
        UpdateTrayMenu();
    }

    public void ShowWindow()
    {
        if (_window is null || _quitting) return;
        _desktop.MainWindow ??= _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        _window.Activate();
        UpdateTrayMenu();
    }

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true } && _window.WindowState != WindowState.Minimized) HideToTray();
        else ShowWindow();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        SavePlacement();
        _store.TrySave();
        // Start flushing stores now, off the UI thread; Program waits for it after the lifetime ends.
        _ = _host.DisposeAsync();
        _desktop.Shutdown();
    }

    public void Restart()
    {
        _ctx.RestartRequested = true;
        Quit();
    }

    private void ApplyPlacement(Window window)
    {
        var screens = window.Screens.All.Select(s => new ScreenArea(s.WorkingArea, s.Scaling)).ToArray();
        var saved = _store.Settings.Window;
        if (!CanPosition && saved is not null) saved = saved with { X = null, Y = null };
        var placement = WindowPlacementLogic.Resolve(saved, screens);
        window.Width = placement.Width;
        window.Height = placement.Height;
        _normalSize = new Size(placement.Width, placement.Height);
        if (placement.Position is { } p)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = p;
            _normalPosition = p;
        }
        if (placement.Maximized)
        {
            window.WindowState = WindowState.Maximized;
            _wasMaximized = true;
        }
    }

    private void TrackPlacement(Window window)
    {
        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal && CanPosition) _normalPosition = e.Point;
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Window.WindowStateProperty)
            {
                if (window.WindowState is WindowState.Maximized or WindowState.FullScreen) _wasMaximized = true;
                else if (window.WindowState == WindowState.Normal) _wasMaximized = false;
            }
            else if (e.Property == Avalonia.Controls.Window.ClientSizeProperty && window.WindowState == WindowState.Normal)
            {
                _normalSize = window.ClientSize;
            }
        };
    }

    private void SavePlacement()
    {
        if (_window is null) return;
        _store.Settings.Window = WindowPlacementLogic.Capture(_normalSize.Width, _normalSize.Height,
            CanPosition ? _normalPosition : null, _wasMaximized);
        _store.TrySave();
    }

    // ---- Tray ----

    private void CreateTray()
    {
        if (_tray is not null) return;
        var menu = new NativeMenu();
        _toggleItem = new NativeMenuItem("Hide Cuebot");
        _toggleItem.Click += (_, _) => ToggleWindow();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        foreach (var status in _connections)
        {
            var item = new NativeMenuItem { IsEnabled = false };
            _statusItems.Add((status, item));
            menu.Items.Add(item);
            status.StatusChanged += OnStatusChanged;
        }
        if (_connections.Length > 0) menu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit Cuebot");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        try
        {
            using var icon = AssetLoader.Open(new Uri("avares://Cuebot/Assets/cuebot.ico"));
            _tray = new TrayIcon { Icon = new WindowIcon(icon), Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ToggleWindow();
            TrayIcon.SetIcons(_app, [_tray]);
        }
        catch (Exception ex)
        {
            // No tray support at all on this platform/session: carry on without it.
            _log.Warn("Cuebot", $"Tray icon unavailable: {ex.Message}");
            _tray = null;
            SetTrayAvailable(false);
        }
        UpdateTrayMenu();
    }

    private void DestroyTray()
    {
        if (_tray is null) return;
        foreach (var (status, _) in _statusItems) status.StatusChanged -= OnStatusChanged;
        _statusItems.Clear();
        TrayIcon.SetIcons(_app, []);
        _tray.Dispose();
        _tray = null;
        // Hidden with no tray left: bring the window back.
        if (_window is { IsVisible: false } && !_quitting) ShowWindow();
    }

    private void OnStatusChanged() => Dispatcher.UIThread.Post(UpdateTrayMenu);

    private void UpdateTrayMenu()
    {
        if (_tray is null) return;
        var hidden = _window is null || !_window.IsVisible || _window.WindowState == WindowState.Minimized;
        if (_toggleItem is not null) _toggleItem.Header = hidden ? "Show Cuebot" : "Hide Cuebot";
        foreach (var (status, item) in _statusItems) item.Header = $"{status.Name}: {status.State}";
        var summary = string.Join(", ", _connections.Select(c => $"{c.Name} {c.State.ToString().ToLowerInvariant()}"));
        _tray.ToolTipText = summary.Length == 0 ? "Cuebot" : $"Cuebot - {summary}";
    }

    // ---- IShellHost ----

    public void ApplySettings()
    {
        Theme.Apply(_app, General.Theme);
        if (General.ShowTrayIcon) CreateTray();
        else DestroyTray();
    }

    public void OpenFolder(string path) => FolderLauncher.Open(path);

    public void Dispose()
    {
        foreach (var (status, _) in _statusItems) status.StatusChanged -= OnStatusChanged;
        _tray?.Dispose();
        _tray = null;
    }
}
