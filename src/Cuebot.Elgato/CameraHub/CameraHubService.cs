using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Elgato.Common;

namespace Cuebot.Elgato.CameraHub;

/// <summary>Bot event types published by <see cref="CameraHubService"/> (Streamer.bot's Camera Hub webcam triggers).</summary>
public static class CameraHubEvents
{
    public const string Prefix = "elgato.cameraHub.";
    public const string Connected = Prefix + "connected";
    public const string Disconnected = Prefix + "disconnected";
    public const string WebcamConnected = Prefix + "webcam.connected";
    public const string WebcamDisconnected = Prefix + "webcam.disconnected";
    public const string BrightnessChanged = Prefix + "webcam.brightnessChanged";
    public const string ContrastChanged = Prefix + "webcam.contrastChanged";
    public const string SaturationChanged = Prefix + "webcam.saturationChanged";
    public const string SharpnessChanged = Prefix + "webcam.sharpnessChanged";
    public const string FocusChanged = Prefix + "webcam.focusChanged";
    public const string PanChanged = Prefix + "webcam.panChanged";
    public const string TiltChanged = Prefix + "webcam.tiltChanged";
    public const string ZoomChanged = Prefix + "webcam.zoomChanged";
    public const string WhiteBalanceChanged = Prefix + "webcam.whiteBalanceChanged";
    public const string LiveWhiteBalanceChanged = Prefix + "webcam.liveWhiteBalanceChanged";
    public const string ShutterSpeedChanged = Prefix + "webcam.shutterSpeedChanged";
    public const string LiveShutterSpeedChanged = Prefix + "webcam.liveShutterSpeedChanged";
    public const string AutoFocusEnabled = Prefix + "webcam.autoFocusEnabled";
    public const string AutoFocusDisabled = Prefix + "webcam.autoFocusDisabled";
    public const string AutoWhiteBalanceEnabled = Prefix + "webcam.autoWhiteBalanceEnabled";
    public const string AutoWhiteBalanceDisabled = Prefix + "webcam.autoWhiteBalanceDisabled";
    public const string AutoExposureEnabled = Prefix + "webcam.autoExposureEnabled";
    public const string AutoExposureDisabled = Prefix + "webcam.autoExposureDisabled";
    public const string AntiFlickerChanged = Prefix + "webcam.antiFlickerChanged";
    public const string Mirrored = Prefix + "webcam.mirrored";
    public const string Flipped = Prefix + "webcam.flipped";
    public const string DeviceOrientationChanged = Prefix + "webcam.deviceOrientationChanged";
}

/// <summary>
/// Streamer.bot's "Camera Hub" webcam features. Camera Hub itself (Windows/macOS) has no public control interface (see
/// PROTOCOL.md), so Cuebot talks to the camera directly through its standard UVC controls: on Linux via V4L2, where
/// Camera Hub does not exist. Changes made by other apps are noticed by polling and published as the Camera Hub triggers.
/// </summary>
public sealed class CameraHubService : IConnectionStatus, IAsyncDisposable
{
    public const string ServiceName = "Camera Hub";
    private const string LogSource = "Camera Hub";

    /// <summary>Why nothing can run on this OS (null when a backend exists).</summary>
    public static readonly string? UnavailableReason = OperatingSystem.IsLinux()
        ? null
        : "Not available on this system: Elgato Camera Hub has no public control API, and direct webcam control is only implemented for Linux (V4L2). See src/Cuebot.Elgato/PROTOCOL.md.";

    private readonly JsonFile<ElgatoSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IWebcamBackend? _backend;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private Dictionary<string, Webcam> _webcams = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private bool _announced;

