using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Windows: a WH_MOUSE_LL hook on its own message-loop thread. The hook callback only reads the event and hands it
/// to the thread pool (a slow callback would lag the whole desktop's mouse), then passes the click on unchanged.
/// Clicks injected by other programs (and by Cuebot) are ignored.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMouseClickSource(AppLog log) : IMouseClickSource
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;
    private const uint LlmhfInjected = 0x1;

    private readonly object _gate = new();
    private HookProc? _proc;   // kept alive while the hook is installed
    private Thread? _thread;
    private uint _threadId;
    private volatile string _status = "Not started";

    public string BackendName => "Windows";
    public string Status => _status;
    public event Action<MouseClick>? Clicked;

    public bool Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return true;
            var ready = new ManualResetEventSlim();
            var ok = false;
            _thread = new Thread(() => Loop(ready, value => ok = value)) { IsBackground = true, Name = "Cuebot mouse hook" };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));   // not disposed: a late thread may still set it
            if (!ok)
            {
                _thread.Join(TimeSpan.FromSeconds(1));
                _thread = null;
            }
            return ok;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            if (thread is not null) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        thread?.Join(TimeSpan.FromSeconds(2));
        _status = "Stopped";
    }

    private void Loop(ManualResetEventSlim ready, Action<bool> started)
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        var hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            _status = "Could not install the mouse hook (error " + Marshal.GetLastWin32Error() + ")";
            started(false);
            ready.Set();
            return;
        }
        _status = "Watching mouse clicks";
        started(true);
        ready.Set();
        try
        {
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch (Exception ex)
        {
            _status = "Mouse hook thread stopped: " + ex.Message;
            log.Error("Input", "Mouse hook loop failed", ex);
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if ((data.Flags & LlmhfInjected) == 0)
                {
                    var button = message switch
                    {
                        WmLButtonDown => MouseButton.Left,
                        WmRButtonDown => MouseButton.Right,
                        WmMButtonDown => MouseButton.Middle,
                        _ => (data.MouseData >> 16) == 2 ? MouseButton.X2 : MouseButton.X1,
                    };
                    var click = new MouseClick(button, data.X, data.Y, HeldModifiers());
                    ThreadPool.QueueUserWorkItem(_ => Raise(click));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Raise(MouseClick click)
    {
        try { Clicked?.Invoke(click); }
        catch (Exception ex) { log.Error("Input", "Mouse click handler failed", ex); }
    }

    private static HotkeyModifiers HeldModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var m = HotkeyModifiers.None;
        if (Down(0x11)) m |= HotkeyModifiers.Ctrl;
        if (Down(0x12)) m |= HotkeyModifiers.Alt;
        if (Down(0x10)) m |= HotkeyModifiers.Shift;
        if (Down(0x5B) || Down(0x5C)) m |= HotkeyModifiers.Meta;
        return m;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsllHookStruct
    {
        public readonly int X;
        public readonly int Y;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
        public uint Private;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
