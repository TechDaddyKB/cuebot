using System.Runtime.Versioning;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// macOS: Quartz keyboard events posted to the HID event tap, so the focused app sees an ordinary key press. Each
/// event carries the modifiers held at that point as its flags (apps read those, not the modifier key events). Posting
/// needs the Accessibility permission: the first press asks for it, and presses fail with a message until it is on.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeyboardOutput : KeyboardOutputBase
{
    internal const string PermissionMessage =
        "Cuebot needs the Accessibility permission to press keys: open System Settings > Privacy & Security > "
        + "Accessibility, turn Cuebot on, then quit and reopen Cuebot";

    private const int HidEventTap = 0;
    private volatile string _status = "Ready (Quartz events)";
    private bool _asked;

    public override string BackendName => "macOS";
    public override string Status => _status;

    protected override async Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
    {
        if (!MacQuartz.CGPreflightPostEventAccess())
        {
            if (!_asked) MacQuartz.CGRequestPostEventAccess();   // the system prompt, shown once
            _asked = true;
            _status = "No keyboard output: " + PermissionMessage;
            throw new InvalidOperationException(PermissionMessage);
        }
        // Map every key first: a combo with a key a Mac cannot send must not press its modifiers at all.
        var codes = events.Select(e => (e.Down, Code: KeyCodes.Mac(e.Key) ?? throw new InvalidOperationException($"Key '{e.Key}' cannot be sent on a Mac")))
            .ToArray();
        var source = MacQuartz.CGEventSourceCreate(MacQuartz.HidSystemState);
        var down = new List<(ushort Code, ulong Flag)>();   // pressed and not yet released, so a failure can let go
        try
        {
            foreach (var (isDown, code) in codes)
            {
                if (isDown) down.Add(code);
                else down.Remove(code);
                Post(source, code.Code, isDown, down);
                await Task.Delay(2, ct);   // some apps drop events that arrive in the same instant
            }
        }
        finally
        {
            // Cancelled or failed half way: release whatever is still held, last pressed first.
            for (var i = down.Count - 1; i >= 0; i--)
            {
                var key = down[i];
                down.RemoveAt(i);
                try { Post(source, key.Code, false, down); }
                catch (InvalidOperationException) { }
            }
            if (source != IntPtr.Zero) MacQuartz.CFRelease(source);
        }
        _status = "Ready (Quartz events)";
    }

    /// <summary>Posts one key event whose flags are the modifiers held in <paramref name="held"/>.</summary>
    private static void Post(IntPtr source, ushort code, bool keyDown, List<(ushort Code, ulong Flag)> held)
    {
        var cgEvent = MacQuartz.CGEventCreateKeyboardEvent(source, code, keyDown);
        if (cgEvent == IntPtr.Zero) throw new InvalidOperationException("macOS could not create the key press");
        try
        {
            MacQuartz.CGEventSetFlags(cgEvent, held.Aggregate(0UL, (flags, k) => flags | k.Flag));
            MacQuartz.CGEventPost(HidEventTap, cgEvent);
        }
        finally
        {
            MacQuartz.CFRelease(cgEvent);
        }
    }
}
