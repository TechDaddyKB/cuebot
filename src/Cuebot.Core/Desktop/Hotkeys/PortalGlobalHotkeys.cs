using System.Collections.Concurrent;
using Cuebot.Core.Desktop.DBus;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>
/// Global hotkeys through the XDG Desktop Portal (org.freedesktop.portal.GlobalShortcuts), which works on Wayland
/// (KDE Plasma, GNOME 48+, Hyprland, ...). Each apply opens a new portal session and binds the shortcuts with their
/// preferred triggers; the desktop may ask the user to confirm or pick other keys (reported as ActualTrigger).
/// "Activated" signals for the current session raise <see cref="Pressed"/>.
/// </summary>
internal sealed class PortalGlobalHotkeys : IGlobalHotkeys
{
    public const string AppId = "io.github.cuebot.Cuebot";
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string Shortcuts = "org.freedesktop.portal.GlobalShortcuts";

    private readonly DBusConnection _bus;
    private readonly AppLog _log;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(uint Code, Dictionary<object, object?> Results)>> _requests = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private volatile string? _session;
    private volatile string _status = "Connected to the desktop portal";
    private int _token;

    private PortalGlobalHotkeys(DBusConnection bus, AppLog log)
    {
        _bus = bus;
        _log = log;
        _bus.Signal += OnSignal;
        _bus.Closed += ex => _status = "Desktop portal connection lost" + (ex is null ? "" : ": " + ex.Message);
    }

    public string BackendName => "XDG portal";
    public string Status => _status;
    public event Action<string>? Pressed;

    /// <summary>Connects and checks that the portal offers GlobalShortcuts. Returns null (with the reason) otherwise.</summary>
    public static async Task<(PortalGlobalHotkeys? Backend, string Reason)> TryCreateAsync(AppLog log, CancellationToken ct)
    {
        DBusConnection? bus = null;
        try
        {
            bus = await DBusConnection.ConnectSessionAsync(ct);
            var version = await bus.CallAsync(DBusMessage.Call(Portal, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss",
                Shortcuts, "version"), ct);
            var v = (version.Body.FirstOrDefault() as DBusVariant)?.Value;
            // Non-sandboxed apps tell the portal who they are (xdg-desktop-portal 1.19+); older portals lack this.
            try
            {
                await bus.CallAsync(DBusMessage.Call(Portal, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}",
                    AppId, new Dictionary<string, DBusVariant>()), ct);
            }
            catch (DBusException) { }
            await bus.AddMatchAsync($"type='signal',sender='{Portal}',interface='org.freedesktop.portal.Request',member='Response'", ct);
            await bus.AddMatchAsync($"type='signal',sender='{Portal}',interface='{Shortcuts}'", ct);
            log.Info("Hotkeys", $"Using the desktop portal GlobalShortcuts interface (version {v})");
            return (new PortalGlobalHotkeys(bus, log), "");
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException or IOException or System.Net.Sockets.SocketException
                                       or TimeoutException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            bus?.Dispose();
            var reason = ex is DBusException { Name: "org.freedesktop.DBus.Error.InvalidArgs" or "org.freedesktop.DBus.Error.UnknownProperty"
                or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown" }
                ? "the desktop portal has no GlobalShortcuts support"
                : ex.Message;
            return (null, reason);
        }
    }

    public async Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
    {
        await _applyLock.WaitAsync(ct);
        try
        {
            if (_session is { } old)
            {
                _session = null;
                try { await _bus.CallAsync(DBusMessage.Call(Portal, old, "org.freedesktop.portal.Session", "Close"), ct); }
                catch (DBusException ex) { _log.Debug("Hotkeys", "Closing the portal session failed: " + ex.Message); }
            }
            if (hotkeys.Count == 0)
            {
                _status = "No hotkeys defined";
                return [];
            }

            var created = await RequestAsync(token => DBusMessage.Call(Portal, PortalPath, Shortcuts, "CreateSession", "a{sv}",
                new Dictionary<string, DBusVariant>
                {
                    ["handle_token"] = new("s", token),
                    ["session_handle_token"] = new("s", "cuebot" + Interlocked.Increment(ref _token)),
                }), TimeSpan.FromSeconds(30), ct);
            if (created.Code != 0) return Fail(hotkeys, "The desktop refused a shortcuts session");
            var session = (created.Results.GetValueOrDefault("session_handle") as DBusVariant)?.Value as string;
            if (string.IsNullOrEmpty(session)) return Fail(hotkeys, "The desktop portal returned no session");

            var list = hotkeys.Select(h => (object?)new object?[]
            {
                h.Id,
                new Dictionary<string, DBusVariant>
                {
                    ["description"] = new("s", h.Description.Length > 0 ? h.Description : h.Gesture.ToString()),
                    ["preferred_trigger"] = new("s", h.Gesture.ToPortalTrigger()),
                },
            }).ToList();
            // The desktop may show a dialog here, so allow time for the user to answer.
            var bound = await RequestAsync(token => DBusMessage.Call(Portal, PortalPath, Shortcuts, "BindShortcuts", "oa(sa{sv})sa{sv}",
                session, list, "", new Dictionary<string, DBusVariant> { ["handle_token"] = new("s", token) }), TimeSpan.FromMinutes(3), ct);
            if (bound.Code != 0) return Fail(hotkeys, bound.Code == 1 ? "Cancelled in the desktop's shortcut dialog" : "The desktop refused the shortcuts");
            _session = session;

            var triggers = new Dictionary<string, string>();
            if ((bound.Results.GetValueOrDefault("shortcuts") as DBusVariant)?.Value is List<object?> shortcuts)
            {
                foreach (var item in shortcuts.OfType<object?[]>())
                {
                    if (item is [string id, Dictionary<object, object?> props])
                        triggers[id] = (props.GetValueOrDefault("trigger_description") as DBusVariant)?.Value as string ?? "";
                }
            }
            var results = hotkeys.Select(h => triggers.TryGetValue(h.Id, out var t)
                ? new HotkeyBindingResult(h.Id, true, null, t.Length > 0 ? t : null)
                : new HotkeyBindingResult(h.Id, triggers.Count == 0, triggers.Count == 0 ? null : "Not bound by the desktop")).ToArray();
            _status = $"{results.Count(r => r.Ok)} of {hotkeys.Count} hotkey(s) bound through the desktop portal (change keys in the desktop's settings)";
            return results;
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException or IOException or ObjectDisposedException)
        {
            _status = "Desktop portal error: " + ex.Message;
            return Fail(hotkeys, ex.Message);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private IReadOnlyList<HotkeyBindingResult> Fail(IReadOnlyList<HotkeyRegistration> hotkeys, string reason)
    {
        _status = reason;
        return hotkeys.Select(h => new HotkeyBindingResult(h.Id, false, reason)).ToArray();
    }

    /// <summary>Portal request pattern: the call returns a Request object whose Response signal carries the result.</summary>
    private async Task<(uint Code, Dictionary<object, object?> Results)> RequestAsync(Func<string, DBusMessage> call, TimeSpan timeout,
        CancellationToken ct)
    {
        var token = "cuebot" + Interlocked.Increment(ref _token);
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
            else if (signal.Interface == Shortcuts && signal.Member == "Activated" && signal.Body is [string session, string id, ..]
                     && session == _session)
            {
                Pressed?.Invoke(id);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Hotkeys", "Portal signal handling failed", ex);
        }
    }

    public void Dispose()
    {
        _bus.Signal -= OnSignal;
        _bus.Dispose();
        _applyLock.Dispose();
    }
}
