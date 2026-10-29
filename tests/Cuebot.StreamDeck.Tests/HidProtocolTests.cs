using Cuebot.StreamDeck.Hid;

namespace Cuebot.StreamDeck.Tests;

/// <summary>The direct USB (HID) protocol: device table, report building and parsing, mapping. No hardware.</summary>
public class HidProtocolTests
{
    private static StreamDeckHidModel Model(ushort pid) => StreamDeckHidModels.Find(StreamDeckHidModels.ElgatoVendorId, pid)!;

    private static readonly StreamDeckHidModel Xl = Model(0x006C);
    private static readonly StreamDeckHidModel Plus = Model(0x0084);

    [Fact]
    public void Device_table_has_unique_product_ids_and_describes_the_xl()
    {
        var ids = StreamDeckHidModels.All.SelectMany(m => m.ProductIds).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());

        Assert.Equal("Stream Deck XL", Xl.Name);
        Assert.Equal((32, 32, 8, 4, 96), (Xl.KeyCount, Xl.ImageKeyCount, Xl.Columns, Xl.Rows, Xl.KeyPixels));
        Assert.Equal((StreamDeckImageFormat.Jpeg, true, true, 0), (Xl.ImageFormat, Xl.FlipX, Xl.FlipY, Xl.Rotation));
        Assert.Equal((1024, 8, 32, 2, 6), (Xl.ImageReportLength, Xl.ImageHeaderLength, Xl.FeatureReportLength, Xl.SerialOffset, Xl.FirmwareOffset));
        Assert.Equal(2, Xl.ElgatoType);   // the plugin's device.type for an XL
        Assert.True(Xl.Supported);
        Assert.Same(Xl, Model(0x008F));   // XL v2
        Assert.Equal((1, 1), Xl.Position(9));
        Assert.Equal((3, 7), Xl.Position(31));

