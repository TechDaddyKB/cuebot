namespace Cuebot.Core;

/// <summary>
/// Locations of user data. The data folder defaults to %APPDATA%\Cuebot on Windows, ~/.config/Cuebot on Linux and
/// ~/Library/Application Support/Cuebot on macOS; the CUEBOT_DATA environment variable overrides it (used by portable
/// installs and tests). With streamer profiles, <see cref="Root"/> is the active profile's folder inside it and
/// <see cref="Shared"/> the data folder itself, which keeps what every profile shares (logs, the Python runtime,
/// downloaded voices and models, caches). Without profiles (tests, tools) both are the same folder.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null, string? shared = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("CUEBOT_DATA")
            ?? DefaultRoot(OperatingSystem.IsMacOS(), folder => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create));
        Shared = shared ?? Root;
        System.IO.Directory.CreateDirectory(Root);
        System.IO.Directory.CreateDirectory(Logs);
    }

    /// <summary>The active profile's folder: settings, sign-ins, overlays and everything else of one persona.</summary>
    public string Root { get; }

    /// <summary>The data folder every profile shares.</summary>
    public string Shared { get; }

    /// <summary>
    /// The data folder when nothing overrides it. .NET maps ApplicationData to ~/.config on macOS, but Mac apps keep
    /// their data in ~/Library/Application Support (LocalApplicationData there).
    /// </summary>
    public static string DefaultRoot(bool isMacOS, Func<Environment.SpecialFolder, string> getFolder) =>
        Path.Combine(getFolder(isMacOS ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData), "Cuebot");

    public string Logs => Path.Combine(Shared, "logs");

    public string File(string name) => Path.Combine(Root, name);

    public string GetDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A file every profile shares, like <c>app.json</c>.</summary>
    public string SharedFile(string name) => Path.Combine(Shared, name);

    /// <summary>A folder every profile shares, like the Python runtime or downloaded voices.</summary>
    public string GetSharedDirectory(string name)
    {
        var path = Path.Combine(Shared, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
