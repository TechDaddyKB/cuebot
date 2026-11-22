using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Sends key presses to whichever application has focus (the "Keyboard Press" sub-action, CPH KeyboardPress).
/// Implementations: Windows SendInput; on Linux the XDG Desktop Portal RemoteDesktop interface (Wayland and modern X11
/// desktops; the desktop asks the user once) with X11 XTest as the fallback; macOS Quartz events; and a no-op that
/// reports why.
/// Registered in DI as a singleton (<see cref="InputFeatures.AddInputFeatures"/>).
/// </summary>
public interface IKeyboardOutput
{
    /// <summary>"Windows", "XDG portal", "X11", "macOS", "Unavailable", or "Not chosen yet" before the first press on Linux.</summary>
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

    /// <summary>
    /// Presses each combination in turn, keeping it down for <paramref name="hold"/> before letting go. The keys are
    /// released even when <paramref name="ct"/> is cancelled. Zero is an ordinary press.
    /// </summary>
    /// <exception cref="InvalidOperationException">No backend can send keys, or the desktop refused.</exception>
    Task HoldAsync(IReadOnlyList<HotkeyGesture> combos, TimeSpan hold, CancellationToken ct = default);
}

/// <summary>Parsing, pacing between combos and serialization shared by the backends; they only send one combo's key events.</summary>
public abstract class KeyboardOutputBase : IKeyboardOutput, IDisposable
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Keys being held and by how many holds, so overlapping holds let go only with the last one. Guarded by the gate.</summary>
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);

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
                // A key that a hold keeps down stays down: a tap must not release it.
                var events = KeyEvent.For(combos[i]).Where(e => !_held.ContainsKey(e.Key)).ToArray();
                if (events.Length > 0) await SendAsync(events, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HoldAsync(IReadOnlyList<HotkeyGesture> combos, TimeSpan hold, CancellationToken ct = default)
    {
        if (hold <= TimeSpan.Zero)
        {
            await PressAsync(combos, ct);
            return;
        }
        for (var i = 0; i < combos.Count; i++)
        {
            if (i > 0 && ComboDelay > TimeSpan.Zero) await Task.Delay(ComboDelay, ct);
            await HoldOneAsync(combos[i], hold, ct);
        }
    }

    private async Task HoldOneAsync(HotkeyGesture combo, TimeSpan hold, CancellationToken ct)
    {
        var keys = KeyEvent.For(combo).Where(e => e.Down).Select(e => e.Key).ToArray();
        await _gate.WaitAsync(ct);
        var counted = false;
        try
        {
            try
            {
                var downs = keys.Where(k => !_held.ContainsKey(k)).Select(k => new KeyEvent(k, true)).ToArray();
                foreach (var key in keys) _held[key] = _held.GetValueOrDefault(key) + 1;
                counted = true;
                if (downs.Length > 0) await SendAsync(downs, ct);
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(hold, ct);
        }
        finally
        {
            if (counted) await ReleaseAsync(keys);
        }
    }

    /// <summary>Ends one hold of these keys, on any path: a cancelled run must never leave a key stuck.</summary>
    private async Task ReleaseAsync(IReadOnlyList<string> keys)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // shutting down: ReleaseHeldKeys already let go
        }
        try
        {
            var ups = new List<KeyEvent>();
            foreach (var key in keys.Reverse())
            {
                if (!_held.TryGetValue(key, out var count)) continue;
                if (count > 1)
                {
                    _held[key] = count - 1;
                    continue;
                }
                _held.Remove(key);
                ups.Add(new KeyEvent(key, false));
            }
            if (ups.Count > 0) await SendAsync(ups, timeout.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lets go of every held key. Backends call this first in Dispose, while they can still send.</summary>
    protected void ReleaseHeldKeys()
    {
        try
        {
            if (!_gate.Wait(ReleaseTimeout)) return;
        }
        catch (ObjectDisposedException)
        {
            return; // already disposed
        }
        try
        {
            if (_held.Count == 0) return;
            var ups = _held.Keys.OrderBy(k => new KeyEvent(k, false).IsModifier).Select(k => new KeyEvent(k, false)).ToArray();
            _held.Clear();
            SendAsync(ups, CancellationToken.None).Wait(ReleaseTimeout);
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or ObjectDisposedException)
        {
            // Closing anyway; nothing more can be done.
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
        ReleaseHeldKeys();
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
        if (OperatingSystem.IsMacOS()) return new MacKeyboardOutput();
        return new UnavailableKeyboardOutput("this operating system is not supported");
    }
}
