using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Cuebot.Elgato.CameraHub;

/// <summary>
/// Linux webcam controls through the kernel's V4L2 API (the same ioctls v4l2-ctl uses): VIDIOC_QUERYCAP to list capture
/// devices, VIDIOC_QUERYCTRL for ranges, VIDIOC_G_CTRL / VIDIOC_S_CTRL to read and write. Only libc is used.
/// Structures are read from byte buffers at their fixed kernel offsets (linux/videodev2.h), so no struct marshalling.
/// </summary>
internal sealed class V4l2Backend : IWebcamBackend
{
    // _IOR('V', 0, struct v4l2_capability /* 104 bytes */)
    internal const ulong VidiocQuerycap = 0x80685600;
    // _IOWR('V', 36, struct v4l2_queryctrl /* 68 bytes */)
    internal const ulong VidiocQueryctrl = 0xC0445624;
    // _IOWR('V', 27 / 28, struct v4l2_control /* 8 bytes */)
    internal const ulong VidiocGCtrl = 0xC008561B;
    internal const ulong VidiocSCtrl = 0xC008561C;

    internal const int CapabilitySize = 104;
    internal const int QueryCtrlSize = 68;
    private const uint CapVideoCapture = 0x00000001;
    private const uint CapDeviceCaps = 0x80000000;
    private const uint CtrlFlagDisabled = 0x0001;
    private const int ORdwr = 2, ONonblock = 0x800;

    private const uint UserBase = 0x00980900;
    private const uint CameraBase = 0x009A0900;

    /// <summary>V4L2 control ids (linux/v4l2-controls.h).</summary>
    internal static uint ControlId(WebcamControl control) => control switch
    {
        WebcamControl.Brightness => UserBase + 0,
        WebcamControl.Contrast => UserBase + 1,
        WebcamControl.Saturation => UserBase + 2,
        WebcamControl.AutoWhiteBalance => UserBase + 12,
        WebcamControl.Mirror => UserBase + 20,              // HFLIP
        WebcamControl.Flip => UserBase + 21,                // VFLIP
        WebcamControl.AntiFlicker => UserBase + 24,         // POWER_LINE_FREQUENCY
        WebcamControl.WhiteBalance => UserBase + 26,        // WHITE_BALANCE_TEMPERATURE
        WebcamControl.Sharpness => UserBase + 27,
        WebcamControl.AutoExposure => CameraBase + 1,       // EXPOSURE_AUTO (menu)
        WebcamControl.Exposure => CameraBase + 2,           // EXPOSURE_ABSOLUTE (100 us)
        WebcamControl.Pan => CameraBase + 8,                // PAN_ABSOLUTE
        WebcamControl.Tilt => CameraBase + 9,               // TILT_ABSOLUTE
        WebcamControl.Focus => CameraBase + 10,             // FOCUS_ABSOLUTE
        WebcamControl.AutoFocus => CameraBase + 12,         // FOCUS_AUTO
        WebcamControl.Zoom => CameraBase + 13,              // ZOOM_ABSOLUTE
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public string Description => "V4L2";

    public IReadOnlyList<WebcamDeviceInfo> Enumerate()
    {
        string[] nodes;
        try
        {
            nodes = Directory.GetFiles("/dev", "video*");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        var devices = new List<WebcamDeviceInfo>();
        var seenBus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in nodes.OrderBy(NodeNumber))
        {
            var fd = Open(path);
            if (fd < 0) continue;
            try
            {
                var cap = new byte[CapabilitySize];
                if (Ioctl(fd, VidiocQuerycap, cap) < 0) continue;
                var (card, bus, caps) = ParseCapability(cap);
                if ((caps & CapVideoCapture) == 0) continue;     // metadata nodes of the same camera
                if (!seenBus.Add(bus)) continue;
                devices.Add(new WebcamDeviceInfo(bus.Length > 0 ? bus : path, CleanName(card), path));
            }
            finally
            {
                Close(fd);
            }
        }
        return devices;
    }

    public WebcamRange? Query(WebcamDeviceInfo device, WebcamControl control)
    {
        var buffer = new byte[QueryCtrlSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ControlId(control));
        if (!WithDevice(device, fd => Ioctl(fd, VidiocQueryctrl, buffer) >= 0)) return null;
        return ParseQueryCtrl(buffer);
    }

    public int Get(WebcamDeviceInfo device, WebcamControl control)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ControlId(control));
        if (!WithDevice(device, fd => Ioctl(fd, VidiocGCtrl, buffer) >= 0))
            throw new IOException($"Reading {control} from {device.Name} failed (errno {Marshal.GetLastPInvokeError()}).");
        return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
    }

    public void Set(WebcamDeviceInfo device, WebcamControl control, int value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ControlId(control));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), value);
        if (!WithDevice(device, fd => Ioctl(fd, VidiocSCtrl, buffer) >= 0))
            throw new IOException($"Setting {control} on {device.Name} failed (errno {Marshal.GetLastPInvokeError()}; is an automatic mode on?).");
    }

    /// <summary>v4l2_capability: driver[16], card[32], bus_info[32], version, capabilities, device_caps, reserved[3].</summary>
    internal static (string Card, string Bus, uint Caps) ParseCapability(byte[] cap)
    {
        var card = CString(cap.AsSpan(16, 32));
        var bus = CString(cap.AsSpan(48, 32));
        var capabilities = BinaryPrimitives.ReadUInt32LittleEndian(cap.AsSpan(84));
        var deviceCaps = BinaryPrimitives.ReadUInt32LittleEndian(cap.AsSpan(88));
        return (card, bus, (capabilities & CapDeviceCaps) != 0 ? deviceCaps : capabilities);
    }

    /// <summary>v4l2_queryctrl: id, type, name[32], minimum, maximum, step, default_value, flags, reserved[2].</summary>
    internal static WebcamRange? ParseQueryCtrl(byte[] q)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(56));
        if ((flags & CtrlFlagDisabled) != 0) return null;
        return new WebcamRange(
            BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(40)),
            BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(44)),
            BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(48)),
            BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(52)));
    }

    /// <summary>UVC drivers often report "Elgato Facecam: Elgato Facecam"; keep one copy.</summary>
    internal static string CleanName(string card)
    {
        var colon = card.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && card[(colon + 2)..].StartsWith(card[..colon], StringComparison.Ordinal)) return card[..colon];
        return card;
    }

    private static string CString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    private static int NodeNumber(string path) =>
        int.TryParse(Path.GetFileName(path).AsSpan(5), out var n) ? n : int.MaxValue;

    private static bool WithDevice(WebcamDeviceInfo device, Func<int, bool> action)
    {
        var fd = Open(device.Path);
        if (fd < 0) throw new IOException($"Cannot open {device.Path} (errno {Marshal.GetLastPInvokeError()}; is the user in the 'video' group?).");
        try
        {
            return action(fd);
        }
        finally
        {
            Close(fd);
        }
    }

    private static int Open(string path) => NativeMethods.open(path, ORdwr | ONonblock);

    private static void Close(int fd) => _ = NativeMethods.close(fd);

    private static int Ioctl(int fd, ulong request, byte[] buffer)
    {
        int result;
        // EINTR (4): retry, like v4l2-ctl's xioctl.
        do result = NativeMethods.ioctl(fd, (nuint)request, buffer);
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        return result;
    }

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, nuint request, byte[] argp);
    }
}
