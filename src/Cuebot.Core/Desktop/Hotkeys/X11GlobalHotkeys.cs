using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>
/// X11 fallback: XGrabKey on the root window of a private display connection, polled on its own thread. Each combo
/// is grabbed with and without Caps Lock / Num Lock so those locks don't break it. Grab conflicts (BadAccess) are
/// caught by an error handler that chains to the previously installed one (e.g. Avalonia's).
/// Under Wayland (XWayland) this only sees keys while an X11 window has focus, so the portal is preferred.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11GlobalHotkeys : IGlobalHotkeys
{
    private const int KeyPress = 2;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    private const int GrabModeAsync = 1;
    private const byte BadAccess = 10;
    private static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private static readonly object HandlerGate = new();
    private static XErrorHandler? _handler;
    private static IntPtr _previousHandler;
    private static readonly ConcurrentDictionary<IntPtr, int> LastErrors = new();

    private readonly AppLog _log;
    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<(uint Keycode, uint Mods), string> _grabs = [];   // loop thread only
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile string _status = "Using X11 key grabs";

    private X11GlobalHotkeys(AppLog log, IntPtr display)
    {
        _log = log;
        _display = display;
        _root = XDefaultRootWindow(display);
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
            _status = "Using X11 key grabs through XWayland (keys only reach Cuebot while an X11 window has focus)";
        _thread = new Thread(Loop) { IsBackground = true, Name = "Cuebot X11 hotkeys" };
        _thread.Start();
    }

    public string BackendName => "X11";
    public string Status => _status;
    public event Action<string>? Pressed;

    /// <summary>Opens a display connection when DISPLAY is set and libX11 is present.</summary>
    public static (X11GlobalHotkeys? Backend, string Reason) TryCreate(AppLog log)
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return (null, "DISPLAY is not set");
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return (null, "Cannot open the X display");
            InstallErrorHandler();
            return (new X11GlobalHotkeys(log, display), "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, "libX11 is not installed");
        }
    }

    public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<IReadOnlyList<HotkeyBindingResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { done.SetResult(ApplyOnLoop(hotkeys)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.WaitAsync(ct);
    }

    private IReadOnlyList<HotkeyBindingResult> ApplyOnLoop(IReadOnlyList<HotkeyRegistration> hotkeys)
    {
        foreach (var (keycode, mods) in _grabs.Keys) XUngrabKey(_display, (int)keycode, mods, _root);
        _grabs.Clear();
        var results = new List<HotkeyBindingResult>();
        foreach (var hotkey in hotkeys)
        {
            var sym = HotkeyKeys.XKeysym(hotkey.Gesture.Key) is { } name ? XStringToKeysym(name) : IntPtr.Zero;
            var keycode = sym == IntPtr.Zero ? (byte)0 : XKeysymToKeycode(_display, sym);
            if (keycode == 0)
            {
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"Key '{hotkey.Gesture.Key}' is not on this keyboard layout"));
                continue;
            }
            var mods = Modifiers(hotkey.Gesture.Modifiers);
            if (_grabs.ContainsKey((keycode, mods)))
            {
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"{hotkey.Gesture} is used twice"));
                continue;
            }
            LastErrors[_display] = 0;
            foreach (var variant in LockVariants) XGrabKey(_display, keycode, mods | variant, _root, false, GrabModeAsync, GrabModeAsync);
            XSync(_display, false);
            if (LastErrors.TryGetValue(_display, out var error) && error == BadAccess)
            {
                foreach (var variant in LockVariants) XUngrabKey(_display, keycode, mods | variant, _root);
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"{hotkey.Gesture} is already grabbed by another application"));
                continue;
            }
            _grabs[(keycode, mods)] = hotkey.Id;
            results.Add(new HotkeyBindingResult(hotkey.Id, true));
        }
        XFlush(_display);
        return results;
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Shift) ? ShiftMask : 0) | (m.HasFlag(HotkeyModifiers.Ctrl) ? ControlMask : 0)
        | (m.HasFlag(HotkeyModifiers.Alt) ? Mod1Mask : 0) | (m.HasFlag(HotkeyModifiers.Meta) ? Mod4Mask : 0);

    private void Loop()
    {
        var ev = Marshal.AllocHGlobal(256);   // XEvent is 192 bytes on 64-bit
        try
        {
            while (_running)
            {
                while (_work.TryDequeue(out var work)) work();
                var handled = false;
                while (_running && XPending(_display) > 0)
                {
                    XNextEvent(_display, ev);
                    handled = true;
                    if (Marshal.ReadInt32(ev) != KeyPress) continue;
                    // XKeyEvent (LP64): state at offset 80, keycode at 84.
                    var state = (uint)Marshal.ReadInt32(ev, 80) & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask);
                    var keycode = (uint)Marshal.ReadInt32(ev, 84);
                    if (_grabs.TryGetValue((keycode, state), out var id))
                    {
                        try { Pressed?.Invoke(id); }
                        catch (Exception ex) { _log.Error("Hotkeys", "Hotkey handler failed", ex); }
                    }
                }
                if (!handled) Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            _status = "X11 hotkey thread stopped: " + ex.Message;
            _log.Error("Hotkeys", "X11 hotkey loop failed", ex);
        }
        finally
        {
            foreach (var (keycode, mods) in _grabs.Keys)
                foreach (var variant in LockVariants) XUngrabKey(_display, (int)keycode, mods | variant, _root);
            XCloseDisplay(_display);
            Marshal.FreeHGlobal(ev);
        }
    }

    private static void InstallErrorHandler()
    {
        lock (HandlerGate)
        {
            if (_handler is not null) return;
            _handler = OnXError;
            _previousHandler = XSetErrorHandler(_handler);
        }
    }

    /// <summary>Records errors on our displays; others go to the previous handler (never Xlib's exiting default for ours).</summary>
    private static int OnXError(IntPtr display, IntPtr errorEvent)
    {
        if (LastErrors.ContainsKey(display))
        {
            // XErrorEvent (LP64): error_code is the byte at offset 32.
            LastErrors[display] = Marshal.ReadByte(errorEvent, 32);
            return 0;
        }
        return _previousHandler != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<XErrorHandler>(_previousHandler)(display, errorEvent)
            : 0;
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(2));
        LastErrors.TryRemove(_display, out _);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(LibX11)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport(LibX11)] private static extern int XPending(IntPtr display);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(LibX11)] private static extern int XSync(IntPtr display, bool discard);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
}
