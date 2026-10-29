using Cuebot.Core;
using Cuebot.Core.Storage;
using Cuebot.StreamDeck.Hid;

namespace Cuebot.StreamDeck.Tests;

/// <summary>Direct USB mode end to end with a fake HID transport: hot-plug, triggers, sub-actions, status.</summary>
public sealed class HidServiceTests : IAsyncDisposable
{
    private const string Serial = "CL50K2A01783";
    private readonly Harness _h = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-hid-service-").FullName;
    private readonly FakeHidTransport _transport = new();
    private readonly JsonFile<StreamDeckHidSettings> _settings;
    private StreamDeckHidService? _service;

    public HidServiceTests() => _settings = new JsonFile<StreamDeckHidSettings>(Path.Combine(_dir, "streamdeck-usb.json"), _h.Log);

    private StreamDeckHidService Service => _service ??= new StreamDeckHidService(_settings, _h.Bus, _h.Log, _h.Hub, _transport)
    {
        OverlayDuration = TimeSpan.FromMilliseconds(100),
    };

    public async ValueTask DisposeAsync()
    {
        if (_service is not null) await _service.DisposeAsync();
        _settings.Dispose();
        await _h.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), what);
    }

    private FakeHidDevice ConnectXl()
    {
        var device = _transport.Add(FakeHidTransport.Xl());
        Service.Scan();
        return device;
    }

    [Fact]
    public void A_plugged_in_xl_is_opened_painted_and_announced()
    {
        var device = ConnectXl();

        Assert.Equal(ConnectionState.Connected, Service.State);
        Assert.Equal($"Stream Deck XL ({Serial}) on USB", Service.StatusMessage);
        var info = Assert.Single(Service.Devices);
        Assert.Equal((Serial, "1.01.000", "/dev/hidraw0", true), (info.Serial, info.Firmware, info.Path, info.Connected));

        Assert.Equal(new byte[] { 0x03, 0x08, 60 }, Assert.Single(device.Features)[..3]);   // default brightness
        Assert.Equal(new byte[1023], device.Written.First()[1..]);                          // key stream reset first
        Assert.Equal(32, device.ImageCount);                                                 // every key painted (blank)

        var connected = Assert.Single(_h.EventsOf(StreamDeckHub.ConnectedEvent));
        Assert.Equal(("usb:" + Serial, Serial, "Stream Deck XL", "usb", "1.01.000"),
            (connected.Args["instanceId"], connected.Args["deviceId"], connected.Args["deviceType"], connected.Args["connection"], connected.Args["firmware"]));
        var infoEvent = Assert.Single(_h.EventsOf(StreamDeckHub.InfoEvent));
        Assert.Equal(("Stream Deck XL", 8L, 4L, 2L), (infoEvent.Args["device0.type"], infoEvent.Args["data.devices[0].size.columns"],
            infoEvent.Args["data.devices[0].size.rows"], infoEvent.Args["data.devices[0].type"]));

        Service.Scan();   // nothing changes on a second scan
        Assert.Equal(1, _transport.Opens);
        Assert.Single(_h.EventsOf(StreamDeckHub.ConnectedEvent));
        Assert.Contains($"hid-{Serial}-31", _h.Hub.KnownButtonIds);
    }

    [Fact]
    public async Task Key_presses_fire_the_stream_deck_action_trigger_with_the_button_id()
    {
        _settings.Value.Devices.Add(new StreamDeckHidDeviceConfig { Serial = Serial, Name = "Desk XL", Keys = [new() { Index = 9, ButtonId = "go-live" }] });
        var device = ConnectXl();

        device.PushKeys(32, 9);
        device.PushKeys(32, 9, 0);
        device.PushKeys(32);
        await Eventually(() => _h.EventsOf(StreamDeckHub.ActionEvent).Length == 4, "four key events");

        var events = _h.EventsOf(StreamDeckHub.ActionEvent);
        Assert.Equal(["go-live/keyDown", $"hid-{Serial}-0/keyDown", $"hid-{Serial}-0/keyUp", "go-live/keyUp"],
            events.Select(e => $"{e.Args["buttonId"]}/{e.Args["buttonEvent"]}"));
        var down = events[0];
        Assert.Equal(("Keypad", false, 1, 1, 9, "Desk XL", Serial, "Stream Deck XL", 0),
            (down.Args["controller"], down.Args["isDial"], down.Args["row"], down.Args["column"], down.Args["keyIndex"], down.Args["deviceName"],
                down.Args["deviceId"], down.Args["deviceType"], down.Args["state"]));
        Assert.Equal($"usb:{Serial}:key9", down.Args["context"]);

        // The existing trigger matches by button id and event exactly as for plugin keys.
        Assert.True(StreamDeckTriggers.MatchesAction(down, "other, GO-LIVE", "Key down"));
        Assert.False(StreamDeckTriggers.MatchesAction(events[3], "go-live", "Key down"));
        Assert.True(StreamDeckTriggers.MatchesAction(events[3], "go-live", "Key up"));
    }

    [Fact]
    public async Task Sub_actions_reach_usb_keys_by_button_id_and_are_remembered()
    {
        _settings.Value.Devices.Add(new StreamDeckHidDeviceConfig
        {
            Serial = Serial,
            Keys = [new() { Index = 3, ButtonId = "mic", Title = "Mic" }, new() { Index = 4, ButtonId = "mic" }],
        });
        var device = ConnectXl();
        Assert.Equal("Mic", (await Service.FaceAsync(Serial, 3, KeyOverlay.None)).Title);   // the key's own title

        var before = device.ImageCountFor(3);
        Assert.Equal(2, await _h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", "MIC", Title: "Muted", State: 1)));
        Assert.Equal(2, await _h.Hub.SendCommandAsync(new StreamDeckCommand("setState", "mic", State: 1)));
        Assert.Equal(before + 2, device.ImageCountFor(3));   // repainted once per command
        Assert.Equal(3, device.ImageCountFor(4));            // painted on open, then twice
        Assert.Equal("Muted", (await Service.FaceAsync(Serial, 3, KeyOverlay.None)).Title);
        Assert.Equal("Muted", (await Service.FaceAsync(Serial, 4, KeyOverlay.None)).Title);

        Assert.Equal(2, await _h.Hub.SendCommandAsync(new StreamDeckCommand("toggleState", "mic")));
        Assert.Equal("Mic", (await Service.FaceAsync(Serial, 3, KeyOverlay.None)).Title);   // state 0 has no title set: the key's own
        Assert.Equal("", (await Service.FaceAsync(Serial, 4, KeyOverlay.None)).Title);

        var png = HidPlatformTests.MarkerPicture();
        await _h.Hub.SendCommandAsync(new StreamDeckCommand("setImage", "mic", Image: "data:image/png;base64," + Convert.ToBase64String(png)));
        Assert.Equal(png, (await Service.FaceAsync(Serial, 3, KeyOverlay.None)).Image);
        await _h.Hub.SendCommandAsync(new StreamDeckCommand("setImage", "mic", Image: ""));   // empty restores
        Assert.Null((await Service.FaceAsync(Serial, 3, KeyOverlay.None)).Image);

        await _h.Hub.SendCommandAsync(new StreamDeckCommand("setValue", $"hid-{Serial}-0", Value: "42"));
        Assert.Equal("42", (await Service.FaceAsync(Serial, 0, KeyOverlay.None)).Title);

        // Ids nobody has: 0 keys, and the plugin warning mentions both kinds of decks.
        Assert.Equal(0, await _h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", "nothing", Title: "x")));
        Assert.Contains(_h.Log.Recent(), l => l.Message.Contains("no USB Stream Deck key has that id", StringComparison.Ordinal));

        // A deck plugged in later shows what was set before.
        await _h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", "hid-OTHER-1", Title: "Later"));
        _transport.Add(FakeHidTransport.Xl("OTHER", "/dev/hidraw3"));
        Service.Scan();
        Assert.Equal(2, Service.Devices.Count);
        Assert.Equal("Later", (await Service.FaceAsync("OTHER", 1, KeyOverlay.None)).Title);
    }

    [Fact]
    public async Task Show_ok_flashes_and_then_the_key_is_repainted()
    {
        var device = ConnectXl();
        var before = device.ImageCountFor(5);
        Assert.Equal(1, await _h.Hub.SendCommandAsync(new StreamDeckCommand("showOk", $"hid-{Serial}-5")));
        Assert.Equal(before + 1, device.ImageCountFor(5));
        await Eventually(() => device.ImageCountFor(5) == before + 2, "repainted without the overlay");
    }

    [Fact]
    public async Task Unplugging_fires_disconnected_and_replugging_reconnects()
    {
        var device = ConnectXl();
        device.Unplug();
        _transport.Remove("/dev/hidraw0");
        await Eventually(() => _h.EventsOf(StreamDeckHub.DisconnectedEvent).Length == 1, "disconnected");
        Assert.Equal(Serial, _h.EventsOf(StreamDeckHub.DisconnectedEvent)[0].Args["deviceId"]);
        Service.Scan();
        Assert.Equal(ConnectionState.Disconnected, Service.State);
        Assert.Equal("No Stream Deck on USB", Service.StatusMessage);
        Assert.True(device.Disposed);

        _transport.Handles.Remove("/dev/hidraw0");
        ConnectXl();
        Assert.Equal(ConnectionState.Connected, Service.State);
        Assert.Equal(2, _h.EventsOf(StreamDeckHub.ConnectedEvent).Length);
    }

    [Fact]
    public async Task A_deck_that_vanishes_from_the_scan_is_closed()
    {
        var device = ConnectXl();
        _transport.Remove("/dev/hidraw0");
        Service.Scan();
        Assert.Empty(Service.Devices);
        await Eventually(() => _h.EventsOf(StreamDeckHub.DisconnectedEvent).Length == 1 && device.Disposed, "closed and announced");
    }

    [Fact]
    public void Decks_that_cannot_be_used_are_explained_in_the_status()
    {
        // On USB without hidraw: StreamController (libusb) detached the kernel driver.
        _transport.Add(new HidDeviceInfo(null, 0x0FD9, 0x006C, Serial, "Stream Deck XL", "7-2"));
        Service.Scan();
        Assert.Equal(ConnectionState.Error, Service.State);
        Assert.Contains("another program has taken it over", Service.StatusMessage);
        Assert.Contains("StreamController", Service.StatusMessage);
        Assert.False(Assert.Single(Service.Devices).Connected);
        Assert.Contains(_h.Log.Recent(), l => l.Source == StreamDeckHidService.LogSource && l.Message.Contains("taken it over", StringComparison.Ordinal));

        // Held open by another program, no permission, unsupported model, an Elgato product that is no deck.
        _transport.Devices.Clear();
        _transport.Add(FakeHidTransport.Xl("BUSY1", "/dev/hidraw1"));
        _transport.OpenErrors["/dev/hidraw1"] = new HidOpenException(HidOpenError.Busy, "/dev/hidraw1 is in use by streamcontroller (pid 7)");
        _transport.Add(FakeHidTransport.Xl("PERM1", "/dev/hidraw2"));
        _transport.OpenErrors["/dev/hidraw2"] = new HidOpenException(HidOpenError.PermissionDenied, "no permission to open /dev/hidraw2 (install the udev rule)");
        _transport.Add(new HidDeviceInfo("/dev/hidraw4", 0x0FD9, 0x0063, "MINI1", "Stream Deck Mini"));
        _transport.Add(new HidDeviceInfo("/dev/hidraw5", 0x0FD9, 0x0070, "CAM1", "Elgato Facecam"));
        Service.Scan();
        Assert.Equal(3, Service.Devices.Count);   // the earlier problem is gone with its device; the camera is ignored
        Assert.Contains("in use by streamcontroller (pid 7)", Service.StatusMessage);
        Assert.Contains("udev rule", Service.StatusMessage);
        Assert.Contains("Stream Deck Mini · serial MINI1: not supported in direct USB mode yet", Service.StatusMessage);
        Assert.Equal(ConnectionState.Error, Service.State);

        // One good deck: connected, problems still listed.
        _transport.Add(FakeHidTransport.Xl());
        Service.Scan();
        Assert.Equal(ConnectionState.Connected, Service.State);
        Assert.StartsWith($"Stream Deck XL ({Serial}) on USB; ", Service.StatusMessage);
    }

    [Fact]
    public async Task Turning_off_releases_and_blanks_the_deck()
    {
        var device = ConnectXl();
        Service.SetEnabled(false);
        Service.Scan();
        Assert.Equal(ConnectionState.Disconnected, Service.State);
        Assert.Equal("Direct USB mode is off", Service.StatusMessage);
        Assert.Contains(device.Features, f => f[0] == 0x03 && f[1] == 0x02);   // reset
        Assert.Single(_h.EventsOf(StreamDeckHub.DisconnectedEvent));
        Assert.True(device.Disposed);
        _settings.Flush();
        using (var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "streamdeck-usb.json"))))
            Assert.False(saved.RootElement.EnumerateObject().First(p => p.NameEquals("enabled") || p.NameEquals("Enabled")).Value.GetBoolean());

        Service.Scan();
        Assert.Equal(1, _transport.Opens);   // stays closed
    }

    [Fact]
    public async Task Disposing_resets_the_deck_and_restores_the_brightness()
    {
        _settings.Value.Brightness = 35;
        var device = ConnectXl();
        await Service.DisposeAsync();
        var features = device.Features.ToArray();
        Assert.Equal(new byte[] { 0x03, 0x02 }, features[^2][..2]);
        Assert.Equal(new byte[] { 0x03, 0x08, 35 }, features[^1][..3]);
        Assert.True(device.Disposed);
        Assert.Empty(_h.EventsOf(StreamDeckHub.DisconnectedEvent));   // shutting down runs no actions
    }

    [Fact]
    public async Task Settings_changes_repaint_and_set_brightness()
    {
        var device = ConnectXl();
        Service.SetBrightness(120);
        Assert.Equal(100, Service.Settings.Brightness);
        Assert.Equal(new byte[] { 0x03, 0x08, 100 }, device.Features.Last()[..3]);

        var before = device.ImageCount;
        await Service.UpdateDeviceAsync(Serial, d =>
        {
            d.Name = "Left deck";
            d.Keys.Add(new StreamDeckHidKeyConfig { Index = 2, ButtonId = "scene" });
            d.Keys.Add(new StreamDeckHidKeyConfig { Index = 6 });   // empty entries are dropped
        });
        Assert.Equal(before + 32, device.ImageCount);
        var config = Assert.Single(Service.Settings.Devices);
        Assert.Equal("scene", Assert.Single(config.Keys).ButtonId);
        Assert.Equal("Left deck", Assert.Single(Service.Devices).Name);
        Assert.Contains("scene", _h.Hub.KnownButtonIds);
    }

    [Fact]
    public async Task Stream_deck_plus_dials_and_touch_strip_fire_dial_events()
    {
        var device = _transport.Add(new HidDeviceInfo("/dev/hidraw6", 0x0FD9, 0x0084, "PLUS1", "Stream Deck +"), new FakeHidDevice("PLUS1"));
        Service.Scan();
        Assert.Equal(ConnectionState.Connected, Service.State);

        byte[] Report(params (int Index, byte Value)[] bytes)
        {
            var r = new byte[14];
            r[0] = 0x01;
            foreach (var (i, v) in bytes) r[i] = v;
            return r;
        }

        device.Push(Report((1, 0x03), (4, 0x00), (6, 1)));                   // dial 1 pressed
        device.Push(Report((1, 0x03), (4, 0x01), (6, 0xFE)));                // dial 1 turned -2
        device.Push(Report((1, 0x03), (4, 0x00)));                           // released
        device.Push(Report((1, 0x02), (4, 2), (6, 0x58), (7, 0x02), (8, 40)));   // long touch at x=600: segment 3
        await Eventually(() => _h.EventsOf(StreamDeckHub.ActionEvent).Length == 4, "four dial events");

        var events = _h.EventsOf(StreamDeckHub.ActionEvent);
        Assert.Equal(["hid-PLUS1-dial1/dialDown", "hid-PLUS1-dial1/dialRotate", "hid-PLUS1-dial1/dialUp", "hid-PLUS1-dial3/touchTap"],
            events.Select(e => $"{e.Args["buttonId"]}/{e.Args["buttonEvent"]}"));
        Assert.Equal(("Encoder", true, -2, true), (events[1].Args["controller"], events[1].Args["isDial"], events[1].Args["ticks"], events[1].Args["pressed"]));
        Assert.Equal((600, 40, true), (events[3].Args["tapX"], events[3].Args["tapY"], events[3].Args["hold"]));
        Assert.True(StreamDeckTriggers.MatchesAction(events[1], "hid-PLUS1-dial1", "Dial rotate"));
        Assert.True(StreamDeckTriggers.MatchesAction(events[3], "", "Any key or dial press"));
    }

    [Fact]
    public void Without_a_hid_backend_the_status_says_why()
    {
        _transport.UnavailableReason = "Direct USB control of a Stream Deck is Linux-only for now";
        _transport.Add(FakeHidTransport.Xl());
        Service.Scan();
        Assert.Equal(ConnectionState.Disconnected, Service.State);
        Assert.Contains("Linux-only", Service.StatusMessage);
        Assert.Equal(0, _transport.Opens);
    }

    [Fact]
    public async Task Start_scans_in_the_background_and_never_throws()
    {
        _transport.Add(FakeHidTransport.Xl());
        _settings.Value.ScanIntervalMs = 250;
        await Service.StartAsync();
        await Eventually(() => Service.State == ConnectionState.Connected, "connected by the scan loop");
        _transport.Remove("/dev/hidraw0");
        await Eventually(() => Service.State == ConnectionState.Disconnected, "noticed the unplug by scanning");
    }
}
