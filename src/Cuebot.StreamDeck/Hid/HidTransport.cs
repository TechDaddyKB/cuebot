namespace Cuebot.StreamDeck.Hid;

/// <summary>
/// A USB HID device seen by a scan. <see cref="Path"/> is null when the device is on USB but has no HID interface the
/// system exposes (on Linux: no hidraw node, because another program such as StreamController detached the kernel
/// driver to talk to it over libusb).
/// </summary>
/// <param name="Path">What <see cref="IHidTransport.Open"/> takes (Linux: <c>/dev/hidrawN</c>), or null.</param>
/// <param name="Serial">The USB serial number (may be empty).</param>
/// <param name="Name">The product name the device reports.</param>
/// <param name="UsbPort">Where it is plugged in (Linux: the sysfs USB device name such as <c>7-2</c>); for messages.</param>
public sealed record HidDeviceInfo(string? Path, ushort VendorId, ushort ProductId, string Serial, string Name, string UsbPort = "")
{
    public bool HasHidInterface => Path is not null;

    /// <summary>A stable key for this physical device: serial when known, else the port or path.</summary>
    public string Key => Serial.Length > 0 ? Serial : UsbPort.Length > 0 ? "usb:" + UsbPort : Path ?? "";
}

/// <summary>Why a HID device could not be opened.</summary>
public enum HidOpenError { PermissionDenied, Busy, Gone, Other }

/// <summary>Opening a HID device failed; <see cref="Error"/> says why, the message is user-facing.</summary>
public sealed class HidOpenException(HidOpenError error, string message) : IOException(message)
{
    public HidOpenError Error { get; } = error;
}

/// <summary>The device went away (unplugged, or the driver was detached) while it was open.</summary>
public sealed class HidDisconnectedException(string message) : IOException(message);

/// <summary>
/// One open HID device. Reports always start with their report id (byte 0), as on the wire. <see cref="Read"/> may run
/// on one thread while writes run on another; the caller serializes writes.
/// </summary>
public interface IHidDevice : IDisposable
{
    /// <summary>Waits up to <paramref name="timeoutMs"/> for an input report; returns its length, or 0 on timeout.</summary>
    /// <exception cref="HidDisconnectedException">The device is gone.</exception>
    int Read(byte[] buffer, int timeoutMs);

    /// <summary>Writes one output report (byte 0 is the report id).</summary>
    /// <exception cref="HidDisconnectedException">The device is gone.</exception>
    void Write(ReadOnlySpan<byte> report);

    /// <summary>Reads a feature report; the result starts with the report id.</summary>
    byte[] GetFeature(byte reportId, int length);

    /// <summary>Sends a feature report (byte 0 is the report id).</summary>
    void SetFeature(ReadOnlySpan<byte> report);
}

/// <summary>Platform access to HID devices, behind an interface so the Stream Deck logic is tested with a fake.</summary>
public interface IHidTransport
{
    /// <summary>Why direct HID access does not work on this system, or null.</summary>
    string? UnavailableReason { get; }

    /// <summary>Every device of this vendor, including ones on USB without a usable HID interface (Path null).</summary>
    IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId);

    /// <exception cref="HidOpenException">Not openable (permissions, used by another program, gone).</exception>
    IHidDevice Open(HidDeviceInfo device);
}

/// <summary>The transport for this OS: hidraw on Linux; elsewhere one that explains it is not available yet.</summary>
public static class HidTransports
{
    public static IHidTransport CreateDefault() =>
        OperatingSystem.IsLinux() ? new LinuxHidTransport() : new UnavailableHidTransport(
            "Direct USB control of a Stream Deck is Linux-only for now; on Windows and macOS use the Stream Deck app with the Cuebot plugin");
}

/// <summary>A transport that finds nothing and says why (used on systems without a HID backend).</summary>
public sealed class UnavailableHidTransport(string reason) : IHidTransport
{
    public string? UnavailableReason => reason;
    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId) => [];
    public IHidDevice Open(HidDeviceInfo device) => throw new HidOpenException(HidOpenError.Other, reason);
}
