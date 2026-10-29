using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuebot.StreamDeck.Hid;

/// <summary>
/// HID on Linux through the kernel's hidraw driver, with nothing but libc: devices are found in sysfs
/// (<c>/sys/class/hidraw/*/device/uevent</c>: HID_ID, HID_NAME, HID_UNIQ = serial), reports are read and written on
/// <c>/dev/hidrawN</c>, feature reports go through the HIDIOCGFEATURE / HIDIOCSFEATURE ioctls (linux/hidraw.h). USB
/// devices of the vendor that have no hidraw node (another program detached the kernel driver to use libusb) are
/// reported with a null path so the status can say so. Hot-plug is noticed by scanning again (no udev dependency).
/// </summary>
public sealed class LinuxHidTransport : IHidTransport
{
    private readonly string _sys;
    private readonly string _proc;

    /// <param name="sysRoot">sysfs mount (tests point it at a fake tree).</param>
    /// <param name="procRoot">procfs mount, to find other programs holding the device open.</param>
    public LinuxHidTransport(string sysRoot = "/sys", string procRoot = "/proc")
    {
        _sys = sysRoot;
        _proc = procRoot;
    }

    public string? UnavailableReason => OperatingSystem.IsLinux() ? null : "hidraw exists only on Linux";

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var found = new List<HidDeviceInfo>();
        var hidraw = Path.Combine(_sys, "class", "hidraw");
        if (Directory.Exists(hidraw))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(hidraw).Order(StringComparer.Ordinal))
            {
                var info = ReadHidraw(entry);
                if (info is not null && info.VendorId == vendorId) found.Add(info);
            }
        }

        // USB devices of the vendor with no hidraw node: held by a libusb program (kernel driver detached).
        var usb = Path.Combine(_sys, "bus", "usb", "devices");
        if (Directory.Exists(usb))
        {
            foreach (var dir in Directory.EnumerateFileSystemEntries(usb).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains(':')) continue;   // interfaces (7-2:1.0), not devices
                if (ReadHex(Path.Combine(dir, "idVendor")) != vendorId || ReadHex(Path.Combine(dir, "idProduct")) is not { } pid) continue;
                var serial = ReadText(Path.Combine(dir, "serial"));
                if (found.Any(f => f.UsbPort == name || (serial.Length > 0 && f.Serial == serial))) continue;
                found.Add(new HidDeviceInfo(null, vendorId, pid, serial, ReadText(Path.Combine(dir, "product")), name));
            }
        }
        return found;
    }

    private static HidDeviceInfo? ReadHidraw(string entry)
    {
        var uevent = ReadText(Path.Combine(entry, "device", "uevent"));
        if (ParseUevent(uevent) is not { } id) return null;
        var node = "/dev/" + Path.GetFileName(entry);
        return new HidDeviceInfo(node, id.Vendor, id.Product, id.Uniq, id.Name, UsbPort(entry));
    }

    /// <summary>HID_ID (bus:vendor:product, hex), HID_NAME and HID_UNIQ from a hid device's uevent; null for non-USB.</summary>
    internal static (ushort Vendor, ushort Product, string Name, string Uniq)? ParseUevent(string text)
    {
        string? hidId = null, name = "", uniq = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var value = line[(eq + 1)..];
            switch (line[..eq])
            {
                case "HID_ID": hidId = value; break;
                case "HID_NAME": name = value.Trim(); break;
                case "HID_UNIQ": uniq = value.Trim(); break;
            }
        }
        var parts = hidId?.Split(':');
        if (parts is not { Length: 3 }) return null;
        if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) || bus != 3) return null;   // 3 = BUS_USB
        if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) || vid > ushort.MaxValue) return null;
        if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) || pid > ushort.MaxValue) return null;
        return ((ushort)vid, (ushort)pid, name, uniq);
    }

    /// <summary>
    /// The USB device a hidraw entry belongs to: /sys/class/hidraw/hidrawN links to
    /// …/&lt;usb device&gt;/&lt;interface&gt;/&lt;hid device&gt;/hidraw/hidrawN.
    /// </summary>
    private static string UsbPort(string entry)
    {
        try
        {
            var target = new FileInfo(entry).LinkTarget;
            var real = target is null ? entry : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry)!, target));
            var usbDevice = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(real))));
            return usbDevice is null ? "" : Path.GetFileName(usbDevice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>Other processes (that this user may inspect) with the node open: "name (pid N)", or null.</summary>
    internal string? FindOtherUser(string node)
    {
        if (!Directory.Exists(_proc)) return null;
        var self = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        foreach (var pidDir in Directory.EnumerateDirectories(_proc))
        {
            var pid = Path.GetFileName(pidDir);
            if (pid == self || pid.Length == 0 || !pid.All(char.IsAsciiDigit)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(pidDir, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget != node) continue;
                    var comm = ReadText(Path.Combine(pidDir, "comm"));
                    return $"{(comm.Length > 0 ? comm : "another program")} (pid {pid})";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or it just exited.
            }
        }
        return null;
    }

    public IHidDevice Open(HidDeviceInfo device)
    {
        if (device.Path is not { } path)
            throw new HidOpenException(HidOpenError.Busy, "its HID interface is not available (another program has detached the kernel driver)");
        if (!OperatingSystem.IsLinux()) throw new HidOpenException(HidOpenError.Other, UnavailableReason ?? "not supported");
        if (FindOtherUser(path) is { } user) throw new HidOpenException(HidOpenError.Busy, $"{path} is in use by {user}");
        return OpenNode(path);
    }

    [SupportedOSPlatform("linux")]
    private static LinuxHidDevice OpenNode(string path)
    {
        var fd = Native.open(path, Native.ORdwr | Native.OCloexec);
        if (fd >= 0) return new LinuxHidDevice(fd, path);
        var errno = Marshal.GetLastPInvokeError();
        throw errno switch
        {
            Native.EAcces or Native.EPerm => new HidOpenException(HidOpenError.PermissionDenied,
                $"no permission to open {path} (install the udev rule from packaging/linux/70-cuebot-streamdeck.rules)"),
            Native.ENoent or Native.ENodev or Native.ENxio => new HidOpenException(HidOpenError.Gone, $"{path} went away"),
            Native.EBusy => new HidOpenException(HidOpenError.Busy, $"{path} is busy"),
            _ => new HidOpenException(HidOpenError.Other, $"cannot open {path} (errno {errno})"),
        };
    }

    // ---- ioctl numbers (asm-generic/ioctl.h: dir << 30 | size << 16 | type << 8 | nr; x86, x86-64, ARM, ARM64, RISC-V) --

    private const uint IocWrite = 1, IocRead = 2;

    /// <summary>HIDIOCSFEATURE(len) = _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x06, len).</summary>
    internal static uint SetFeatureRequest(int length) => Ioc(IocWrite | IocRead, 'H', 0x06, length);

    /// <summary>HIDIOCGFEATURE(len) = _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x07, len).</summary>
    internal static uint GetFeatureRequest(int length) => Ioc(IocWrite | IocRead, 'H', 0x07, length);

    private static uint Ioc(uint dir, char type, uint nr, int size) => (dir << 30) | ((uint)size << 16) | ((uint)type << 8) | nr;

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static ushort? ReadHex(string path) =>
        ushort.TryParse(ReadText(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---- An open hidraw node ----------------------------------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    private sealed class LinuxHidDevice(int fd, string path) : IHidDevice
    {
        private readonly Lock _gate = new();
        private int _fd = fd;

        public int Read(byte[] buffer, int timeoutMs)
        {
            var fd = Fd();
            var poll = new Native.PollFd { Fd = fd, Events = Native.PollIn };
            var ready = Native.poll(ref poll, 1, timeoutMs);
            if (ready < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Native.EIntr) return 0;
                throw new IOException($"poll on {path} failed (errno {errno})");
            }
            if (ready == 0) return 0;
            if ((poll.Revents & (Native.PollErr | Native.PollHup | Native.PollNval)) != 0 && (poll.Revents & Native.PollIn) == 0)
                throw new HidDisconnectedException($"{path} was disconnected");
            var n = Native.read(fd, buffer, buffer.Length);
            if (n >= 0) return (int)n;
            var readErrno = Marshal.GetLastPInvokeError();
            if (readErrno is Native.EIntr or Native.EAgain) return 0;
            throw Failure("read", readErrno);
        }

        public void Write(ReadOnlySpan<byte> report)
        {
            var buffer = report.ToArray();
            int errno;
            nint n;
            do
            {
                n = Native.write(Fd(), buffer, buffer.Length);
                errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (n < 0 && errno == Native.EIntr);
            if (n < 0) throw Failure("write", errno);
            if (n != buffer.Length) throw new IOException($"short write to {path} ({n} of {buffer.Length} bytes)");
        }

        public byte[] GetFeature(byte reportId, int length)
        {
            var buffer = new byte[length];
            buffer[0] = reportId;
            var n = Ioctl(GetFeatureRequest(length), buffer, "HIDIOCGFEATURE");
            return buffer[..Math.Min(n, length)];
        }

        public void SetFeature(ReadOnlySpan<byte> report)
        {
            var buffer = report.ToArray();
            Ioctl(SetFeatureRequest(buffer.Length), buffer, "HIDIOCSFEATURE");
        }

        private int Ioctl(uint request, byte[] buffer, string what)
        {
            int result, errno;
            do
            {
                result = Native.ioctl(Fd(), request, buffer);
                errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (result < 0 && errno == Native.EIntr);
            if (result < 0) throw Failure(what, errno);
            return result;
        }

        private IOException Failure(string what, int errno) => errno is Native.ENodev or Native.EIo or Native.ENxio or Native.EPipe or Native.ENoent
            ? new HidDisconnectedException($"{path} was disconnected ({what}: errno {errno})")
            : new IOException($"{what} on {path} failed (errno {errno})");

        private int Fd()
        {
            lock (_gate) return _fd >= 0 ? _fd : throw new ObjectDisposedException(path);
        }

        public void Dispose()
        {
            int fd;
            lock (_gate)
            {
                fd = _fd;
                _fd = -1;
            }
            if (fd >= 0) _ = Native.close(fd);
        }
    }

    [SupportedOSPlatform("linux")]
    private static class Native
    {
        public const int ORdwr = 2, OCloexec = 0x80000;
        public const int EPerm = 1, ENoent = 2, EIntr = 4, EIo = 5, ENxio = 6, EAgain = 11, EAcces = 13, EBusy = 16, ENodev = 19, EPipe = 32;
        public const short PollIn = 0x1, PollErr = 0x8, PollHup = 0x10, PollNval = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern nint read(int fd, byte[] buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        public static extern nint write(int fd, byte[] buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        public static extern int poll(ref PollFd fds, nuint count, int timeoutMs);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, nuint request, byte[] argp);
    }
}
