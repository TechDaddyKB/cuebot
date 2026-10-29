using System.Collections.Concurrent;
using Cuebot.StreamDeck.Hid;

namespace Cuebot.StreamDeck.Tests;

/// <summary>A HID device in memory: queued input reports, recorded output and feature reports.</summary>
internal sealed class FakeHidDevice(string serial = "CL50K2A01783", string firmware = "1.01.000") : IHidDevice
{
    private readonly BlockingCollection<byte[]> _input = new();
    private volatile bool _gone;

    public ConcurrentQueue<byte[]> Written { get; } = new();
    public ConcurrentQueue<byte[]> Features { get; } = new();
    public bool Disposed { get; private set; }

    public void Push(params byte[] report) => _input.Add(report);

    /// <summary>Key states as a gen-2 input report (0x01, 3 header bytes, one byte per key).</summary>
    public void PushKeys(int keyCount, params int[] pressed)
    {
        var report = new byte[4 + keyCount];
        report[0] = 0x01;
        report[2] = (byte)keyCount;
        foreach (var k in pressed) report[4 + k] = 1;
        Push(report);
    }

    public void Unplug() => _gone = true;

    public int Read(byte[] buffer, int timeoutMs)
    {
        if (_gone) throw new HidDisconnectedException("unplugged");
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!_input.TryTake(out var report, Math.Min(timeoutMs, 50))) return 0;
        report.CopyTo(buffer, 0);
        return report.Length;
    }

    public void Write(ReadOnlySpan<byte> report)
    {
        if (_gone) throw new HidDisconnectedException("unplugged");
        Written.Enqueue(report.ToArray());
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        var report = new byte[length];
        report[0] = reportId;
        var text = reportId == 0x06 ? serial : firmware;
        var offset = reportId == 0x06 ? 2 : 6;
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(report, offset);
        return report;
    }

    public void SetFeature(ReadOnlySpan<byte> report) => Features.Enqueue(report.ToArray());

    /// <summary>Image reports (0x02 0x07) sent for one key.</summary>
    public byte[][] ImagesFor(int key) => [.. Written.Where(r => r.Length > 2 && r[0] == 0x02 && r[1] == 0x07 && r[2] == key)];

    /// <summary>Complete images (last page) sent for one key.</summary>
    public int ImageCountFor(int key) => Written.Count(r => r.Length > 3 && r[0] == 0x02 && r[1] == 0x07 && r[2] == key && r[3] == 1);

    public int ImageCount => Written.Count(r => r.Length > 3 && r[0] == 0x02 && r[1] == 0x07 && r[3] == 1);

    public void Dispose() => Disposed = true;
}

/// <summary>A transport whose device list and open results the test controls.</summary>
internal sealed class FakeHidTransport : IHidTransport
{
    public List<HidDeviceInfo> Devices { get; } = [];
    public Dictionary<string, FakeHidDevice> Handles { get; } = [];
    public Dictionary<string, HidOpenException> OpenErrors { get; } = [];
    public string? UnavailableReason { get; set; }
    public int Opens;

    public static HidDeviceInfo Xl(string serial = "CL50K2A01783", string path = "/dev/hidraw0") =>
        new(path, StreamDeckHidModels.ElgatoVendorId, 0x006C, serial, "Elgato Stream Deck XL", "7-2");

    public FakeHidDevice Add(HidDeviceInfo info, FakeHidDevice? device = null)
    {
        lock (Devices) Devices.Add(info);
        if (info.Path is not null) Handles[info.Path] = device ?? new FakeHidDevice(info.Serial);
        return info.Path is null ? new FakeHidDevice() : Handles[info.Path];
    }

    public void Remove(string path)
    {
        lock (Devices) Devices.RemoveAll(d => d.Path == path);
    }

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        lock (Devices) return [.. Devices.Where(d => d.VendorId == vendorId)];
    }

    public IHidDevice Open(HidDeviceInfo device)
    {
        Interlocked.Increment(ref Opens);
        if (device.Path is null) throw new HidOpenException(HidOpenError.Busy, "no HID interface");
        if (OpenErrors.TryGetValue(device.Path, out var error)) throw error;
        return Handles[device.Path];
    }
}
