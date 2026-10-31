using System.Globalization;
using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>An open veadotube program, as announced in its instances folder.</summary>
public sealed record VeadotubeInstance(string Id, string Name, string Version, string Server, DateTimeOffset Time)
{
    /// <summary>"mini" for veadotube mini, "veado" for veadotube (the id's first segment).</summary>
    public string Type => Id.Split('-')[0];

    /// <summary>The launch timestamp (the id's hex middle segment), so the first one opened sorts first.</summary>
    public ulong Launched => Id.Split('-') is { Length: 3 } parts
                             && ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t) ? t : ulong.MaxValue;
}

/// <summary>
/// Finds open veadotube programs: each one writes a JSON file named after its instance id into
/// <c>~/.veadotube/instances</c> (<c>%USERPROFILE%\.veadotube\instances</c> on Windows) and keeps its time fresh.
/// Files that are not valid JSON, have no server address, or are older than <see cref="MaxAge"/> are ignored.
/// </summary>
public static class VeadotubeInstances
{
    /// <summary>veadotube's docs say 10 seconds; a little slack for a busy PC.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".veadotube", "instances");

    /// <summary>Open instances with a server, the first one opened first.</summary>
    public static IReadOnlyList<VeadotubeInstance> Find(string folder, DateTimeOffset now)
    {
        if (!Directory.Exists(folder)) return [];
        var found = new List<VeadotubeInstance>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (Parse(Path.GetFileName(file), File.ReadAllText(file)) is { } instance && now - instance.Time <= MaxAge)
                    found.Add(instance);
            }
            catch (IOException)
            {
                // Being rewritten or removed right now.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to read.
            }
        }
        return [.. found.OrderBy(i => i.Launched).ThenBy(i => i.Id, StringComparer.Ordinal)];
    }

    public static VeadotubeInstance? Parse(string fileName, string json)
    {
        if (JsonUtil.TryParse(json) is not { ValueKind: JsonValueKind.Object } root) return null;
        var server = root.Str("server");
        if (server.Length == 0 || root.Num("time") is not { } time) return null;
        var id = root.Str("id") is { Length: > 0 } i ? i : fileName;
        return new VeadotubeInstance(id, root.Str("name") is { Length: > 0 } n ? n : id, root.Str("version"), server,
            DateTimeOffset.FromUnixTimeSeconds((long)time));
    }

    /// <summary>
    /// The instance a setting asks for: empty picks the first one opened; otherwise the id, the id's type ("mini",
    /// "veado") or the display name, case-insensitive. Null when none matches.
    /// </summary>
    public static VeadotubeInstance? Pick(IReadOnlyList<VeadotubeInstance> instances, string wanted)
    {
        var w = wanted.Trim();
        if (w.Length == 0) return instances.FirstOrDefault();
        return instances.FirstOrDefault(i => string.Equals(i.Id, w, StringComparison.OrdinalIgnoreCase))
               ?? instances.FirstOrDefault(i => string.Equals(i.Name, w, StringComparison.OrdinalIgnoreCase))
               ?? instances.FirstOrDefault(i => string.Equals(i.Type, w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the setting is an address (host:port) rather than an instance name.</summary>
    public static bool IsAddress(string setting)
    {
        var s = setting.Trim();
        var colon = s.LastIndexOf(':');
        return colon > 0 && int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
               && !s.Contains(' ');
    }
}
