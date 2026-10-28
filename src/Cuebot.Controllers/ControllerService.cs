using System.Globalization;
using Cuebot.Controllers.Device;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Controllers;

/// <summary>An input caught by <see cref="ControllerService.LearnAsync"/>, with trigger settings that match it.</summary>
public sealed record LearnedInput(
    ControllerSnapshot Controller,
    string Kind,
    string Name,
    string Label,
    string TriggerType,
    IReadOnlyDictionary<string, string> TriggerSettings)
{
    /// <summary>"Button South (A) on #1 Xbox Controller".</summary>
    public string Description => Label.Length > 0 && !string.Equals(Label, Name, StringComparison.OrdinalIgnoreCase)
        ? $"{Kind} {Name} ({Label}) on {Controller.DisplayName}"
        : $"{Kind} {Name} on {Controller.DisplayName}";

    /// <summary>"button = South, state = Pressed, controller = #1".</summary>
    public string SettingsText => string.Join(", ", TriggerSettings.Select(kv => $"{kv.Key} = {kv.Value}"));
}

/// <summary>
/// Game-controller input: turns device events from an <see cref="IControllerBackend"/> into bus events
/// (controller.button/axis/hat/connected/disconnected) with deadzones and axis throttling, keeps live state for the UI,
/// and runs rumble/LED commands. The backend only runs while something needs it (see <see cref="ControllerWatchMode"/>).
/// </summary>
public sealed class ControllerService : IConnectionStatus, IPeriodicService, IAsyncDisposable
{
    /// <summary>How long a sub-action, script or the learn mode keeps controllers watched in Automatic mode.</summary>
    public static readonly TimeSpan LeaseTime = TimeSpan.FromMinutes(5);

    private readonly IControllerBackend _backend;
    private readonly JsonFile<ControllerSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly AppPaths _paths;
    private readonly Func<bool> _triggersWanted;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<uint, ControllerState> _controllers = [];
    private readonly AxisThrottle _throttle = new();
    private readonly List<(TaskCompletionSource<LearnedInput> Tcs, string Filter)> _learners = [];
    private readonly LinkedList<string> _recentButtons = new();
    private readonly LinkedList<string> _recentAxes = new();
    private ITimer? _flushTimer;
    private Task<ControllerBackendStart>? _starting;
    private bool _running;
    private bool _failed;
    private bool _disposed;
    private string _version = "";
    private string _error = "";
    private DateTimeOffset _leaseUntil = DateTimeOffset.MinValue;
    private int _holds;
    private long _inputVersion;

    public ControllerService(IControllerBackend backend, JsonFile<ControllerSettings> settings, EventBus bus, AppLog log,
        AppPaths paths, Func<bool> triggersWanted, TimeProvider? time = null)
    {
        _backend = backend;
        _settings = settings;
        _bus = bus;
        _log = log;
        _paths = paths;
        _triggersWanted = triggersWanted;
        _time = time ?? TimeProvider.System;
        _backend.EventReceived += OnRaw;
        _backend.Faulted += OnFaulted;
    }

