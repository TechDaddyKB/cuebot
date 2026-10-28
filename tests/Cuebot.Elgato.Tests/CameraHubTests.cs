using System.Buffers.Binary;
using System.Text;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Common;
using Cuebot.Elgato.Tests.Support;

namespace Cuebot.Elgato.Tests;

public sealed class CameraHubServiceTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly FakeWebcamBackend _backend = new();

    public void Dispose() => _env.Dispose();

    private CameraHubService Create() => new(_env.Settings, _env.Bus, _env.Log, _backend);

    [Fact]
    public async Task Connect_lists_webcams_and_publishes_connected()
    {
        await using var hub = Create();
        await hub.ConnectAsync();
        Assert.True(hub.IsConnected);
        Assert.Equal("Elgato Facecam (fake)", hub.StatusMessage);
        var webcam = Assert.Single(hub.Webcams);
        Assert.Equal(50, webcam.Display(WebcamControl.Brightness));   // 128 of 0-255
        Assert.Equal(100, webcam.Display(WebcamControl.Zoom));
        Assert.Single(_env.Of(CameraHubEvents.Connected));
        Assert.Empty(_env.Of(CameraHubEvents.WebcamConnected));   // initial scan is not an event

        await hub.DisconnectAsync();
        Assert.Single(_env.Of(CameraHubEvents.Disconnected));
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public async Task Changes_made_elsewhere_become_camera_hub_triggers()
    {
        await using var hub = Create();
        await hub.ConnectAsync();

        _backend.Values[WebcamControl.Brightness] = 255;
        _backend.Values[WebcamControl.Zoom] = 250;
        _backend.Values[WebcamControl.AutoFocus] = 0;
        _backend.Values[WebcamControl.AntiFlicker] = 1;
        _backend.Values[WebcamControl.Mirror] = 1;
        _backend.Values[WebcamControl.WhiteBalance] = 6500;
        await hub.RefreshAsync();

        var brightness = Assert.Single(_env.Of(CameraHubEvents.BrightnessChanged));
        Assert.Equal(100, brightness.Args["brightness"]);
        Assert.Equal(50, brightness.Args["oldBrightness"]);
        Assert.Equal("Elgato Facecam", brightness.Args["deviceName"]);
        Assert.Equal("usb-0000:00:14.0-3", brightness.Args["deviceId"]);

        var zoom = Assert.Single(_env.Of(CameraHubEvents.ZoomChanged));
        Assert.Equal(250, zoom.Args["zoomValue"]);
        Assert.Equal("250%", zoom.Args["zoomPercent"]);
        Assert.Equal(100, zoom.Args["oldZoomValue"]);
        Assert.Equal(400, zoom.Args["maximumZoom"]);

        Assert.Equal(false, Assert.Single(_env.Of(CameraHubEvents.AutoFocusDisabled)).Args["autoFocus"]);
        var flicker = Assert.Single(_env.Of(CameraHubEvents.AntiFlickerChanged));
        Assert.Equal("50 Hz", flicker.Args["antiFlicker"]);
        Assert.Equal("60 Hz", flicker.Args["oldAntiFlicker"]);
        Assert.Equal(true, Assert.Single(_env.Of(CameraHubEvents.Mirrored)).Args["mirrored"]);
        Assert.Single(_env.Of(CameraHubEvents.DeviceOrientationChanged));
        Assert.Equal(6500, Assert.Single(_env.Of(CameraHubEvents.WhiteBalanceChanged)).Args["whiteBalance"]);

        _backend.Values[WebcamControl.AutoWhiteBalance] = 1;
        _backend.Values[WebcamControl.WhiteBalance] = 4000;
        await hub.RefreshAsync();
        Assert.Single(_env.Of(CameraHubEvents.AutoWhiteBalanceEnabled));
        Assert.Equal(4000, Assert.Single(_env.Of(CameraHubEvents.LiveWhiteBalanceChanged)).Args["whiteBalance"]);
    }

    [Fact]
    public async Task Sub_action_values_are_converted_to_device_units()
    {
        await using var hub = Create();
        await hub.ConnectAsync();

        Assert.Equal(100, await hub.SetAsync("", WebcamControl.Brightness, 100));
        Assert.Equal(255, _backend.Values[WebcamControl.Brightness]);
        Assert.Equal(75, await hub.SetAsync("Elgato Facecam", WebcamControl.Brightness, -25, adjust: true));
        Assert.Equal(191, _backend.Values[WebcamControl.Brightness]);

        await hub.SetAsync("", WebcamControl.Zoom, 400);
        Assert.Equal(400, _backend.Values[WebcamControl.Zoom]);
        await hub.SetAsync("", WebcamControl.Zoom, 999);
        Assert.Equal(400, _backend.Values[WebcamControl.Zoom]);   // clamped

        await hub.SetAsync("", WebcamControl.WhiteBalance, 5555);
        Assert.Equal(5560, _backend.Values[WebcamControl.WhiteBalance]);   // device step 10

        Assert.True(await hub.SetSwitchAsync("", WebcamControl.AutoWhiteBalance, SwitchMode.Toggle));
        Assert.Equal(1, _backend.Values[WebcamControl.AutoWhiteBalance]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SetAsync("", WebcamControl.Pan, 50));   // camera has no pan
        await Assert.ThrowsAsync<ArgumentException>(() => hub.SetAsync("Logitech", WebcamControl.Brightness, 50));
        Assert.Single(_env.Of(CameraHubEvents.AutoWhiteBalanceEnabled));   // own changes are published too
    }

    [Fact]
    public async Task Webcams_appearing_and_disappearing_are_published()
    {
        await using var hub = Create();
        await hub.ConnectAsync();
        _backend.Devices.Add(new WebcamDeviceInfo("usb-0000:00:14.0-4", "Elgato Facecam Pro", "/dev/video2"));
        await hub.RefreshAsync();
        Assert.Equal("Elgato Facecam Pro", Assert.Single(_env.Of(CameraHubEvents.WebcamConnected)).Args["deviceName"]);

        _backend.Devices.RemoveAt(0);
        await hub.RefreshAsync();
        Assert.Equal("Elgato Facecam", Assert.Single(_env.Of(CameraHubEvents.WebcamDisconnected)).Args["deviceName"]);
        Assert.Equal("Elgato Facecam Pro", Assert.Single(hub.Webcams).Name);
    }

    [Fact]
    public async Task Name_filter_limits_the_webcams()
    {
        _env.Settings.Value.CameraHub.NameFilter = "Elgato";
        _backend.Devices.Add(new WebcamDeviceInfo("usb-1", "HD Pro Webcam C920", "/dev/video4"));
        await using var hub = Create();
        await hub.ConnectAsync();
        Assert.Equal("Elgato Facecam", Assert.Single(hub.Webcams).Name);
    }

    [Fact]
    public async Task Without_a_backend_it_reports_unavailable()
    {
        await using var hub = new CameraHubService(_env.Settings, _env.Bus, _env.Log, backend: null, useDefaultBackend: false);
        Assert.False(hub.IsAvailable);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(hub.ConnectAsync);
        _env.Settings.Value.CameraHub.AutoConnect = true;
        await hub.StartAsync();   // never throws
        Assert.Equal(Core.ConnectionState.Disconnected, hub.State);
    }
}

