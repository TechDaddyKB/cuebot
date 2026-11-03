namespace Cuebot.Core;

/// <summary>
/// Locations of user data. Defaults to %APPDATA%\Cuebot on Windows, ~/.config/Cuebot on Linux and
/// ~/Library/Application Support/Cuebot on macOS; the CUEBOT_DATA environment variable overrides it (used by portable
/// installs and tests).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("CUEBOT_DATA")
            ?? DefaultRoot(OperatingSystem.IsMacOS(), folder => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create));
        System.IO.Directory.CreateDirectory(Root);
        System.IO.Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    /// <summary>
    /// The data folder when nothing overrides it. .NET maps ApplicationData to ~/.config on macOS, but Mac apps keep
    /// their data in ~/Library/Application Support (LocalApplicationData there).
    /// </summary>
    public static string DefaultRoot(bool isMacOS, Func<Environment.SpecialFolder, string> getFolder) =>
        Path.Combine(getFolder(isMacOS ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData), "Cuebot");

    public string Logs => Path.Combine(Root, "logs");

    public string File(string name) => Path.Combine(Root, name);

    public string GetDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
