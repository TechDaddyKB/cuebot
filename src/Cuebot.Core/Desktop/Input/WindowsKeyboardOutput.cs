using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Windows: one SendInput call per combo (virtual key plus its scan code, so both message-based and scan-code
/// reading applications see it). Windows blocks input into windows of elevated (administrator) applications
/// when Cuebot is not elevated itself; SendInput then reports fewer events sent.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeyboardOutput : KeyboardOutputBase
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventFExtendedKey = 0x1, KeyEventFKeyUp = 0x2;
    private volatile string _status = "Ready (SendInput)";

    public override string BackendName => "Windows";
    public override string Status => _status;

    protected override Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
    {
        var inputs = new Input[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var (vk, extended) = KeyCodes.Windows(events[i].Key) ?? throw new InvalidOperationException($"Key '{events[i].Key}' cannot be sent");
            inputs[i] = new Input
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Ki = new KeybdInput
                    {
                        Vk = vk,
                        Scan = (ushort)MapVirtualKey(vk, 0),
                        Flags = (extended ? KeyEventFExtendedKey : 0) | (events[i].Down ? 0 : KeyEventFKeyUp),
                    },
                },
            };
        }
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            _status = "Last press failed";
            throw new InvalidOperationException(error == 0
                ? "Windows blocked the key press (the focused window may belong to an administrator application)"
                : "Windows blocked the key press: " + new Win32Exception(error).Message);
        }
        _status = "Ready (SendInput)";
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    /// <summary>Sized like the largest member (MOUSEINPUT), as SendInput checks cbSize against the full INPUT.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
