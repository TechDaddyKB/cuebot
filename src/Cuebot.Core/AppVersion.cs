using System.Reflection;

namespace Cuebot.Core;

/// <summary>
/// Cuebot's version as released ("0.1.0-rc.3"), without the build metadata. Every place that shows or sends the
/// version uses this: the assembly version alone drops the prerelease part ("0.1.0").
/// </summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
