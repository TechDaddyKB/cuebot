using Cuebot.App.Platform;

namespace Cuebot.UiTests;

public class LinuxBackendTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Prefers_wayland_when_a_compositor_is_present() =>
        Assert.Equal(DisplayBackend.Wayland, LinuxBackend.Choose([], Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"))));

    [Fact]
    public void Uses_x11_without_a_compositor() =>
        Assert.Equal(DisplayBackend.X11, LinuxBackend.Choose([], Env(("DISPLAY", ":0"))));

    [Fact]
    public void Command_line_beats_environment_which_beats_detection()
    {
        var env = Env(("WAYLAND_DISPLAY", "wayland-0"), ("CUEBOT_BACKEND", "x11"));
        Assert.Equal(DisplayBackend.X11, LinuxBackend.Choose([], env));
        Assert.Equal(DisplayBackend.Wayland, LinuxBackend.Choose(["--backend=Wayland"], env));
    }

    [Fact]
    public void Falls_back_only_once_and_only_when_not_forced()
    {
        var auto = Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"));
        Assert.True(LinuxBackend.CanFallBack([], auto));
        Assert.False(LinuxBackend.CanFallBack(["--backend=wayland"], auto));
        Assert.False(LinuxBackend.CanFallBack([], Env(("WAYLAND_DISPLAY", "wayland-0"))));   // no XWayland to fall back to

        var relaunched = Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), (LinuxBackend.FallbackMarker, "1"));
        Assert.Equal(DisplayBackend.X11, LinuxBackend.Choose([], relaunched));
        Assert.False(LinuxBackend.CanFallBack([], relaunched));
    }
}
