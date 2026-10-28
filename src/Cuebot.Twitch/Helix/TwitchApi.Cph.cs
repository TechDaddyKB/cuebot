using System.Globalization;
using System.Text.Json;

namespace Cuebot.Twitch;

/// <summary>A clip from Helix "Get Clips".</summary>
public sealed record TwitchClipInfo(
    string Id, string Url, string EmbedUrl, string BroadcasterId, string BroadcasterName, string CreatorId, string CreatorName,
    string VideoId, string GameId, string Language, string Title, int ViewCount, DateTimeOffset? CreatedAt, string ThumbnailUrl,
    double Duration, bool IsFeatured);

/// <summary>A moderator or VIP of the channel.</summary>
public sealed record TwitchChannelUser(string UserId, string UserLogin, string UserName);

/// <summary>A blocked term of the channel.</summary>
public sealed record TwitchBlockedTerm(string Id, string Text);

/// <summary>
/// Helix reads behind Streamer.bot's clip, moderator, VIP, blocked-term and cheermote C# methods, plus sending a
/// message and pinning it in one go.
/// </summary>
public sealed partial class TwitchApi
{
    private const int MaxClips = 1000;

    /// <summary>
    /// Clips of a channel (<paramref name="broadcasterId"/>) or a category (<paramref name="gameId"/>), most viewed first
    /// (Twitch's order), optionally within a time range and only featured or only non-featured ones. At most
    /// <paramref name="count"/> clips (Twitch returns at most 1000 in total).
    /// </summary>
    public async Task<IReadOnlyList<TwitchClipInfo>> GetClipsAsync(string? broadcasterId, string? gameId, DateTimeOffset? startedAt,
        DateTimeOffset? endedAt, int count, bool? featured, CancellationToken ct = default)
    {
        RequireSignedIn();
        if (string.IsNullOrWhiteSpace(broadcasterId) == string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Give either a channel or a category for clips.");
        var limit = Math.Clamp(count <= 0 ? MaxClips : count, 1, MaxClips);
        var clips = new List<TwitchClipInfo>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(string.IsNullOrWhiteSpace(gameId)
                ? Q(("broadcaster_id", broadcasterId!.Trim()))
                : Q(("game_id", gameId.Trim())));
            query.Add(new("first", Math.Min(100, limit - clips.Count).ToString(CultureInfo.InvariantCulture)));
            if (startedAt is { } start) query.Add(new("started_at", start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            if (endedAt is { } end) query.Add(new("ended_at", end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            if (featured is { } f) query.Add(new("is_featured", f ? "true" : "false"));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, "clips", query, null, "Listing clips", ct).ConfigureAwait(false);
            var page = json.Items("data").Select(Clip).ToList();
            clips.AddRange(page);
            cursor = page.Count > 0 && (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && clips.Count < limit);
        return clips.Count > limit ? clips[..limit] : clips;
    }

    /// <summary>Every moderator of the channel.</summary>
    public Task<IReadOnlyList<TwitchChannelUser>> GetModeratorsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ModerationRead, TwitchScopes.ManageModerators, TwitchScopes.ReadModerators);
        return ChannelUsersAsync("moderation/moderators", "Listing moderators", ct);
    }

    /// <summary>Every VIP of the channel.</summary>
    public Task<IReadOnlyList<TwitchChannelUser>> GetVipsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageVips, TwitchScopes.ReadVips);
        return ChannelUsersAsync("channels/vips", "Listing VIPs", ct);
    }

    /// <summary>Every blocked term of the channel.</summary>
    public async Task<IReadOnlyList<TwitchBlockedTerm>> GetBlockedTermsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBlockedTerms);
        var terms = new List<TwitchBlockedTerm>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId), ("first", "100")));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, "moderation/blocked_terms", query, null, "Listing blocked terms", ct).ConfigureAwait(false);
            terms.AddRange(json.Items("data").Select(t => new TwitchBlockedTerm(t.Str("id"), t.Str("text"))));
            cursor = (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && terms.Count < 10_000);
        return terms;
    }

    /// <summary>The channel's cheermotes as Helix returns them (the "data" array).</summary>
    public async Task<JsonElement> GetCheermotesAsync(CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "bits/cheermotes", Q(("broadcaster_id", BroadcasterId)), null, "Listing cheermotes", ct)
            .ConfigureAwait(false);
        return json.Child("data") ?? default;
    }

    /// <summary>Sends a chat message as the broadcaster and pins it. Returns the message id.</summary>
    public async Task<string> SendAndPinMessageAsync(string message, int durationSeconds = 0, CancellationToken ct = default)
    {
        if (!_isConnected()) throw new InvalidOperationException("Twitch is not connected.");
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("No message to send.");
        Require(TwitchScopes.WriteChat);
        var id = await _helix.SendChatMessageAsync(BroadcasterId, BroadcasterId, message, ct).ConfigureAwait(false);
        if (id.Length == 0) throw new TwitchApiException("Twitch did not return the sent message's id.");
        await PinMessageAsync(id, durationSeconds, ct).ConfigureAwait(false);
        return id;
    }

    private async Task<IReadOnlyList<TwitchChannelUser>> ChannelUsersAsync(string path, string operation, CancellationToken ct)
    {
        var users = new List<TwitchChannelUser>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("first", "100")));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, path, query, null, operation, ct).ConfigureAwait(false);
            users.AddRange(json.Items("data").Select(u => new TwitchChannelUser(u.Str("user_id"), u.Str("user_login"), u.Str("user_name"))));
            cursor = (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && users.Count < 10_000);
        return users;
    }

    private static TwitchClipInfo Clip(JsonElement c) => new(
        c.Str("id"), c.Str("url"), c.Str("embed_url"), c.Str("broadcaster_id"), c.Str("broadcaster_name"), c.Str("creator_id"),
        c.Str("creator_name"), c.Str("video_id"), c.Str("game_id"), c.Str("language"), c.Str("title"), c.Int("view_count"),
        HelixParse.Time(c, "created_at"), c.Str("thumbnail_url"),
        c.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0, c.Bool("is_featured"));
}
