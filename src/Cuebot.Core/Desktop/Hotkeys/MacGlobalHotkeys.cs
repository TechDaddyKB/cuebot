using System.Runtime.Versioning;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>
/// The pure part of the macOS backend: which registration a key press (key code and Quartz modifier flags) belongs to.
/// Only Shift, Control, Option and Command count; Caps Lock, Fn and the numeric-pad flag are ignored.
/// </summary>
internal sealed class MacHotkeyTable
{
    private readonly Dictionary<(int Code, HotkeyModifiers Modifiers), string> _byKey = [];

    /// <summary>Builds the table; registrations whose key has no Mac key code, or repeat an earlier combo, fail.</summary>
    public static (MacHotkeyTable Table, IReadOnlyList<HotkeyBindingResult> Results) Build(IReadOnlyList<HotkeyRegistration> hotkeys)
    {
        var table = new MacHotkeyTable();
        var results = new List<HotkeyBindingResult>();
        foreach (var hotkey in hotkeys)
        {
            if (HotkeyKeys.MacKeyCode(hotkey.Gesture.Key) is not { } code)
            {
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"Key '{hotkey.Gesture.Key}' is not on a Mac keyboard"));
                continue;
            }
            if (!table._byKey.TryAdd((code, hotkey.Gesture.Modifiers), hotkey.Id))
            {
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"{hotkey.Gesture} is already used by another hotkey"));
                continue;
            }
            results.Add(new HotkeyBindingResult(hotkey.Id, true));
        }
        return (table, results);
    }

    public int Count => _byKey.Count;

    public string? Match(int keyCode, ulong flags) => _byKey.GetValueOrDefault((keyCode, Modifiers(flags)));

    public static HotkeyModifiers Modifiers(ulong flags) =>
        ((flags & MacEventFlags.Control) != 0 ? HotkeyModifiers.Ctrl : 0)
        | ((flags & MacEventFlags.Alternate) != 0 ? HotkeyModifiers.Alt : 0)
        | ((flags & MacEventFlags.Shift) != 0 ? HotkeyModifiers.Shift : 0)
        | ((flags & MacEventFlags.Command) != 0 ? HotkeyModifiers.Meta : 0);
}

/// <summary>
/// macOS: a listen-only Quartz event tap on its own thread sees every key press and fires the matching hotkey. It
/// needs the Input Monitoring permission (asked for the first time a hotkey is enabled), works without a window
/// (headless too), and does not swallow the key: the app in front still receives it, so prefer combos it ignores.
/// Meta is the Command key.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacGlobalHotkeys(AppLog log) : IGlobalHotkeys
{
    internal const string PermissionMessage =
        "Cuebot needs the Input Monitoring permission: open System Settings > Privacy & Security > Input Monitoring, "
        + "turn Cuebot on, then quit and reopen Cuebot";

    private readonly object _gate = new();
    private volatile MacHotkeyTable _table = new();
    private MacQuartz.ListenTap? _tap;
    private volatile string _status = "Not started";
    private bool _disposed;

    public string BackendName => "macOS";
    public string Status => _status;
    public event Action<string>? Pressed;

    public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (table, results) = MacHotkeyTable.Build(hotkeys);
            if (table.Count > 0 && _tap is null && !TryStartTap(out var error))
            {
                _status = "No global hotkeys: " + error;
                return Task.FromResult<IReadOnlyList<HotkeyBindingResult>>(
                    results.Select(r => r.Ok ? r with { Ok = false, Error = error } : r).ToArray());
            }
            _table = table;
            var ok = results.Count(r => r.Ok);
            _status = hotkeys.Count == 0 ? "No hotkeys defined" : $"{ok} of {hotkeys.Count} hotkey(s) registered";
            return Task.FromResult<IReadOnlyList<HotkeyBindingResult>>(results);
        }
    }

    private bool TryStartTap(out string error)
    {
        if (!MacQuartz.CGPreflightListenEventAccess())
        {
            MacQuartz.CGRequestListenEventAccess();   // the system prompt, shown once
            error = PermissionMessage;
            log.Warn("Hotkeys", error);
            return false;
        }
        _tap = MacQuartz.ListenTap.TryStart(MacQuartz.MaskOf(MacQuartz.KeyDown), OnEvent, "Cuebot hotkeys");
        error = _tap is null ? "macOS refused to watch the keyboard (" + PermissionMessage + ")" : "";
        if (_tap is null) log.Warn("Hotkeys", error);
        return _tap is not null;
    }

    private void OnEvent(uint type, IntPtr cgEvent)
    {
        if (type != MacQuartz.KeyDown || MacQuartz.CGEventGetIntegerValueField(cgEvent, MacQuartz.KeyboardEventAutorepeat) != 0) return;
        var code = (int)MacQuartz.CGEventGetIntegerValueField(cgEvent, MacQuartz.KeyboardEventKeycode);
        if (_table.Match(code, MacQuartz.CGEventGetFlags(cgEvent)) is not { } id) return;
        // Off the tap thread: macOS disables a tap whose callback is slow.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Pressed?.Invoke(id); }
            catch (Exception ex) { log.Error("Hotkeys", "Hotkey handler failed", ex); }
        });
    }

    public void Dispose()
    {
        MacQuartz.ListenTap? tap;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tap = _tap;
            _tap = null;
        }
        tap?.Dispose();
    }
}
