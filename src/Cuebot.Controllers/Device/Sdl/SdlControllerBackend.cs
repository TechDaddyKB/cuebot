using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Cuebot.Core.Logging;
using static Cuebot.Controllers.Device.Sdl.SdlNative;

namespace Cuebot.Controllers.Device.Sdl;

/// <summary>
/// SDL3 joystick + gamepad subsystems on one dedicated background thread. SDL is initialised, polled and shut down
/// on that thread only, with no SDL video subsystem and no windows, so it never touches Avalonia's event loop, the
/// screensaver or the desktop. Background input is enabled (SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS), SDL's POSIX signal
/// handlers are disabled. Commands (rumble, LED, mappings) are queued to the thread. See PROTOCOL.md.
/// </summary>
internal sealed class SdlControllerBackend : IControllerBackend
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly AppLog _log;
    private readonly Func<string?> _probe;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);

    // Backend thread only:
    private readonly Dictionary<uint, IntPtr> _gamepads = [];
    private readonly Dictionary<uint, IntPtr> _joysticks = [];
    private ControllerBackendOptions _options = new();

    private Thread? _thread;
    private volatile bool _stop;
    private Task<ControllerBackendStart>? _started;

    /// <param name="probe">Checks that the native library loads (null = fine, else the reason); tests replace it.</param>
    public SdlControllerBackend(AppLog log, Func<string?>? probe = null)
    {
        _log = log;
        _probe = probe ?? Probe;
    }

    public string Name => "SDL3";

    public event Action<ControllerRawEvent>? EventReceived;
    public event Action<string>? Faulted;

    public Task<ControllerBackendStart> StartAsync(ControllerBackendOptions options)
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true } && _started is not null) return _started;
            var tcs = new TaskCompletionSource<ControllerBackendStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = false;
            _options = options;
            _started = tcs.Task;
            _thread = new Thread(() => Run(tcs)) { IsBackground = true, Name = "Cuebot controllers (SDL3)" };
            _thread.Start();
            return tcs.Task;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _started = null;
        }
        if (thread is null) return;
        _stop = true;
        _wake.Set();
        if (thread != Thread.CurrentThread && !thread.Join(StopTimeout))
            _log.Warn("Controllers", "The SDL controller thread did not stop in time");
    }

    public Task<bool> RumbleAsync(uint id, ushort low, ushort high, uint durationMs) => Post(() =>
        _gamepads.TryGetValue(id, out var pad) ? SDL_RumbleGamepad(pad, low, high, durationMs)
        : _joysticks.TryGetValue(id, out var joy) && SDL_RumbleJoystick(joy, low, high, durationMs), false);

    public Task<bool> SetLedAsync(uint id, byte red, byte green, byte blue) => Post(() =>
        _gamepads.TryGetValue(id, out var pad) ? SDL_SetGamepadLED(pad, red, green, blue)
        : _joysticks.TryGetValue(id, out var joy) && SDL_SetJoystickLED(joy, red, green, blue), false);

    public Task<int> AddMappingsAsync(string file) => Post(() => SDL_AddGamepadMappingsFromFile(file), -1);

    /// <summary>Runs <paramref name="work"/> on the SDL thread; <paramref name="fallback"/> when SDL is not running.</summary>
    private Task<T> Post<T>(Func<T> work, T fallback)
    {
        lock (_gate)
            if (_thread is not { IsAlive: true }) return Task.FromResult(fallback);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        _wake.Set();
        // The thread may have stopped between the check and the enqueue: never leave the caller waiting forever.
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : fallback,
            TaskScheduler.Default);
    }

    private void Run(TaskCompletionSource<ControllerBackendStart> started)
    {
        var initialised = false;
        try
        {
            if (_probe() is { } problem)
            {
                started.TrySetResult(new ControllerBackendStart(false, "", problem));
                return;
            }
            SDL_SetHint("SDL_NO_SIGNAL_HANDLERS", "1");                  // .NET owns SIGINT/SIGTERM
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");    // input while Cuebot is not focused
            SDL_SetHint("SDL_JOYSTICK_THREAD", "1");                     // Windows raw input on SDL's own thread
            SDL_SetHint("SDL_APP_NAME", "Cuebot");
            if (!SDL_Init(InitGamepad | InitJoystick))
            {
                started.TrySetResult(new ControllerBackendStart(false, "", "SDL could not start: " + Error()));
                return;
            }
            initialised = true;
            var mappings = 0;
            if (_options.MappingsFile is { Length: > 0 } file)
            {
                mappings = SDL_AddGamepadMappingsFromFile(file);
                if (mappings < 0) _log.Warn("Controllers", $"Could not read controller mappings from {file}: {Error()}");
            }
            EnumerateConnected();
            Drain();
            started.TrySetResult(new ControllerBackendStart(true, VersionText(), null, Math.Max(0, mappings)));

            while (!_stop)
            {
                while (_commands.TryDequeue(out var command)) command();
                Drain();
                _wake.WaitOne(PollInterval);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (!started.TrySetResult(new ControllerBackendStart(false, "", "the SDL3 library could not be loaded: " + ex.Message)))
                Faulted?.Invoke(ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("Controllers", "The SDL controller thread failed", ex);
            if (!started.TrySetResult(new ControllerBackendStart(false, "", ex.Message))) Faulted?.Invoke(ex.Message);
        }
        finally
        {
            try
            {
                foreach (var pad in _gamepads.Values) SDL_CloseGamepad(pad);
                foreach (var joy in _joysticks.Values) SDL_CloseJoystick(joy);
                _gamepads.Clear();
                _joysticks.Clear();
                if (initialised) SDL_Quit();
            }
            catch (Exception ex) { _log.Error("Controllers", "SDL shutdown failed", ex); }
            while (_commands.TryDequeue(out var command))
            {
                try { command(); }
                catch { /* the caller gets its fallback */ }
            }
        }
    }

    /// <summary>Opens what is already plugged in (SDL also queues ADDED events for those; duplicates are ignored).</summary>
    private void EnumerateConnected()
    {
        var list = SDL_GetJoysticks(out var count);
        if (list == IntPtr.Zero) return;
        try
        {
            for (var i = 0; i < count; i++) Added((uint)Marshal.ReadInt32(list, i * 4));
        }
        finally { SDL_free(list); }
    }

    private void Drain()
    {
        while (!_stop && SDL_PollEvent(out var e)) Handle(in e);
    }

    private void Handle(in Event e)
    {
        var id = e.Which;
        switch (e.Type)
        {
            case EventJoystickAdded or EventGamepadAdded:
                Added(id);
                break;
            case EventGamepadRemapped:
                if (_gamepads.TryGetValue(id, out var remapped)) Raise(new DeviceAddedEvent(GamepadInfo(id, remapped)));
                break;
            case EventJoystickRemoved or EventGamepadRemoved:
                Removed(id);
                break;
            case EventGamepadAxis when _gamepads.ContainsKey(id):
                Raise(new AxisEvent(id, e.Index, e.AxisValue));
                break;
            case EventGamepadButtonDown or EventGamepadButtonUp when _gamepads.ContainsKey(id):
                Raise(new ButtonEvent(id, e.Index, e.Type == EventGamepadButtonDown));
                break;
            case EventJoystickAxis when _joysticks.ContainsKey(id):
                Raise(new AxisEvent(id, e.Index, e.AxisValue));
                break;
            case EventJoystickButtonDown or EventJoystickButtonUp when _joysticks.ContainsKey(id):
                Raise(new ButtonEvent(id, e.Index, e.Type == EventJoystickButtonDown));
                break;
            case EventJoystickHat when _joysticks.ContainsKey(id):
                Raise(new HatEvent(id, e.Index, e.Byte21));
                break;
            case EventJoystickBattery when _gamepads.ContainsKey(id) || _joysticks.ContainsKey(id):
                Raise(new BatteryEvent(id, Power(e.PowerState), e.Percent));
                break;
        }
    }

    private void Added(uint id)
    {
        if (_gamepads.ContainsKey(id)) return;
        if (SDL_IsGamepad(id))
        {
            // A joystick that just got a mapping becomes a gamepad: reopen it through the gamepad API.
            if (_joysticks.Remove(id, out var old)) SDL_CloseJoystick(old);
            var pad = SDL_OpenGamepad(id);
            if (pad == IntPtr.Zero)
            {
                _log.Warn("Controllers", $"Could not open gamepad {id}: {Error()}");
                return;
            }
            _gamepads[id] = pad;
            Raise(new DeviceAddedEvent(GamepadInfo(id, pad)));
            return;
        }
        if (!_options.IncludeJoysticks || _joysticks.ContainsKey(id)) return;
        var joy = SDL_OpenJoystick(id);
        if (joy == IntPtr.Zero)
        {
            _log.Warn("Controllers", $"Could not open joystick {id}: {Error()}");
            return;
        }
        _joysticks[id] = joy;
        Raise(new DeviceAddedEvent(JoystickInfo(id, joy)));
    }

    private void Removed(uint id)
    {
        var known = false;
        if (_gamepads.Remove(id, out var pad))
        {
            SDL_CloseGamepad(pad);
            known = true;
        }
        if (_joysticks.Remove(id, out var joy))
        {
            SDL_CloseJoystick(joy);
            known = true;
        }
        if (known) Raise(new DeviceRemovedEvent(id));
    }

    private static ControllerDeviceInfo GamepadInfo(uint id, IntPtr pad)
    {
        var joy = SDL_GetGamepadJoystick(pad);
        var power = SDL_GetGamepadPowerInfo(pad, out var percent);
        var props = joy == IntPtr.Zero ? 0u : SDL_GetJoystickProperties(joy);
        return new ControllerDeviceInfo
        {
            Id = id,
            Name = Fallback(Text(SDL_GetGamepadName(pad)), "Gamepad"),
            Guid = joy == IntPtr.Zero ? "" : GuidText(SDL_GetJoystickGUID(joy)),
            IsGamepad = true,
            Type = ControllerNames.TypeName(SDL_GetGamepadType(pad)),
            PlayerIndex = SDL_GetGamepadPlayerIndex(pad),
            VendorId = SDL_GetGamepadVendor(pad),
            ProductId = SDL_GetGamepadProduct(pad),
            Serial = Text(SDL_GetGamepadSerial(pad)),
            Buttons = ControllerNames.GamepadButtons.Length,
            Axes = ControllerNames.GamepadAxes.Length,
            Hats = 0,
            CanRumble = props != 0 && SDL_GetBooleanProperty(props, PropRumble, false),
            HasRgbLed = props != 0 && SDL_GetBooleanProperty(props, PropRgbLed, false),
            Power = Power(power),
            BatteryPercent = percent,
        };
    }

    private static ControllerDeviceInfo JoystickInfo(uint id, IntPtr joy)
    {
        var power = SDL_GetJoystickPowerInfo(joy, out var percent);
        var props = SDL_GetJoystickProperties(joy);
        return new ControllerDeviceInfo
        {
            Id = id,
            Name = Fallback(Text(SDL_GetJoystickName(joy)), "Joystick"),
            Guid = GuidText(SDL_GetJoystickGUID(joy)),
            IsGamepad = false,
            Type = ControllerNames.JoystickType,
            PlayerIndex = SDL_GetJoystickPlayerIndex(joy),
            VendorId = SDL_GetJoystickVendor(joy),
            ProductId = SDL_GetJoystickProduct(joy),
            Buttons = Math.Max(0, SDL_GetNumJoystickButtons(joy)),
            Axes = Math.Max(0, SDL_GetNumJoystickAxes(joy)),
            Hats = Math.Max(0, SDL_GetNumJoystickHats(joy)),
            CanRumble = props != 0 && SDL_GetBooleanProperty(props, PropRumble, false),
            HasRgbLed = props != 0 && SDL_GetBooleanProperty(props, PropRgbLed, false),
            Power = Power(power),
            BatteryPercent = percent,
        };
    }

    private static string Fallback(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;

    /// <summary>SDL_PowerState (-1 error, 0 unknown, 1 on battery, 2 no battery, 3 charging, 4 charged).</summary>
    internal static ControllerPower Power(int state) => state switch
    {
        1 => ControllerPower.OnBattery,
        2 => ControllerPower.NoBattery,
        3 => ControllerPower.Charging,
        4 => ControllerPower.Charged,
        -1 => ControllerPower.Error,
        _ => ControllerPower.Unknown,
    };

    private void Raise(ControllerRawEvent e)
    {
        try { EventReceived?.Invoke(e); }
        catch (Exception ex) { _log.Error("Controllers", "Controller event handler failed", ex); }
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
