using System.Text.Json;
using Cuebot.Elgato.Common;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Tests.Support;

namespace Cuebot.Elgato.Tests;

public sealed class LightApiTests
{
    [Theory]
    [InlineData(143, 7000)]
    [InlineData(213, 4700)]
    [InlineData(344, 2900)]
    public void Mireds_convert_to_kelvin_in_50K_steps(int mired, int kelvin) => Assert.Equal(kelvin, ElgatoColor.MiredToKelvin(mired));

    [Theory]
    [InlineData(7000, 143)]
    [InlineData(4500, 222)]
    [InlineData(2900, 344)]
    [InlineData(10000, 143)]   // clamped
    [InlineData(200, 200)]     // already API units
    public void Kelvin_or_api_units_become_api_units(double value, int mired) => Assert.Equal(mired, ElgatoColor.ToMired(value));

    [Fact]
    public void Out_of_range_temperature_is_rejected() => Assert.Throws<ArgumentException>(() => ElgatoColor.ToMired(50));

    [Fact]
    public void Parses_key_light_and_light_strip_answers()
    {
        using var keyLight = JsonDocument.Parse("""{"numberOfLights":1,"lights":[{"on":1,"brightness":23,"temperature":200}]}""");
        var k = Assert.Single(ElgatoLightClient.ParseLights(keyLight.RootElement));
        Assert.Equal(new LightState(true, 23, 200, null, null), k);
        Assert.Equal(5000, k.Kelvin);

        using var strip = JsonDocument.Parse("""{"numberOfLights":1,"lights":[{"on":0,"hue":332.0,"saturation":75.5,"brightness":40}]}""");
        var s = Assert.Single(ElgatoLightClient.ParseLights(strip.RootElement));
        Assert.False(s.On);
        Assert.Null(s.Temperature);
        Assert.Equal(332.0, s.Hue);
        Assert.Equal(75.5, s.Saturation);
    }

    [Fact]
    public void Parses_accessory_info()
    {
        using var doc = JsonDocument.Parse("""
            {"productName":"Elgato Key Light Mini","hardwareBoardType":202,"firmwareBuildNumber":206,"firmwareVersion":"1.0.4",
             "serialNumber":"GW40K1A00809","displayName":"Desk","features":["lights"],"wifi-info":{"ssid":"x","macAddress":"3C:6A:9D:00:00:01"}}
            """);
        var info = ElgatoLightClient.ParseAccessoryInfo(doc.RootElement);
        Assert.Equal("Elgato Key Light Mini", info.ProductName);
        Assert.Equal("GW40K1A00809", info.SerialNumber);
        Assert.Equal("Desk", info.DisplayName);
        Assert.Equal("3C:6A:9D:00:00:01", info.MacAddress);
        Assert.Equal(["lights"], info.Features);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 9123)]
    [InlineData("192.168.1.20:9000", "192.168.1.20", 9000)]
    [InlineData("http://keylight.local:9123/", "keylight.local", 9123)]
    [InlineData("keylight.local", "keylight.local", 9123)]
    [InlineData("[fe80::1]:9123", "fe80::1", 9123)]
    [InlineData("fe80::1", "fe80::1", 9123)]
    public void Parses_manual_addresses(string text, string host, int port) =>
        Assert.Equal((host, port), ElgatoLightsService.ParseAddress(text));
}