        Assert.Equal((4, 120, false), (Plus.DialCount, Plus.KeyPixels, Plus.FlipX));
        Assert.Equal(StreamDeckHidInputLayout.Plus, Plus.Input);
        Assert.Equal((10, 8), (Model(0x009A).KeyCount, Model(0x009A).ImageKeyCount));   // Neo: 8 display keys + 2 touch keys
        Assert.False(Model(0x0086).HasDisplay);   // Pedal
        Assert.False(Model(0x0063).Supported);    // Mini: legacy report format, not driven yet
        Assert.False(Model(0x00AA).Supported);    // Studio: not described yet
        Assert.Null(StreamDeckHidModels.Find(StreamDeckHidModels.ElgatoVendorId, 0x0070));   // an Elgato product that is not a deck
        Assert.Null(StreamDeckHidModels.Find(0x1234, 0x006C));
    }

    [Fact]
    public void Images_are_split_into_1024_byte_pages_with_the_gen2_header()
    {
        var image = Enumerable.Range(0, 2000).Select(i => (byte)(i % 251)).ToArray();
        var reports = StreamDeckHidProtocol.ImageReports(Xl, 31, image);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(1024, r.Length));
        Assert.Equal(new byte[] { 0x02, 0x07, 31, 0, 0xF8, 0x03, 0, 0 }, reports[0][..8]);   // 1016 bytes, page 0, not last
        Assert.Equal(new byte[] { 0x02, 0x07, 31, 1, 0xD8, 0x03, 1, 0 }, reports[1][..8]);   // 984 bytes, page 1, last
        Assert.Equal(image, (byte[])[.. reports[0][8..], .. reports[1][8..992]]);
        Assert.All(reports[1][992..], b => Assert.Equal(0, b));   // zero padding

        var exact = StreamDeckHidProtocol.ImageReports(Xl, 0, new byte[1016 * 2]);
        Assert.Equal(2, exact.Count);
        Assert.Equal((0, 1), (exact[0][3], exact[1][3]));

        var empty = Assert.Single(StreamDeckHidProtocol.ImageReports(Xl, 5, []));
        Assert.Equal(new byte[] { 0x02, 0x07, 5, 1, 0, 0, 0, 0 }, empty[..8]);

        Assert.Throws<ArgumentOutOfRangeException>(() => StreamDeckHidProtocol.ImageReports(Xl, 32, image));
        Assert.Throws<NotSupportedException>(() => StreamDeckHidProtocol.ImageReports(Model(0x0063), 0, image));
    }

    [Fact]
    public void Feature_reports_set_brightness_and_reset()
    {
        var brightness = StreamDeckHidProtocol.BrightnessReport(Xl, 60);
        Assert.Equal(32, brightness.Length);
        Assert.Equal(new byte[] { 0x03, 0x08, 60 }, brightness[..3]);
        Assert.All(brightness[3..], b => Assert.Equal(0, b));
        Assert.Equal(100, StreamDeckHidProtocol.BrightnessReport(Xl, 150)[2]);
        Assert.Equal(0, StreamDeckHidProtocol.BrightnessReport(Xl, -5)[2]);
        Assert.Equal(new byte[] { 0x03, 0x02, 0 }, StreamDeckHidProtocol.ResetReport(Xl)[..3]);
        var keyStream = StreamDeckHidProtocol.ResetKeyStreamReport(Xl);
        Assert.Equal(1024, keyStream.Length);
        Assert.Equal(0x02, keyStream[0]);
        Assert.All(keyStream[1..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Serial_and_firmware_are_read_from_their_offsets()
    {
        var serial = new byte[32];
        serial[0] = 0x06;
        serial[1] = 0x0C;
        "CL50K2A01783"u8.CopyTo(serial.AsSpan(2));
        Assert.Equal("CL50K2A01783", StreamDeckHidProtocol.ParseSerial(Xl, serial));

        var firmware = new byte[32];
        firmware[0] = 0x05;
        firmware[1] = 0x0C;
        firmware[2] = 0xAB;   // checksum bytes before the text
        "1.01.000"u8.CopyTo(firmware.AsSpan(6));
        firmware[20] = (byte)'x';   // data after the NUL is ignored
        Assert.Equal("1.01.000", StreamDeckHidProtocol.ParseFirmware(Xl, firmware));

        var plusSerial = new byte[32];
        "A00SA3"u8.CopyTo(plusSerial.AsSpan(5));
        Assert.Equal("A00SA3", StreamDeckHidProtocol.ParseSerial(Plus, plusSerial));
        Assert.Equal("", StreamDeckHidProtocol.ParseSerial(Xl, new byte[2]));
        Assert.Equal("AB", StreamDeckHidProtocol.ExtractString("  A\u0001B \0zz"u8));
    }

    [Fact]
    public void Key_states_are_parsed_and_changes_found()
    {
        var report = new byte[36];
        report[0] = 0x01;
        report[2] = 32;
        report[4 + 3] = 1;
        report[4 + 31] = 1;
        var keys = Assert.IsType<KeyStatesInput>(StreamDeckHidProtocol.ParseInput(Xl, report));
        Assert.Equal([3, 31], keys.Pressed.Select((p, i) => (p, i)).Where(x => x.p).Select(x => x.i));

        Assert.Null(StreamDeckHidProtocol.ParseInput(Xl, report[..20]));       // too short
        report[0] = 0x02;
        Assert.Null(StreamDeckHidProtocol.ParseInput(Xl, report));             // not an input report

        var before = new bool[32];
        before[3] = true;
        before[5] = true;
        Assert.Equal([(5, false), (31, true)], StreamDeckHidProtocol.Changes(before, keys.Pressed));
    }

    [Fact]
    public void Plus_reports_keys_dials_and_touches()
    {
        byte[] Report(params (int Index, byte Value)[] bytes)
        {
            var r = new byte[14];
            r[0] = 0x01;
            foreach (var (i, v) in bytes) r[i] = v;
            return r;
        }

        var keys = Assert.IsType<KeyStatesInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x00), (4 + 2, 1))));
        Assert.Equal([false, false, true, false, false, false, false, false], keys.Pressed);

        var turn = Assert.IsType<DialTurnInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x03), (4, 0x01), (5, 0xFF), (7, 0x03))));
        Assert.Equal([-1, 0, 3, 0], turn.Ticks);

        var press = Assert.IsType<DialPressInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x03), (4, 0x00), (8, 1))));
        Assert.Equal([false, false, false, true], press.Pressed);

        var tap = Assert.IsType<TouchInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x02), (4, 1), (6, 0x90), (7, 0x01), (8, 50))));
        Assert.Equal(new TouchInput(TouchKind.Short, 400, 50), tap);
        Assert.Equal(TouchKind.Long, Assert.IsType<TouchInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x02), (4, 2)))).Kind);
        var drag = Assert.IsType<TouchInput>(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x02), (4, 3), (6, 10), (10, 0x20), (11, 0x03), (12, 7))));
        Assert.Equal(new TouchInput(TouchKind.Drag, 10, 0, 800, 7), drag);
        Assert.Null(StreamDeckHidProtocol.ParseInput(Plus, Report((1, 0x07))));
    }

    [Fact]
    public void Keys_map_to_configured_or_default_button_ids()
    {
        Assert.Equal("hid-CL50K2A01783-5", StreamDeckHidMapping.DefaultButtonId("CL50K2A01783", 5));
        Assert.Equal("hid-AB12-dial2", StreamDeckHidMapping.DefaultButtonId("AB12", 2, dial: true));
        Assert.Equal("hid-A_B_-0", StreamDeckHidMapping.DefaultButtonId("A B/", 0));
        Assert.Equal("hid-unknown-0", StreamDeckHidMapping.DefaultButtonId("  ", 0));

        var settings = new StreamDeckHidSettings
        {
            Devices =
            [
                new StreamDeckHidDeviceConfig
                {
                    Serial = "cl50k2a01783",
                    Keys = [new() { Index = 0, ButtonId = " scene-main " }, new() { Index = 1, Title = "No id" }],
                    Dials = [new() { Index = 0, ButtonId = "volume" }],
                },
            ],
        };
        Assert.Equal("scene-main", StreamDeckHidMapping.ButtonId(settings, "CL50K2A01783", 0));   // serial match ignores case
        Assert.Equal("hid-CL50K2A01783-1", StreamDeckHidMapping.ButtonId(settings, "CL50K2A01783", 1));   // no id: default
        Assert.Equal("volume", StreamDeckHidMapping.ButtonId(settings, "CL50K2A01783", 0, dial: true));
        Assert.Equal("hid-OTHER-0", StreamDeckHidMapping.ButtonId(settings, "OTHER", 0));
    }

    [Fact]
    public void Linux_hidraw_uevent_and_ioctl_numbers()
    {
        var uevent = "DRIVER=hid-generic\nHID_ID=0003:00000FD9:0000006C\nHID_NAME=Elgato Stream Deck XL\nHID_PHYS=usb-0000:16:00.4-2/input0\nHID_UNIQ=CL50K2A01783\n";
        Assert.Equal(((ushort)0x0FD9, (ushort)0x006C, "Elgato Stream Deck XL", "CL50K2A01783"), LinuxHidTransport.ParseUevent(uevent));
        Assert.Null(LinuxHidTransport.ParseUevent("HID_ID=0005:0000046D:0000B023\n"));   // Bluetooth, not USB
        Assert.Null(LinuxHidTransport.ParseUevent("DRIVER=hid-generic\n"));

        // linux/hidraw.h: HIDIOCSFEATURE(len) _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x06, len), HIDIOCGFEATURE ... 0x07.
        Assert.Equal(0xC0204806u, LinuxHidTransport.SetFeatureRequest(32));
        Assert.Equal(0xC0204807u, LinuxHidTransport.GetFeatureRequest(32));
        Assert.Equal(0xC0114807u, LinuxHidTransport.GetFeatureRequest(17));
    }
}
