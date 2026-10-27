using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Sends key presses to whichever application has focus (the "Keyboard Press" sub-action, CPH KeyboardPress).
/// Implementations: Windows SendInput; on Linux the XDG Desktop Portal RemoteDesktop interface (Wayland and modern X11
/// desktops; the desktop asks the user once) with X11 XTest as the fallback; and a no-op that reports why.
/// Registered in DI as a singleton (<see cref="InputFeatures.AddInputFeatures"/>).
/// </summary>
public interface IKeyboardOutput
{
    /// <summary>"Windows", "XDG portal", "X11", "Unavailable", or "Not chosen yet" before the first press on Linux.</summary>
    string BackendName { get; }

    /// <summary>Human-readable state ("Ready", "No keyboard output: ...").</summary>
    string Status { get; }

    /// <summary>
    /// Presses each combination in turn (modifiers down, key down and up, modifiers up). <paramref name="keys"/> is
    /// "Ctrl+Shift+F1", several combos "Ctrl+C, Ctrl+V", or SendKeys syntax such as "^c" or "{ENTER}"; see <see cref="KeyCombos"/>.
    /// </summary>
    /// <exception cref="FormatException">The keys could not be parsed.</exception>
    /// <exception cref="InvalidOperationException">No backend can send keys (the message says why), or the desktop refused.</exception>
    Task PressAsync(string keys, CancellationToken ct = default);

    /// <summary>Presses already-parsed combinations in turn.</summary>
    /// <exception cref="InvalidOperationException">No backend can send keys, or the desktop refused.</exception>
    Task PressAsync(IReadOnlyList<HotkeyGesture> combos, CancellationToken ct = default);
}

/// <summary>Parsing, pacing between combos and serialization shared by the backends; they only send one combo's key events.</summary>
public abstract class KeyboardOutputBase : IKeyboardOutput, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Pause between combos so the target application sees separate presses.</summary>
    public TimeSpan ComboDelay { get; init; } = TimeSpan.FromMilliseconds(30);

    public abstract string BackendName { get; }
    public abstract string Status { get; }

    public Task PressAsync(string keys, CancellationToken ct = default) => PressAsync(KeyCombos.Parse(keys), ct);

    public async Task PressAsync(IReadOnlyList<HotkeyGesture> combos, CancellationToken ct = default)
    {
        if (combos.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            for (var i = 0; i < combos.Count; i++)
            {
                if (i > 0 && ComboDelay > TimeSpan.Zero) await Task.Delay(ComboDelay, ct);
                await SendAsync(KeyEvent.For(combos[i]), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends one combo's events (already in press order).</summary>
    protected abstract Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct);

    public virtual void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Used where no backend works; every press fails with the reason.</summary>
public sealed class UnavailableKeyboardOutput(string reason) : KeyboardOutputBase
{
    public override string BackendName => "Unavailable";
    public override string Status => "No keyboard output: " + reason;

    protected override Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct) =>
        throw new InvalidOperationException(Status);
}

public static class KeyboardOutputs
{
    /// <summary>The best backend for this OS. On Linux nothing connects (and no permission is asked) until the first press.</summary>
    public static IKeyboardOutput CreateDefault(AppLog log, Func<string?>? loadPortalToken = null, Action<string>? savePortalToken = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsKeyboardOutput();
        if (OperatingSystem.IsLinux()) return new LinuxKeyboardOutput(log, loadPortalToken, savePortalToken);
        return new UnavailableKeyboardOutput("this operating system is not supported");
    }
}
