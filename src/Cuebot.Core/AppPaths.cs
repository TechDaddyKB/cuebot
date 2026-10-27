namespace Cuebot.Core;

/// <summary>
/// Locations of user data. Defaults to %APPDATA%\Cuebot on Windows and ~/.config/Cuebot on Linux;
/// the CUEBOT_DATA environment variable overrides it (used by portable installs and tests).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("CUEBOT_DATA")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
                "Cuebot");
        System.IO.Directory.CreateDirectory(Root);
        System.IO.Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    public string Logs => Path.Combine(Root, "logs");

    public string File(string name) => Path.Combine(Root, name);

    public string GetDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