public sealed class LightsServiceTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Adding_a_light_reads_it_fires_device_added_once_and_persists_it()
    {
        await using var server = new FakeLightServer { DisplayName = "Left key", On = true, Brightness = 40 };
        await using var lights = _env.Lights();

        var light = await lights.AddLightAsync($"127.0.0.1:{server.Port}");
        Assert.Equal(server.Serial, light.Id);
        Assert.Equal("Left key", light.Name);
        Assert.True(light.IsOnline);
        Assert.Equal(40, light.State!.Brightness);
        Assert.NotNull(light.SettingsJson);

        var added = Assert.Single(_env.Of(LightEvents.DeviceAdded));
        Assert.Equal("Left key", added.Args["deviceName"]);
        Assert.Equal(server.Serial, added.Args["deviceId"]);
        Assert.Equal("Elgato Key Light", added.Args["productName"]);
        Assert.Equal(true, added.Args["on"]);
        Assert.Equal("On", added.Args["powerState"]);
        Assert.Equal(4700, added.Args["temperature"]);

        await lights.AddLightAsync($"127.0.0.1:{server.Port}");
        Assert.Single(_env.Of(LightEvents.DeviceAdded));
        var known = Assert.Single(_env.Settings.Value.Lights.Devices);
        Assert.True(known.Manual);
        Assert.Equal(server.Port, known.Port);
        Assert.Empty(_env.Of(LightEvents.BrightnessChanged));
    }

    [Fact]
    public async Task Polling_turns_changes_made_elsewhere_into_triggers()
    {
        await using var server = new FakeLightServer();
        _env.Settings.Value.Lights.InfoIntervalSeconds = 1;
        await using var lights = _env.Lights();
        await lights.AddLightAsync($"127.0.0.1:{server.Port}");
        await lights.StartPollingAsync();
        await Wait.UntilAsync(() => lights.State == Core.ConnectionState.Connected, "connected status");
        Assert.Equal("1 light reachable", lights.StatusMessage);

        server.On = true;
        server.Brightness = 55;
        server.Temperature = 250;   // 4000 K
        await Wait.UntilAsync(() => _env.Of(LightEvents.TemperatureChanged).Length > 0, "temperature event");

        var power = Assert.Single(_env.Of(LightEvents.DevicePowerStateChanged));
        Assert.Equal(true, power.Args["on"]);
        Assert.Equal(false, power.Args["oldOn"]);
        Assert.Single(_env.Of(LightEvents.DevicePoweredOn));
        Assert.Empty(_env.Of(LightEvents.DevicePoweredOff));
        var brightness = Assert.Single(_env.Of(LightEvents.BrightnessChanged));
        Assert.Equal(55, brightness.Args["brightness"]);
        Assert.Equal(20, brightness.Args["oldBrightness"]);
        var temperature = Assert.Single(_env.Of(LightEvents.TemperatureChanged));
        Assert.Equal(4000, temperature.Args["temperature"]);
        Assert.Equal(4700, temperature.Args["oldTemperature"]);
        Assert.Equal(250, temperature.Args["temperatureRaw"]);

        server.DisplayName = "Key Light Right";
        server.PowerOnBrightness = 60;
        await Wait.UntilAsync(() => _env.Of(LightEvents.DeviceRenamed).Length > 0 && _env.Of(LightEvents.DeviceConfigurationChanged).Length > 0,
            "rename and configuration events");
        var renamed = Assert.Single(_env.Of(LightEvents.DeviceRenamed));
        Assert.Equal("Key Light Right", renamed.Args["deviceName"]);
        Assert.Equal("Elgato Key Light", renamed.Args["oldDeviceName"]);
        var config = Assert.Single(_env.Of(LightEvents.DeviceConfigurationChanged));
        Assert.Contains("\"powerOnBrightness\":60", (string)config.Args["settings"]!);
        Assert.Contains("\"powerOnBrightness\":20", (string)config.Args["oldSettings"]!);

        server.On = false;
        await Wait.UntilAsync(() => _env.Of(LightEvents.DevicePoweredOff).Length > 0, "powered off");
        Assert.Equal(2, _env.Of(LightEvents.DevicePowerStateChanged).Length);
        await lights.StopAsync();
        Assert.Equal(Core.ConnectionState.Disconnected, lights.State);
    }

    [Fact]
    public async Task Commands_put_only_the_changed_fields_and_publish_the_result()
    {
        await using var server = new FakeLightServer { Brightness = 30 };
        await using var lights = _env.Lights();
        await lights.AddLightAsync($"127.0.0.1:{server.Port}");
        while (server.Bodies.TryDequeue(out _)) { }

        await lights.SetPowerAsync("Elgato Key Light", SwitchMode.Toggle);
        Assert.True(server.On);
        Assert.Equal("""{"numberOfLights":1,"lights":[{"on":1}]}""", Assert.Single(server.Bodies));

        await lights.SetBrightnessAsync("all", 15, adjust: true);
        Assert.Equal(45, server.Brightness);
        await lights.SetBrightnessAsync(server.Serial, 150);
        Assert.Equal(100, server.Brightness);

        await lights.SetTemperatureAsync("127.0.0.1", 4500);
        Assert.Equal(222, server.Temperature);
        await lights.SetTemperatureAsync("", -500, adjust: true);
        Assert.Equal(250, server.Temperature);   // 4500 - 500 = 4000 K

        await lights.IdentifyAsync("");
        Assert.Equal(1, server.Identifies);

        Assert.Single(_env.Of(LightEvents.DevicePoweredOn));
        Assert.Equal([45, 100], _env.Of(LightEvents.BrightnessChanged).Select(e => (int)e.Args["brightness"]!));
        Assert.Equal(2, _env.Of(LightEvents.TemperatureChanged).Length);
        Assert.Throws<ArgumentException>(() => lights.Resolve("Nope"));
    }

    [Fact]
    public async Task A_light_that_stops_answering_goes_offline_and_comes_back()
    {
        await using var server = new FakeLightServer();
        await using var lights = _env.Lights();
        await lights.AddLightAsync($"127.0.0.1:{server.Port}");
        await lights.StartPollingAsync();

        server.Down = true;
        await Wait.UntilAsync(() => _env.Of(LightEvents.DeviceOffline).Length > 0, "offline event");
        Assert.False(lights.Lights[0].IsOnline);
        await Wait.UntilAsync(() => lights.State == Core.ConnectionState.Connecting, "retrying status");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lights.SetPowerAsync("", SwitchMode.On));

        server.Down = false;
        await Wait.UntilAsync(() => _env.Of(LightEvents.DeviceOnline).Length > 0, "online event");
        await Wait.UntilAsync(() => lights.State == Core.ConnectionState.Connected, "connected again");
        Assert.Empty(_env.Of(LightEvents.DeviceRemoved));
    }

    [Fact]
    public async Task Known_lights_are_restored_from_settings_without_a_new_device_added()
    {
        await using var server = new FakeLightServer();
        await using (var first = _env.Lights()) await first.AddLightAsync($"127.0.0.1:{server.Port}");
        while (_env.Published.TryDequeue(out _)) { }

        await using var second = _env.Lights();
        var light = Assert.Single(second.Lights);
        Assert.False(light.IsOnline);
        await second.RefreshAsync(includeInfo: true);
        Assert.True(light.IsOnline);
        Assert.Empty(_env.Of(LightEvents.DeviceAdded));
        Assert.Empty(_env.Of(LightEvents.DeviceOnline));   // first answer after start is not a "came back"
    }

    [Fact]
    public async Task Discovery_adds_lights_from_mdns_answers_and_respects_removal()
    {
        await using var server = new FakeLightServer(serial: "BW33J1A01234", product: "Elgato Light Strip") { Temperature = null, Hue = 30, Saturation = 80 };
        _env.Mdns.Answers.Add(new MdnsPacket(ElgatoAnswer("Elgato Light Strip 1A2B", "elgato-light-strip-1a2b.local", server.Port, "127.0.0.1"),
            System.Net.IPAddress.Loopback));
        await using var lights = _env.Lights();

        var added = await lights.DiscoverAsync();
        var light = Assert.Single(added);
        Assert.Equal("BW33J1A01234", light.Id);
        Assert.False(light.IsManual);
        Assert.Equal(Mdns.BuildQuery(Mdns.ElgatoService), Assert.Single(_env.Mdns.Queries));
        Assert.Single(_env.Of(LightEvents.DeviceAdded));

        await lights.SetColorAsync("", 200, 50);
        Assert.Equal(200, server.Hue);
        var color = Assert.Single(_env.Of(LightEvents.ColorChanged));
        Assert.Equal(30.0, color.Args["oldHue"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lights.SetTemperatureAsync("", 5000));

        Assert.Empty(await lights.DiscoverAsync());   // already known at that address

        lights.RemoveLights("Elgato Light Strip");
        Assert.Single(_env.Of(LightEvents.DeviceRemoved));
        Assert.Empty(lights.Lights);
        Assert.Contains("BW33J1A01234", _env.Settings.Value.Lights.Ignored);
        Assert.Empty(await lights.DiscoverAsync());

        await lights.AddLightAsync($"127.0.0.1:{server.Port}");   // by hand: un-ignored
        Assert.Empty(_env.Settings.Value.Lights.Ignored);
        Assert.Equal(2, _env.Of(LightEvents.DeviceAdded).Length);
    }

    [Fact]
    public async Task Discovery_follows_a_known_light_to_its_new_address()
    {
        await using var server = new FakeLightServer();
        _env.Settings.Value.Lights.Devices.Add(new KnownLight { Id = server.Serial, Address = "127.0.0.1", Port = 1, Name = "Old" });
        _env.Mdns.Answers.Add(new MdnsPacket(ElgatoAnswer("Elgato Key Light 7F3A", "elgato-key-light-7f3a.local", server.Port, "127.0.0.1"),
            System.Net.IPAddress.Loopback));
        await using var lights = _env.Lights();

        Assert.Empty(await lights.DiscoverAsync());
        var light = Assert.Single(lights.Lights);
        Assert.Equal(server.Port, light.Port);
        Assert.True(light.IsOnline);
        Assert.Empty(_env.Of(LightEvents.DeviceAdded));
    }

    internal static byte[] ElgatoAnswer(string instance, string host, int port, string ip) => new DnsPacketBuilder()
        .Ptr("_elg._tcp.local", $"{instance}._elg._tcp.local")
        .Srv($"{instance}._elg._tcp.local", port, host)
        .Txt($"{instance}._elg._tcp.local", "mf=Elgato", "dt=53", "id=3C:6A:9D:14:7F:3A", "md=Elgato Key Light 20GAK9901", "pv=1.0")
        .A(host, ip)
        .Build();
}
