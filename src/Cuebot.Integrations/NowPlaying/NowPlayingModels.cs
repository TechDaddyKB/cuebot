using System.Globalization;

namespace Cuebot.Integrations.NowPlaying;

public enum PlaybackState { Stopped, Paused, Playing }

/// <summary>A media key Cuebot can press on a player.</summary>
public enum MediaCommand { PlayPause, Play, Pause, Next, Previous }

/// <summary>Where the "now playing" song comes from.</summary>
public enum NowPlayingSource
{
    /// <summary>Whatever plays: a music player on this computer first, then Spotify.</summary>
    Automatic,

    /// <summary>Only the player named in <see cref="NowPlayingSettings.Player"/>.</summary>
    Player,

    /// <summary>Only the signed-in Spotify account (works on every system, also macOS).</summary>
    Spotify,
}

/// <summary>A song (or episode) as a player reports it. Empty strings for what the player does not say.</summary>
public sealed record Song
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";

    /// <summary>Cover image: an http(s) link, or a file:// address for local players.</summary>
    public string ArtUrl { get; init; } = "";

    public TimeSpan? Duration { get; init; }

    /// <summary>A web link to the song (Spotify only).</summary>
    public string Link { get; init; } = "";

    /// <summary>The Spotify URI (spotify:track:…), Spotify only.</summary>
    public string Uri { get; init; } = "";

    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0;

    /// <summary>"3:45" (or "1:02:03"); "" when unknown.</summary>
    public string DurationText => Duration is { } d && d > TimeSpan.Zero
        ? d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"m\:ss", CultureInfo.InvariantCulture)
        : "";

    /// <summary>Same song, ignoring art and links that some players fill in later.</summary>
    public bool SameSongAs(Song? other) => other is not null
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Artist, other.Artist, StringComparison.Ordinal)
        && string.Equals(Album, other.Album, StringComparison.Ordinal);

    public static readonly Song None = new();
}

/// <summary>
/// One player Cuebot can see: an MPRIS player (Linux), a media session (Windows) or the Spotify account.
/// </summary>
/// <param name="Id">Stable while the player runs: the bus name, the app id, or "spotify".</param>
/// <param name="Name">Friendly name ("Spotify", "mpv", "Firefox").</param>
/// <param name="FromSpotify">True for the Spotify account (web API) rather than a player on this computer.</param>
public sealed record PlayerSession(string Id, string Name, PlaybackState State, Song Song, bool FromSpotify = false)
{
    public bool IsPlaying => State == PlaybackState.Playing;
}

/// <summary>Picks the session "now playing" means for the chosen source.</summary>
public static class SessionPicker
{
    /// <param name="system">Players on this computer.</param>
    /// <param name="systemCurrentId">The session the system itself calls current (Windows), if any.</param>
    /// <param name="spotify">The Spotify account's playback, when signed in.</param>
    public static PlayerSession? Pick(IReadOnlyList<PlayerSession> system, string? systemCurrentId, PlayerSession? spotify,
        NowPlayingSource source, string player)
    {
        var withSong = system.Where(s => !s.Song.IsEmpty).ToList();
        var spotifySong = spotify is { Song.IsEmpty: false } ? spotify : null;
        switch (source)
        {
            case NowPlayingSource.Spotify:
                return spotifySong;
            case NowPlayingSource.Player:
                var wanted = player.Trim();
                if (wanted.Length == 0) return null;
                var matching = withSong.Where(s => Matches(s, wanted)).ToList();
                if (spotifySong is not null && Matches(spotifySong, wanted)) matching.Add(spotifySong);
                return Best(matching, systemCurrentId);
            default:
                var playing = withSong.Where(s => s.IsPlaying).ToList();
                if (playing.Count > 0) return Best(playing, systemCurrentId);
                if (spotifySong is { IsPlaying: true }) return spotifySong;
                return Best(withSong, systemCurrentId) ?? spotifySong;
        }
    }

    /// <summary>True when <paramref name="wanted"/> is part of the player's name or id ("spotify" matches "Spotify").</summary>
    public static bool Matches(PlayerSession session, string wanted) =>
        session.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) || session.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase);

    private static PlayerSession? Best(List<PlayerSession> candidates, string? currentId) =>
        candidates.FirstOrDefault(s => s.IsPlaying && s.Id == currentId)
        ?? candidates.FirstOrDefault(s => s.IsPlaying)
        ?? candidates.FirstOrDefault(s => s.Id == currentId)
        ?? candidates.FirstOrDefault();
}
