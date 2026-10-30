using System.Runtime.InteropServices;

namespace Cuebot.Scripting;

/// <summary>One python-build-standalone archive Cuebot installs, with the SHA-256 it must have.</summary>
internal sealed record PythonBuild(string Target, string FileName, string Sha256, long Size)
{
    public string DownloadUrl =>
        $"https://github.com/astral-sh/python-build-standalone/releases/download/{PinnedPython.Release}/{Uri.EscapeDataString(FileName)}";

    /// <summary>Download size rounded to megabytes, for messages.</summary>
    public int Megabytes => (int)Math.Round(Size / 1_000_000d);
}

/// <summary>
/// The exact Python build each Cuebot version installs. Pinned (rather than "the latest release") so every install of a
/// Cuebot version gets the same interpreter, and checked against these SHA-256 values compiled into Cuebot, so a
/// changed or tampered download is refused. To move to a newer build, run <c>tools/update-python-pin.py</c> and paste
/// its output here.
/// </summary>
internal static class PinnedPython
{
    /// <summary>python-build-standalone release tag.</summary>
    public const string Release = "20261026";

    public const string Version = "3.13.18";

    private static readonly PythonBuild[] Builds =
    [
        new("x86_64-pc-windows-msvc", "cpython-3.13.18+20261026-x86_64-pc-windows-msvc-install_only_stripped.tar.gz",
            "7369f7a35f261222a6ac8b3a87c1c88eeebee8af945d85346202591a64038dd3", 22023022),
        new("aarch64-pc-windows-msvc", "cpython-3.13.18+20261026-aarch64-pc-windows-msvc-install_only_stripped.tar.gz",
            "bd3315abb8abb8ce5b19c2c2b709d318f983e0b8753d0830831df07c46474699", 21036090),
        new("x86_64-unknown-linux-gnu", "cpython-3.13.18+20261026-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz",
            "df15fe60d3ff55bf9dcf25824b5572756bb8816ff93865a273d3301d7066a8b7", 35395906),
        new("aarch64-unknown-linux-gnu", "cpython-3.13.18+20261026-aarch64-unknown-linux-gnu-install_only_stripped.tar.gz",
            "578d9da1983463ffb0c8c98ab0da2f18177f44069328eb0af847860c1eb05c3e", 29492455),
    ];

    public static IReadOnlyList<PythonBuild> All => Builds;

    /// <summary>The build target triple for the current OS/architecture, or null when unsupported.</summary>
    public static string? CurrentTarget() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 when OperatingSystem.IsWindows() => "x86_64-pc-windows-msvc",
        Architecture.Arm64 when OperatingSystem.IsWindows() => "aarch64-pc-windows-msvc",
        Architecture.X64 when OperatingSystem.IsLinux() => "x86_64-unknown-linux-gnu",
        Architecture.Arm64 when OperatingSystem.IsLinux() => "aarch64-unknown-linux-gnu",
        _ => null,
    };

    public static PythonBuild? For(string? target) => Builds.FirstOrDefault(b => b.Target == target);

    /// <summary>The build for this computer, or null when Cuebot has no managed Python for it.</summary>
    public static PythonBuild? ForThisComputer() => For(CurrentTarget());
}
