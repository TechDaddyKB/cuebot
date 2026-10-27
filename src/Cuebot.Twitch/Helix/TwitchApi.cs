using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>
/// Helix operations for the signed-in broadcaster's channel (the broadcaster is also the moderator), plus chat and
/// whispers that can come from the optional bot account. Every call checks sign-in and the scope it needs first,
/// so a missing permission fails with a message saying what to enable.
/// </summary>
public sealed class TwitchApi
{
    private const int MaxPollChoices = 5;
    private const int MaxPredictionOutcomes = 10;

    private readonly HelixClient _helix;
    private readonly TwitchTokenManager _tokens;
    private readonly HelixClient _botHelix;
    private readonly TwitchTokenManager _botTokens;
    private readonly Func<TwitchSettings> _settings;
    private readonly Func<bool> _isConnected;
    private readonly Action<BotEvent> _publish;
    private readonly SentMessageTracker _sent;

    internal TwitchApi(HelixClient helix, TwitchTokenManager tokens, HelixClient botHelix, TwitchTokenManager botTokens,
        Func<TwitchSettings> settings, Func<bool> isConnected, Action<BotEvent> publish, SentMessageTracker sent)
    {
        _helix = helix;
        _tokens = tokens;
        _botHelix = botHelix;
        _botTokens = botTokens;
        _settings = settings;
        _isConnected = isConnected;
        _publish = publish;
        _sent = sent;
    }

    private string BroadcasterId => _tokens.UserId;

    /// <summary>The signed-in broadcaster (empty when signed out).</summary>
    public (string UserId, string Login) Broadcaster => (_tokens.UserId, _tokens.Login);

    /// <summary>The signed-in bot account (empty when there is none).</summary>
    public (string UserId, string Login) Bot => (_botTokens.UserId, _botTokens.Login);

    /// <summary>True when the bot account is signed in.</summary>
    public bool IsBotReady => _botTokens.HasToken && _botTokens.UserId.Length > 0;

    // ---------------------------------------------------------------- chat

    /// <summary>
    /// Posts to the broadcaster's chat (optionally as a reply). Blank messages are ignored. Throws
    /// <see cref="InvalidOperationException"/> when not connected and <see cref="TwitchApiException"/> when Twitch
    /// rejects or drops the message. Returns the account that sent it.
    /// </summary>
    public async Task<TwitchSender> SendChatMessageAsync(string message, TwitchSender sender = TwitchSender.Default,
        string? replyToMessageId = null, CancellationToken ct = default)
    {
        if (!_isConnected()) throw new InvalidOperationException("Twitch is not connected.");
        if (string.IsNullOrWhiteSpace(message)) return sender;
        var chosen = Choose(sender, _settings().SendChatAsBot, TwitchScopes.WriteChat);
        var (helix, tokens) = Account(chosen);
        RequireScope(tokens, chosen, TwitchScopes.WriteChat);
        var id = await helix.SendChatMessageAsync(BroadcasterId, tokens.UserId, message, ct, replyToMessageId).ConfigureAwait(false);
        _sent.Add(id);
        return chosen;
    }

    /// <summary>Whispers <paramref name="user"/> (login, or "id:..."). The sending account needs a verified phone number.</summary>
    public async Task SendWhisperAsync(string user, string message, TwitchSender sender = TwitchSender.Default, CancellationToken ct = default)
    {
        RequireSignedIn();
        if (string.IsNullOrWhiteSpace(message)) return;
        var chosen = Choose(sender, _settings().SendWhispersAsBot, TwitchScopes.ManageWhispers);
        var (helix, tokens) = Account(chosen);
        RequireScope(tokens, chosen, TwitchScopes.ManageWhispers);
        var to = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await helix.RequestAsync(HttpMethod.Post, "whispers", Q(("from_user_id", tokens.UserId), ("to_user_id", to)),
            new { message }, "Sending whisper", ct).ConfigureAwait(false);
    }