    public CameraHubService(JsonFile<ElgatoSettings> settings, EventBus bus, AppLog log, IWebcamBackend? backend = null, bool useDefaultBackend = true)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _backend = backend ?? (useDefaultBackend && OperatingSystem.IsLinux() ? new V4l2Backend() : null);
        StatusMessage = _backend is null ? UnavailableReason ?? "Not available" : "Not started";
    }

    public string Name => ServiceName;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; }
    public event Action? StatusChanged;

    /// <summary>A webcam appeared, went away, or a control value changed.</summary>
    public event Action? WebcamsChanged;

    public CameraHubSettings Settings => _settings.Value.CameraHub;

    public bool IsAvailable => _backend is not null;

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    public IReadOnlyList<Webcam> Webcams
    {
        get { lock (_gate) return _webcams.Values.ToArray(); }
    }

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Starts when auto-connect is on and webcam control is available here. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect || _backend is null) return;
        try
        {
            await ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Automatic start failed: {ex.Message}");
        }
    }

    public async Task ConnectAsync()
    {
        if (_backend is null) throw new PlatformNotSupportedException(UnavailableReason);
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Looking for webcams...");
            await ScanAsync(publishChanges: false, CancellationToken.None).ConfigureAwait(false);
            _announced = true;
            UpdateStatus();
            _bus.Publish(new BotEvent(CameraHubEvents.Connected, new Dictionary<string, object?> { ["backend"] = _backend.Description }));
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            if (_announced)
            {
                _announced = false;
                _bus.Publish(new BotEvent(CameraHubEvents.Disconnected, new Dictionary<string, object?> { ["reason"] = "disconnected" }));
            }
            SetStatus(ConnectionState.Disconnected, _backend is null ? UnavailableReason ?? "Not available" : "Stopped");
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Reads every webcam once and publishes what changed since the last read.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => ScanAsync(publishChanges: true, ct);

    /// <summary>Webcams matching a comma-separated list of names or ids; empty = the first webcam.</summary>
    public IReadOnlyList<Webcam> Resolve(string device)
    {
        var webcams = Webcams;
        if (webcams.Count == 0) throw new InvalidOperationException(IsConnected ? "No webcam found." : "Camera Hub (webcam control) is not started.");
        var wanted = device.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0) return [webcams[0]];
        if (wanted.Any(w => w is "*" || w.Equals("all", StringComparison.OrdinalIgnoreCase))) return webcams;
        var matched = webcams.Where(w => wanted.Any(x => string.Equals(x, w.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, w.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(x, w.Device.Path, StringComparison.Ordinal))).ToArray();
        return matched.Length > 0 ? matched : throw new ArgumentException($"No webcam matches '{device}'.");
    }

    /// <summary>Sets a control in Streamer.bot units (see <see cref="WebcamUnits"/>); returns the new display value of the first webcam.</summary>
    public async Task<int> SetAsync(string device, WebcamControl control, double value, bool adjust = false, CancellationToken ct = default)
    {
        var backend = _backend ?? throw new PlatformNotSupportedException(UnavailableReason);
        int? result = null;
        foreach (var webcam in Resolve(device))
        {
            if (!webcam.Ranges.TryGetValue(control, out var range))
                throw new InvalidOperationException($"{webcam.Name} has no {control} control.");
            var current = webcam.Display(control) ?? 0;
            var raw = WebcamUnits.ToRaw(control, adjust ? current + value : value, range);
            await RunAsync(() => backend.Set(webcam.Device, control, raw), ct).ConfigureAwait(false);
            result ??= WebcamUnits.ToDisplay(control, raw, range);
        }
        await ScanAsync(publishChanges: true, ct).ConfigureAwait(false);
        return result ?? 0;
    }

    /// <summary>Turns an on/off control (auto focus, auto white balance, auto exposure, mirror, flip) on, off or over.</summary>
    public async Task<bool> SetSwitchAsync(string device, WebcamControl control, SwitchMode mode, CancellationToken ct = default)
    {
        bool? result = null;
        foreach (var webcam in Resolve(device))
        {
            var on = Steps.Desired(mode, (webcam.Display(control) ?? 0) != 0);
            await SetAsync(webcam.Id, control, on ? 1 : 0, false, ct).ConfigureAwait(false);
            result ??= on;
        }
        return result ?? false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(250, Settings.PollIntervalMs)), ct).ConfigureAwait(false);
                try
                {
                    await ScanAsync(publishChanges: true, ct).ConfigureAwait(false);
                    UpdateStatus();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.Warn(LogSource, $"Reading webcams failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    private async Task ScanAsync(bool publishChanges, CancellationToken ct)
    {
        var backend = _backend ?? throw new PlatformNotSupportedException(UnavailableReason);
        await _deviceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var filter = Settings.NameFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var devices = await RunAsync(() => backend.Enumerate(), ct).ConfigureAwait(false);
            var next = new Dictionary<string, Webcam>(StringComparer.Ordinal);
            foreach (var device in devices)
            {
                if (filter.Length > 0 && !filter.Any(f => device.Name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                Dictionary<string, Webcam> previous;
                lock (_gate) previous = _webcams;
                var webcam = previous.TryGetValue(device.Id, out var known) && known.Device == device ? known : new Webcam(device);
                var old = webcam.Values;
                try
                {
                    await RunAsync(() => Read(backend, webcam), ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _log.Debug(LogSource, $"Skipping {device.Name}: {ex.Message}");
                    continue;
                }
                next[device.Id] = webcam;
                if (!publishChanges) continue;
                if (known is null) Publish(CameraHubEvents.WebcamConnected, webcam, []);
                else Diff(webcam, old);
            }
            Dictionary<string, Webcam> gone;
            lock (_gate)
            {
                gone = _webcams.Where(p => !next.ContainsKey(p.Key)).ToDictionary();
                _webcams = next;
            }
            if (publishChanges)
                foreach (var webcam in gone.Values) Publish(CameraHubEvents.WebcamDisconnected, webcam, []);
        }
        finally
        {
            _deviceGate.Release();
        }
        WebcamsChanged?.Invoke();
    }

    private static void Read(IWebcamBackend backend, Webcam webcam)
    {
        var ranges = new Dictionary<WebcamControl, WebcamRange>();
        var values = new Dictionary<WebcamControl, int>();
        foreach (var control in Enum.GetValues<WebcamControl>())
        {
            var range = webcam.Ranges.TryGetValue(control, out var r) ? r : backend.Query(webcam.Device, control);
            if (range is null) continue;
            ranges[control] = range;
            try
            {
                values[control] = backend.Get(webcam.Device, control);
            }
            catch (IOException)
            {
                // Some drivers refuse to read a control while its automatic mode is on.
            }
        }
        if (ranges.Count == 0 && webcam.Ranges.Count == 0) throw new IOException("no camera controls");
        webcam.Ranges = ranges;
        webcam.Values = values;
    }

    private void Diff(Webcam webcam, IReadOnlyDictionary<WebcamControl, int> old)
    {
        int? Old(WebcamControl c) => old.TryGetValue(c, out var raw) && webcam.Ranges.TryGetValue(c, out var range) ? WebcamUnits.ToDisplay(c, raw, range) : null;
        bool On(WebcamControl c) => (webcam.Display(c) ?? 0) != 0;
        foreach (var control in webcam.Values.Keys)
        {
            var before = Old(control);
            var now = webcam.Display(control);
            if (before is null || now is null || before == now) continue;
            switch (control)
            {
                case WebcamControl.Brightness: Changed(CameraHubEvents.BrightnessChanged, "brightness"); break;
                case WebcamControl.Contrast: Changed(CameraHubEvents.ContrastChanged, "contrast"); break;
                case WebcamControl.Saturation: Changed(CameraHubEvents.SaturationChanged, "saturation"); break;
                case WebcamControl.Sharpness: Changed(CameraHubEvents.SharpnessChanged, "sharpness"); break;
                case WebcamControl.Focus: Changed(CameraHubEvents.FocusChanged, "focus"); break;
                case WebcamControl.Pan: Changed(CameraHubEvents.PanChanged, "pan"); break;
                case WebcamControl.Tilt: Changed(CameraHubEvents.TiltChanged, "tilt"); break;
                case WebcamControl.WhiteBalance:
                    Changed(On(WebcamControl.AutoWhiteBalance) ? CameraHubEvents.LiveWhiteBalanceChanged : CameraHubEvents.WhiteBalanceChanged, "whiteBalance");
                    break;
                case WebcamControl.Exposure:
                    Publish(On(WebcamControl.AutoExposure) ? CameraHubEvents.LiveShutterSpeedChanged : CameraHubEvents.ShutterSpeedChanged, webcam, new()
                    {
                        ["shutterSpeed"] = WebcamUnits.ShutterSpeed(now.Value), ["oldShutterSpeed"] = WebcamUnits.ShutterSpeed(before.Value),
                        ["exposure"] = now, ["oldExposure"] = before,
                    });
                    break;
                case WebcamControl.Zoom:
                    Publish(CameraHubEvents.ZoomChanged, webcam, new()
                    {
                        ["zoomValue"] = now, ["zoomPercent"] = $"{now}%", ["oldZoomValue"] = before, ["oldZoomPercent"] = $"{before}%",
                        ["maximumZoom"] = WebcamUnits.MaxZoomPercent,
                    });
                    break;
                case WebcamControl.AutoFocus: Toggle(CameraHubEvents.AutoFocusEnabled, CameraHubEvents.AutoFocusDisabled, "autoFocus"); break;
                case WebcamControl.AutoWhiteBalance:
                    Toggle(CameraHubEvents.AutoWhiteBalanceEnabled, CameraHubEvents.AutoWhiteBalanceDisabled, "autoWhiteBalance");
                    break;
                case WebcamControl.AutoExposure: Toggle(CameraHubEvents.AutoExposureEnabled, CameraHubEvents.AutoExposureDisabled, "autoExposure"); break;
                case WebcamControl.AntiFlicker:
                    Publish(CameraHubEvents.AntiFlickerChanged, webcam, new()
                    {
                        ["antiFlicker"] = AntiFlicker.Name(now.Value), ["oldAntiFlicker"] = AntiFlicker.Name(before.Value),
                    });
                    break;
                case WebcamControl.Mirror:
                case WebcamControl.Flip:
                    var orientation = new Dictionary<string, object?> { ["flipped"] = On(WebcamControl.Flip), ["mirrored"] = On(WebcamControl.Mirror) };
                    Publish(control == WebcamControl.Mirror ? CameraHubEvents.Mirrored : CameraHubEvents.Flipped, webcam, orientation);
                    Publish(CameraHubEvents.DeviceOrientationChanged, webcam, orientation);
                    break;
            }

            void Changed(string type, string name) =>
                Publish(type, webcam, new() { [name] = now, ["old" + char.ToUpperInvariant(name[0]) + name[1..]] = before });

            void Toggle(string enabled, string disabled, string name) =>
                Publish(now != 0 ? enabled : disabled, webcam, new() { [name] = now != 0 });
        }
    }

    private void Publish(string type, Webcam webcam, Dictionary<string, object?> extra)
    {
        var args = new Dictionary<string, object?> { ["deviceId"] = webcam.Id, ["deviceName"] = webcam.Name, ["devicePath"] = webcam.Device.Path };
        foreach (var (key, value) in extra) args[key] = value;
        _bus.Publish(new BotEvent(type, args));
    }

    private void UpdateStatus()
    {
        var webcams = Webcams;
        var description = _backend?.Description ?? "";
        if (webcams.Count == 0) SetStatus(ConnectionState.Connecting, $"No webcam found ({description}); checking again");
        else SetStatus(ConnectionState.Connected, $"{string.Join(", ", webcams.Select(w => w.Name))} ({description})");
    }

    private void SetStatus(ConnectionState state, string message)
    {
        if (State == state && StatusMessage == message) return;
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) => Task.Run(work, ct);

    private static Task RunAsync(Action work, CancellationToken ct) => Task.Run(work, ct);

    private async Task StopLoopAsync()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Error while stopping: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
