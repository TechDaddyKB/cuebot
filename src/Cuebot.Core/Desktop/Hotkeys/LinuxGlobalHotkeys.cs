using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>
/// Linux: picks a backend the first time hotkeys are applied — the XDG portal's GlobalShortcuts when the desktop
/// offers it (Wayland-friendly), else X11 key grabs when DISPLAY is set, else unavailable with the reason.
/// </summary>
internal sealed class LinuxGlobalHotkeys(AppLog log) : IGlobalHotkeys
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IGlobalHotkeys? _backend;
    private string _status = "Not started (a backend is chosen when the first hotkey is enabled)";

    public string BackendName => _backend?.BackendName ?? "Not chosen yet";
    public string Status => _backend?.Status ?? _status;
    public event Action<string>? Pressed;

    public async Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_backend is null)
            {
                if (hotkeys.Count == 0)
                {
                    _status = "No hotkeys defined";
                    return [];
                }
                _backend = await ChooseAsync(ct);
                _backend.Pressed += id => Pressed?.Invoke(id);
            }
            return await _backend.ApplyAsync(hotkeys, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IGlobalHotkeys> ChooseAsync(CancellationToken ct)
    {
        var reasons = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            if (Desktop.DBus.DBusConnection.SessionAddress() is not null)
            {
                var (portal, reason) = await PortalGlobalHotkeys.TryCreateAsync(log, ct);
                if (portal is not null) return portal;
                reasons.Add("portal: " + reason);
            }
            else reasons.Add("portal: no D-Bus session bus");

            var (x11, why) = X11GlobalHotkeys.TryCreate(log);
            if (x11 is not null)
            {
                log.Info("Hotkeys", "Using X11 key grabs (" + string.Join("; ", reasons) + ")");
                return x11;
            }
            reasons.Add("X11: " + why);
        }
        var message = string.Join("; ", reasons);
        log.Warn("Hotkeys", "No global hotkey backend: " + message);
        return new UnavailableGlobalHotkeys(message);
    }

    public void Dispose()
    {
        _backend?.Dispose();
        _gate.Dispose();
    }
}
