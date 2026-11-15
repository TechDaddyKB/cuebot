using System.Globalization;

namespace Cuebot.Integrations.Steam;

/// <summary>A Steam app that is running: its id and its name from the app's manifest (the id when unknown).</summary>
public sealed record SteamApp(long AppId, string Name);

/// <summary>Where Steam's files are and what it is running. Overridable for tests.</summary>
public interface ISteamSource
{
    /// <summary>Ids of the Steam apps running right now (games and tools alike).</summary>
    IReadOnlyCollection<long> RunningAppIds();

    /// <summary>The app's name from its appmanifest, or null when it is not installed in a library Cuebot can see.</summary>
    string? NameOf(long appId);
}

/// <summary>
/// Reads Steam's own records. Running apps come from Steam's registry (Windows: HKCU\Software\Valve\Steam\Apps\*\Running;
/// Linux and macOS: the same keys in registry.vdf) and, on Linux, from the SteamAppId that Steam gives every process
/// it starts (several apps can run at once, e.g. VTube Studio next to a game). Names come from appmanifest_*.acf in
/// every library listed in libraryfolders.vdf.
/// </summary>
public sealed class SteamFiles : ISteamSource
{
    private readonly IReadOnlyList<string> _roots;
    private readonly string _proc;
    private readonly Dictionary<long, string?> _names = [];
    private DateTime _namesRead;

    /// <param name="roots">Steam install folders to look in (default: the usual ones for this OS).</param>
    /// <param name="proc">The /proc folder (Linux), for tests.</param>
    public SteamFiles(IReadOnlyList<string>? roots = null, string proc = "/proc")
    {
        _roots = roots ?? DefaultRoots();
        _proc = proc;
    }

    public static IReadOnlyList<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = WindowsRegistry("SteamPath") as string;
            return [.. new[] { fromRegistry, @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" }.OfType<string>()];
        }
        if (OperatingSystem.IsMacOS()) return [Path.Combine(home, "Library", "Application Support", "Steam")];
        return
        [
            Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam"), Path.Combine(home, ".steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        ];
    }

    public IReadOnlyCollection<long> RunningAppIds()
    {
        var ids = new HashSet<long>();
        if (OperatingSystem.IsWindows()) ids.UnionWith(WindowsRunning());
        else
        {
            foreach (var registry in RegistryFiles())
                if (SteamVdf.Load(registry)?.Path("Registry", "HKCU", "Software", "Valve", "Steam", "apps") is { } apps)
                    ids.UnionWith(Running(apps));
            if (OperatingSystem.IsLinux()) ids.UnionWith(ProcessAppIds(_proc));
        }
        ids.Remove(0);
        return ids;
    }

    /// <summary>Apps whose "Running" value is 1 in an "apps" block of registry.vdf.</summary>
    internal static IEnumerable<long> Running(SteamVdf apps) =>
        apps.Blocks.Where(b => b.Value["Running"] == "1")
            .Select(b => long.TryParse(b.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0);

    /// <summary>The SteamAppId of this user's processes (Steam sets it for everything it launches).</summary>
    internal static IEnumerable<long> ProcessAppIds(string proc)
    {
        var ids = new HashSet<long>();
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(proc).Where(d => Path.GetFileName(d).All(char.IsAsciiDigit)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ids;
        }
        foreach (var dir in dirs)
        {
            string environ;
            try
            {
                environ = File.ReadAllText(Path.Combine(dir, "environ"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;   // another user's process, or it just ended
            }
            foreach (var entry in environ.Split('\0'))
                if (entry.StartsWith("SteamAppId=", StringComparison.Ordinal)
                    && long.TryParse(entry.AsSpan("SteamAppId=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    ids.Add(id);
                    break;
                }
        }
        return ids;
    }

    private IEnumerable<string> RegistryFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = _roots.Select(r => Path.Combine(r, "registry.vdf")).Append(Path.Combine(home, ".steam", "registry.vdf"));
        return candidates.Where(File.Exists).Distinct();
    }

    public string? NameOf(long appId)
    {
        lock (_names)
        {
            // Libraries change rarely: read the manifests again at most once a minute.
            if (DateTime.UtcNow - _namesRead > TimeSpan.FromMinutes(1))
            {
                _names.Clear();
                _namesRead = DateTime.UtcNow;
            }
            if (_names.TryGetValue(appId, out var cached)) return cached;
            var name = Libraries().Select(l => Path.Combine(l, "steamapps", $"appmanifest_{appId}.acf"))
                .Select(SteamVdf.Load).OfType<SteamVdf>().Select(m => m.Block("AppState")?["name"] ?? "")
                .FirstOrDefault(n => n.Length > 0);
            _names[appId] = name;
            return name;
        }
    }

    /// <summary>Every Steam library folder (the install folder and those in libraryfolders.vdf).</summary>
    internal IEnumerable<string> Libraries()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in _roots.Where(Directory.Exists))
        {
            if (seen.Add(Path.GetFullPath(root))) yield return root;
            if (SteamVdf.Load(Path.Combine(root, "steamapps", "libraryfolders.vdf"))?.Block("libraryfolders") is not { } folders) continue;
            foreach (var folder in folders.Blocks.Values)
                if (folder["path"] is { Length: > 0 } path && Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                    yield return path;
        }
    }

    private static IEnumerable<long> WindowsRunning()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        using var apps = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps");
        if (apps is null) yield break;
        foreach (var name in apps.GetSubKeyNames())
        {
            using var app = apps.OpenSubKey(name);
            if (app?.GetValue("Running") is int running && running == 1
                && long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                yield return id;
        }
    }

    private static object? WindowsRegistry(string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return steam?.GetValue(value);
    }
}
