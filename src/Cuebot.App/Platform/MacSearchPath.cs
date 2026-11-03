namespace Cuebot.App.Platform;

/// <summary>
/// An app started from Finder or the Dock gets launchd's PATH (/usr/bin:/bin:/usr/sbin:/sbin), without the folders
/// where Homebrew and user installs put command-line tools. Cuebot looks for espeak-ng, gh, Python and the assistant
/// CLIs on PATH, so on macOS those folders are added (after what is already there) at startup.
/// </summary>
internal static class MacSearchPath
{
    public static string[] CommonFolders(string home) =>
        ["/opt/homebrew/bin", "/usr/local/bin", Path.Combine(home, ".local", "bin")];

    /// <summary>The PATH with each common folder appended unless it is already in it.</summary>
    public static string Extend(string? path, string home)
    {
        var parts = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var folder in CommonFolders(home))
        {
            if (!parts.Contains(folder, StringComparer.Ordinal)) parts.Add(folder);
        }
        return string.Join(':', parts);
    }

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("PATH", Extend(Environment.GetEnvironmentVariable("PATH"), home));
    }
}
