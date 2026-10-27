using System.Runtime.InteropServices;

namespace Cuebot.App.Updates;

/// <summary>How this copy of Cuebot was installed; decides what an update looks like.</summary>
public enum PackageKind
{
    /// <summary>The Windows zip (portable folder).</summary>
    WindowsZip,
    AppImage,
    Flatpak,
    /// <summary>Installed by a package manager into /usr/lib/cuebot (AUR cuebot / cuebot-bin).</summary>
    SystemPackage,
    /// <summary>The Linux tarball, or a build run from anywhere else.</summary>
    LinuxTarball,
    /// <summary>Another OS (macOS builds are not published).</summary>
    Other,
}

/// <summary>What packaging detection looks at; injectable for tests.</summary>
public sealed record PackagingEnvironment(
    bool IsWindows,
    bool IsLinux,
    Func<string, string?> GetEnvironmentVariable,
    Func<string, bool> FileExists,
    string BaseDirectory,
    Architecture Architecture)
{
    public static PackagingEnvironment Current { get; } = new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable,
        File.Exists,
        AppContext.BaseDirectory,
        RuntimeInformation.OSArchitecture);
}

public static class PackagingDetector
{
    public const string SystemInstallFolder = "/usr/lib/cuebot";

    public static PackageKind Detect(PackagingEnvironment env)
    {
        if (env.IsWindows) return PackageKind.WindowsZip;
        if (!env.IsLinux) return PackageKind.Other;
        // Flatpak first (its files live under /app), then AppImage (whose mount also contains usr/lib/cuebot).
        if (!string.IsNullOrEmpty(env.GetEnvironmentVariable("FLATPAK_ID")) || env.FileExists("/.flatpak-info")) return PackageKind.Flatpak;
        if (!string.IsNullOrEmpty(env.GetEnvironmentVariable("APPIMAGE"))) return PackageKind.AppImage;
        var dir = env.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        if (dir.Equals(SystemInstallFolder, StringComparison.Ordinal) || dir.StartsWith(SystemInstallFolder + "/", StringComparison.Ordinal))
            return PackageKind.SystemPackage;
        return PackageKind.LinuxTarball;
    }

    public static string Describe(PackageKind kind) => kind switch
    {
        PackageKind.WindowsZip => "Windows zip",
        PackageKind.AppImage => "AppImage",
        PackageKind.Flatpak => "Flatpak",
        PackageKind.SystemPackage => "System package (/usr/lib/cuebot)",
        PackageKind.LinuxTarball => "Linux tarball",
        _ => "Other",
    };

    /// <summary>The release asset name for this packaging, or null when updates come from a package manager.</summary>
    public static string? AssetName(PackageKind kind, string version, Architecture arch) => kind switch
    {
        PackageKind.WindowsZip => "Cuebot-win-x64.zip",
        PackageKind.AppImage => "Cuebot-x86_64.AppImage",
        PackageKind.LinuxTarball => $"Cuebot-{version}-linux-{(arch == Architecture.Arm64 ? "arm64" : "x64")}.tar.gz",
        _ => null,
    };

    /// <summary>How to install an update for this packaging. Cuebot never installs updates itself.</summary>
    public static string Instructions(PackageKind kind) => kind switch
    {
        PackageKind.WindowsZip =>
            "Download the zip, close Cuebot and replace the program folder with the new one. Your data (actions, settings) stays where it is.",
        PackageKind.AppImage =>
            "Download the new AppImage, make it executable and use it in place of the old one. Your data stays where it is.",
        PackageKind.Flatpak =>
            "Cuebot is installed as a Flatpak: update it with \"flatpak update\" (or your software centre), or install the new .flatpak bundle from the release page.",
        PackageKind.SystemPackage =>
            "Cuebot was installed by your package manager: update it there (for example \"yay -Syu cuebot\" or \"paru -Syu\") instead of downloading.",
        PackageKind.LinuxTarball =>
            "Download the tarball for your architecture and unpack it over the old folder. Your data stays where it is.",
        _ => "Open the release page to download the new version.",
    };
}
