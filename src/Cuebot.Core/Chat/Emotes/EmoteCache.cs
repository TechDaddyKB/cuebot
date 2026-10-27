using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Chat.Emotes;

/// <summary>A downloaded set and when it was fetched.</summary>
public sealed record CachedEmoteSet(DateTimeOffset FetchedAt, IReadOnlyList<Emote> Emotes);

/// <summary>
/// Disk cache for emote sets: one JSON file per provider and scope under the data folder's cache/emotes directory,
/// so a restart does not hit every provider again and a provider outage falls back to the last good copy.
/// </summary>
public sealed class EmoteDiskCache(string directory, AppLog log)
{
    public string Directory { get; } = directory;

    public static EmoteDiskCache For(AppPaths paths, AppLog log) => new(paths.GetDirectory(Path.Combine("cache", "emotes")), log);

    public CachedEmoteSet? Load(string provider, string scope)
    {
        var path = PathFor(provider, scope);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CachedEmoteSet>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warn("Emotes", $"Ignoring unreadable cache {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public void Save(string provider, string scope, CachedEmoteSet set)
    {
        var path = PathFor(provider, scope);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(set, Json.Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("Emotes", $"Could not write cache {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public void Clear()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    internal string PathFor(string provider, string scope)
    {
        var name = $"{provider}-{scope}";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray());
        return Path.Combine(Directory, safe + ".json");
    }
}