public sealed class WebcamUnitTests
{
    [Fact]
    public void Percent_controls_map_onto_the_device_range()
    {
        var range = new WebcamRange(-36000, 36000, 3600, 0);
        Assert.Equal(50, WebcamUnits.ToDisplay(WebcamControl.Pan, 0, range));
        Assert.Equal(-36000, WebcamUnits.ToRaw(WebcamControl.Pan, 0, range));
        Assert.Equal(36000, WebcamUnits.ToRaw(WebcamControl.Pan, 100, range));
        Assert.Equal(7200, WebcamUnits.ToRaw(WebcamControl.Pan, 60, range));   // snapped to the 3600 step
    }

    [Fact]
    public void Auto_exposure_uses_the_v4l2_menu_values()
    {
        var range = new WebcamRange(0, 3, 1, 3);
        Assert.Equal(1, WebcamUnits.ToDisplay(WebcamControl.AutoExposure, 3, range));
        Assert.Equal(0, WebcamUnits.ToDisplay(WebcamControl.AutoExposure, 1, range));
        Assert.Equal(3, WebcamUnits.ToRaw(WebcamControl.AutoExposure, 1, range));
        Assert.Equal(1, WebcamUnits.ToRaw(WebcamControl.AutoExposure, 0, range));
    }

    [Theory]
    [InlineData(166, "1/60")]
    [InlineData(100, "1/100")]
    [InlineData(20000, "2s")]
    public void Exposure_time_reads_as_a_shutter_speed(int exposure, string text) => Assert.Equal(text, WebcamUnits.ShutterSpeed(exposure));

