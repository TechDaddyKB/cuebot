using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Cuebot.Core.Desktop.DBus;

namespace Cuebot.Integrations.NowPlaying.Mpris;

/// <summary>One MPRIS player as last seen on the session bus.</summary>
/// <param name="BusName">Well-known name, e.g. org.mpris.MediaPlayer2.spotify.</param>
/// <param name="UniqueName">The bus connection that owns it (":1.42"); signals come from this name.</param>
/// <param name="Identity">The player's own name (org.mpris.MediaPlayer2.Identity), "" when it gave none.</param>
internal sealed record MprisPlayer(string BusName, string UniqueName, string Identity, PlaybackState State, Song Song)
{
    public string FriendlyName => Identity.Length > 0 ? Identity : MprisParser.NameFromBus(BusName);

    public PlayerSession ToSession() => new(BusName, FriendlyName, State, Song);
}

/// <summary>
/// Pure MPRIS D-Bus Interface Specification 2.2 mapping: bus names, the Metadata map (xesam:/mpris: keys) and
/// PlaybackStatus. https://specifications.freedesktop.org/mpris-spec/latest/
/// </summary>
internal static partial class MprisParser
{
    public const string BusPrefix = "org.mpris.MediaPlayer2.";
    public const string ObjectPath = "/org/mpris/MediaPlayer2";
    public const string RootInterface = "org.mpris.MediaPlayer2";
    public const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>Signals for players appearing and leaving (arg0namespace needs D-Bus 1.5, 2011).</summary>
    public const string NameOwnerRule =
        "type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged',arg0namespace='org.mpris.MediaPlayer2'";

    /// <summary>Song and play/pause changes of every player.</summary>
    public const string PropertiesRule =
        "type='signal',interface='org.freedesktop.DBus.Properties',member='PropertiesChanged',path='/org/mpris/MediaPlayer2'";

    public static bool IsPlayerBusName(string? name) =>
        name is not null && name.StartsWith(BusPrefix, StringComparison.Ordinal) && name.Length > BusPrefix.Length;

    /// <summary>"org.mpris.MediaPlayer2.chromium.instance1234" → "chromium".</summary>
    public static string NameFromBus(string busName)
    {
        var name = busName.StartsWith(BusPrefix, StringComparison.Ordinal) ? busName[BusPrefix.Length..] : busName;
        return InstanceSuffix().Replace(name, "");
    }

    public static PlaybackState ParseStatus(object? value) => Unwrap(value) switch
    {
        "Playing" => PlaybackState.Playing,
        "Paused" => PlaybackState.Paused,
        _ => PlaybackState.Stopped,
    };

    /// <summary>The Metadata a{sv} (or a variant holding it) as a <see cref="Song"/>.</summary>
    public static Song ParseMetadata(object? value)
    {
        if (Unwrap(value) is not IDictionary map) return Song.None;
        string Text(string key) => map.Contains(key) ? JoinText(Unwrap(map[key])) : "";
        var title = Text("xesam:title");
        if (title.Length == 0 && Text("xesam:url") is { Length: > 0 } url) title = FileTitle(url);
        var artist = Text("xesam:artist");
        if (artist.Length == 0) artist = Text("xesam:albumArtist");
        return new Song
        {
            Title = title,
            Artist = artist,
            Album = Text("xesam:album"),
            ArtUrl = Text("mpris:artUrl"),
            Duration = map.Contains("mpris:length") ? Microseconds(Unwrap(map["mpris:length"])) : null,
        };
    }

    /// <summary>Applies a PropertiesChanged map (only the keys it holds).</summary>
    public static MprisPlayer Apply(MprisPlayer player, IDictionary changed)
    {
        if (changed.Contains("Metadata")) player = player with { Song = ParseMetadata(changed["Metadata"]) };
        if (changed.Contains("PlaybackStatus")) player = player with { State = ParseStatus(changed["PlaybackStatus"]) };
        return player;
    }

    /// <summary>True when an invalidated-properties list means the song or status must be read again.</summary>
    public static bool NeedsReload(object? invalidated) =>
        Unwrap(invalidated) is IEnumerable list and not string
        && list.Cast<object?>().Any(p => p is "Metadata" or "PlaybackStatus");

    public static object? Unwrap(object? value)
    {
        while (value is DBusVariant variant) value = variant.Value;
        return value;
    }

    private static string JoinText(object? value) => value switch
    {
        string s => s.Trim(),
        IEnumerable list => string.Join(", ", list.Cast<object?>().Select(Unwrap).OfType<string>().Select(s => s.Trim()).Where(s => s.Length > 0)),
        null => "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>mpris:length is int64 microseconds; some players send other number types.</summary>
    private static TimeSpan? Microseconds(object? value)
    {
        double? us = value switch
        {
            long l => l, ulong u => u, int i => i, uint u => u, double d => d, short s => s, ushort s => s, byte b => b,
            _ => null,
        };
        return us is > 0 and < 1e15 ? TimeSpan.FromMicroseconds(us.Value) : null;
    }

    /// <summary>"file:///music/Artist%20-%20Song.mp3" → "Artist - Song".</summary>
    private static string FileTitle(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : url;
            return Path.GetFileNameWithoutExtension(path.TrimEnd('/'));
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"\.instance[_0-9]*$")]
    private static partial Regex InstanceSuffix();
}
