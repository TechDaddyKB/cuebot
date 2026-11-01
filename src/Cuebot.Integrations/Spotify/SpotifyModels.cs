using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuebot.Integrations.NowPlaying;

namespace Cuebot.Integrations.Spotify;

/// <summary>A Spotify problem, worded for streamers (shown in chat replies, the page and the log).</summary>
public class SpotifyException(string message, int status = 0, string reason = "", TimeSpan? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>Spotify's reason code (NO_ACTIVE_DEVICE, PREMIUM_REQUIRED…) or the OAuth error (invalid_grant…).</summary>
    public string Reason { get; } = reason;

    /// <summary>From a 429 answer: how long Spotify wants Cuebot to wait.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The sign-in is gone (refresh token expired or revoked, app changed): the user must sign in again.</summary>
public sealed class SpotifySignInRequiredException(string message, string reason = "sign_in") : SpotifyException(message, 401, reason);

/// <summary>Where Cuebot talks to Spotify (tests point these at a fake).</summary>
public sealed record SpotifyEndpoints(Uri Accounts, Uri Api)
{
    public static readonly SpotifyEndpoints Production = new(new Uri("https://accounts.spotify.com/"), new Uri("https://api.spotify.com/v1/"));
}

public sealed record SpotifyTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn, string Scope);

/// <summary>A Spotify track (or podcast episode).</summary>
public sealed record SpotifyTrack(string Uri, string Id, string Title, string Artist, string Album, string Link, string ArtUrl, TimeSpan? Duration)
{
    public Song ToSong() => new() { Title = Title, Artist = Artist, Album = Album, Link = Link, ArtUrl = ArtUrl, Duration = Duration, Uri = Uri };
}

/// <summary>What the Spotify account is playing (GET /me/player/currently-playing).</summary>
public sealed record SpotifyPlayback(bool IsPlaying, SpotifyTrack? Track, TimeSpan? Progress)
{
    public PlayerSession ToSession() =>
        new("spotify", "Spotify", IsPlaying ? PlaybackState.Playing : PlaybackState.Paused, Track?.ToSong() ?? Song.None, FromSpotify: true);
}

/// <summary>What a link or text names.</summary>
public enum SpotifyLinkKind { NotALink, Track, OtherLink }

public static partial class SpotifyLinks
{
    /// <summary>
    /// "spotify:track:ID", "https://open.spotify.com/track/ID?si=…" (also /intl-xx/track/ and /embed/track/) → Track
    /// with the 22-character id; other open.spotify.com / spotify: links (albums, playlists) → OtherLink.
    /// </summary>
    public static SpotifyLinkKind Parse(string text, out string trackId)
    {
        trackId = "";
        var t = text.Trim().Trim('<', '>');
        var match = TrackUri().Match(t);
        if (!match.Success) match = TrackUrl().Match(t);
        if (match.Success)
        {
            trackId = match.Groups["id"].Value;
            return SpotifyLinkKind.Track;
        }
        return OtherLink().IsMatch(t) ? SpotifyLinkKind.OtherLink : SpotifyLinkKind.NotALink;
    }

    [GeneratedRegex(@"^spotify:track:(?<id>[A-Za-z0-9]{22})$")]
    private static partial Regex TrackUri();