    public async Task SendAnnouncementAsync(string message, string color = "primary", CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageAnnouncements);
        if (string.IsNullOrWhiteSpace(message)) return;
        var normalized = AnnouncementColors.All.FirstOrDefault(c => c.Equals(color.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "primary";
        await _helix.RequestAsync(HttpMethod.Post, "chat/announcements", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            new { message, color = normalized }, "Sending announcement", ct).ConfigureAwait(false);
    }

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageChatMessages);
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("No message id to delete.");
        return _helix.RequestAsync(HttpMethod.Delete, "moderation/chat",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId), ("message_id", messageId.Trim())), null,
            "Deleting chat message", ct);
    }

    public Task ClearChatAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageChatMessages);
        return _helix.RequestAsync(HttpMethod.Delete, "moderation/chat",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)), null, "Clearing chat", ct);
    }

    public async Task<IReadOnlyList<TwitchChatter>> GetChattersAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadChatters);
        var chatters = new List<TwitchChatter>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>(Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId), ("first", "1000")));
            if (cursor is not null) query.Add(new("after", cursor));
            var json = await _helix.RequestAsync(HttpMethod.Get, "chat/chatters", query, null, "Listing chatters", ct).ConfigureAwait(false);
            chatters.AddRange(json.Items("data").Select(c => new TwitchChatter(c.Str("user_id"), c.Str("user_login"), c.Str("user_name"))));
            cursor = (json.Child("pagination") ?? default).Str("cursor") is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null && chatters.Count < 10_000);
        return chatters;
    }

    // ---------------------------------------------------------------- users

    /// <summary>Looks up a user by login (a leading @ is ignored) or by id written as "id:12345". Null when not found.</summary>
    public async Task<TwitchUser?> GetUserAsync(string user, CancellationToken ct = default)
    {
        RequireSignedIn();
        var (key, value) = UserQuery(user);
        var json = await _helix.RequestAsync(HttpMethod.Get, "users", Q((key, value)), null, "Looking up user", ct).ConfigureAwait(false);
        return json.Items("data").Select(HelixParse.User).FirstOrDefault();
    }

    public async Task<TwitchUser?> GetUserByIdAsync(string userId, CancellationToken ct = default) =>
        await GetUserAsync("id:" + userId, ct).ConfigureAwait(false);

    /// <summary>When <paramref name="userId"/> follows the channel; null when they do not.</summary>
    public async Task<TwitchFollow?> GetFollowAsync(string userId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadFollowers);
        var json = await _helix.RequestAsync(HttpMethod.Get, "channels/followers", Q(("broadcaster_id", BroadcasterId), ("user_id", userId)),
            null, "Getting follow info", ct).ConfigureAwait(false);
        return json.Items("data").Select(f => new TwitchFollow(f.Str("user_id"), f.Str("user_login"), f.Str("user_name"),
            HelixParse.Time(f, "followed_at") ?? DateTimeOffset.MinValue)).FirstOrDefault();
    }

    /// <summary>The user's subscription to the channel; null when not subscribed.</summary>
    public async Task<TwitchSubscription?> GetSubscriptionAsync(string userId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadSubscriptions);
        var json = await _helix.RequestAsync(HttpMethod.Get, "subscriptions", Q(("broadcaster_id", BroadcasterId), ("user_id", userId)),
            null, "Getting subscription", ct).ConfigureAwait(false);
        return json.Items("data").Select(s => new TwitchSubscription(s.Str("user_id"), s.Str("user_login"), s.Str("user_name"),
            Tier(s.Str("tier")), s.Bool("is_gift"), s.Str("gifter_name"))).FirstOrDefault();
    }

    /// <summary>Follower total and the most recent follower.</summary>
    public async Task<TwitchCount> GetFollowerCountAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadFollowers);
        var json = await _helix.RequestAsync(HttpMethod.Get, "channels/followers", Q(("broadcaster_id", BroadcasterId), ("first", "1")),
            null, "Getting followers", ct).ConfigureAwait(false);
        var latest = json.Items("data").FirstOrDefault();
        return new TwitchCount(json.Int("total"), latest.Str("user_id"), latest.Str("user_login"), latest.Str("user_name"));
    }

    /// <summary>Subscriber total and sub points.</summary>
    public async Task<TwitchCount> GetSubscriberCountAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadSubscriptions);
        var json = await _helix.RequestAsync(HttpMethod.Get, "subscriptions", Q(("broadcaster_id", BroadcasterId), ("first", "1")),
            null, "Getting subscribers", ct).ConfigureAwait(false);
        return new TwitchCount(json.Int("total"), "", "", "", json.Int("points"));
    }

    public async Task<IReadOnlyList<TwitchTeam>> GetTeamsAsync(string userId, CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "teams/channel", Q(("broadcaster_id", userId)), null, "Getting teams", ct)
            .ConfigureAwait(false);
        return json.Items("data").Select(t => new TwitchTeam(t.Str("id"), t.Str("team_name"), t.Str("team_display_name"))).ToArray();
    }

    // ---------------------------------------------------------------- channel and stream

    /// <summary>Channel information of the broadcaster, or of <paramref name="broadcasterId"/>.</summary>
    public async Task<TwitchChannelInfo?> GetChannelAsync(string? broadcasterId = null, CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "channels", Q(("broadcaster_id", broadcasterId ?? BroadcasterId)), null,
            "Getting channel info", ct).ConfigureAwait(false);
        return json.Items("data").Select(HelixParse.Channel).FirstOrDefault();
    }

    public Task UpdateChannelAsync(ChannelUpdate update, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBroadcast);
        var body = new Dictionary<string, object?>();
        if (update.Title is { } title)
        {
            if (title.Trim().Length == 0) throw new ArgumentException("The stream title cannot be empty.");
            body["title"] = title.Trim();
        }
        if (update.GameId is { } game) body["game_id"] = game;
        if (update.Language is { Length: > 0 } language) body["broadcaster_language"] = language;
        if (update.Tags is { } tags) body["tags"] = NormalizeTags(tags);
        if (update.IsBrandedContent is { } branded) body["is_branded_content"] = branded;
        if (update.ContentLabels is { Count: > 0 } labels)
            body["content_classification_labels"] = labels.Select(l => new { id = l.Key, is_enabled = l.Value }).ToArray();
        if (body.Count == 0) return Task.CompletedTask;
        return _helix.RequestAsync(HttpMethod.Patch, "channels", Q(("broadcaster_id", BroadcasterId)), body, "Updating channel", ct);
    }

    /// <summary>Twitch allows up to 10 tags of at most 25 letters or digits each, without spaces.</summary>
    internal static string[] NormalizeTags(IEnumerable<string> tags)
    {
        var result = tags.Select(t => new string(t.Trim().TrimStart('#').Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 10) throw new ArgumentException("Twitch allows at most 10 tags.");
        if (result.FirstOrDefault(t => t.Length > 25) is { } tooLong) throw new ArgumentException($"The tag '{tooLong}' is longer than 25 characters.");
        return result;
    }

    public async Task<IReadOnlyList<TwitchCategory>> SearchCategoriesAsync(string query, CancellationToken ct = default)
    {
        RequireSignedIn();
        if (string.IsNullOrWhiteSpace(query)) return [];
        var json = await _helix.RequestAsync(HttpMethod.Get, "search/categories", Q(("query", query.Trim()), ("first", "20")), null,
            "Searching categories", ct).ConfigureAwait(false);
        return json.Items("data").Select(c => new TwitchCategory(c.Str("id"), c.Str("name"), c.Str("box_art_url"))).ToArray();
    }

    /// <summary>Finds a category by exact name (case-insensitive), falling back to the best search match.</summary>
    public async Task<TwitchCategory?> FindCategoryAsync(string name, CancellationToken ct = default)
    {
        RequireSignedIn();
        name = name.Trim();
        if (name.Length == 0) return null;
        var json = await _helix.RequestAsync(HttpMethod.Get, "games", Q(("name", name)), null, "Looking up category", ct).ConfigureAwait(false);
        if (json.Items("data").Select(c => new TwitchCategory(c.Str("id"), c.Str("name"), c.Str("box_art_url"))).FirstOrDefault() is { } exact)
            return exact;
        var found = await SearchCategoriesAsync(name, ct).ConfigureAwait(false);
        return found.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
    }

    /// <summary>The live stream of the broadcaster (or <paramref name="userId"/>); null when offline.</summary>
    public async Task<TwitchStreamInfo?> GetStreamAsync(string? userId = null, CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "streams", Q(("user_id", userId ?? BroadcasterId)), null, "Getting stream info", ct)
            .ConfigureAwait(false);
        return json.Items("data").Select(HelixParse.Stream).FirstOrDefault();
    }

    public async Task<TwitchMarker> CreateStreamMarkerAsync(string description = "", CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBroadcast);
        object body = string.IsNullOrWhiteSpace(description)
            ? new { user_id = BroadcasterId }
            : new { user_id = BroadcasterId, description = description.Trim() };
        var json = await _helix.RequestAsync(HttpMethod.Post, "streams/markers", null, body, "Creating stream marker", ct).ConfigureAwait(false);
        var marker = json.Items("data").FirstOrDefault();
        return new TwitchMarker(marker.Str("id"), marker.Int("position_seconds"), marker.Str("description"));
    }

    public async Task<TwitchClip> CreateClipAsync(bool hasDelay = false, CancellationToken ct = default)
    {
        Require(TwitchScopes.EditClips);
        var json = await _helix.RequestAsync(HttpMethod.Post, "clips",
            Q(("broadcaster_id", BroadcasterId), ("has_delay", hasDelay ? "true" : "false")), null, "Creating clip", ct).ConfigureAwait(false);
        var clip = json.Items("data").FirstOrDefault();
        return new TwitchClip(clip.Str("id"), clip.Str("edit_url"));
    }

    // ---------------------------------------------------------------- ads and raids

    /// <summary>Runs an ad of 30 to 180 seconds.</summary>
    public async Task<TwitchCommercial> RunCommercialAsync(int lengthSeconds, CancellationToken ct = default)
    {
        Require(TwitchScopes.EditCommercial);
        var json = await _helix.RequestAsync(HttpMethod.Post, "channels/commercial", null,
            new { broadcaster_id = BroadcasterId, length = Math.Clamp(lengthSeconds, 30, 180) }, "Running ad", ct).ConfigureAwait(false);
        var result = json.Items("data").FirstOrDefault();
        return new TwitchCommercial(result.Int("length"), result.Int("retry_after"), result.Str("message"));
    }

    public async Task<TwitchAdSchedule> GetAdScheduleAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadAds, TwitchScopes.ManageAds);
        var json = await _helix.RequestAsync(HttpMethod.Get, "channels/ads", Q(("broadcaster_id", BroadcasterId)), null, "Getting ad schedule", ct)
            .ConfigureAwait(false);
        return AdSchedule(json.Items("data").FirstOrDefault());
    }

    public async Task<TwitchAdSchedule> SnoozeAdAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageAds);
        var json = await _helix.RequestAsync(HttpMethod.Post, "channels/ads/schedule/snooze", Q(("broadcaster_id", BroadcasterId)), null,
            "Snoozing ad", ct).ConfigureAwait(false);
        return AdSchedule(json.Items("data").FirstOrDefault());
    }

    private static TwitchAdSchedule AdSchedule(JsonElement e) => new(UnixOrIso(e, "next_ad_at"), UnixOrIso(e, "last_ad_at"),
        e.Int("duration"), e.Int("preroll_free_time"), e.Int("snooze_count"), UnixOrIso(e, "snooze_refresh_at"));

    /// <summary>The ads endpoints return times as unix seconds (sometimes as strings) or RFC 3339.</summary>
    private static DateTimeOffset? UnixOrIso(JsonElement e, string name)
    {
        var text = e.Str(name);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        return HelixParse.Time(e, name);
    }

    /// <summary>Starts a raid on <paramref name="target"/> (login or "id:...") and publishes "twitch.raidStart".</summary>
    public async Task StartRaidAsync(string target, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRaids);
        var user = await GetUserAsync(target, ct).ConfigureAwait(false) ?? throw new TwitchApiException($"Twitch user '{target}' was not found.");
        var json = await _helix.RequestAsync(HttpMethod.Post, "raids", Q(("from_broadcaster_id", BroadcasterId), ("to_broadcaster_id", user.Id)),
            null, "Starting raid", ct).ConfigureAwait(false);
        var result = json.Items("data").FirstOrDefault();
        _publish(new BotEvent(TwitchEventTypes.RaidStart, new Dictionary<string, object?>
        {
            ["user"] = user.DisplayName,
            ["userName"] = user.Login,
            ["userId"] = user.Id,
            ["isMature"] = result.Bool("is_mature"),
            ["createdAt"] = result.Str("created_at"),
        }));
    }

    public Task CancelRaidAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRaids);
        return _helix.RequestAsync(HttpMethod.Delete, "raids", Q(("broadcaster_id", BroadcasterId)), null, "Cancelling raid", ct);
    }

    public async Task SendShoutoutAsync(string target, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageShoutouts);
        var to = await ResolveUserIdAsync(target, ct).ConfigureAwait(false);
        await _helix.RequestAsync(HttpMethod.Post, "chat/shoutouts",
            Q(("from_broadcaster_id", BroadcasterId), ("to_broadcaster_id", to), ("moderator_id", BroadcasterId)), null,
            "Sending shoutout", ct).ConfigureAwait(false);
    }

    public async Task<TwitchCharityCampaign?> GetCharityCampaignAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadCharity);
        var json = await _helix.RequestAsync(HttpMethod.Get, "charity/campaigns", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting charity campaign", ct).ConfigureAwait(false);
        return json.Items("data").Select(c =>
        {
            var current = c.Child("current_amount") ?? default;
            var target = c.Child("target_amount") ?? default;
            return new TwitchCharityCampaign(c.Str("id"), c.Str("charity_name"), c.Str("charity_description"),
                Money(current), Money(target), current.Str("currency"));
        }).FirstOrDefault();
    }

    /// <summary>Formats a Twitch amount object {value, decimal_places} as a plain number.</summary>
    internal static string Money(JsonElement amount)
    {
        if (amount.ValueKind != JsonValueKind.Object) return "";
        var places = amount.Int("decimal_places");
        var value = (decimal)(amount.Child("value") is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var l) ? l : 0);
        for (var i = 0; i < places; i++) value /= 10;
        return value.ToString(places > 0 ? "0." + new string('0', places) : "0", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- moderation

    public Task BanAsync(string userId, string reason = "", CancellationToken ct = default) => BanCoreAsync(userId, null, reason, ct);

    /// <summary>Times a user out for 1 second up to 14 days.</summary>
    public Task TimeoutAsync(string userId, int seconds, string reason = "", CancellationToken ct = default) =>
        BanCoreAsync(userId, Math.Clamp(seconds, 1, 1_209_600), reason, ct);

    private Task BanCoreAsync(string userId, int? duration, string reason, CancellationToken ct)
    {
        Require(TwitchScopes.ManageBannedUsers);
        RequireUser(userId);
        object data = duration is { } d
            ? new { user_id = userId, duration = d, reason }
            : new { user_id = userId, reason };
        return _helix.RequestAsync(HttpMethod.Post, "moderation/bans", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            new { data }, duration is null ? "Banning user" : "Timing out user", ct);
    }

    /// <summary>Lifts a ban or a timeout.</summary>
    public Task UnbanAsync(string userId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBannedUsers);
        RequireUser(userId);
        return _helix.RequestAsync(HttpMethod.Delete, "moderation/bans",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId), ("user_id", userId)), null, "Unbanning user", ct);
    }

    public Task WarnAsync(string userId, string reason, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageWarnings);
        RequireUser(userId);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A warning needs a reason.");
        return _helix.RequestAsync(HttpMethod.Post, "moderation/warnings", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            new { data = new { user_id = userId, reason } }, "Warning user", ct);
    }

    public Task AddVipAsync(string userId, CancellationToken ct = default) =>
        Role(HttpMethod.Post, "channels/vips", TwitchScopes.ManageVips, userId, "Adding VIP", ct);

    public Task RemoveVipAsync(string userId, CancellationToken ct = default) =>
        Role(HttpMethod.Delete, "channels/vips", TwitchScopes.ManageVips, userId, "Removing VIP", ct);

    public Task AddModeratorAsync(string userId, CancellationToken ct = default) =>
        Role(HttpMethod.Post, "moderation/moderators", TwitchScopes.ManageModerators, userId, "Adding moderator", ct);

    public Task RemoveModeratorAsync(string userId, CancellationToken ct = default) =>
        Role(HttpMethod.Delete, "moderation/moderators", TwitchScopes.ManageModerators, userId, "Removing moderator", ct);

    private Task Role(HttpMethod method, string path, string scope, string userId, string operation, CancellationToken ct)
    {
        Require(scope);
        RequireUser(userId);
        return _helix.RequestAsync(method, path, Q(("broadcaster_id", BroadcasterId), ("user_id", userId)), null, operation, ct);
    }

    public async Task<TwitchChatSettings> GetChatSettingsAsync(CancellationToken ct = default)
    {
        RequireSignedIn();
        var json = await _helix.RequestAsync(HttpMethod.Get, "chat/settings", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting chat settings", ct).ConfigureAwait(false);
        return HelixParse.ChatSettings(json.Items("data").FirstOrDefault());
    }

    public async Task<TwitchChatSettings> UpdateChatSettingsAsync(ChatSettingsUpdate update, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageChatSettings);
        var body = new Dictionary<string, object?>();
        if (update.EmoteMode is { } emote) body["emote_mode"] = emote;
        if (update.FollowerMode is { } follower) body["follower_mode"] = follower;
        if (update.FollowerModeDuration is { } minutes) body["follower_mode_duration"] = Math.Clamp(minutes, 0, 129_600);
        if (update.SlowMode is { } slow) body["slow_mode"] = slow;
        if (update.SlowModeWaitTime is { } wait) body["slow_mode_wait_time"] = Math.Clamp(wait, 3, 120);
        if (update.SubscriberMode is { } subs) body["subscriber_mode"] = subs;
        if (update.UniqueChatMode is { } unique) body["unique_chat_mode"] = unique;
        var json = await _helix.RequestAsync(HttpMethod.Patch, "chat/settings", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            body, "Updating chat settings", ct).ConfigureAwait(false);
        return HelixParse.ChatSettings(json.Items("data").FirstOrDefault());
    }

    public async Task<TwitchShieldMode> GetShieldModeAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageShieldMode);
        var json = await _helix.RequestAsync(HttpMethod.Get, "moderation/shield_mode", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            null, "Getting shield mode", ct).ConfigureAwait(false);
        return Shield(json.Items("data").FirstOrDefault());
    }

    public async Task<TwitchShieldMode> SetShieldModeAsync(bool active, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageShieldMode);
        var json = await _helix.RequestAsync(HttpMethod.Put, "moderation/shield_mode", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            new { is_active = active }, "Updating shield mode", ct).ConfigureAwait(false);
        return Shield(json.Items("data").FirstOrDefault());
    }

    private static TwitchShieldMode Shield(JsonElement e) =>
        new(e.Bool("is_active"), e.Str("moderator_name"), HelixParse.Time(e, "last_activated_at"));

    public async Task<string> AddBlockedTermAsync(string text, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBlockedTerms);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("No term to block.");
        var json = await _helix.RequestAsync(HttpMethod.Post, "moderation/blocked_terms", Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId)),
            new { text = text.Trim() }, "Adding blocked term", ct).ConfigureAwait(false);
        return json.Items("data").FirstOrDefault().Str("id");
    }

    public Task RemoveBlockedTermAsync(string termId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageBlockedTerms);
        return _helix.RequestAsync(HttpMethod.Delete, "moderation/blocked_terms",
            Q(("broadcaster_id", BroadcasterId), ("moderator_id", BroadcasterId), ("id", termId)), null, "Removing blocked term", ct);
    }

    /// <summary>Allows or denies a message AutoMod held for review.</summary>
    public Task ResolveAutomodMessageAsync(string messageId, bool allow, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageAutomod);
        return _helix.RequestAsync(HttpMethod.Post, "moderation/automod/message", null,
            new { user_id = BroadcasterId, msg_id = messageId, action = allow ? "ALLOW" : "DENY" }, "Resolving AutoMod message", ct);
    }

    // ---------------------------------------------------------------- polls

    public async Task<TwitchPoll> CreatePollAsync(string title, IReadOnlyList<string> choices, int durationSeconds,
        int channelPointsPerVote = 0, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManagePolls);
        var options = choices.Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A poll needs a title.");
        if (options.Length is < 2 or > MaxPollChoices) throw new ArgumentException($"A poll needs 2 to {MaxPollChoices} choices.");
        var body = new Dictionary<string, object?>
        {
            ["broadcaster_id"] = BroadcasterId,
            ["title"] = title.Trim(),
            ["choices"] = options.Select(c => new { title = c }).ToArray(),
            ["duration"] = Math.Clamp(durationSeconds, 15, 1800),
        };
        if (channelPointsPerVote > 0)
        {
            body["channel_points_voting_enabled"] = true;
            body["channel_points_per_vote"] = Math.Min(channelPointsPerVote, 1_000_000);
        }
        var json = await _helix.RequestAsync(HttpMethod.Post, "polls", null, body, "Creating poll", ct).ConfigureAwait(false);
        return HelixParse.Poll(json.Items("data").FirstOrDefault());
    }

    /// <summary>The most recent poll (active or not); null when the channel has none.</summary>
    public async Task<TwitchPoll?> GetLatestPollAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManagePolls);
        var json = await _helix.RequestAsync(HttpMethod.Get, "polls", Q(("broadcaster_id", BroadcasterId), ("first", "1")), null,
            "Getting poll", ct).ConfigureAwait(false);
        return json.Items("data").Select(HelixParse.Poll).FirstOrDefault();
    }

    /// <summary>Ends the active poll: terminated (results shown) or archived (hidden).</summary>
    public async Task<TwitchPoll> EndPollAsync(bool archive = false, string? pollId = null, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManagePolls);
        var id = pollId;
        if (string.IsNullOrEmpty(id))
        {
            var latest = await GetLatestPollAsync(ct).ConfigureAwait(false);
            if (latest is null || !latest.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                throw new TwitchApiException("There is no active poll.");
            id = latest.Id;
        }
        var json = await _helix.RequestAsync(HttpMethod.Patch, "polls", null,
            new { broadcaster_id = BroadcasterId, id, status = archive ? "ARCHIVED" : "TERMINATED" }, "Ending poll", ct).ConfigureAwait(false);
        return HelixParse.Poll(json.Items("data").FirstOrDefault());
    }

    // ---------------------------------------------------------------- predictions

    public async Task<TwitchPrediction> CreatePredictionAsync(string title, IReadOnlyList<string> outcomes, int windowSeconds,
        CancellationToken ct = default)
    {
        Require(TwitchScopes.ManagePredictions);
        var options = outcomes.Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A prediction needs a title.");
        if (options.Length is < 2 or > MaxPredictionOutcomes)
            throw new ArgumentException($"A prediction needs 2 to {MaxPredictionOutcomes} outcomes.");
        var json = await _helix.RequestAsync(HttpMethod.Post, "predictions", null, new
        {
            broadcaster_id = BroadcasterId,
            title = title.Trim(),
            outcomes = options.Select(o => new { title = o }).ToArray(),
            prediction_window = Math.Clamp(windowSeconds, 30, 1800),
        }, "Creating prediction", ct).ConfigureAwait(false);
        return HelixParse.Prediction(json.Items("data").FirstOrDefault());
    }

    public async Task<TwitchPrediction?> GetLatestPredictionAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ManagePredictions);
        var json = await _helix.RequestAsync(HttpMethod.Get, "predictions", Q(("broadcaster_id", BroadcasterId), ("first", "1")), null,
            "Getting prediction", ct).ConfigureAwait(false);
        return json.Items("data").Select(HelixParse.Prediction).FirstOrDefault();
    }

    public Task<TwitchPrediction> LockPredictionAsync(CancellationToken ct = default) =>
        EndPredictionAsync("LOCKED", null, ["ACTIVE"], ct);

    public Task<TwitchPrediction> CancelPredictionAsync(CancellationToken ct = default) =>
        EndPredictionAsync("CANCELED", null, ["ACTIVE", "LOCKED"], ct);

    /// <summary>
    /// Resolves the latest prediction. <paramref name="winner"/> is an outcome title (case-insensitive), a 1-based
    /// outcome number, or an outcome id. <paramref name="title"/>, when given, must match the prediction's title.
    /// </summary>
    public async Task<TwitchPrediction> ResolvePredictionAsync(string winner, string? title = null, CancellationToken ct = default)
    {
        var latest = await LatestOpenPredictionAsync(["ACTIVE", "LOCKED"], ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(title) && !latest.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new TwitchApiException($"The open prediction is '{latest.Title}', not '{title.Trim()}'.");
        var outcome = FindOutcome(latest, winner) ?? throw new TwitchApiException($"The prediction has no outcome '{winner}'.");
        return await EndPredictionAsync("RESOLVED", outcome.Id, ["ACTIVE", "LOCKED"], ct, latest).ConfigureAwait(false);
    }

    internal static TwitchPredictionOutcome? FindOutcome(TwitchPrediction prediction, string winner)
    {
        winner = winner.Trim();
        return prediction.Outcomes.FirstOrDefault(o => o.Title.Equals(winner, StringComparison.OrdinalIgnoreCase))
               ?? prediction.Outcomes.FirstOrDefault(o => o.Id == winner)
               ?? (int.TryParse(winner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= prediction.Outcomes.Count
                   ? prediction.Outcomes[n - 1]
                   : null);
    }

    private async Task<TwitchPrediction> LatestOpenPredictionAsync(string[] allowed, CancellationToken ct)
    {
        var latest = await GetLatestPredictionAsync(ct).ConfigureAwait(false);
        if (latest is null || !allowed.Contains(latest.Status, StringComparer.OrdinalIgnoreCase))
            throw new TwitchApiException("There is no open prediction.");
        return latest;
    }

    private async Task<TwitchPrediction> EndPredictionAsync(string status, string? winningOutcomeId, string[] allowed, CancellationToken ct,
        TwitchPrediction? known = null)
    {
        Require(TwitchScopes.ManagePredictions);
        var latest = known ?? await LatestOpenPredictionAsync(allowed, ct).ConfigureAwait(false);
        object body = winningOutcomeId is null
            ? new { broadcaster_id = BroadcasterId, id = latest.Id, status }
            : new { broadcaster_id = BroadcasterId, id = latest.Id, status, winning_outcome_id = winningOutcomeId };
        var json = await _helix.RequestAsync(HttpMethod.Patch, "predictions", null, body, "Updating prediction", ct).ConfigureAwait(false);
        return HelixParse.Prediction(json.Items("data").FirstOrDefault());
    }

    // ---------------------------------------------------------------- channel point rewards

    /// <summary>
    /// Every custom reward of the channel. Rewards created by Cuebot's Client ID are flagged
    /// <see cref="TwitchReward.IsManageable"/>; only those can be changed through the API.
    /// </summary>
    public async Task<IReadOnlyList<TwitchReward>> GetRewardsAsync(CancellationToken ct = default)
    {
        Require(TwitchScopes.ReadRedemptions, TwitchScopes.ManageRedemptions);
        var all = await _helix.RequestAsync(HttpMethod.Get, "channel_points/custom_rewards", Q(("broadcaster_id", BroadcasterId)), null,
            "Getting rewards", ct).ConfigureAwait(false);
        var manageable = new HashSet<string>(StringComparer.Ordinal);
        if (HasScope(_tokens, TwitchScopes.ManageRedemptions))
        {
            var mine = await _helix.RequestAsync(HttpMethod.Get, "channel_points/custom_rewards",
                Q(("broadcaster_id", BroadcasterId), ("only_manageable_rewards", "true")), null, "Getting rewards", ct).ConfigureAwait(false);
            foreach (var r in mine.Items("data")) manageable.Add(r.Str("id"));
        }
        return all.Items("data").Select(r => HelixParse.Reward(r, manageable.Contains(r.Str("id")))).ToArray();
    }

    /// <summary>Finds a reward by id or (case-insensitive) title.</summary>
    public async Task<TwitchReward> FindRewardAsync(string idOrTitle, CancellationToken ct = default)
    {
        var key = idOrTitle.Trim();
        var rewards = await GetRewardsAsync(ct).ConfigureAwait(false);
        return rewards.FirstOrDefault(r => r.Id == key)
               ?? rewards.FirstOrDefault(r => r.Title.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? throw new TwitchApiException($"No channel point reward '{key}' was found.");
    }

    public async Task<TwitchReward> CreateRewardAsync(RewardUpdate reward, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRedemptions);
        if (string.IsNullOrWhiteSpace(reward.Title)) throw new ArgumentException("A reward needs a title.");
        if (reward.Cost is null or < 1) throw new ArgumentException("A reward needs a cost of at least 1.");
        var json = await _helix.RequestAsync(HttpMethod.Post, "channel_points/custom_rewards", Q(("broadcaster_id", BroadcasterId)),
            RewardBody(reward), "Creating reward", ct).ConfigureAwait(false);
        return HelixParse.Reward(json.Items("data").FirstOrDefault(), true);
    }

    public async Task<TwitchReward> UpdateRewardAsync(string rewardId, RewardUpdate update, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRedemptions);
        var json = await _helix.RequestAsync(HttpMethod.Patch, "channel_points/custom_rewards", Q(("broadcaster_id", BroadcasterId), ("id", rewardId)),
            RewardBody(update), "Updating reward (only rewards created by Cuebot can be changed)", ct).ConfigureAwait(false);
        return HelixParse.Reward(json.Items("data").FirstOrDefault(), true);
    }

    public Task DeleteRewardAsync(string rewardId, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRedemptions);
        return _helix.RequestAsync(HttpMethod.Delete, "channel_points/custom_rewards", Q(("broadcaster_id", BroadcasterId), ("id", rewardId)),
            null, "Deleting reward (only rewards created by Cuebot can be deleted)", ct);
    }

    internal static Dictionary<string, object?> RewardBody(RewardUpdate r)
    {
        var body = new Dictionary<string, object?>();
        if (r.Title is { } title) body["title"] = title.Trim();
        if (r.Prompt is { } prompt) body["prompt"] = prompt;
        if (r.Cost is { } cost) body["cost"] = Math.Max(1, cost);
        if (r.BackgroundColor is { Length: > 0 } color) body["background_color"] = color.StartsWith('#') ? color : "#" + color;
        if (r.IsEnabled is { } enabled) body["is_enabled"] = enabled;
        if (r.IsPaused is { } paused) body["is_paused"] = paused;
        if (r.IsUserInputRequired is { } input) body["is_user_input_required"] = input;
        if (r.SkipQueue is { } skip) body["should_redemptions_skip_request_queue"] = skip;
        if (r.MaxPerStream is { } perStream)
        {
            body["is_max_per_stream_enabled"] = perStream > 0;
            if (perStream > 0) body["max_per_stream"] = perStream;
        }
        if (r.MaxPerUserPerStream is { } perUser)
        {
            body["is_max_per_user_per_stream_enabled"] = perUser > 0;
            if (perUser > 0) body["max_per_user_per_stream"] = perUser;
        }
        if (r.GlobalCooldownSeconds is { } cooldown)
        {
            body["is_global_cooldown_enabled"] = cooldown > 0;
            if (cooldown > 0) body["global_cooldown_seconds"] = cooldown;
        }
        return body;
    }

    /// <summary>Marks a redemption fulfilled, or cancels it (refunding the points). Only for rewards created by Cuebot.</summary>
    public Task UpdateRedemptionAsync(string rewardId, string redemptionId, bool fulfil, CancellationToken ct = default)
    {
        Require(TwitchScopes.ManageRedemptions);
        if (string.IsNullOrWhiteSpace(rewardId) || string.IsNullOrWhiteSpace(redemptionId))
            throw new ArgumentException("A reward id and a redemption id are needed.");
        return _helix.RequestAsync(HttpMethod.Patch, "channel_points/custom_rewards/redemptions",
            Q(("broadcaster_id", BroadcasterId), ("reward_id", rewardId.Trim()), ("id", redemptionId.Trim())),
            new { status = fulfil ? "FULFILLED" : "CANCELED" }, fulfil ? "Fulfilling redemption" : "Cancelling redemption", ct);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Resolves a login (leading @ ignored) or "id:..." to a user id.</summary>
    public async Task<string> ResolveUserIdAsync(string user, CancellationToken ct = default)
    {
        var (key, value) = UserQuery(user);
        if (key == "id") return value;
        var found = await GetUserAsync(user, ct).ConfigureAwait(false);
        return found?.Id ?? throw new TwitchApiException($"Twitch user '{value}' was not found.");
    }

    internal static (string Key, string Value) UserQuery(string user)
    {
        var text = user.Trim();
        if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) return ("id", text[3..].Trim());
        text = text.TrimStart('@').ToLowerInvariant();
        if (text.Length == 0) throw new ArgumentException("No Twitch user given.");
        return ("login", text);
    }

    /// <summary>Picks the sending account: the bot only when it is signed in with the needed scope.</summary>
    internal static TwitchSender ChooseSender(TwitchSender requested, bool botReady, bool preferBot) => requested switch
    {
        TwitchSender.Bot => botReady ? TwitchSender.Bot : throw new InvalidOperationException("The Twitch bot account is not signed in."),
        TwitchSender.Broadcaster => TwitchSender.Broadcaster,
        _ => botReady && preferBot ? TwitchSender.Bot : TwitchSender.Broadcaster,
    };

    private TwitchSender Choose(TwitchSender requested, bool preferBot, string scope)
    {
        RequireSignedIn();
        var botReady = IsBotReady && (requested == TwitchSender.Bot || HasScope(_botTokens, scope));
        return ChooseSender(requested, botReady, preferBot);
    }

    private (HelixClient, TwitchTokenManager) Account(TwitchSender sender) =>
        sender == TwitchSender.Bot ? (_botHelix, _botTokens) : (_helix, _tokens);

    private void RequireSignedIn()
    {
        if (_settings().ClientId.Trim().Length == 0 || !_tokens.HasToken || _tokens.UserId.Length == 0)
            throw new InvalidOperationException("Sign in to Twitch first.");
    }

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("No Twitch user given.");
    }

    /// <summary>Checks sign-in and that the broadcaster token has at least one of <paramref name="anyOf"/>.</summary>
    private void Require(params string[] anyOf)
    {
        RequireSignedIn();
        if (!anyOf.Any(s => HasScope(_tokens, s))) throw MissingScope(anyOf[0], "the broadcaster");
    }

    private static void RequireScope(TwitchTokenManager tokens, TwitchSender sender, string scope)
    {
        if (!HasScope(tokens, scope)) throw MissingScope(scope, sender == TwitchSender.Bot ? "the bot account" : "the broadcaster");
    }

    private static bool HasScope(TwitchTokenManager tokens, string scope) => tokens.Scopes.Contains(scope, StringComparer.Ordinal);

    internal static TwitchApiException MissingScope(string scope, string account) =>
        new($"Cuebot is not authorized for this: {account} did not grant the Twitch scope '{scope}'. "
            + "Enable it on the Twitch page (Settings tab) and sign in again.", 403);

    private static string Tier(string tier) => tier.Length == 4 && tier.EndsWith("000", StringComparison.Ordinal) ? tier[..1] : tier;

    private static IEnumerable<KeyValuePair<string, string>> Q(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value));
}
