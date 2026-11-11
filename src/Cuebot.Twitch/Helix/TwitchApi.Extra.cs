using System.Globalization;
using System.Text.Json;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>Helix reads and actions added from the 2026 Twitch API reference (clips, hype train, schedule, AutoMod...).</summary>
public sealed partial class TwitchApi
{
    private const int MaxListItems = 1000;

    // ---------------------------------------------------------------- clips

    /// <summary>
    /// Clips the live stream with an optional title and length (5 to 60 seconds, Twitch's default 30). Twitch returns
    /// as soon as the clip is requested; it may take a few seconds to exist.
    /// </summary>
    public async Task<TwitchClip> CreateClipAsync(bool hasDelay, string? title, double? durationSeconds, CancellationToken ct = default)
    {
        Require(TwitchScopes.EditClips);
        var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("has_delay", hasDelay ? "true" : "false")));
        if (!string.IsNullOrWhiteSpace(title)) query.Add(new("title", title.Trim()));
        if (durationSeconds is { } d && d > 0) query.Add(new("duration", ClipLength(d)));
        var json = await _helix.RequestAsync(HttpMethod.Post, "clips", query, null, "Creating clip", ct).ConfigureAwait(false);
        var clip = json.Items("data").FirstOrDefault();
        return new TwitchClip(clip.Str("id"), clip.Str("edit_url"));
    }

    /// <summary>
    /// Clips a past broadcast (VOD): the clip ends <paramref name="endOffsetSeconds"/> into the video and lasts
    /// <paramref name="durationSeconds"/> (5 to 60). Twitch requires a title.
    /// </summary>
    public async Task<TwitchClip> CreateClipFromVodAsync(string videoId, int endOffsetSeconds, string title, double? durationSeconds = null,
        CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageClips, TwitchScopes.EditorManageClips);
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("No video id given.");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Twitch needs a title for a clip from a past broadcast.");
        var length = durationSeconds is { } d && d > 0 ? Math.Clamp(d, 5, 60) : 30;
        var query = new List<KeyValuePair<string, string>>(Q(("editor_id", BroadcasterId), ("broadcaster_id", BroadcasterId),
            ("vod_id", videoId.Trim()), ("vod_offset", Math.Max((int)Math.Ceiling(length), endOffsetSeconds).ToString(CultureInfo.InvariantCulture)),
            ("title", title.Trim()), ("duration", ClipLength(length))));
        var json = await _helix.RequestAsync(HttpMethod.Post, "videos/clips", query, null, "Creating clip from past broadcast", ct).ConfigureAwait(false);
        var clip = json.Items("data").FirstOrDefault();
        return new TwitchClip(clip.Str("id"), clip.Str("edit_url"));
    }

    private static string ClipLength(double seconds) =>
        Math.Round(Math.Clamp(seconds, 5, 60), 1).ToString("0.0", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- videos, search, markers

    /// <summary>A channel's newest videos (type: archive, highlight, upload or all), newest first.</summary>
    public async Task<IReadOnlyList<TwitchVideo>> GetVideosAsync(string? user = null, string type = "archive", int count = 1,
        CancellationToken ct = default)
    {
        RequireSignedIn();
        var userId = string.IsNullOrWhiteSpace(user) ? BroadcasterId : await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        var kind = type.Trim().ToLowerInvariant() is "archive" or "highlight" or "upload" or "all" ? type.Trim().ToLowerInvariant() : "archive";
        var json = await _helix.RequestAsync(HttpMethod.Get, "videos",
            Q(("user_id", userId), ("type", kind), ("sort", "time"), ("first", Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture))),
            null, "Getting videos", ct).ConfigureAwait(false);
        return json.Items("data").Select(v => new TwitchVideo(v.Str("id"), v.Str("user_login"), v.Str("user_name"), v.Str("title"),
            v.Str("url"), v.Str("type"), v.Str("duration"), v.Int("view_count"), HelixParse.Time(v, "created_at"), v.Str("stream_id"),
            v.Str("thumbnail_url").Replace("%{width}", "320", StringComparison.Ordinal).Replace("%{height}", "180", StringComparison.Ordinal)))
            .ToArray();
    }

    /// <summary>Channels whose name matches <paramref name="query"/> (live ones first when <paramref name="liveOnly"/>).</summary>
    public async Task<IReadOnlyList<TwitchChannelSearchResult>> SearchChannelsAsync(string query, bool liveOnly = false, int count = 10,
        CancellationToken ct = default)
    {
        RequireSignedIn();
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Nothing to search for.");
        var json = await _helix.RequestAsync(HttpMethod.Get, "search/channels",
            Q(("query", query.Trim()), ("live_only", liveOnly ? "true" : "false"), ("first", Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture))),
            null, "Searching channels", ct).ConfigureAwait(false);
        return json.Items("data").Select(c => new TwitchChannelSearchResult(c.Str("id"), c.Str("broadcaster_login"), c.Str("display_name"),
            c.Bool("is_live"), c.Str("title"), c.Str("game_name"), c.Str("game_id"), c.Str("thumbnail_url"), HelixParse.Time(c, "started_at"))).ToArray();
    }

    /// <summary>The stream markers of the current (or most recent) broadcast, oldest first.</summary>
    public async Task<IReadOnlyList<TwitchStreamMarker>> GetStreamMarkersAsync(int count = 20, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBroadcast);
        var json = await _helix.RequestAsync(HttpMethod.Get, "streams/markers",
            Q(("user_id", BroadcasterId), ("first", Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture))), null,
            "Getting stream markers", ct).ConfigureAwait(false);
        return json.Items("data").SelectMany(u => u.Items("videos")).SelectMany(v => v.Items("markers").Select(m =>
            new TwitchStreamMarker(m.Str("id"), m.Str("description"), m.Int("position_seconds"), HelixParse.Time(m, "created_at"), m.Str("url"),
                v.Str("video_id")))).OrderBy(m => m.PositionSeconds).ToArray();
    }

    // ---------------------------------------------------------------- hype train, goals, bits, power-ups, charity

    /// <summary>The running hype train (null when none) and the channel's records.</summary>
    public async Task<TwitchHypeTrainStatus> GetHypeTrainStatusAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadHypeTrain);
        var json = await _helix.RequestAsync(HttpMethod.Get, "hypetrain/status", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting hype train status", ct).ConfigureAwait(false);
        var data = json.Items("data").FirstOrDefault();
        var current = data.ValueKind == JsonValueKind.Object ? data.Child("current") : null;
        var record = data.ValueKind == JsonValueKind.Object ? data.Child("all_time_high") : null;
        TwitchHypeTrain? train = current is { ValueKind: JsonValueKind.Object } c
            ? new TwitchHypeTrain(c.Str("id"), c.Str("type"), c.Int("level"), c.Int("total"), c.Int("progress"), c.Int("goal"),
                HelixParse.Time(c, "started_at"), HelixParse.Time(c, "expires_at"), c.Bool("is_shared_train"),
                c.Items("top_contributions").Select(t => new TwitchHypeTrainContribution(t.Str("user_id"), t.Str("user_login"),
                    t.Str("user_name"), t.Str("type"), t.Int("total"))).ToArray())
            : null;
        return new TwitchHypeTrainStatus(train,
            record is { ValueKind: JsonValueKind.Object } r ? r.Int("level") : 0,
            record is { ValueKind: JsonValueKind.Object } r2 ? r2.Int("total") : 0,
            record is { ValueKind: JsonValueKind.Object } r3 ? HelixParse.Time(r3, "achieved_at") : null);
    }

    /// <summary>The channel's active creator goals.</summary>
    public async Task<IReadOnlyList<TwitchCreatorGoal>> GetCreatorGoalsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadGoals);
        var json = await _helix.RequestAsync(HttpMethod.Get, "goals", Q(("broadcaster_id", BroadcasterId)), null, "Getting creator goals", ct)
            .ConfigureAwait(false);
        return json.Items("data").Select(g => new TwitchCreatorGoal(g.Str("id"), g.Str("type"), g.Str("description"), g.Int("current_amount"),
            g.Int("target_amount"), HelixParse.Time(g, "created_at"))).ToArray();
    }

    /// <summary>Top cheerers for a period (day, week, month, year or all), ranked.</summary>
    public async Task<IReadOnlyList<TwitchLeaderboardEntry>> GetBitsLeaderboardAsync(int count = 10, string period = "all",
        CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadBits);
        var span = period.Trim().ToLowerInvariant() is "day" or "week" or "month" or "year" or "all" ? period.Trim().ToLowerInvariant() : "all";
        var json = await _helix.RequestAsync(HttpMethod.Get, "bits/leaderboard",
            Q(("count", Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture)), ("period", span)), null,
            "Getting bits leaderboard", ct).ConfigureAwait(false);
        return json.Items("data").Select(l => new TwitchLeaderboardEntry(l.Int("rank"), l.Str("user_id"), l.Str("user_login"), l.Str("user_name"),
            l.Int("score"))).ToArray();
    }

    /// <summary>The channel's custom Power-ups (for picking one in a trigger, or reading its state).</summary>
    public async Task<IReadOnlyList<TwitchCustomPowerUp>> GetCustomPowerUpsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadBits);
        var json = await _helix.RequestAsync(HttpMethod.Get, "bits/custom_power_ups", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting custom power-ups", ct).ConfigureAwait(false);
        return json.Items("data").Select(p => new TwitchCustomPowerUp(p.Str("id"), p.Str("title"), p.Str("prompt"), p.Int("bits"),
            p.Bool("is_enabled"), p.Bool("is_paused"), p.Bool("is_in_stock"), p.Bool("is_user_input_required"),
            (p.Child("image") ?? p.Child("default_image") ?? default).Str("url_4x"))).ToArray();
    }

    /// <summary>The donations of the running charity campaign, newest first as Twitch lists them.</summary>
    public async Task<IReadOnlyList<TwitchCharityDonation>> GetCharityDonationsAsync(int count = 20, CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadCharity);
        var json = await _helix.RequestAsync(HttpMethod.Get, "charity/donations",
            Q(("broadcaster_id", BroadcasterId), ("first", Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture))), null,
            "Getting charity donations", ct).ConfigureAwait(false);
        return json.Items("data").Select(d =>
        {
            var amount = d.Child("amount") ?? default;
            return new TwitchCharityDonation(d.Str("id"), d.Str("user_id"), d.Str("user_login"), d.Str("user_name"), Money(amount), amount.Str("currency"));
        }).ToArray();
    }

    // ---------------------------------------------------------------- moderation

    /// <summary>Unban requests with a status (pending, approved, denied, acknowledged or canceled).</summary>
    public async Task<IReadOnlyList<TwitchUnbanRequest>> GetUnbanRequestsAsync(string status = "pending", CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageUnbanRequests);
        var wanted = status.Trim().ToLowerInvariant() is "pending" or "approved" or "denied" or "acknowledged" or "canceled"
            ? status.Trim().ToLowerInvariant() : "pending";
        var requests = new List<TwitchUnbanRequest>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId),
                ("status", wanted), ("first", "100")));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, "moderation/unban_requests", query, null, "Getting unban requests", ct)
                .ConfigureAwait(false);
            requests.AddRange(json.Items("data").Select(UnbanRequest));
            cursor = (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && requests.Count < MaxListItems);
        return requests;
    }

    /// <summary>Approves or denies an unban request, with an optional message to the user (up to 500 characters).</summary>
    public async Task<TwitchUnbanRequest?> ResolveUnbanRequestAsync(string requestId, bool approve, string? resolutionText = null,
        CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageUnbanRequests);
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("No unban request id given.");
        var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId),
            ("unban_request_id", requestId.Trim()), ("status", approve ? "approved" : "denied")));
        if (!string.IsNullOrWhiteSpace(resolutionText))
        {
            CheckLength(resolutionText, 500, "An unban request answer");
            query.Add(new("resolution_text", resolutionText.Trim()));
        }
        var json = await _helix.RequestAsync(HttpMethod.Patch, "moderation/unban_requests", query, null,
            approve ? "Approving unban request" : "Denying unban request", ct).ConfigureAwait(false);
        return json.Items("data").Select(UnbanRequest).FirstOrDefault();
    }

    private static TwitchUnbanRequest UnbanRequest(JsonElement r) => new(r.Str("id"), r.Str("user_id"), r.Str("user_login"), r.Str("user_name"),
        r.Str("text"), r.Str("status"), HelixParse.Time(r, "created_at"), r.Str("moderator_name"), r.Str("resolution_text"));

    /// <summary>Banned and timed-out users; with <paramref name="user"/>, only that user (an empty list when not banned).</summary>
    public async Task<IReadOnlyList<TwitchBannedUser>> GetBannedUsersAsync(string? user = null, CancellationToken ct = default)
    {
        Require(TwitchScopes.ModerationRead, TwitchScopes.ManageBannedUsers);
        string? userId = string.IsNullOrWhiteSpace(user) ? null : await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        var banned = new List<TwitchBannedUser>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("first", "100")));
            if (userId is not null) query.Add(new("user_id", userId));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, "moderation/banned", query, null, "Getting banned users", ct).ConfigureAwait(false);
            banned.AddRange(json.Items("data").Select(b => new TwitchBannedUser(b.Str("user_id"), b.Str("user_login"), b.Str("user_name"),
                b.Str("reason"), HelixParse.Time(b, "created_at"), b.Str("expires_at") is { Length: > 0 } ? HelixParse.Time(b, "expires_at") : null,
                b.Str("moderator_name"))));
            cursor = (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && banned.Count < 10_000);
        return banned;
    }

    /// <summary>The channel's AutoMod levels (0 off to 4 strictest); OverallLevel is null when set per category.</summary>
    public async Task<TwitchAutomodSettings> GetAutomodSettingsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageAutomodSettings);
        var json = await _helix.RequestAsync(HttpMethod.Get, "moderation/automod/settings",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)), null, "Getting AutoMod settings", ct).ConfigureAwait(false);
        return AutomodSettings(json.Items("data").FirstOrDefault());
    }

    /// <summary>Sets every AutoMod category to one overall level (0 off to 4 strictest).</summary>
    public async Task<TwitchAutomodSettings> SetAutomodLevelAsync(int overallLevel, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageAutomodSettings);
        var json = await _helix.RequestAsync(HttpMethod.Put, "moderation/automod/settings",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)), new { overall_level = Math.Clamp(overallLevel, 0, 4) },
            "Setting the AutoMod level", ct).ConfigureAwait(false);
        return AutomodSettings(json.Items("data").FirstOrDefault());
    }

    private static TwitchAutomodSettings AutomodSettings(JsonElement s) => new(
        s.Child("overall_level") is { ValueKind: JsonValueKind.Number } level ? level.GetInt32() : null,
        s.Int("disability"), s.Int("aggression"), s.Int("sexuality_sex_or_gender"), s.Int("misogyny"), s.Int("bullying"), s.Int("swearing"),
        s.Int("race_ethnicity_or_religion"), s.Int("sex_based_terms"));

    /// <summary>Blocks a user for the broadcaster's account (they can no longer whisper, follow or chat-mention it).</summary>
    public async Task BlockUserAsync(string user, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBlockedUsers);
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await _helix.RequestAsync(HttpMethod.Put, "users/blocks", Q(("target_user_id", id), ("source_context", "chat")), null, "Blocking user", ct)
            .ConfigureAwait(false);
    }

    public async Task UnblockUserAsync(string user, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBlockedUsers);
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await _helix.RequestAsync(HttpMethod.Delete, "users/blocks", Q(("target_user_id", id)), null, "Unblocking user", ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- schedule

    /// <summary>The next scheduled broadcasts (from now) and the vacation, if one is set.</summary>
    public async Task<TwitchSchedule> GetScheduleAsync(int count = 5, CancellationToken ct = default)
    {
        RequireSignedIn();
        JsonElement json;
        try
        {
            json = await _helix.RequestAsync(HttpMethod.Get, "schedule",
                Q(("broadcaster_id", BroadcasterId), ("first", Math.Clamp(count, 1, 25).ToString(CultureInfo.InvariantCulture))), null,
                "Getting the stream schedule", ct).ConfigureAwait(false);
        }
        catch (TwitchApiException ex) when (ex.StatusCode == 404)
        {
            return new TwitchSchedule([], null, null);   // no schedule set up
        }
        var data = json.Child("data") ?? default;
        var vacation = data.Child("vacation");
        var segments = data.Items("segments").Select(s =>
        {
            var category = s.Child("category");
            return new TwitchScheduleSegment(s.Str("id"), s.Str("title"), HelixParse.Time(s, "start_time"), HelixParse.Time(s, "end_time"),
                category is { ValueKind: JsonValueKind.Object } c ? c.Str("name") : "", category is { ValueKind: JsonValueKind.Object } c2 ? c2.Str("id") : "",
                s.Bool("is_recurring"), s.Str("canceled_until").Length > 0);
        }).ToArray();
        return new TwitchSchedule(segments,
            vacation is { ValueKind: JsonValueKind.Object } v ? HelixParse.Time(v, "start_time") : null,
            vacation is { ValueKind: JsonValueKind.Object } v2 ? HelixParse.Time(v2, "end_time") : null);
    }

    /// <summary>Adds a broadcast to the schedule; <paramref name="timeZone"/> is an IANA name (empty: this computer's).</summary>
    public async Task<string> AddScheduleSegmentAsync(DateTimeOffset start, int durationMinutes, string? title = null, string? category = null,
        bool recurring = false, string? timeZone = null, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageSchedule);
        var body = new Dictionary<string, object>
        {
            ["start_time"] = start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["timezone"] = string.IsNullOrWhiteSpace(timeZone) ? IanaTimeZone() : timeZone.Trim(),
            ["duration"] = Math.Clamp(durationMinutes, 30, 1380).ToString(CultureInfo.InvariantCulture),
            ["is_recurring"] = recurring,
        };
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title.Trim().Length > 140 ? title.Trim()[..140] : title.Trim();
        if (!string.IsNullOrWhiteSpace(category) && await GetCategoryByNameOrIdAsync(category, ct).ConfigureAwait(false) is { } found)
            body["category_id"] = found;
        var json = await _helix.RequestAsync(HttpMethod.Post, "schedule/segment", Q(("broadcaster_id", BroadcasterId)), body,
            "Adding to the stream schedule", ct).ConfigureAwait(false);
        return ((json.Child("data") ?? default).Items("segments").FirstOrDefault()).Str("id");
    }

    /// <summary>Cancels (or restores) one scheduled broadcast without deleting it.</summary>
    public Task SetScheduleSegmentCanceledAsync(string segmentId, bool canceled, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageSchedule);
        if (string.IsNullOrWhiteSpace(segmentId)) throw new ArgumentException("No schedule segment id given.");
        return _helix.RequestAsync(HttpMethod.Patch, "schedule/segment", Q(("broadcaster_id", BroadcasterId), ("id", segmentId.Trim())),
            new { is_canceled = canceled }, canceled ? "Cancelling a scheduled broadcast" : "Restoring a scheduled broadcast", ct);
    }

    public Task DeleteScheduleSegmentAsync(string segmentId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageSchedule);
        if (string.IsNullOrWhiteSpace(segmentId)) throw new ArgumentException("No schedule segment id given.");
        return _helix.RequestAsync(HttpMethod.Delete, "schedule/segment", Q(("broadcaster_id", BroadcasterId), ("id", segmentId.Trim())), null,
            "Removing a scheduled broadcast", ct);
    }

    /// <summary>Turns vacation on (between the two times) or off. Scheduled broadcasts in it are hidden, not deleted.</summary>
    public Task SetVacationAsync(bool enabled, DateTimeOffset? start = null, DateTimeOffset? end = null, string? timeZone = null,
        CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageSchedule);
        var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("is_vacation_enabled", enabled ? "true" : "false")));
        if (enabled)
        {
            if (start is null || end is null || end <= start) throw new ArgumentException("A vacation needs a start and a later end.");
            query.Add(new("vacation_start_time", start.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            query.Add(new("vacation_end_time", end.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            query.Add(new("timezone", string.IsNullOrWhiteSpace(timeZone) ? IanaTimeZone() : timeZone.Trim()));
        }
        return _helix.RequestAsync(HttpMethod.Patch, "schedule/settings", query, null, enabled ? "Starting a vacation" : "Ending the vacation", ct);
    }

    private static string IanaTimeZone() =>
        TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";

    private async Task<string?> GetCategoryByNameOrIdAsync(string category, CancellationToken ct)
    {
        var text = category.Trim();
        if (text.All(char.IsAsciiDigit)) return text;
        var json = await _helix.RequestAsync(HttpMethod.Get, "games", Q(("name", text)), null, "Finding category", ct).ConfigureAwait(false);
        return json.Items("data").Select(g => g.Str("id")).FirstOrDefault();
    }

    // ---------------------------------------------------------------- chat extras

    /// <summary>The shared chat session the channel is in, or null.</summary>
    public async Task<TwitchSharedChatSession?> GetSharedChatSessionAsync(CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "shared_chat/session", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting the shared chat session", ct).ConfigureAwait(false);
        return json.Items("data").Select(s => new TwitchSharedChatSession(s.Str("session_id"), s.Str("host_broadcaster_id"),
            s.Items("participants").Select(p => p.Str("broadcaster_id")).ToArray(), HelixParse.Time(s, "created_at"))).FirstOrDefault();
    }

    /// <summary>Chat name colors (hex, empty when never set) of up to 100 users, by user id.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetChatColorsAsync(IEnumerable<string> users, CancellationToken ct = default)
    {
        RequireSignedIn();
        var ids = new List<string>();
        foreach (var user in users.Where(u => !string.IsNullOrWhiteSpace(u)).Take(100)) ids.Add(await ResolveUserIdAsync(user, ct).ConfigureAwait(false));
        if (ids.Count == 0) return new Dictionary<string, string>();
        var json = await _helix.RequestAsync(HttpMethod.Get, "chat/color", ids.Select(i => KeyValuePair.Create("user_id", i)), null,
            "Getting chat colors", ct).ConfigureAwait(false);
        return json.Items("data").ToDictionary(c => c.Str("user_id"), c => c.Str("color"), StringComparer.Ordinal);
    }

    /// <summary>
    /// Emotes: the channel's own (<paramref name="source"/> "channel"), Twitch's global ones ("global"), or those of
    /// emote set ids ("set", with <paramref name="setIds"/>).
    /// </summary>
    public async Task<IReadOnlyList<TwitchEmote>> GetEmotesAsync(string source = "channel", IEnumerable<string>? setIds = null,
        CancellationToken ct = default)
    {
        RequireSignedIn();
        var (path, query) = source.Trim().ToLowerInvariant() switch
        {
            "global" => ("chat/emotes/global", Enumerable.Empty<KeyValuePair<string, string>>()),
            "set" => ("chat/emotes/set", (setIds ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Take(25).Select(s => KeyValuePair.Create("emote_set_id", s.Trim()))),
            _ => ("chat/emotes", Q(("broadcaster_id", BroadcasterId))),
        };
        var json = await _helix.RequestAsync(HttpMethod.Get, path, query, null, "Getting emotes", ct).ConfigureAwait(false);
        var template = json.Str("template");
        return json.Items("data").Select(e =>
        {
            var animated = e.Strings("format").Contains("animated");
            var url = template.Length > 0
                ? template.Replace("{{id}}", e.Str("id"), StringComparison.Ordinal).Replace("{{format}}", animated ? "animated" : "static", StringComparison.Ordinal)
                    .Replace("{{theme_mode}}", "dark", StringComparison.Ordinal).Replace("{{scale}}", "3.0", StringComparison.Ordinal)
                : (e.Child("images") ?? default).Str("url_4x");
            return new TwitchEmote(e.Str("id"), e.Str("name"), e.Str("emote_type"), e.Str("tier"), e.Str("emote_set_id"), animated, url);
        }).ToArray();
    }

    /// <summary>Twitch's content classification labels (id and name), for setting them on the channel.</summary>
    public async Task<IReadOnlyList<(string Id, string Name, string Description)>> GetContentLabelsAsync(CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "content_classification_labels", null, null, "Getting content labels", ct)
            .ConfigureAwait(false);
        return json.Items("data").Select(l => (l.Str("id"), l.Str("name"), l.Str("description"))).ToArray();
    }
}
