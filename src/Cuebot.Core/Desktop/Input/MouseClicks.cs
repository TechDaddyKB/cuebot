using Cuebot.Core.Actions;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

public enum MouseButton { Left, Right, Middle, X1, X2 }

/// <summary>One mouse button press anywhere on the desktop: screen position and the modifier keys held.</summary>
public sealed record MouseClick(MouseButton Button, int X, int Y, HotkeyModifiers Modifiers);

/// <summary>
/// Global mouse button presses (seen while another application has focus, without taking the click from it).
/// Implementations: a Windows low-level mouse hook, X11 XInput2 raw button events, and a no-op. Wayland has no
/// equivalent: a Wayland client only sees the pointer over its own windows, and no portal reports clicks elsewhere.
/// </summary>
public interface IMouseClickSource : IDisposable
{
    /// <summary>"Windows", "X11" or "Unavailable".</summary>
    string BackendName { get; }

    string Status { get; }

    /// <summary>Raised on a background thread.</summary>
    event Action<MouseClick>? Clicked;

    /// <summary>Starts listening; false (see <see cref="Status"/>) when it cannot.</summary>
    bool Start();

    void Stop();
}

public sealed class UnavailableMouseClickSource(string reason) : IMouseClickSource
{
    public string BackendName => "Unavailable";
    public string Status => "No mouse click triggers: " + reason;
    public event Action<MouseClick>? Clicked { add { } remove { } }
    public bool Start() => false;
    public void Stop() { }
    public void Dispose() { }
}

public static class MouseClickSources
{
    /// <summary>
    /// Wayland note: under a Wayland session X11 raw events only arrive while an X11 (XWayland) window has focus,
    /// and without DISPLAY there is nothing to listen to; see <see cref="IMouseClickSource"/>.
    /// </summary>
    public const string WaylandReason =
        "Wayland does not let applications see clicks made in other windows, and no desktop portal offers it";

    public static IMouseClickSource CreateDefault(AppLog log)
    {
        if (OperatingSystem.IsWindows()) return new WindowsMouseClickSource(log);
        if (OperatingSystem.IsLinux())
        {
            var (x11, reason) = X11MouseClickSource.TryCreate(log);
            if (x11 is not null) return x11;
            return new UnavailableMouseClickSource(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
                ? WaylandReason + " (X11: " + reason + ")"
                : reason);
        }
        return new UnavailableMouseClickSource("this operating system is not supported");
    }
}

public static class InputEvents
{
    public const string MouseClick = "input.mouseClick";
}

/// <summary>
/// Publishes "input.mouseClick" (mouseButton, mouseX, mouseY, modifiers) for each click. The OS hook is only
/// installed while an enabled action has an enabled Mouse Click trigger (checked every timer tick), so Cuebot does
/// not watch the mouse when nothing uses it.
/// </summary>
public sealed class MouseClickService : IPeriodicService, IDisposable
{
    private readonly IMouseClickSource _source;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<bool> _wanted;
    private readonly object _gate = new();
    private bool _running;
    private bool _warned;

    public MouseClickService(IMouseClickSource source, EventBus bus, AppLog log, Func<bool> wanted)
    {
        _source = source;
        _bus = bus;
        _log = log;
        _wanted = wanted;
        _source.Clicked += OnClicked;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public string BackendName => _source.BackendName;
    public string Status => _source.Status;

    void IPeriodicService.Tick(DateTimeOffset now) => Sync();

    /// <summary>Starts or stops the OS hook to match whether any trigger needs it.</summary>
    public void Sync()
    {
        bool wanted;
        try { wanted = _wanted(); }
        catch (ObjectDisposedException) { return; }
        lock (_gate)
        {
            if (wanted == _running) return;
            if (wanted)
            {
                _running = _source.Start();
                if (_running) _log.Info("Input", $"Watching mouse clicks ({_source.BackendName})");
                else if (!_warned)
                {
                    _warned = true;
                    _log.Warn("Input", _source.Status);
                }
            }
            else
            {
                _source.Stop();
                _running = false;
                _warned = false;
                _log.Info("Input", "Stopped watching mouse clicks");
            }
        }
    }

    /// <summary>Publishes a click (the source's handler; public for tests).</summary>
    public void Publish(MouseClick click)
    {
        _bus.Publish(new BotEvent(InputEvents.MouseClick, new Dictionary<string, object?>
        {
            ["mouseButton"] = click.Button.ToString(),
            ["mouseX"] = click.X,
            ["mouseY"] = click.Y,
            ["modifiers"] = ModifierText(click.Modifiers),
            // Streamer.bot Mouse Click names (one event per click).
            ["mouse.button"] = click.Button.ToString(),
            ["mouse.clicks"] = 1,
            ["mouse.x"] = click.X,
            ["mouse.y"] = click.Y,
        }));
    }

    private void OnClicked(MouseClick click)
    {
        try { Publish(click); }
        catch (Exception ex) { _log.Error("Input", "Mouse click handling failed", ex); }
    }

    /// <summary>"Ctrl+Shift", or "" for none.</summary>
    public static string ModifierText(HotkeyModifiers m) =>
        string.Join('+', KeyEvent.Modifiers.Where(x => m.HasFlag(x.Flag)).Select(x => x.Name));

    /// <summary>
    /// Trigger filter: <paramref name="button"/> "Any" or a button name; <paramref name="modifiers"/> empty = any,
    /// "None" = no modifier held, else exactly those ("Ctrl+Shift").
    /// </summary>
    public static bool Matches(string button, string modifiers, IReadOnlyDictionary<string, object?> args)
    {
        string Arg(string key) => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        button = button.Trim();
        if (button.Length > 0 && !string.Equals(button, "Any", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(button, Arg("mouseButton"), StringComparison.OrdinalIgnoreCase)) return false;
        modifiers = modifiers.Trim();
        if (modifiers.Length == 0) return true;
        if (!TryParseModifiers(modifiers, out var wanted)) return false;
        return TryParseModifiers(Arg("modifiers"), out var held) && held == wanted;
    }

    /// <summary>"Ctrl+Shift", "None" or "" → flags.</summary>
    public static bool TryParseModifiers(string text, out HotkeyModifiers modifiers)
    {
        modifiers = HotkeyModifiers.None;
        text = text.Trim();
        if (text.Length == 0 || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase)) return true;
        // Reuse the hotkey parser: "Ctrl+Shift" + a dummy key.
        if (!HotkeyGesture.TryParse(text + "+A", out var g, out _)) return false;
        modifiers = g.Modifiers;
        return true;
    }

    public void Dispose()
    {
        _source.Clicked -= OnClicked;
        _source.Dispose();
    }
}
