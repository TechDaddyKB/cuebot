using System.Text;
using System.Text.Json;

namespace Cuebot.Integrations.NowPlaying.Smtc;

/// <summary>One line from the helper: the session list, an error, or "this Windows cannot do it".</summary>
internal sealed record SmtcMessage(string Type, IReadOnlyList<PlayerSession> Sessions, string CurrentId, string Message)
{
    public bool IsSessions => Type == "sessions";
    public bool IsUnsupported => Type == "unsupported";
    public bool IsError => Type == "error";
}

/// <summary>
/// Pure parsing and formatting for the PowerShell helper (smtc-helper.ps1): its JSON lines and its stdin commands.
/// Playback status numbers are GlobalSystemMediaTransportControlsSessionPlaybackStatus (Closed 0, Opened 1,
/// Changing 2, Stopped 3, Playing 4, Paused 5).
/// </summary>
internal static class SmtcProtocol
{
    /// <summary>The embedded helper script, without its comment lines (keeps the command line short).</summary>
    public static string Script()
    {
        using var stream = typeof(SmtcProtocol).Assembly.GetManifestResourceStream("Cuebot.Integrations.NowPlaying.smtc-helper.ps1")
            ?? throw new InvalidOperationException("The Windows media helper script is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith('#'));
        return string.Join("\n", lines);
    }

    /// <summary>The arguments for powershell.exe: the script as -EncodedCommand (Base64 of UTF-16LE).</summary>
    public static string Arguments(string script) =>
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>A command line for the helper's stdin.</summary>
    public static string Command(MediaCommand command, string sessionId) =>
        command switch
        {
            MediaCommand.Play => "play",
            MediaCommand.Pause => "pause",
            MediaCommand.Next => "next",
            MediaCommand.Previous => "previous",
            _ => "playpause",
        } + "\t" + sessionId.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Parses one output line; null for anything that is not a helper message.</summary>
    public static SmtcMessage? Parse(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type != "sessions") return new SmtcMessage(type, [], "", Str(root, "message"));
            var sessions = new List<PlayerSession>();
            if (root.TryGetProperty("sessions", out var list))
            {
                // ConvertTo-Json writes a lone object for one-item lists in some PowerShell versions.
                var items = list.ValueKind switch
                {
                    JsonValueKind.Array => list.EnumerateArray().ToArray(),
                    JsonValueKind.Object => [list],
                    _ => [],
                };
                foreach (var item in items)
                {
                    var id = Str(item, "id");
                    if (id.Length == 0) continue;
                    var artist = Str(item, "artist");
                    if (artist.Length == 0) artist = Str(item, "albumArtist");
                    var ms = item.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                    var song = new Song
                    {
                        Title = Str(item, "title"),
                        Artist = artist,
                        Album = Str(item, "album"),
                        Duration = ms > 0 ? TimeSpan.FromMilliseconds(ms) : null,
                    };
                    sessions.Add(new PlayerSession(id, FriendlyName(id), Status(item), song));
                }
            }
            return new SmtcMessage(type, sessions, Str(root, "current"), "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlaybackState Status(JsonElement item) =>
        item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32() switch { 4 => PlaybackState.Playing, 5 => PlaybackState.Paused, _ => PlaybackState.Stopped }
            : PlaybackState.Stopped;

    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Spotify", ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["opera"] = "Opera",
        ["brave"] = "Brave", ["vlc"] = "VLC", ["ZuneMusic"] = "Media Player", ["ZuneVideo"] = "Films & TV",
        ["AppleMusic"] = "Apple Music", ["foobar2000"] = "foobar2000", ["MusicBee"] = "MusicBee", ["TIDAL"] = "TIDAL",
        ["Deezer"] = "Deezer", ["AmazonMusic"] = "Amazon Music", ["YouTubeMusic"] = "YouTube Music",
    };

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "Media Player".</summary>
    public static string FriendlyName(string appId)
    {
        var name = appId;
        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        foreach (var (key, label) in KnownApps)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return label;
        return name.Length > 0 ? name : appId;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}
