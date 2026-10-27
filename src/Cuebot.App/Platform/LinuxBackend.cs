namespace Cuebot.App.Platform;

public enum DisplayBackend { Default, Wayland, X11 }

/// <summary>
/// Wayland-first display backend selection on Linux. Order of precedence:
/// <c>--backend=wayland|x11</c>, then <c>CUEBOT_BACKEND</c>, then Wayland whenever a compositor is
/// advertised (<c>WAYLAND_DISPLAY</c>), else X11. A failed Wayland start relaunches on X11 once
/// (see <see cref="Program"/>), marked by <see cref="FallbackMarker"/> so it can never loop.
/// </summary>
public static class LinuxBackend
{
    public const string FallbackMarker = "CUEBOT_WAYLAND_FAILED";
    private const string Option = "--backend=";

    public static DisplayBackend Choose(IReadOnlyList<string> args, Func<string, string?> env)
    {
        var requested = args.FirstOrDefault(a => a.StartsWith(Option, StringComparison.OrdinalIgnoreCase))?[Option.Length..]
                        ?? env("CUEBOT_BACKEND");
        var parsed = Parse(requested);
        if (parsed is not DisplayBackend.Default) return parsed;

        if (!string.IsNullOrEmpty(env(FallbackMarker))) return DisplayBackend.X11;
        return string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) ? DisplayBackend.X11 : DisplayBackend.Wayland;
    }

    /// <summary>Wayland was chosen automatically (not forced), so falling back to X11 is allowed.</summary>
    public static bool CanFallBack(IReadOnlyList<string> args, Func<string, string?> env) =>
        Parse(args.FirstOrDefault(a => a.StartsWith(Option, StringComparison.OrdinalIgnoreCase))?[Option.Length..]
              ?? env("CUEBOT_BACKEND")) is DisplayBackend.Default
        && !string.IsNullOrEmpty(env("DISPLAY"))
        && string.IsNullOrEmpty(env(FallbackMarker));

    private static DisplayBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "wayland" => DisplayBackend.Wayland,
        "x11" or "xwayland" => DisplayBackend.X11,
        _ => DisplayBackend.Default,
    };
}
