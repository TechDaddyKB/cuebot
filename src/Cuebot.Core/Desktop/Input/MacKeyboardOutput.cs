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
        var source = MacQuartz.CGEventSourceCreate(MacQuartz.HidSystemState);
        try
        {
            ulong held = 0;
            foreach (var e in events)
            {
                var (code, flag) = KeyCodes.Mac(e.Key) ?? throw new InvalidOperationException($"Key '{e.Key}' cannot be sent on a Mac");
                held = e.Down ? held | flag : held & ~flag;
                var cgEvent = MacQuartz.CGEventCreateKeyboardEvent(source, code, e.Down);
                if (cgEvent == IntPtr.Zero) throw new InvalidOperationException("macOS could not create the key press");
                try
                {
                    MacQuartz.CGEventSetFlags(cgEvent, held);
                    MacQuartz.CGEventPost(HidEventTap, cgEvent);
                }
                finally
                {
                    MacQuartz.CFRelease(cgEvent);
                }
                await Task.Delay(2, ct);   // some apps drop events that arrive in the same instant
            }
        }
        finally
        {
            if (source != IntPtr.Zero) MacQuartz.CFRelease(source);
        }
        _status = "Ready (Quartz events)";
    }
}
