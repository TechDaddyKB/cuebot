using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuebot.Core.Desktop.DBus;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Linux: picks a backend at the first press — the XDG Desktop Portal's RemoteDesktop interface when the desktop
/// offers it (Wayland-first; the desktop asks the user once and the permission is remembered with a restore token),
/// else X11 XTest when DISPLAY is set, else unavailable with the reasons. A portal that fails at start-up (refused,
/// no keyboard device) falls back to X11 for that press and is retried on the next one.
/// </summary>
internal sealed class LinuxKeyboardOutput(AppLog log, Func<string?>? loadToken, Action<string>? saveToken) : KeyboardOutputBase
{
    private readonly SemaphoreSlim _choose = new(1, 1);
    private KeyboardOutputBase? _backend;
    private string _status = "Not started (a backend is chosen at the first key press)";

    public override string BackendName => _backend?.BackendName ?? "Not chosen yet";
    public override string Status => _backend?.Status ?? _status;

    protected override async Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
    {
        var backend = await ChooseAsync(ct);
        if (backend is PortalKeyboardOutput portal)
        {
            try
            {
                await portal.SendEventsAsync(events, ct);
                return;
            }
            catch (InvalidOperationException ex) when (portal.CanFallBack && OperatingSystem.IsLinux())
            {
                // The user refused or the desktop has no keyboard device: try X11 once, keep the portal for later presses.
                var (x11, _) = X11KeyboardOutput.TryCreate();
                if (x11 is null) throw;
                log.Warn("Keyboard", "Desktop portal keyboard failed (" + ex.Message + "); using X11 XTest for this press");
                using (x11) await x11.SendEventsAsync(events, ct);
                return;
            }
        }
        if (OperatingSystem.IsLinux() && backend is X11KeyboardOutput x) await x.SendEventsAsync(events, ct);
        else throw new InvalidOperationException(backend.Status);
    }

    private async Task<KeyboardOutputBase> ChooseAsync(CancellationToken ct)
    {
        if (_backend is { } chosen) return chosen;
        await _choose.WaitAsync(ct);
        try
        {
            if (_backend is { } again) return again;
            var reasons = new List<string>();
            if (OperatingSystem.IsLinux())
            {
                if (DBusConnection.SessionAddress() is not null)
                {
                    var (portal, reason) = await PortalKeyboardOutput.TryCreateAsync(log, loadToken, saveToken, ct);
                    if (portal is not null) return _backend = portal;
                    reasons.Add("portal: " + reason);
                }
                else reasons.Add("portal: no D-Bus session bus");

                var (x11, why) = X11KeyboardOutput.TryCreate();
                if (x11 is not null)
                {
                    log.Info("Keyboard", "Using X11 XTest for key presses (" + string.Join("; ", reasons) + ")");
                    return _backend = x11;
                }
                reasons.Add("X11: " + why);
            }
            _status = string.Join("; ", reasons);
            log.Warn("Keyboard", "No keyboard output backend: " + _status);
            return _backend = new UnavailableKeyboardOutput(_status);
        }
        finally
        {
            _choose.Release();
        }
    }

