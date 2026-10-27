using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>One hotkey to register: <paramref name="Id"/> comes back in <see cref="IGlobalHotkeys.Pressed"/>.</summary>
public sealed record HotkeyRegistration(string Id, string Description, HotkeyGesture Gesture);

/// <summary>Outcome of registering one hotkey. <paramref name="ActualTrigger"/> is set when the desktop chose a different key.</summary>
public sealed record HotkeyBindingResult(string Id, bool Ok, string? Error = null, string? ActualTrigger = null);

/// <summary>
/// System-wide hotkeys (they work while another application has focus). Implementations: Windows RegisterHotKey,
/// the XDG Desktop Portal GlobalShortcuts interface (Wayland and modern X11 desktops), X11 XGrabKey, and a no-op.
/// </summary>
public interface IGlobalHotkeys : IDisposable
{
    /// <summary>"Windows", "XDG portal", "X11" or "Unavailable".</summary>
    string BackendName { get; }

    /// <summary>Human-readable state for the Hotkeys page ("3 hotkeys registered", "No global hotkey support: ...").</summary>
    string Status { get; }

    /// <summary>Raised on a background thread with the registration id.</summary>
    event Action<string>? Pressed;

    /// <summary>Replaces every registered hotkey with <paramref name="hotkeys"/>.</summary>
    Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default);
}

/// <summary>Used where no backend works; every registration fails with the reason.</summary>
public sealed class UnavailableGlobalHotkeys(string reason) : IGlobalHotkeys
{
    public string BackendName => "Unavailable";
    public string Status => "No global hotkey support: " + reason;

    public event Action<string>? Pressed { add { } remove { } }

    public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HotkeyBindingResult>>(hotkeys.Select(h => new HotkeyBindingResult(h.Id, false, reason)).ToArray());

    public void Dispose() { }
}

public static class GlobalHotkeys
{
    /// <summary>The best backend for this OS. Nothing is registered (and no connection made) until the first apply.</summary>
    public static IGlobalHotkeys CreateDefault(AppLog log)
    {
        if (OperatingSystem.IsWindows()) return new WindowsGlobalHotkeys(log);
        if (OperatingSystem.IsLinux()) return new LinuxGlobalHotkeys(log);
        return new UnavailableGlobalHotkeys("this operating system is not supported");
    }
}