    [Fact]
    public void Anti_flicker_names_round_trip()
    {
        Assert.Equal(2, AntiFlicker.Parse("60 Hz"));
        Assert.Equal(3, AntiFlicker.Parse("auto"));
        Assert.Equal("50 Hz", AntiFlicker.Name(1));
        Assert.Throws<ArgumentException>(() => AntiFlicker.Parse("70 Hz"));
    }
}

/// <summary>The V4L2 constants and structure offsets, checked against linux/videodev2.h without a camera.</summary>
public sealed class V4l2LayoutTests
{
    private static ulong Ioc(uint dir, char type, uint nr, uint size) => (dir << 30) | (size << 16) | ((uint)type << 8) | nr;

    [Fact]
    public void Ioctl_numbers_match_the_kernel_macros()
    {
        Assert.Equal(V4l2Backend.VidiocQuerycap, Ioc(2, 'V', 0, 104));
        Assert.Equal(V4l2Backend.VidiocQueryctrl, Ioc(3, 'V', 36, 68));
        Assert.Equal(V4l2Backend.VidiocGCtrl, Ioc(3, 'V', 27, 8));
        Assert.Equal(V4l2Backend.VidiocSCtrl, Ioc(3, 'V', 28, 8));
    }

    [Fact]
    public void Control_ids_match_v4l2_controls_h()
    {
        Assert.Equal(0x00980900u, V4l2Backend.ControlId(WebcamControl.Brightness));
        Assert.Equal(0x0098091Au, V4l2Backend.ControlId(WebcamControl.WhiteBalance));
        Assert.Equal(0x00980918u, V4l2Backend.ControlId(WebcamControl.AntiFlicker));
        Assert.Equal(0x009A0901u, V4l2Backend.ControlId(WebcamControl.AutoExposure));
        Assert.Equal(0x009A090Au, V4l2Backend.ControlId(WebcamControl.Focus));
        Assert.Equal(0x009A090Du, V4l2Backend.ControlId(WebcamControl.Zoom));
        Assert.All(Enum.GetValues<WebcamControl>(), c => V4l2Backend.ControlId(c));
    }

    [Fact]
    public void Parses_v4l2_capability_and_queryctrl()
    {
        var cap = new byte[V4l2Backend.CapabilitySize];
        Encoding.ASCII.GetBytes("uvcvideo").CopyTo(cap, 0);
        Encoding.ASCII.GetBytes("Elgato Facecam: Elgato Facecam").CopyTo(cap, 16);
        Encoding.ASCII.GetBytes("usb-0000:00:14.0-3").CopyTo(cap, 48);
        BinaryPrimitives.WriteUInt32LittleEndian(cap.AsSpan(84), 0x84A00001);
        BinaryPrimitives.WriteUInt32LittleEndian(cap.AsSpan(88), 0x04200001);
        var (card, bus, caps) = V4l2Backend.ParseCapability(cap);
        Assert.Equal("Elgato Facecam", V4l2Backend.CleanName(card));
        Assert.Equal("usb-0000:00:14.0-3", bus);
        Assert.Equal(0x04200001u, caps);

        var q = new byte[V4l2Backend.QueryCtrlSize];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(40), 100);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(44), 400);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(48), 1);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(52), 100);
        Assert.Equal(new WebcamRange(100, 400, 1, 100), V4l2Backend.ParseQueryCtrl(q));
        BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(56), 1);   // V4L2_CTRL_FLAG_DISABLED
        Assert.Null(V4l2Backend.ParseQueryCtrl(q));
    }
}
