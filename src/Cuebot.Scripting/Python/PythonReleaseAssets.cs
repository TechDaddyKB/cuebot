using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cuebot.Scripting;

/// <summary>A downloadable file of a GitHub release.</summary>
internal sealed record ReleaseAsset(string Name, string DownloadUrl);

/// <summary>A python-build-standalone archive chosen for this machine.</summary>
internal sealed record PythonBuild(ReleaseAsset Asset, Version PythonVersion);

/// <summary>Pure helpers for picking a python-build-standalone release asset and checking its checksum.</summary>
internal static partial class PythonReleaseAssets
{
    public const string ChecksumAssetName = "SHA256SUMS";

    /// <summary>Supported Python minor versions (3.x), most preferred first.</summary>
    private static readonly int[] SupportedMinors = [13, 12];

    [GeneratedRegex(@"^cpython-(?<version>3\.(?<minor>\d+)\.\d+)\+\d+-(?<target>[a-z0-9_-]+)-install_only\.tar\.gz\z")]
    private static partial Regex InstallOnlyArchive();

    /// <summary>The build target triple for the current OS/architecture, or null when unsupported.</summary>
    public static string? CurrentTarget() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 when OperatingSystem.IsWindows() => "x86_64-pc-windows-msvc",
        Architecture.Arm64 when OperatingSystem.IsWindows() => "aarch64-pc-windows-msvc",
        Architecture.X64 when OperatingSystem.IsLinux() => "x86_64-unknown-linux-gnu",
        Architecture.Arm64 when OperatingSystem.IsLinux() => "aarch64-unknown-linux-gnu",
        _ => null,
    };

    /// <summary>
    /// Picks the "install_only" .tar.gz archive for <paramref name="target"/>, preferring the newest patch of the most
    /// preferred supported minor version. Returns null when the release has no matching archive.
    /// </summary>
    public static PythonBuild? Select(IEnumerable<ReleaseAsset> assets, string target) =>
        assets
            .Select(asset => (asset, match: InstallOnlyArchive().Match(asset.Name)))
            .Where(x => x.match.Success && x.match.Groups["target"].Value == target)
            .Select(x => (x.asset, minor: int.Parse(x.match.Groups["minor"].Value), version: Version.Parse(x.match.Groups["version"].Value)))
            .Where(x => SupportedMinors.Contains(x.minor))
            .OrderBy(x => Array.IndexOf(SupportedMinors, x.minor))
            .ThenByDescending(x => x.version)
            .Select(x => new PythonBuild(x.asset, x.version))
            .FirstOrDefault();

    /// <summary>Reads the assets of a GitHub "release" API response.</summary>
    public static IReadOnlyList<ReleaseAsset> ParseRelease(JsonElement release) =>
        release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray()
                .Select(a => new ReleaseAsset(
                    a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : ""))
                .Where(a => a.Name.Length > 0 && a.DownloadUrl.Length > 0)
                .ToArray()
            : [];

    /// <summary>Finds the expected SHA-256 (lower-case hex) of a file in a "hash  filename" checksum list.</summary>
    public static string? FindChecksum(string checksums, string fileName) =>
        checksums.Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName)
            .Select(parts => parts[0].ToLowerInvariant())
            .FirstOrDefault();
}
