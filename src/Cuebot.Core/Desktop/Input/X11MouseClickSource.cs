using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// X11: XInput2 raw button presses selected on the root window of a private display connection, polled on its own
/// thread like the X11 hotkeys. Raw events are a copy (the click still reaches the application under the pointer),
/// unlike XGrabButton, which would take every click. Position and held modifiers come from XQueryPointer.
/// Under Wayland (XWayland) the X server only reports clicks while an X11 window has focus, so this does not work for
/// native Wayland applications (see <see cref="MouseClickSources.WaylandReason"/>).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11MouseClickSource : IMouseClickSource
{
    private const int GenericEvent = 35;
    private const int XiRawButtonPress = 15;
    private const int XiAllMasterDevices = 1;
    private const uint ShiftMask = 1, ControlMask = 4, Mod1Mask = 8, Mod4Mask = 64;

    private readonly AppLog _log;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile string _status = "Not started";

    private X11MouseClickSource(AppLog log) => _log = log;

    public string BackendName => "X11";
    public string Status => _status;
    public event Action<MouseClick>? Clicked;

    /// <summary>Checks for DISPLAY, libX11/libXi and the XInput 2 extension (the connection is reopened on each start).</summary>
    public static (X11MouseClickSource? Source, string Reason) TryCreate(AppLog log)
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return (null, "DISPLAY is not set");
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return (null, "Cannot open the X display");
            try
            {
                return Open(display, out _, out var reason) ? (new X11MouseClickSource(log), "") : (null, reason);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, "libX11 or libXi is not installed");
        }
    }

    /// <summary>Checks XInput 2 and selects raw button presses on the root window.</summary>
    private static bool Open(IntPtr display, out int opcode, out string reason)
    {
        if (!XQueryExtension(display, "XInputExtension", out opcode, out _, out _))
        {
            reason = "the X server has no XInput extension";
            return false;
        }
        int major = 2, minor = 0;
        if (XIQueryVersion(display, ref major, ref minor) != 0)
        {
            reason = "the X server has no XInput 2";
            return false;
        }
        reason = "";
        return true;
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return true;
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                _status = "Cannot open the X display";
                return false;
            }
            if (!Open(display, out var opcode, out var reason) || !SelectRawPresses(display))
            {
                XCloseDisplay(display);
                _status = reason.Length > 0 ? reason : "Could not select XInput 2 events";
                return false;
            }
            _running = true;
            _thread = new Thread(() => Loop(display, opcode)) { IsBackground = true, Name = "Cuebot X11 mouse" };
            _thread.Start();
            _status = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
                ? "Watching X11 clicks through XWayland (clicks in Wayland applications are not seen)"
                : "Watching mouse clicks (XInput 2)";
            return true;
        }
    }

    private static bool SelectRawPresses(IntPtr display)
    {
        var mask = Marshal.AllocHGlobal(4);
        var eventMask = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(mask, 0);
            Marshal.WriteByte(mask, XiRawButtonPress >> 3, (byte)(1 << (XiRawButtonPress & 7)));
            // XIEventMask (LP64): int deviceid, int mask_len, unsigned char* mask.
            Marshal.WriteInt32(eventMask, 0, XiAllMasterDevices);
            Marshal.WriteInt32(eventMask, 4, 4);
            Marshal.WriteIntPtr(eventMask, 8, mask);
            var ok = XISelectEvents(display, XDefaultRootWindow(display), eventMask, 1) == 0;
            XFlush(display);
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(eventMask);
            Marshal.FreeHGlobal(mask);
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _running = false;
        }
        thread?.Join(TimeSpan.FromSeconds(2));
        _status = "Stopped";
    }

    private void Loop(IntPtr display, int opcode)
    {
        var ev = Marshal.AllocHGlobal(256);   // XEvent is 192 bytes on 64-bit
        var root = XDefaultRootWindow(display);
        try
        {
            while (_running)
            {
                var handled = false;
                while (_running && XPending(display) > 0)
                {
                    XNextEvent(display, ev);
                    handled = true;
                    // XGenericEventCookie (LP64): type 0, extension 32, evtype 36, data 48.
                    if (Marshal.ReadInt32(ev) != GenericEvent || Marshal.ReadInt32(ev, 32) != opcode) continue;
                    if (!XGetEventData(display, ev)) continue;
                    try
                    {
                        if (Marshal.ReadInt32(ev, 36) != XiRawButtonPress) continue;
                        var data = Marshal.ReadIntPtr(ev, 48);
                        // XIRawEvent (LP64): detail (the button number) at 56.
                        var button = Marshal.ReadInt32(data, 56) switch
                        {
                            1 => MouseButton.Left,
                            2 => MouseButton.Middle,
                            3 => MouseButton.Right,
                            8 => MouseButton.X1,
                            9 => MouseButton.X2,
                            _ => (MouseButton?)null,   // 4-7 are the scroll wheel
                        };
                        if (button is not { } b) continue;
                        XQueryPointer(display, root, out _, out _, out var x, out var y, out _, out _, out var mask);
                        Raise(new MouseClick(b, x, y, Modifiers(mask)));
                    }
                    finally
                    {
                        XFreeEventData(display, ev);
                    }
                }
                if (!handled) Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            _status = "X11 mouse thread stopped: " + ex.Message;
            _log.Error("Input", "X11 mouse loop failed", ex);
        }
        finally
        {
            XCloseDisplay(display);
            Marshal.FreeHGlobal(ev);
        }
    }

    private static HotkeyModifiers Modifiers(uint mask) =>
        ((mask & ControlMask) != 0 ? HotkeyModifiers.Ctrl : 0) | ((mask & Mod1Mask) != 0 ? HotkeyModifiers.Alt : 0)
        | ((mask & ShiftMask) != 0 ? HotkeyModifiers.Shift : 0) | ((mask & Mod4Mask) != 0 ? HotkeyModifiers.Meta : 0);

    private void Raise(MouseClick click)
    {
        try { Clicked?.Invoke(click); }
        catch (Exception ex) { _log.Error("Input", "Mouse click handler failed", ex); }
    }

    public void Dispose() => Stop();

    private const string LibX11 = "libX11.so.6";
    private const string LibXi = "libXi.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern bool XQueryExtension(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string name, out int opcode, out int firstEvent, out int firstError);
    [DllImport(LibX11)] private static extern int XPending(IntPtr display);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(LibX11)] private static extern bool XGetEventData(IntPtr display, IntPtr cookie);
    [DllImport(LibX11)] private static extern void XFreeEventData(IntPtr display, IntPtr cookie);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] private static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport(LibXi)] private static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(LibXi)] private static extern int XISelectEvents(IntPtr display, IntPtr window, IntPtr masks, int count);
}
