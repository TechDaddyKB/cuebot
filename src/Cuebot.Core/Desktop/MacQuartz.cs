using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuebot.Core.Desktop;

/// <summary>Quartz CGEventFlags modifier bits (plain numbers, so platform-neutral code can test them).</summary>
internal static class MacEventFlags
{
    public const ulong Shift = 0x20000, Control = 0x40000, Alternate = 0x80000, Command = 0x100000;
}

/// <summary>
/// The few CoreGraphics (Quartz event services) and CoreFoundation calls the macOS desktop backends use: event taps
/// for hotkeys and mouse clicks, posted events for key presses, and the privacy checks that guard both.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacQuartz
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // CGEventType
    public const uint LeftMouseDown = 1, RightMouseDown = 3, KeyDown = 10, KeyUp = 11, OtherMouseDown = 25;
    public const uint TapDisabledByTimeout = 0xFFFFFFFE, TapDisabledByUserInput = 0xFFFFFFFF;

    // CGEventField
    public const int MouseEventButtonNumber = 3, KeyboardEventAutorepeat = 8, KeyboardEventKeycode = 9;

    // CGEventTapLocation, CGEventTapPlacement, CGEventTapOptions, CGEventSourceStateID
    public const int SessionEventTap = 1, HeadInsertEventTap = 0, TapListenOnly = 1, HidSystemState = 1;

    public static ulong MaskOf(params uint[] types) => types.Aggregate(0UL, (mask, type) => mask | (1UL << (int)type));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr EventTapCallback(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest, EventTapCallback callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    public static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(CoreGraphics)]
    public static extern long CGEventGetIntegerValueField(IntPtr cgEvent, int field);

    [DllImport(CoreGraphics)]
    public static extern ulong CGEventGetFlags(IntPtr cgEvent);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventSourceCreate(int stateId);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetFlags(IntPtr cgEvent, ulong flags);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(int tap, IntPtr cgEvent);

    /// <summary>Whether this process may listen to keyboard and mouse input (Input Monitoring).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightListenEventAccess();

    /// <summary>Asks for Input Monitoring once (the system shows its prompt the first time only).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestListenEventAccess();

    /// <summary>Whether this process may send key presses to other apps (Accessibility).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightPostEventAccess();

    /// <summary>Asks for Accessibility once (the system shows its prompt the first time only).</summary>
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestPostEventAccess();

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);

    [DllImport(CoreFoundation)]
    public static extern void CFMachPortInvalidate(IntPtr port);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    /// <summary>kCFRunLoopCommonModes (an exported CFStringRef constant).</summary>
    public static IntPtr CommonModes => _commonModes.Value;

    private static readonly Lazy<IntPtr> _commonModes = new(() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes")));

    /// <summary>
    /// Runs a listen-only event tap on its own thread with its own run loop until <see cref="Dispose"/>. The callback
    /// runs on that thread and must return quickly (macOS disables a slow tap; it is re-enabled here when that happens).
    /// </summary>
    public sealed class ListenTap : IDisposable
    {
        private readonly EventTapCallback _callback;   // kept alive for as long as the tap exists
        private readonly Action<uint, IntPtr> _onEvent;
        private IntPtr _tap;
        private IntPtr _runLoop;
        private readonly Thread _thread;

        private ListenTap(ulong mask, Action<uint, IntPtr> onEvent, string threadName, out bool created)
        {
            _onEvent = onEvent;
            _callback = OnEvent;
            using var ready = new ManualResetEventSlim();
            var ok = false;
            _thread = new Thread(() =>
            {
                _tap = CGEventTapCreate(SessionEventTap, HeadInsertEventTap, TapListenOnly, mask, _callback, IntPtr.Zero);
                ok = _tap != IntPtr.Zero;
                if (!ok)
                {
                    ready.Set();
                    return;
                }
                var source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
                _runLoop = CFRunLoopGetCurrent();
                CFRunLoopAddSource(_runLoop, source, CommonModes);
                CGEventTapEnable(_tap, true);
                ready.Set();
                CFRunLoopRun();
                CFMachPortInvalidate(_tap);
                CFRelease(source);
                CFRelease(_tap);
            }) { IsBackground = true, Name = threadName };
            _thread.Start();
            ready.Wait();
            created = ok;
        }

        /// <summary>Starts a tap for the event types in <paramref name="mask"/>, or returns null when macOS refuses one.</summary>
        public static ListenTap? TryStart(ulong mask, Action<uint, IntPtr> onEvent, string threadName)
        {
            var tap = new ListenTap(mask, onEvent, threadName, out var created);
            return created ? tap : null;
        }

        private IntPtr OnEvent(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo)
        {
            if (type is TapDisabledByTimeout or TapDisabledByUserInput)
            {
                CGEventTapEnable(_tap, true);
                return cgEvent;
            }
            try { _onEvent(type, cgEvent); }
            catch { /* never let an exception cross into native code */ }
            return cgEvent;
        }

        public void Dispose()
        {
            if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
