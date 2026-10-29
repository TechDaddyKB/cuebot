using Cuebot.StreamDeck.Hid;
using SkiaSharp;

namespace Cuebot.StreamDeck.Tests;

/// <summary>Linux enumeration against a fake sysfs/procfs tree, key image rendering and image loading.</summary>
public sealed class HidPlatformTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-hid-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Sys => Path.Combine(_root, "sys");

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + "\n");
    }

    /// <summary>Lays out sysfs like the kernel: class/hidraw/hidrawN → devices/…/&lt;usb&gt;/&lt;if&gt;/&lt;hid&gt;/hidraw/hidrawN.</summary>
    private void AddHidraw(string node, string usb, string hidId, string name, string uniq)
    {
        var hidDir = $"devices/pci0000:00/usb7/{usb}/{usb}:1.0/{hidId.Replace(":", "", StringComparison.Ordinal)}.0001";
        var nodeDir = Path.Combine(Sys, hidDir, "hidraw", node);
        Directory.CreateDirectory(nodeDir);
        Write(Path.Combine(Sys, hidDir, "uevent"), $"DRIVER=hid-generic\nHID_ID={hidId}\nHID_NAME={name}\nHID_UNIQ={uniq}");
        Directory.CreateSymbolicLink(Path.Combine(nodeDir, "device"), "../../../" + Path.GetFileName(hidDir));
        Directory.CreateDirectory(Path.Combine(Sys, "class", "hidraw"));
        Directory.CreateSymbolicLink(Path.Combine(Sys, "class", "hidraw", node), "../../" + hidDir + "/hidraw/" + node);
    }

    private void AddUsb(string usb, string vid, string pid, string serial, string product)
    {
        var dir = Path.Combine(Sys, "devices", "pci0000:00", "usb7", usb);
        Write(Path.Combine(dir, "idVendor"), vid);
        Write(Path.Combine(dir, "idProduct"), pid);
        Write(Path.Combine(dir, "serial"), serial);
        Write(Path.Combine(dir, "product"), product);
        Directory.CreateDirectory(Path.Combine(Sys, "bus", "usb", "devices"));
        Directory.CreateSymbolicLink(Path.Combine(Sys, "bus", "usb", "devices", usb), "../../../devices/pci0000:00/usb7/" + usb);
    }

    [Fact]
    public void Linux_enumeration_finds_hidraw_decks_and_decks_taken_over_by_libusb()
    {
        if (!OperatingSystem.IsLinux()) return;   // symbolic links in the fake tree behave like sysfs only on Linux
        AddUsb("7-2", "0fd9", "006c", "CL50K2A01783", "Stream Deck XL");
        AddHidraw("hidraw0", "7-2", "0003:00000FD9:0000006C", "Elgato Stream Deck XL", "CL50K2A01783");
        AddUsb("3-1", "0fd9", "0080", "AL12345", "Stream Deck MK.2");                     // no hidraw: driver detached
        AddUsb("3-4", "1532", "0244", "", "Tartarus");                                     // another vendor
        AddHidraw("hidraw7", "3-4", "0003:00001532:00000244", "Razer Tartarus", "");
        Directory.CreateDirectory(Path.Combine(Sys, "bus", "usb", "devices", "7-2:1.0"));  // interfaces are skipped

        var found = new LinuxHidTransport(Sys, Path.Combine(_root, "proc")).Enumerate(StreamDeckHidModels.ElgatoVendorId);

        Assert.Equal(2, found.Count);
        Assert.Equal(new HidDeviceInfo("/dev/hidraw0", 0x0FD9, 0x006C, "CL50K2A01783", "Elgato Stream Deck XL", "7-2"), found[0]);
        Assert.Equal(new HidDeviceInfo(null, 0x0FD9, 0x0080, "AL12345", "Stream Deck MK.2", "3-1"), found[1]);
        Assert.False(found[1].HasHidInterface);
        Assert.Equal("CL50K2A01783", found[0].Key);
    }

    [Fact]
    public void Linux_transport_names_another_program_holding_the_node()
    {
        if (!OperatingSystem.IsLinux()) return;
        var proc = Path.Combine(_root, "proc");
        Write(Path.Combine(proc, "4242", "comm"), "streamcontroller");
        Directory.CreateDirectory(Path.Combine(proc, "4242", "fd"));
        File.CreateSymbolicLink(Path.Combine(proc, "4242", "fd", "3"), "/dev/null");
        File.CreateSymbolicLink(Path.Combine(proc, "4242", "fd", "9"), "/dev/hidraw0");
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(proc, self, "fd"));
        File.CreateSymbolicLink(Path.Combine(proc, self, "fd", "5"), "/dev/hidraw1");   // our own handles do not count
        Directory.CreateDirectory(Path.Combine(proc, "sys"));                           // not a process

        var transport = new LinuxHidTransport(Sys, proc);
        Assert.Equal("streamcontroller (pid 4242)", transport.FindOtherUser("/dev/hidraw0"));
        Assert.Null(transport.FindOtherUser("/dev/hidraw1"));
        var error = Assert.Throws<HidOpenException>(() => transport.Open(new HidDeviceInfo("/dev/hidraw0", 0x0FD9, 0x006C, "S", "XL")));
        Assert.Equal(HidOpenError.Busy, error.Error);
        Assert.Contains("streamcontroller (pid 4242)", error.Message);
        Assert.Equal(HidOpenError.Busy, Assert.Throws<HidOpenException>(() => transport.Open(new HidDeviceInfo(null, 0x0FD9, 0x006C, "S", "XL"))).Error);
    }

    [Fact]
    public void Other_systems_get_a_transport_that_explains_why()
    {
        var transport = HidTransports.CreateDefault();
        if (OperatingSystem.IsLinux()) Assert.IsType<LinuxHidTransport>(transport);
        else Assert.Contains("Linux-only", transport.UnavailableReason);
        var unavailable = new UnavailableHidTransport("nope");
        Assert.Empty(unavailable.Enumerate(StreamDeckHidModels.ElgatoVendorId));
        Assert.Throws<HidOpenException>(() => unavailable.Open(FakeHidTransport.Xl()));
    }

    private static readonly StreamDeckHidModel Xl = StreamDeckHidModels.Find(0x0FD9, 0x006C)!;
    private static readonly StreamDeckHidModel Plus = StreamDeckHidModels.Find(0x0FD9, 0x0084)!;

    /// <summary>A red picture with a white top-left quarter, to see where the corner ends up.</summary>
    internal static byte[] MarkerPicture()
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, 32, 32, white);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool IsWhite(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;
    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 80 && c.Blue < 80;

    [Fact]
    public void Xl_images_are_jpeg_at_key_size_mirrored_both_ways()
    {
        var jpeg = StreamDeckKeyRenderer.Render(Xl, new KeyFace(Image: MarkerPicture()));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);   // JPEG SOI
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((96, 96), (decoded.Width, decoded.Height));
        // The XL's panel is upside down: the user's top-left corner is sent as the bottom-right.
        Assert.True(IsWhite(decoded.GetPixel(80, 80)), "white marker at the bottom-right");
        Assert.True(IsRed(decoded.GetPixel(16, 16)), "red at the top-left");

        using var plus = SKBitmap.Decode(StreamDeckKeyRenderer.Render(Plus, new KeyFace(Image: MarkerPicture())));
        Assert.Equal(120, plus.Width);
        Assert.True(IsWhite(plus.GetPixel(20, 20)), "the + is not flipped");
    }

    [Fact]
    public void Titles_and_overlays_draw_on_the_key()
    {
        using var blank = SKBitmap.Decode(StreamDeckKeyRenderer.Render(Xl, KeyFace.Blank));
        Assert.All(Enumerable.Range(0, 96), x => Assert.True(blank.GetPixel(x, 48).Red < 30));
        Assert.True(KeyFace.Blank.IsBlank);

        using var title = StreamDeckKeyRenderer.Draw(96, new KeyFace("Scene\n2"));
        Assert.True(Bright(title) > 50, "the title is drawn in white");
        using var ok = StreamDeckKeyRenderer.Draw(96, new KeyFace(Overlay: KeyOverlay.Ok));
        Assert.True(ok.GetPixel(48, 20).Green > 150, "green OK circle");
        using var alert = StreamDeckKeyRenderer.Draw(96, new KeyFace(Overlay: KeyOverlay.Alert));
        Assert.True(alert.GetPixel(30, 72).Red > 200 && alert.GetPixel(30, 72).Blue < 100, "yellow alert triangle");

        Assert.Throws<NotSupportedException>(() => StreamDeckKeyRenderer.Render(StreamDeckHidModels.Find(0x0FD9, 0x0086)!, KeyFace.Blank));   // Pedal
    }

    private static int Bright(SKBitmap bitmap)
    {
        var n = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (IsWhite(bitmap.GetPixel(x, y))) n++;
        return n;
    }

    [Fact]
    public async Task Images_load_from_data_uris_and_files_and_failures_are_reported()
    {
        var failures = new List<string>();
        var loader = new KeyImageLoader(maxBytes: 100_000);
        loader.Failed += failures.Add;
        var png = MarkerPicture();

        Assert.Equal(png, await loader.LoadAsync("data:image/png;base64," + Convert.ToBase64String(png)));
        var file = Path.Combine(_root, "key.png");
        await File.WriteAllBytesAsync(file, png);
        Assert.Equal(png, await loader.LoadAsync($"\"{file}\""));
        Assert.Equal(png, await loader.LoadAsync(new Uri(file).AbsoluteUri));
        Assert.Null(await loader.LoadAsync(""));
        Assert.Empty(failures);

        Assert.Null(await loader.LoadAsync(Path.Combine(_root, "missing.png")));
        Assert.Null(await loader.LoadAsync("data:image/svg+xml;base64,PHN2Zy8+"));
        Assert.Null(await loader.LoadAsync("https://example.invalid/key.png"));   // no HttpClient given: nothing is fetched
        var big = Path.Combine(_root, "big.png");
        await File.WriteAllBytesAsync(big, new byte[200_000]);
        Assert.Null(await loader.LoadAsync(big));
        Assert.Equal(4, failures.Count);
        Assert.DoesNotContain(failures, f => f.Contains("PHN2Zy8", StringComparison.Ordinal));   // data URIs are not logged in full

        Assert.Null(await loader.LoadAsync(Path.Combine(_root, "missing.png")));   // cached: not reported again
        Assert.Equal(4, failures.Count);
    }
}
