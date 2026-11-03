using System.Runtime.Versioning;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// macOS: a listen-only Quartz event tap for mouse-button presses, on its own thread. It sees clicks in every app and
/// passes them on unchanged; the handler runs on the thread pool so the tap stays fast. Positions are in points from
/// the top left of the main display.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacMouseClickSource(AppLog log) : IMouseClickSource
{
    private readonly object _gate = new();
    private MacQuartz.ListenTap? _tap;
    private volatile string _status = "Not started";

    public string BackendName => "macOS";
    public string Status => _status;
    public event Action<MouseClick>? Clicked;

    public bool Start()
    {
        lock (_gate)
        {
            if (_tap is not null) return true;
            _tap = MacQuartz.ListenTap.TryStart(
                MacQuartz.MaskOf(MacQuartz.LeftMouseDown, MacQuartz.RightMouseDown, MacQuartz.OtherMouseDown), OnEvent, "Cuebot mouse tap");
            if (_tap is null)
            {
                MacQuartz.CGRequestListenEventAccess();
                _status = "macOS refused to watch the mouse: allow Cuebot under System Settings > Privacy & Security > "
                          + "Input Monitoring, then quit and reopen Cuebot";
                log.Warn("Input", _status);
                return false;
            }
            _status = "Watching mouse clicks";
            return true;
        }
    }

    public void Stop()
    {
        MacQuartz.ListenTap? tap;
        lock (_gate)
        {
            tap = _tap;
            _tap = null;
        }
        tap?.Dispose();
        _status = "Stopped";
    }

    private void OnEvent(uint type, IntPtr cgEvent)
    {
        var button = ButtonFor(type, MacQuartz.CGEventGetIntegerValueField(cgEvent, MacQuartz.MouseEventButtonNumber));
        if (button is null) return;
        var at = MacQuartz.CGEventGetLocation(cgEvent);
        var click = new MouseClick(button.Value, (int)Math.Round(at.X), (int)Math.Round(at.Y),
            MacHotkeyTable.Modifiers(MacQuartz.CGEventGetFlags(cgEvent)));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Clicked?.Invoke(click); }
            catch (Exception ex) { log.Error("Input", "Mouse click handler failed", ex); }
        });
    }

    /// <summary>The button for a Quartz mouse-down event type and its button number (2 middle, 3 back, 4 forward).</summary>
    internal static MouseButton? ButtonFor(uint type, long buttonNumber) => type switch
    {
        MacQuartz.LeftMouseDown => MouseButton.Left,
        MacQuartz.RightMouseDown => MouseButton.Right,
        MacQuartz.OtherMouseDown => buttonNumber switch { 2 => MouseButton.Middle, 3 => MouseButton.X1, 4 => MouseButton.X2, _ => null },
        _ => null,
    };

    public void Dispose() => Stop();
}