    [GeneratedRegex(@"^(?:https?://)?open\.spotify\.com/(?:intl-[A-Za-z-]+/)?(?:embed/)?track/(?<id>[A-Za-z0-9]{22})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackUrl();

    [GeneratedRegex(@"^(?:spotify:|(?:https?://)?(?:open\.spotify\.com|spotify\.link)/)", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLink();
}

/// <summary>Pure parsing of Spotify's answers (Web API reference, 2026). Missing fields read as empty.</summary>
public static class SpotifyJson
{
    public static SpotifyTokens ParseTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = Str(root, "access_token");
        if (access.Length == 0) throw new SpotifyException("Spotify did not send a sign-in token. Try again.");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 3600;
        return new SpotifyTokens(access, Str(root, "refresh_token"), TimeSpan.FromSeconds(seconds), Str(root, "scope"));
    }

    /// <summary>Null when nothing plays (204, an ad, or no item).</summary>
    public static SpotifyPlayback? ParseCurrentlyPlaying(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return null;
        var playing = root.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True;
        TimeSpan? progress = root.TryGetProperty("progress_ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? TimeSpan.FromMilliseconds(ms.GetDouble()) : null;
        return new SpotifyPlayback(playing, ParseTrack(item), progress);
    }

    /// <summary>The first track of a search answer (GET /search?type=track), or null.</summary>
    public static SpotifyTrack? ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("items", out var items)
               && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0 && items[0].ValueKind == JsonValueKind.Object
            ? ParseTrack(items[0])
            : null;
    }

    /// <summary>GET /me/player: whether it plays and the device volume (null when the device has none).</summary>
    public static (bool IsPlaying, int? Volume, string Device) ParsePlayer(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, null, "");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var playing = root.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True;
        int? volume = null;
        var device = "";
        if (root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number) volume = v.GetInt32();
            device = Str(d, "name");
        }
        return (playing, volume, device);
    }

    /// <summary>A track object (or an episode: the show stands in for the artist).</summary>
    public static SpotifyTrack ParseTrack(JsonElement item)
    {
        var artist = item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(a => Str(a, "name")).Where(n => n.Length > 0))
            : "";
        var album = "";
        var images = default(JsonElement);
        if (item.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            album = Str(a, "name");
            a.TryGetProperty("images", out images);
        }
        else if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
        {
            if (artist.Length == 0) artist = Str(show, "name");
            show.TryGetProperty("images", out images);
        }
        if (images.ValueKind != JsonValueKind.Array && item.TryGetProperty("images", out var own)) images = own;
        var art = images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Str(images[0], "url") : "";
        var link = item.TryGetProperty("external_urls", out var urls) ? Str(urls, "spotify") : "";
        TimeSpan? duration = item.TryGetProperty("duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.GetDouble() > 0
            ? TimeSpan.FromMilliseconds(ms.GetDouble())
            : null;
        return new SpotifyTrack(Str(item, "uri"), Str(item, "id"), Str(item, "name"), artist, album, link, art, duration);
    }

    /// <summary>The message and reason of an error answer (Web API {"error":{…}} or OAuth {"error":"…"}).</summary>
    public static (string Message, string Reason) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out var error)) return ("", "");
            if (error.ValueKind == JsonValueKind.String) return (Str(root, "error_description"), error.GetString() ?? "");
            return (Str(error, "message"), Str(error, "reason"));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => (v.GetString() ?? "").Trim(),
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";
}

/// <summary>Turns Spotify's error answers into plain words.</summary>
public static class SpotifyErrors
{
    public const string SignInAgain = "Spotify needs you to sign in again: open Cuebot's Music page and click Sign in.";
    public const string NeedsPremium = "Spotify needs Premium to change songs. Showing what plays works without it.";
    public const string NoDevice = "Open Spotify on a device first and play a song, then try again.";

    /// <param name="player">The request was a player command (a 404 then means "no device").</param>
    public static SpotifyException From(int status, string body, TimeSpan? retryAfter, bool player)
    {
        var (message, reason) = SpotifyJson.ParseError(body);
        var lower = message.ToLowerInvariant();
        switch (status)
        {
            case (int)HttpStatusCode.Unauthorized:
                return new SpotifySignInRequiredException(SignInAgain);
            case (int)HttpStatusCode.Forbidden:
                if (reason == "PREMIUM_REQUIRED" || lower.Contains("premium")) return new SpotifyException(NeedsPremium, status, "PREMIUM_REQUIRED");
                if (reason == "VOLUME_CONTROL_DISALLOW" || lower.Contains("volume"))
                    return new SpotifyException("This Spotify device does not let Cuebot change the volume (phones often do not).", status, reason);
                if (lower.Contains("registered") || lower.Contains("developer.spotify.com") || lower.Contains("dashboard"))
                    return new SpotifyException("Your Spotify account is not allowed in your Spotify app yet. On developer.spotify.com open your app, " +
                        "go to User Management and add the email of your Spotify account.", status, "USER_NOT_REGISTERED");
                if (lower.Contains("scope") || lower.Contains("permission"))
                    return new SpotifySignInRequiredException("Cuebot needs more Spotify permissions. Sign out and sign in again on the Music page.", "scope");
                return new SpotifyException($"Spotify said no{Detail(message)}. Make sure Spotify is playing on a device you can control.", status, reason);
            case (int)HttpStatusCode.NotFound when player || reason == "NO_ACTIVE_DEVICE" || lower.Contains("device"):
                return new SpotifyException(NoDevice, status, "NO_ACTIVE_DEVICE");
            case (int)HttpStatusCode.NotFound:
                return new SpotifyException($"Spotify could not find that{Detail(message)}.", status, reason);
            case (int)HttpStatusCode.TooManyRequests:
                var wait = retryAfter ?? TimeSpan.FromSeconds(5);
                return new SpotifyException($"Spotify is busy. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} seconds.",
                    status, "RATE_LIMITED", wait);
            case >= 500:
                return new SpotifyException("Spotify is having trouble right now. Try again in a minute.", status, reason);
            default:
                return new SpotifyException($"Spotify did not accept the request{Detail(message)} (error {status.ToString(CultureInfo.InvariantCulture)}).", status, reason);
        }
    }

    private static string Detail(string message) => message.Length > 0 ? $" (\"{message}\")" : "";
}
