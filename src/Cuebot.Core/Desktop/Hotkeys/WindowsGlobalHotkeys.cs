using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>
/// Windows: RegisterHotKey on a dedicated message-loop thread. Hotkeys are registered for the thread (no window
/// needed), so WM_HOTKEY arrives in that thread's queue; work from other threads is queued and the loop is woken
/// with a thread message. MOD_NOREPEAT stops auto-repeat while a combo is held.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGlobalHotkeys : IGlobalHotkeys
{
    private const uint WmHotkey = 0x0312;
    private const uint WmApp = 0x8000;
    private const uint WmQuit = 0x0012;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly AppLog _log;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<int, string> _registered = [];   // loop thread only
    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private int _nextId = 1;
    private volatile string _status = "Not started";
    private bool _disposed;

    public WindowsGlobalHotkeys(AppLog log) => _log = log;

    public string BackendName => "Windows";
    public string Status => _status;
    public event Action<string>? Pressed;

    public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<IReadOnlyList<HotkeyBindingResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (hotkeys.Count == 0 && _thread is null)
        {
            _status = "No hotkeys defined";
            done.SetResult([]);
            return done.Task;
        }
        Post(() =>
        {
            try { done.SetResult(ApplyOnLoop(hotkeys)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.WaitAsync(ct);
    }

    private IReadOnlyList<HotkeyBindingResult> ApplyOnLoop(IReadOnlyList<HotkeyRegistration> hotkeys)
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(IntPtr.Zero, id);
        _registered.Clear();
        var results = new List<HotkeyBindingResult>();
        foreach (var hotkey in hotkeys)
        {
            if (HotkeyKeys.WindowsVirtualKey(hotkey.Gesture.Key) is not { } vk)
            {
                results.Add(new HotkeyBindingResult(hotkey.Id, false, $"Key '{hotkey.Gesture.Key}' is not supported"));
                continue;
            }
            var id = _nextId++;
            if (_nextId > 0xBFFF) _nextId = 1;
            if (RegisterHotKey(IntPtr.Zero, id, Modifiers(hotkey.Gesture.Modifiers) | ModNoRepeat, (uint)vk))
            {
                _registered[id] = hotkey.Id;
                results.Add(new HotkeyBindingResult(hotkey.Id, true));
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                var message = error == ErrorHotkeyAlreadyRegistered
                    ? $"{hotkey.Gesture} is already used by another application"
                    : new Win32Exception(error).Message;
                results.Add(new HotkeyBindingResult(hotkey.Id, false, message));
            }
        }
        var ok = results.Count(r => r.Ok);
        _status = hotkeys.Count == 0 ? "No hotkeys defined" : $"{ok} of {hotkeys.Count} hotkey(s) registered";
        return results;
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Alt) ? ModAlt : 0) | (m.HasFlag(HotkeyModifiers.Ctrl) ? ModControl : 0)
        | (m.HasFlag(HotkeyModifiers.Shift) ? ModShift : 0) | (m.HasFlag(HotkeyModifiers.Meta) ? ModWin : 0);

    private void Post(Action work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _work.Enqueue(work);
            if (_thread is null)
            {
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "Cuebot hotkeys" };
                _thread.Start();
                ready.Wait();
            }
            PostThreadMessage(_threadId, WmApp, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // creates the thread's message queue before anyone posts to it
        ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey)
                {
                    if (_registered.TryGetValue((int)msg.wParam, out var id))
                    {
                        try { Pressed?.Invoke(id); }
                        catch (Exception ex) { _log.Error("Hotkeys", "Hotkey handler failed", ex); }
                    }
                }
                else if (msg.message == WmApp)
                {
                    while (_work.TryDequeue(out var work)) work();
                }
            }
        }
        catch (Exception ex)
        {
            _status = "Hotkey thread stopped: " + ex.Message;
            _log.Error("Hotkeys", "Hotkey message loop failed", ex);
        }
        finally
        {
            foreach (var id in _registered.Keys) UnregisterHotKey(IntPtr.Zero, id);
            _registered.Clear();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            if (thread is not null) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
