using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Server;
using Cuebot.StreamDeck;
using Cuebot.StreamDeck.Hid;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit.Abstractions;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against a real Stream Deck on USB, driven directly over HID (no Elgato software). See
/// docs/LIVE_TESTING.md: CUEBOT_LIVE_STREAMDECK_HID=1 reads the serial and firmware, changes the brightness, draws a
/// numbered test image on every key, runs the direct USB service end to end (connect events, sub-actions by button id)
/// and leaves the deck reset at 60 % brightness. CUEBOT_LIVE_STREAMDECK_HID_PRESS=1 also waits 30 s for a key press.
/// Linux only for now; close StreamController / OpenDeck first (they take the deck over).
/// </summary>
public sealed class StreamDeckHidLiveTests(ITestOutputHelper output)
{
    private const int FinalBrightness = 60;

    [LiveServiceFact("CUEBOT_LIVE_STREAMDECK_HID", flag: true)]
    public async Task Deck_reports_identity_takes_brightness_and_draws_every_key()
    {
        var (info, model) = FindDeck();
        var transport = new LinuxHidTransport();
        using var deck = new StreamDeckHidDeck(transport.Open(info), model);
        try
        {
            var serial = deck.ReadSerial();
            var firmware = deck.ReadFirmware();
            output.WriteLine($"{model.Name} on {info.Path} (USB {info.UsbPort}): serial {serial}, firmware {firmware}");
            Assert.False(string.IsNullOrWhiteSpace(serial));
            Assert.False(string.IsNullOrWhiteSpace(firmware));
            if (info.Serial.Length > 0) Assert.Equal(info.Serial, serial);   // sysfs HID_UNIQ and feature report 0x06 agree

            deck.ResetKeyStream();
            foreach (var percent in new[] { 20, 100, 70 })
            {
                deck.SetBrightness(percent);
                await Task.Delay(300);
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var key = 0; key < model.ImageKeyCount; key++)
                deck.SetKeyImage(key, StreamDeckKeyRenderer.Render(model, new KeyFace($"{key}", TestPicture(key, model.ImageKeyCount))));
            output.WriteLine($"Drew {model.ImageKeyCount} keys in {clock.ElapsedMilliseconds} ms");
            await Task.Delay(1500);   // long enough to see

            if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMDECK_HID_PRESS") == "1")
            {
                output.WriteLine("Press any key on the deck within 30 seconds...");
                var deadline = DateTime.UtcNow.AddSeconds(30);
                int? pressed = null;
                while (pressed is null && DateTime.UtcNow < deadline)
                {
                    if (deck.ReadInput(250) is KeyStatesInput keys && Array.IndexOf(keys.Pressed, true) is var i and >= 0) pressed = i;
                }
                Assert.NotNull(pressed);
                output.WriteLine($"Key {pressed} pressed");
            }
        }
        finally
        {
            deck.Reset();
            deck.SetBrightness(FinalBrightness);
        }
    }

    [LiveServiceFact("CUEBOT_LIVE_STREAMDECK_HID", flag: true)]
    public async Task Service_connects_fires_triggers_and_applies_sub_actions()
    {
        var (info, model) = FindDeck();
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "streamdeck-hid");
        Directory.CreateDirectory(dataDirectory);
        var settingsFile = Path.Combine(dataDirectory, "streamdeck-usb.json");
        if (File.Exists(settingsFile)) File.Delete(settingsFile);

        var events = new ConcurrentQueue<BotEvent>();
        string serial;
        await using (var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddServers().AddStreamDeck().BuildServiceProvider())
        {
            services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
            var usb = services.GetRequiredService<StreamDeckHidService>();
            var hub = services.GetRequiredService<StreamDeckHub>();
            Assert.Equal(FinalBrightness, usb.Settings.Brightness);
            await usb.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (usb.State != ConnectionState.Connected && DateTime.UtcNow < deadline) await Task.Delay(50);
            output.WriteLine($"Status: {usb.State} - {usb.StatusMessage}");
            Assert.Equal(ConnectionState.Connected, usb.State);

            var device = Assert.Single(usb.Devices, d => d.Connected && d.Path == info.Path);
            serial = device.Serial;
            var connected = Assert.Single(events, e => e.Type == StreamDeckHub.ConnectedEvent);
            Assert.Equal(serial, connected.Args["deviceId"]);
            Assert.Contains(events, e => e.Type == StreamDeckHub.InfoEvent && Equals(e.Args["device0.type"], model.Name));

            // The sub-actions reach the deck through the hub, by the default button id of key 0 and the last key.
            var first = StreamDeckHidMapping.DefaultButtonId(serial, 0);
            var last = StreamDeckHidMapping.DefaultButtonId(serial, model.ImageKeyCount - 1);
            Assert.Contains(first, hub.KnownButtonIds);
            Assert.Equal(1, await hub.SendCommandAsync(new StreamDeckCommand("setTitle", first, Title: "Cuebot\nUSB")));
            Assert.Equal(1, await hub.SendCommandAsync(new StreamDeckCommand("setImage", last,
                Image: "data:image/png;base64," + Convert.ToBase64String(TestPicture(3, 8)))));
            Assert.Equal(1, await hub.SendCommandAsync(new StreamDeckCommand("showOk", last)));
            await Task.Delay(2000);
            Assert.Equal(0, await hub.SendCommandAsync(new StreamDeckCommand("setTitle", "no-such-key", Title: "x")));

            // Turning direct USB mode off releases the deck (reset, default brightness) and fires Disconnected.
            usb.SetEnabled(false);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!events.Any(e => e.Type == StreamDeckHub.DisconnectedEvent) && DateTime.UtcNow < deadline) await Task.Delay(50);
            var disconnected = Assert.Single(events, e => e.Type == StreamDeckHub.DisconnectedEvent);
            Assert.Equal(serial, disconnected.Args["deviceId"]);
            Assert.Equal(ConnectionState.Disconnected, usb.State);
            output.WriteLine($"Turned off: {usb.StatusMessage}");
        }

        output.WriteLine($"Released cleanly; the deck {serial} is reset at {FinalBrightness} %");
    }

    private (HidDeviceInfo Info, StreamDeckHidModel Model) FindDeck()
    {
        Assert.True(OperatingSystem.IsLinux(), "Direct USB mode is Linux-only for now");
        var all = new LinuxHidTransport().Enumerate(StreamDeckHidModels.ElgatoVendorId);
        foreach (var d in all)
            output.WriteLine($"Found {d.Name} {d.VendorId:x4}:{d.ProductId:x4} serial {d.Serial} at {d.Path ?? "(no hidraw: taken over by another program)"}");
        var usable = all.Select(d => (Info: d, Model: StreamDeckHidModels.Find(d.VendorId, d.ProductId)))
            .FirstOrDefault(d => d.Model is { Supported: true } && d.Info.HasHidInterface);
        Assert.True(usable.Model is not null, all.Count == 0
            ? "No Stream Deck on USB"
            : "No usable Stream Deck: close StreamController/OpenDeck (they detach the kernel HID driver) and replug it");
        return (usable.Info, usable.Model!);
    }

    /// <summary>A PNG with a hue per key, so every key looks different and orientation mistakes are visible.</summary>
    private static byte[] TestPicture(int index, int count)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColor.FromHsv(360f * index / Math.Max(1, count), 80, 70));
            using var corner = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, 16, 16, corner);   // top-left marker: must appear top-left on the deck
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
