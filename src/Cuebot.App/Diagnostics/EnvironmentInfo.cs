using System.Reflection;
using System.Runtime.InteropServices;
using Cuebot.App.Updates;

namespace Cuebot.App.Diagnostics;

/// <summary>The machine facts a bug report needs. Contains no user data.</summary>
public sealed record EnvironmentSnapshot
{
    public string AppVersion { get; init; } = "";

    /// <summary>The full informational version (may include the commit, e.g. 0.1.0+abc123).</summary>
    public string BuildVersion { get; init; } = "";

    public string Package { get; init; } = "";
    public string Os { get; init; } = "";

    /// <summary>Linux distribution (os-release PRETTY_NAME); empty elsewhere.</summary>
    public string Distro { get; init; } = "";

    public string Architecture { get; init; } = "";
    public string DotNet { get; init; } = "";
    public string RuntimeId { get; init; } = "";

    /// <summary>XDG_SESSION_TYPE (wayland, x11), plus which display variables are set.</summary>
    public string Session { get; init; } = "";

    /// <summary>XDG_CURRENT_DESKTOP (KDE, GNOME, ...).</summary>
    public string Desktop { get; init; } = "";

    public string DisplayBackend { get; init; } = "";

    public IEnumerable<(string Label, string Value)> Rows()
    {
        yield return ("Cuebot", BuildVersion.Length > 0 && BuildVersion != AppVersion ? $"{AppVersion} ({BuildVersion})" : AppVersion);
        yield return ("Package", Package);
        yield return ("OS", Distro.Length > 0 ? $"{Distro} ({Os})" : Os);
        yield return ("Architecture", Architecture);
        yield return (".NET", RuntimeId.Length > 0 ? $"{DotNet} ({RuntimeId})" : DotNet);
        if (Session.Length > 0) yield return ("Session", Session);
        if (Desktop.Length > 0) yield return ("Desktop", Desktop);
        yield return ("Display backend", DisplayBackend);
    }
}

/// <summary>Collects an <see cref="EnvironmentSnapshot"/>; every source is injectable for tests.</summary>
public static class EnvironmentInfo
{
    public static EnvironmentSnapshot Collect(string displayBackend) =>
        Collect(displayBackend, PackagingEnvironment.Current, ReadOsRelease);

    public static EnvironmentSnapshot Collect(string displayBackend, PackagingEnvironment env, Func<string?> readOsRelease)
    {
        var info = typeof(EnvironmentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var session = "";
        var desktop = "";
        var distro = "";
        if (env.IsLinux)
        {
            var type = env.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
            var flags = new List<string>();
            if (!string.IsNullOrEmpty(env.GetEnvironmentVariable("WAYLAND_DISPLAY"))) flags.Add("WAYLAND_DISPLAY set");
            if (!string.IsNullOrEmpty(env.GetEnvironmentVariable("DISPLAY"))) flags.Add("DISPLAY set");
            session = (type.Length > 0 ? type : "unknown") + (flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "");
            desktop = env.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            distro = ParsePrettyName(readOsRelease()) ?? "";
        }
        return new EnvironmentSnapshot
        {
            AppVersion = SemVersion.Current.ToString(),
            BuildVersion = info,
            Package = PackagingDetector.Describe(PackagingDetector.Detect(env)),
            Os = RuntimeInformation.OSDescription,
            Distro = distro,
            Architecture = env.Architecture.ToString(),
            DotNet = RuntimeInformation.FrameworkDescription,
            RuntimeId = RuntimeInformation.RuntimeIdentifier,
            Session = session,
            Desktop = desktop,
            DisplayBackend = displayBackend,
        };
    }

    /// <summary>os-release of the host: inside Flatpak the host's copy is under /run/host.</summary>
    private static string? ReadOsRelease()
    {
        foreach (var path in new[] { "/run/host/os-release", "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static string? ParsePrettyName(string? osRelease)
    {
        if (osRelease is null) return null;
        string? name = null;
        foreach (var raw in osRelease.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (key == "PRETTY_NAME" && value.Length > 0) return value;
            if (key == "NAME" && value.Length > 0) name = value;
        }
        return name;
    }
}