    public override void Dispose()
    {
        _backend?.Dispose();
        _choose.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Key presses through org.freedesktop.portal.RemoteDesktop: CreateSession, SelectDevices (keyboard), Start (the
/// desktop shows its permission dialog the first time), then NotifyKeyboardKeysym per key. With portal version 2+
/// the permission persists (persist_mode 2) and the restore token is saved so later sessions start without asking.
/// The session stays open for the app's lifetime and is recreated if the desktop closes it.
/// </summary>
internal sealed class PortalKeyboardOutput : KeyboardOutputBase
{
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    private const uint DeviceKeyboard = 1;

    private readonly DBusConnection _bus;
    private readonly AppLog _log;
    private readonly uint _version;
    private readonly Func<string?>? _loadToken;
    private readonly Action<string>? _saveToken;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(uint Code, Dictionary<object, object?> Results)>> _requests = new();
    private volatile string? _session;
    private volatile string _status = "Connected to the desktop portal (permission is asked at the first key press)";
    private int _token;

    private PortalKeyboardOutput(DBusConnection bus, AppLog log, uint version, Func<string?>? loadToken, Action<string>? saveToken)
    {
        _bus = bus;
        _log = log;
        _version = version;
        _loadToken = loadToken;
        _saveToken = saveToken;
        _bus.Signal += OnSignal;
        _bus.Closed += ex =>
        {
            _session = null;
            _status = "Desktop portal connection lost" + (ex is null ? "" : ": " + ex.Message);
        };
    }

    public override string BackendName => "XDG portal";
    public override string Status => _status;

    /// <summary>True when the last failure was the session not starting (refused, no keyboard), not a broken connection.</summary>
    public bool CanFallBack { get; private set; }

    public static async Task<(PortalKeyboardOutput? Backend, string Reason)> TryCreateAsync(AppLog log, Func<string?>? loadToken,
        Action<string>? saveToken, CancellationToken ct)
    {
        DBusConnection? bus = null;
        try
        {
            bus = await DBusConnection.ConnectSessionAsync(ct);
            var version = await bus.CallAsync(DBusMessage.Call(Portal, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss",
                RemoteDesktop, "version"), ct);
            var v = Convert.ToUInt32((version.Body.FirstOrDefault() as DBusVariant)?.Value ?? 1u, System.Globalization.CultureInfo.InvariantCulture);
            var devices = await bus.CallAsync(DBusMessage.Call(Portal, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss",
                RemoteDesktop, "AvailableDeviceTypes"), ct);
            var types = Convert.ToUInt32((devices.Body.FirstOrDefault() as DBusVariant)?.Value ?? 0u, System.Globalization.CultureInfo.InvariantCulture);
            if ((types & DeviceKeyboard) == 0)
            {
                bus.Dispose();
                return (null, "the desktop portal offers no remote keyboard");
            }
            try
            {
                await bus.CallAsync(DBusMessage.Call(Portal, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}",
                    PortalGlobalHotkeys.AppId, new Dictionary<string, DBusVariant>()), ct);
            }
            catch (DBusException) { }
            await bus.AddMatchAsync($"type='signal',sender='{Portal}',interface='org.freedesktop.portal.Request',member='Response'", ct);
            await bus.AddMatchAsync($"type='signal',sender='{Portal}',interface='org.freedesktop.portal.Session',member='Closed'", ct);
            log.Info("Keyboard", $"Using the desktop portal RemoteDesktop interface for key presses (version {v})");
            return (new PortalKeyboardOutput(bus, log, v, loadToken, saveToken), "");
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException or IOException or System.Net.Sockets.SocketException
                                       or TimeoutException or FormatException or InvalidCastException)
        {
            bus?.Dispose();
            var reason = ex is DBusException { Name: "org.freedesktop.DBus.Error.InvalidArgs" or "org.freedesktop.DBus.Error.UnknownProperty"
                or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown" }
                ? "the desktop portal has no RemoteDesktop support"
                : ex.Message;
            return (null, reason);
        }
    }

    protected override Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct) => SendEventsAsync(events, ct);

    public async Task SendEventsAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct);
        try
        {
            foreach (var e in events)
            {
                var keysym = KeyCodes.Keysym(e.Key) ?? throw new InvalidOperationException($"Key '{e.Key}' cannot be sent");
                await _bus.CallAsync(DBusMessage.Call(Portal, PortalPath, RemoteDesktop, "NotifyKeyboardKeysym", "oa{sv}iu",
                    session, new Dictionary<string, DBusVariant>(), keysym, e.Down ? 1u : 0u), ct);
            }
        }
        catch (DBusException ex)
        {
            _session = null;   // a closed or broken session is recreated at the next press
            CanFallBack = false;
            throw new InvalidOperationException("The desktop portal rejected the key press: " + ex.Message, ex);
        }
    }