    public string Name => "Game Controllers";

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                if (_failed) return ConnectionState.Error;
                if (_starting is not null) return ConnectionState.Connecting;
                return _running && _controllers.Count > 0 ? ConnectionState.Connected : ConnectionState.Disconnected;
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (_gate)
            {
                if (_failed) return "Controller input is unavailable: " + _error;
                if (_starting is not null) return "Starting controller input...";
                if (!_running)
                    return Settings.Mode == ControllerWatchMode.Off
                        ? "Turned off in the controller settings"
                        : "Idle: no enabled action uses a controller trigger";
                if (_controllers.Count == 0) return "No controllers connected";
                var names = _controllers.Values.OrderBy(c => c.Slot).Select(c => $"#{c.Slot} {c.Device.Name}");
                return $"{_controllers.Count} controller{(_controllers.Count == 1 ? "" : "s")}: {string.Join(", ", names)}";
            }
        }
    }

    public event Action? StatusChanged;

    /// <summary>A controller connected, disconnected or changed (mapping, battery). Raised on a background thread.</summary>
    public event Action? ControllersChanged;

    public ControllerSettings Settings => _settings.Value;

    public string BackendName => _backend.Name;

    /// <summary>The native library version once started ("SDL 3.4.18"), else "".</summary>
    public string BackendVersion
    {
        get { lock (_gate) return _version; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Changes whenever any input arrives; the UI polls it to refresh live state cheaply.</summary>
    public long InputVersion => Interlocked.Read(ref _inputVersion);

    public IReadOnlyList<ControllerSnapshot> Controllers
    {
        get { lock (_gate) return _controllers.Values.OrderBy(c => c.Slot).Select(c => c.Snapshot()).ToArray(); }
    }

    /// <summary>Button names pressed most recently first (for the trigger editor's suggestions: a simple "learn").</summary>
    public IReadOnlyList<string> RecentButtons
    {
        get { lock (_gate) return [.. _recentButtons]; }
    }

    public IReadOnlyList<string> RecentAxes
    {
        get { lock (_gate) return [.. _recentAxes]; }
    }

    public void SaveSettings()
    {
        _settings.ScheduleSave();
        StatusChanged?.Invoke();
    }

    /// <summary>Auto-start: watches right away when the mode and the profile ask for it. Never throws.</summary>
    public Task StartAsync()
    {
        Sync();
        return Task.CompletedTask;
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Sync();

    /// <summary>Starts or stops the backend to match the mode, the triggers in the profile and any holds/leases.</summary>
    public void Sync()
    {
        bool wanted;
        try { wanted = IsWanted(); }
        catch (ObjectDisposedException) { return; }
        catch (Exception ex)
        {
            _log.Error("Controllers", "Could not check whether controller input is needed", ex);
            return;
        }
        if (wanted) BeginStart();
        else StopBackend("no longer needed");
    }

    private bool IsWanted()
    {
        var mode = Settings.Mode;
        if (mode == ControllerWatchMode.Off) return false;
        if (mode == ControllerWatchMode.Always) return true;
        lock (_gate)
            if (_holds > 0 || _time.GetUtcNow() < _leaseUntil) return true;
        return _triggersWanted();
    }

    /// <summary>Keeps controllers watched until disposed (the Controllers page while it is shown). Starts at once.</summary>
    public IDisposable Hold()
    {
        lock (_gate) _holds++;
        Sync();
        return new Release(this);
    }

    private sealed class Release(ControllerService owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (owner._gate) owner._holds--;
        }
    }

    /// <summary>
    /// Makes sure controllers are being watched (for sub-actions and scripts) and keeps them watched for
    /// <see cref="LeaseTime"/>; waits for the backend to report the connected controllers.
    /// </summary>
    /// <exception cref="InvalidOperationException">Controller input is turned off or cannot start.</exception>
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        if (Settings.Mode == ControllerWatchMode.Off)
            throw new InvalidOperationException("Controller input is turned off (Controllers page → Watch controllers).");
        Task<ControllerBackendStart>? starting;
        lock (_gate)
        {
            var until = _time.GetUtcNow() + LeaseTime;
            if (until > _leaseUntil) _leaseUntil = until;
            if (_running) return;
            if (_failed) throw new InvalidOperationException("Controller input is unavailable: " + _error);
        }
        BeginStart();
        lock (_gate) starting = _starting;
        if (starting is not null) await starting.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_failed) throw new InvalidOperationException("Controller input is unavailable: " + _error);
            if (!_running) throw new InvalidOperationException("Controller input did not start.");
        }
    }

    /// <summary>Forgets a failed start and tries again (Retry button; after changing settings).</summary>
    public void Restart()
    {
        StopBackend("restarting");
        lock (_gate)
        {
            _failed = false;
            _error = "";
        }
        StatusChanged?.Invoke();
        Sync();
    }

    private void BeginStart()
    {
        ControllerBackendOptions options;
        lock (_gate)
        {
            if (_disposed || _running || _starting is not null || _failed) return;
            options = new ControllerBackendOptions { MappingsFile = ResolveMappingsFile(), IncludeJoysticks = Settings.IncludeJoysticks };
            _starting = StartBackendAsync(options);
        }
        StatusChanged?.Invoke();
    }

    private async Task<ControllerBackendStart> StartBackendAsync(ControllerBackendOptions options)
    {
        await Task.Yield();
        ControllerBackendStart result;
        try { result = await _backend.StartAsync(options).ConfigureAwait(false); }
        catch (Exception ex) { result = new ControllerBackendStart(false, "", ex.Message); }

        lock (_gate)
        {
            _starting = null;
            if (_disposed)
            {
                result = result with { Ok = false };
            }
            else if (result.Ok)
            {
                _running = true;
                _version = result.Version;
            }
            else
            {
                _failed = true;
                _error = result.Error ?? "unknown error";
                _controllers.Clear();
            }
        }
        if (_disposed) _backend.Stop();
        else if (result.Ok)
        {
            var mappings = result.MappingsLoaded > 0 ? $", {result.MappingsLoaded} mappings from {options.MappingsFile}" : "";
            _log.Info("Controllers", $"Watching game controllers ({result.Version}{mappings})");
        }
        else _log.Warn("Controllers", "Controller input is unavailable: " + _error);
        StatusChanged?.Invoke();
        ControllersChanged?.Invoke();
        return result;
    }

    private void StopBackend(string reason)
    {
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _running;
            if (!_running) return;
            _running = false;
            _controllers.Clear();
            _throttle.Clear();
        }
        try { _backend.Stop(); }
        catch (Exception ex) { _log.Error("Controllers", "Stopping controller input failed", ex); }
        if (wasRunning) _log.Info("Controllers", $"Stopped watching game controllers ({reason})");
        StatusChanged?.Invoke();
        ControllersChanged?.Invoke();
    }

    private void OnFaulted(string reason)
    {
        lock (_gate)
        {
            _running = false;
            _failed = true;
            _error = reason;
            _controllers.Clear();
            _throttle.Clear();
        }
        _log.Warn("Controllers", "Controller input stopped: " + reason);
        StatusChanged?.Invoke();
        ControllersChanged?.Invoke();
    }

    /// <summary>The mappings file to load: the configured one, else gamecontrollerdb.txt / gamecontroller.txt in the data folder.</summary>
    public string? ResolveMappingsFile()
    {
        var configured = Environment.ExpandEnvironmentVariables(Settings.MappingsFile.Trim());
        if (configured.Length > 0)
        {
            var path = Path.IsPathRooted(configured) ? configured : Path.Combine(_paths.Root, configured);
            if (File.Exists(path)) return path;
            _log.Warn("Controllers", $"Controller mappings file not found: {path}");
            return null;
        }
        foreach (var name in new[] { "gamecontrollerdb.txt", "gamecontroller.txt" })
            if (File.Exists(_paths.File(name))) return _paths.File(name);
        return null;
    }

    /// <summary>Loads the mappings file again (after editing it). Returns how many mappings were added.</summary>
    public async Task<int> ReloadMappingsAsync(CancellationToken ct = default)
    {
        var file = ResolveMappingsFile() ?? throw new InvalidOperationException(
            "No mappings file: set one on the Controllers page or put gamecontrollerdb.txt in the data folder.");
        if (!IsRunning) return 0;   // read when controller input starts
        var added = await _backend.AddMappingsAsync(file).WaitAsync(ct).ConfigureAwait(false);
        if (added < 0) throw new InvalidOperationException($"Could not read controller mappings from {file}.");
        _log.Info("Controllers", $"Loaded {added} controller mappings from {file}");
        return added;
    }

    // ---- input processing (backend thread) -------------------------------------------------------------

    private void OnRaw(ControllerRawEvent e)
    {
        try { Process(e); }
        catch (Exception ex) { _log.Error("Controllers", "Controller input handling failed", ex); }
    }

    /// <summary>Handles one device event (the backend's handler; internal for tests).</summary>
    internal void Process(ControllerRawEvent e)
    {
        var events = new List<BotEvent>(2);
        var changed = false;
        List<(TaskCompletionSource<LearnedInput>, LearnedInput)> learned = [];
        lock (_gate)
        {
            if (!_running && _starting is null) return;   // devices already plugged in arrive while starting
            var now = _time.GetUtcNow();
            switch (e)
            {
                case DeviceAddedEvent added:
                    changed = true;
                    if (_controllers.TryGetValue(added.DeviceId, out var existing)) existing.Update(added.Device);
                    else
                    {
                        var state = new ControllerState(added.Device, FreeSlot(), now);
                        _controllers[added.DeviceId] = state;
                        events.Add(Make(ControllerEvents.Connected, state, a => a["controllerCount"] = _controllers.Count));
                    }
                    break;
                case DeviceRemovedEvent removed:
                    if (_controllers.Remove(removed.Id, out var gone))
                    {
                        changed = true;
                        _throttle.Forget(removed.Id);
                        events.Add(Make(ControllerEvents.Disconnected, gone, a => a["controllerCount"] = _controllers.Count));
                    }
                    break;
                case BatteryEvent battery when _controllers.TryGetValue(battery.Id, out var c):
                    c.Update(c.Device with { Power = battery.Power, BatteryPercent = battery.Percent });
                    changed = true;
                    break;
                case ButtonEvent button when _controllers.TryGetValue(button.Id, out var c):
                    OnButton(c, button, now, events, learned);
                    break;
                case AxisEvent axis when _controllers.TryGetValue(axis.Id, out var c):
                    OnAxis(c, axis, now, events, learned);
                    break;
                case HatEvent hat when _controllers.TryGetValue(hat.Id, out var c):
                    c.EnsureHat(hat.Hat);
                    if (hat.Hat < c.Hats.Length && c.Hats[hat.Hat] != hat.Value)
                    {
                        var previous = c.Hats[hat.Hat];
                        c.Hats[hat.Hat] = hat.Value;
                        events.Add(HatChanged(c, hat.Hat, previous, hat.Value, isDPad: false));
                        LearnHat(c, hat.Hat, hat.Value, learned);
                    }
                    break;
            }
            if (e is ButtonEvent or AxisEvent or HatEvent) Interlocked.Increment(ref _inputVersion);
        }
        foreach (var ev in events) _bus.Publish(ev);
        foreach (var (tcs, input) in learned) tcs.TrySetResult(input);
        if (changed)
        {
            StatusChanged?.Invoke();
            ControllersChanged?.Invoke();
        }
    }

    private int FreeSlot()
    {
        var slot = 1;
        while (_controllers.Values.Any(c => c.Slot == slot)) slot++;
        return slot;
    }

    private void OnButton(ControllerState c, ButtonEvent e, DateTimeOffset now, List<BotEvent> events,
        List<(TaskCompletionSource<LearnedInput>, LearnedInput)> learned)
    {
        c.EnsureButton(e.Button);
        if (e.Button < 0 || e.Button >= c.Buttons.Length || c.Buttons[e.Button] == e.Down) return;
        c.Buttons[e.Button] = e.Down;
        var name = c.ButtonName(e.Button);
        var label = c.ButtonLabel(e.Button);
        long held = 0;
        if (e.Down) c.PressedAt[e.Button] = now;
        else held = (long)Math.Max(0, (now - c.PressedAt[e.Button]).TotalMilliseconds);

        events.Add(Make(ControllerEvents.Button, c, a =>
        {
            a["inputType"] = "Button";
            a["button"] = name;
            a["buttonLabel"] = label;
            a["buttonIndex"] = e.Button;
            a["pressed"] = e.Down;
            a["buttonState"] = e.Down ? "Pressed" : "Released";
            a["heldButtons"] = string.Join(",", c.HeldButtons());
            a["heldButtonCount"] = c.HeldButtons().Count;
            if (!e.Down) a["holdDurationMs"] = held;
        }));

        if (e.Down)
        {
            Remember(_recentButtons, name);
            foreach (var (tcs, filter) in _learners.ToArray())
            {
                if (!ControllerFilter.Matches(filter, c.Snapshot())) continue;
                _learners.Remove((tcs, filter));
                learned.Add((tcs, new LearnedInput(c.Snapshot(), "Button", name, label, ControllerEvents.Button,
                    new Dictionary<string, string> { ["controller"] = $"#{c.Slot}", ["button"] = name, ["state"] = "Pressed" })));
            }
        }

        // The D-pad doubles as Hat1 so one Hat trigger works for gamepads and joysticks.
        if (c.IsGamepad && Settings.DPadAsHat && e.Button is >= ControllerNames.DPadUp and <= ControllerNames.DPadRight)
        {
            var mask = ControllerNames.DPadMask(c.Buttons[ControllerNames.DPadUp], c.Buttons[ControllerNames.DPadDown],
                c.Buttons[ControllerNames.DPadLeft], c.Buttons[ControllerNames.DPadRight]);
            if (mask != c.DPadHat)
            {
                var previous = c.DPadHat;
                c.DPadHat = mask;
                events.Add(HatChanged(c, 0, previous, mask, isDPad: true));
            }
        }
    }

    private void OnAxis(ControllerState c, AxisEvent e, DateTimeOffset now, List<BotEvent> events,
        List<(TaskCompletionSource<LearnedInput>, LearnedInput)> learned)
    {
        c.EnsureAxis(e.Axis);
        if (e.Axis < 0 || e.Axis >= c.Axes.Length) return;
        var isTrigger = ControllerNames.IsTriggerAxis(c.IsGamepad, e.Axis);
        var value = AxisMath.Normalize(e.Value, isTrigger, isTrigger ? Settings.TriggerDeadzone : Settings.StickDeadzone);
        var before = c.Axes[e.Axis];
        c.RawAxes[e.Axis] = e.Value;
        c.Axes[e.Axis] = value;

        if (Math.Abs(value) >= 0.5 && Math.Abs(before) < 0.5)
        {
            var name = ControllerNames.Axis(c.IsGamepad, e.Axis);
            Remember(_recentAxes, name);
            foreach (var (tcs, filter) in _learners.ToArray())
            {
                if (!ControllerFilter.Matches(filter, c.Snapshot())) continue;
                _learners.Remove((tcs, filter));
                learned.Add((tcs, new LearnedInput(c.Snapshot(), "Axis", name, AxisMath.Direction(value), ControllerEvents.Axis,
                    new Dictionary<string, string>
                    {
                        ["controller"] = $"#{c.Slot}", ["axis"] = name, ["direction"] = AxisMath.Direction(value),
                        ["threshold"] = "50", ["mode"] = "Enter",
                    })));
            }
        }

        if (!AxisMath.IsSignificant(c.PublishedAxes[e.Axis], value, Settings.AxisMinChange)) return;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.AxisThrottleMs, 0, 5000));
        if (_throttle.Offer(e.Id, e.Axis, now, interval)) events.Add(AxisMoved(c, e.Axis));
        else ScheduleFlush(interval);
    }

    private BotEvent AxisMoved(ControllerState c, int axis)
    {
        var previous = c.PublishedAxes[axis];
        var value = c.Axes[axis];
        c.PublishedAxes[axis] = value;
        var isTrigger = ControllerNames.IsTriggerAxis(c.IsGamepad, axis);
        return Make(ControllerEvents.Axis, c, a =>
        {
            a["inputType"] = "Axis";
            a["axis"] = ControllerNames.Axis(c.IsGamepad, axis);
            a["axisIndex"] = axis;
            a["axisValue"] = Math.Round(value, 3);
            a["axisPercent"] = (int)Math.Round(value * 100);
            a["axisRaw"] = (int)c.RawAxes[axis];
            a["axisPrevious"] = Math.Round(previous, 3);
            a["axisDirection"] = AxisMath.Direction(value);
            a["isTrigger"] = isTrigger;
        });
    }

    private void ScheduleFlush(TimeSpan interval)
    {
        if (_flushTimer is not null) return;
        _flushTimer = _time.CreateTimer(_ => FlushAxes(), null, interval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Publishes held-back axis values whose throttle interval has passed (timer callback; internal for tests).</summary>
    internal void FlushAxes()
    {
        var events = new List<BotEvent>();
        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            if (!_running) return;
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.AxisThrottleMs, 0, 5000));
            foreach (var (device, axis) in _throttle.TakeDue(_time.GetUtcNow(), interval))
                if (_controllers.TryGetValue(device, out var c) && axis < c.Axes.Length && c.Axes[axis] != c.PublishedAxes[axis])
                    events.Add(AxisMoved(c, axis));
            if (_throttle.HasPending) ScheduleFlush(interval);
        }
        foreach (var e in events) _bus.Publish(e);
    }

    private BotEvent HatChanged(ControllerState c, int hat, byte previous, byte value, bool isDPad) =>
        Make(ControllerEvents.Hat, c, a =>
        {
            a["inputType"] = "Hat";
            a["hat"] = ControllerNames.Hat(hat);
            a["hatIndex"] = hat;
            a["hatValue"] = (int)value;
            a["hatDirection"] = ControllerNames.HatDirection(value);
            a["hatPrevious"] = (int)previous;
            a["hatPreviousDirection"] = ControllerNames.HatDirection(previous);
            a["isDPad"] = isDPad;
        });

    private void LearnHat(ControllerState c, int hat, byte value, List<(TaskCompletionSource<LearnedInput>, LearnedInput)> learned)
    {
        if (value == 0) return;
        foreach (var (tcs, filter) in _learners.ToArray())
        {
            if (!ControllerFilter.Matches(filter, c.Snapshot())) continue;
            _learners.Remove((tcs, filter));
            var direction = ControllerNames.HatDirection(value);
            learned.Add((tcs, new LearnedInput(c.Snapshot(), "Hat", ControllerNames.Hat(hat), direction, ControllerEvents.Hat,
                new Dictionary<string, string> { ["controller"] = $"#{c.Slot}", ["hat"] = ControllerNames.Hat(hat), ["direction"] = direction })));
        }
    }

    private static void Remember(LinkedList<string> list, string name)
    {
        list.Remove(name);
        list.AddFirst(name);
        while (list.Count > 8) list.RemoveLast();
    }

    private static BotEvent Make(string type, ControllerState c, Action<Dictionary<string, object?>> fill)
    {
        var args = ControllerState.DeviceArgs(c.Device, c.Slot);
        fill(args);
        return new BotEvent(type, args);
    }

    // ---- learn ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Waits for the next button press, stick/trigger movement past half way or hat direction (on controllers
    /// matching <paramref name="controller"/>), and returns it with matching trigger settings; null on timeout.
    /// </summary>
    public async Task<LearnedInput?> LearnAsync(TimeSpan timeout, string controller = "", CancellationToken ct = default)
    {
        await EnsureRunningAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<LearnedInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _learners.Add((tcs, controller));
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _learners.RemoveAll(l => l.Tcs == tcs);
        }
    }

    // ---- commands ------------------------------------------------------------------------------------------

    /// <summary>Connected controllers matching a filter (see <see cref="ControllerFilter"/>).</summary>
    public IReadOnlyList<ControllerSnapshot> Resolve(string filter) =>
        Controllers.Where(c => ControllerFilter.Matches(filter, c)).ToArray();

    /// <summary>Rumbles matching controllers; strengths 0-100 %. Returns the controllers that rumbled.</summary>
    public async Task<IReadOnlyList<ControllerSnapshot>> RumbleAsync(string filter, double lowPercent, double highPercent,
        int durationMs, CancellationToken ct = default)
    {
        await EnsureRunningAsync(ct).ConfigureAwait(false);
        var low = ToMotor(lowPercent);
        var high = ToMotor(highPercent);
        var duration = (uint)Math.Clamp(durationMs, 0, 60_000);
        var done = new List<ControllerSnapshot>();
        foreach (var c in Resolve(filter))
            if (await _backend.RumbleAsync(c.Device.Id, low, high, duration).WaitAsync(ct).ConfigureAwait(false))
                done.Add(c);
        return done;
    }

    /// <summary>Sets the LED colour of matching controllers that have one. Returns those that changed.</summary>
    public async Task<IReadOnlyList<ControllerSnapshot>> SetLedAsync(string filter, byte red, byte green, byte blue,
        CancellationToken ct = default)
    {
        await EnsureRunningAsync(ct).ConfigureAwait(false);
        var done = new List<ControllerSnapshot>();
        foreach (var c in Resolve(filter))
            if (await _backend.SetLedAsync(c.Device.Id, red, green, blue).WaitAsync(ct).ConfigureAwait(false))
                done.Add(c);
        return done;
    }

    private static ushort ToMotor(double percent) => (ushort)Math.Round(Math.Clamp(percent, 0, 100) / 100 * 65535);

    /// <summary>"#RRGGBB", "RRGGBB", "#RGB" or "r,g,b".</summary>
    public static (byte R, byte G, byte B) ParseColor(string text)
    {
        text = text.Trim();
        if (text.Contains(','))
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts.All(p => byte.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                return (byte.Parse(parts[0], CultureInfo.InvariantCulture), byte.Parse(parts[1], CultureInfo.InvariantCulture),
                    byte.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        var hex = text.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length == 8) hex = hex[2..];   // #AARRGGBB (Avalonia/Streamer.bot colour pickers)
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        throw new ArgumentException($"'{text}' is not a colour (use #RRGGBB or r,g,b).");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            foreach (var (tcs, _) in _learners) tcs.TrySetCanceled();
            _learners.Clear();
        }
        _backend.EventReceived -= OnRaw;
        _backend.Faulted -= OnFaulted;
        await Task.Run(() =>
        {
            try { _backend.Stop(); }
            catch (Exception ex) { _log.Error("Controllers", "Stopping controller input failed", ex); }
            _backend.Dispose();
        }).ConfigureAwait(false);
        lock (_gate)
        {
            _running = false;
            _controllers.Clear();
        }
    }
}