    private async Task<string> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { } open) return open;
        CanFallBack = true;
        try
        {
            var created = await RequestAsync(token => DBusMessage.Call(Portal, PortalPath, RemoteDesktop, "CreateSession", "a{sv}",
                new Dictionary<string, DBusVariant>
                {
                    ["handle_token"] = new("s", token),
                    ["session_handle_token"] = new("s", "cuebotkb" + Interlocked.Increment(ref _token)),
                }), TimeSpan.FromSeconds(30), ct);
            if (created.Code != 0) throw Fail("The desktop refused a remote keyboard session");
            var session = (created.Results.GetValueOrDefault("session_handle") as DBusVariant)?.Value as string;
            if (string.IsNullOrEmpty(session)) throw Fail("The desktop portal returned no session");

            var select = new Dictionary<string, DBusVariant> { ["types"] = new("u", DeviceKeyboard) };
            if (_version >= 2)
            {
                select["persist_mode"] = new("u", 2u);   // remember the permission until the user revokes it
                if (_loadToken?.Invoke() is { Length: > 0 } restore) select["restore_token"] = new("s", restore);
            }
            var selected = await RequestAsync(token =>
            {
                select["handle_token"] = new("s", token);
                return DBusMessage.Call(Portal, PortalPath, RemoteDesktop, "SelectDevices", "oa{sv}", session, select);
            }, TimeSpan.FromSeconds(30), ct);
            if (selected.Code != 0) throw Fail("The desktop refused the remote keyboard");

            // Start shows the desktop's permission dialog (unless a restore token was accepted): give the user time.
            var started = await RequestAsync(token => DBusMessage.Call(Portal, PortalPath, RemoteDesktop, "Start", "osa{sv}", session, "",
                new Dictionary<string, DBusVariant> { ["handle_token"] = new("s", token) }), TimeSpan.FromMinutes(3), ct);
            if (started.Code != 0) throw Fail(started.Code == 1 ? "Key presses were not allowed in the desktop's dialog" : "The desktop refused the remote keyboard");
            var devices = Convert.ToUInt32((started.Results.GetValueOrDefault("devices") as DBusVariant)?.Value ?? DeviceKeyboard,
                System.Globalization.CultureInfo.InvariantCulture);
            if ((devices & DeviceKeyboard) == 0) throw Fail("The desktop did not grant a keyboard");
            if ((started.Results.GetValueOrDefault("restore_token") as DBusVariant)?.Value is string newToken && newToken.Length > 0)
            {
                try { _saveToken?.Invoke(newToken); }
                catch (Exception ex) { _log.Warn("Keyboard", "Could not save the portal permission token: " + ex.Message); }
            }
            _session = session;
            CanFallBack = false;
            _status = "Ready (desktop portal remote keyboard)";
            return session;
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException or IOException or ObjectDisposedException)
        {
            throw Fail("Desktop portal error: " + ex.Message);
        }
    }

    private InvalidOperationException Fail(string message)
    {
        _status = message;
        return new InvalidOperationException(message);
    }

    /// <summary>Portal request pattern: the call returns a Request object whose Response signal carries the result.</summary>
    private async Task<(uint Code, Dictionary<object, object?> Results)> RequestAsync(Func<string, DBusMessage> call, TimeSpan timeout,
        CancellationToken ct)
    {
        var token = "cuebotkb" + Interlocked.Increment(ref _token);
        var sender = _bus.UniqueName.TrimStart(':').Replace('.', '_');
        var expected = $"{PortalPath}/request/{sender}/{token}";
        var response = new TaskCompletionSource<(uint, Dictionary<object, object?>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[expected] = response;
        try
        {
            var reply = await _bus.CallAsync(call(token), ct);
            if (reply.Body.FirstOrDefault() is string actual && actual != expected)
            {
                _requests.TryRemove(expected, out _);
                _requests[actual] = response;
                expected = actual;
            }
            return await response.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _requests.TryRemove(expected, out _);
        }
    }

    private void OnSignal(DBusMessage signal)
    {
        try
        {
            if (signal.Interface == "org.freedesktop.portal.Request" && signal.Member == "Response" && signal.Path is { } path
                && _requests.TryGetValue(path, out var tcs) && signal.Body is [uint code, Dictionary<object, object?> results])
            {
                tcs.TrySetResult((code, results));
            }
            else if (signal.Interface == "org.freedesktop.portal.Session" && signal.Member == "Closed" && signal.Path == _session)
            {
                _session = null;
                _status = "The desktop closed the remote keyboard session (it is reopened at the next key press)";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Keyboard", "Portal signal handling failed", ex);
        }
    }

    public override void Dispose()
    {
        _bus.Signal -= OnSignal;
        _bus.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// X11 fallback: XTestFakeKeyEvent on a private display connection. Under Wayland (XWayland) the presses only reach
/// X11 applications, so the portal is preferred.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11KeyboardOutput : KeyboardOutputBase
{
    private readonly IntPtr _display;
    private readonly object _gate = new();
    private readonly string _status;
    private bool _disposed;

    private X11KeyboardOutput(IntPtr display)
    {
        _display = display;
        _status = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Ready (X11 XTest through XWayland: only X11 applications receive the keys)"
            : "Ready (X11 XTest)";
    }

    public override string BackendName => "X11";
    public override string Status => _status;

    public static (X11KeyboardOutput? Backend, string Reason) TryCreate()
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return (null, "DISPLAY is not set");
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return (null, "Cannot open the X display");
            if (!XTestQueryExtension(display, out _, out _, out _, out _))
            {
                XCloseDisplay(display);
                return (null, "the X server has no XTest extension");
            }
            return (new X11KeyboardOutput(display), "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, "libX11 or libXtst is not installed");
        }
    }

    protected override Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct) => SendEventsAsync(events, ct);

    public Task SendEventsAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var codes = new uint[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                var sym = KeyCodes.Keysym(events[i].Key) ?? throw new InvalidOperationException($"Key '{events[i].Key}' cannot be sent");
                codes[i] = XKeysymToKeycode(_display, (IntPtr)sym);
                if (codes[i] == 0) throw new InvalidOperationException($"Key '{events[i].Key}' is not on this keyboard layout");
            }
            for (var i = 0; i < events.Count; i++) XTestFakeKeyEvent(_display, codes[i], events[i].Down, IntPtr.Zero);
            XFlush(_display);
        }
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            XCloseDisplay(_display);
        }
        base.Dispose();
    }

    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibXtst)] private static extern bool XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, IntPtr delay);
}
