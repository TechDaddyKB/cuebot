using System.Globalization;
using System.Text.Json;
using Streamer.bot.Plugin.Interface.Enums;
using Streamer.bot.Plugin.Interface.Model;
using Twitch.Common.Models.Api;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's Twitch channel point reward, chat (replies, deletes, pins), moderation (warnings, blocked terms,
/// AutoMod), moderator/VIP list and clip methods, on the Twitch module's script functions. As elsewhere, methods
/// returning bool or data report a failed request as false or null (empty lists) with a warning.
/// </summary>
public sealed partial class CphProxy
{
    // ---- Channel point rewards ------------------------------------------------------------------------------------

    /// <summary>Every channel point reward of the channel; empty when Twitch refused.</summary>
    public List<TwitchReward> TwitchGetRewards()
    {
        var rewards = TryCall("twitch.getRewards", null);
        if (rewards is not { ValueKind: JsonValueKind.Array } list) return [];
        return list.EnumerateArray().Select(r => new TwitchReward
        {
            Id = Prop(r, "id"),
            Title = Prop(r, "title"),
            Prompt = Prop(r, "prompt"),
            Cost = IntProp(r, "cost"),
            InputRequired = Prop(r, "isUserInputRequired") == "True",
            BackgroundColor = Prop(r, "backgroundColor"),
            Paused = Prop(r, "isPaused") == "True",
            Enabled = Prop(r, "isEnabled") == "True",
            IsOurs = Prop(r, "isManageable") == "True",
        }).ToList();
    }

    public void EnableReward(string rewardId) => RewardStep("twitch.setRewardEnabled", rewardId, "enabled", "true");
    public void DisableReward(string rewardId) => RewardStep("twitch.setRewardEnabled", rewardId, "enabled", "false");
    public void PauseReward(string rewardId) => RewardStep("twitch.setRewardPaused", rewardId, "paused", "true");
    public void UnPauseReward(string rewardId) => RewardStep("twitch.setRewardPaused", rewardId, "paused", "false");

    /// <summary>Sets (or with <paramref name="additive"/>, raises) the reward's maximum redemptions per stream.</summary>
    public void UpdateRewardMaxPerStream(string rewardId, long redeems, bool additive = false) =>
        UpdateRewardMax(rewardId, redeems, additive, "twitch.setRewardMaxPerStream", "rewardMaxPerStream");

    /// <summary>Sets (or with <paramref name="additive"/>, raises) the reward's maximum redemptions per user per stream.</summary>
    public void UpdateRewardMaxPerUserPerStream(string rewardId, long redeems, bool additive = false) =>
        UpdateRewardMax(rewardId, redeems, additive, "twitch.setRewardMaxPerUser", "rewardMaxPerUser");

    public bool TwitchRedemptionFulfill(string rewardId, string redemptionId) => Redemption(rewardId, redemptionId, "Fulfil");

    /// <summary>Cancels the redemption, refunding the viewer's points.</summary>
    public bool TwitchRedemptionCancel(string rewardId, string redemptionId) => Redemption(rewardId, redemptionId, "Cancel");

    /// <summary>
    /// The reward's redemption count. Cuebot keeps one counter per reward (whether it survives going live is set per
    /// reward on the Twitch page), so <paramref name="persisted"/> is ignored. Null when the reward is not found.
    /// </summary>
    public TwitchRewardCounter? TwitchGetRewardCounter(string rewardId, bool persisted = true)
    {
        var counters = TryCallForArgs("twitch.getRewardCounter", new { reward = rewardId ?? "", user = "" }, warn: true);
        return counters is null ? null : new TwitchRewardCounter { Count = IntArg(counters, "counter") };
    }

    /// <summary>A user's (by login) redemption count of the reward; null when the reward or user is not found.</summary>
    public TwitchRewardCounter? TwitchGetRewardUserCounter(string userLogin, string rewardId, bool persisted = true) =>
        RewardUserCounter(Login(userLogin), rewardId);

    /// <summary>A user's (by id) redemption count of the reward; null when the reward or user is not found.</summary>
    public TwitchRewardCounter? TwitchGetRewardUserCounterById(string userId, string rewardId, bool persisted = true) =>
        string.IsNullOrWhiteSpace(userId) ? null : RewardUserCounter("id:" + userId.Trim(), rewardId);

    /// <summary>Every user's redemption count of the reward; empty when the reward is not found.</summary>
    public List<TwitchRewardCounter> TwitchGetRewardUserCounters(string rewardId, bool persisted = true)
    {
        var counters = TryCall("twitch.getRewardUserCounters", new { reward = rewardId ?? "" });
        if (counters is not { ValueKind: JsonValueKind.Object } map) return [];
        return map.EnumerateObject().Select(p =>
        {
            var known = KnownTwitchUser(p.Name, "");
            return new TwitchRewardCounter
            {
                UserId = p.Name,
                UserLogin = known?.UserName.ToLowerInvariant() ?? "",
                UserName = known?.DisplayName is { Length: > 0 } display ? display : known?.UserName ?? "",
                Count = p.Value.TryGetInt32(out var n) ? n : 0,
            };
        }).ToList();
    }

    public void TwitchResetRewardCounter(string rewardId) => RewardStep("twitch.resetRewardCounter", rewardId, null, null);
    public void TwitchResetRewardUserCounters(string rewardId) => RewardStep("twitch.resetRewardUserCounters", rewardId, null, null);

    public void TwitchResetUserRewardCounter(string rewardId, string userId) =>
        Try(() => Call("twitch.resetUserRewardCounter", new { reward = rewardId ?? "", user = "id:" + (userId ?? "").Trim() }));

    /// <summary>Resets the user's counters of every reward. <paramref name="persisted"/> is ignored (see <see cref="TwitchGetRewardCounter"/>).</summary>
    public void TwitchResetUserRewardCounters(string userId, bool persisted) =>
        Try(() => Call("twitch.resetUserRewardCounters", new { user = "id:" + (userId ?? "").Trim() }));

    private void RewardStep(string function, string rewardId, string? key, string? value,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var args = new Dictionary<string, object?> { ["reward"] = rewardId ?? "" };
        if (key is not null) args[key] = value;
        Try(() => Call(function, args, method), method);
    }

    private void UpdateRewardMax(string rewardId, long redeems, bool additive, string function, string currentArg,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var max = redeems;
        if (additive)
        {
            var info = TryCallForArgs("twitch.getRewardInfo", new { reward = rewardId ?? "" }, method, warn: true);
            if (info is null) return;
            max += IntArg(info, currentArg);
        }
        var value = Math.Clamp(max, 0, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        Try(() => Call(function, new { reward = rewardId ?? "", max = value }, method), method);
    }

    private bool Redemption(string rewardId, string redemptionId, string status, [System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        !string.IsNullOrWhiteSpace(rewardId) && !string.IsNullOrWhiteSpace(redemptionId)
        && Try(() => Call("twitch.updateRedemption", new { rewardId = rewardId.Trim(), redemptionId = redemptionId.Trim(), status }, method), method);

    private TwitchRewardCounter? RewardUserCounter(string user, string rewardId, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (user.Length == 0) return null;
        var counters = TryCallForArgs("twitch.getRewardCounter", new { reward = rewardId ?? "", user }, method, warn: true);
        if (counters is null) return null;
        var id = user.StartsWith("id:", StringComparison.Ordinal) ? user[3..] : "";
        var known = KnownTwitchUser(id, id.Length == 0 ? user : "");
        return new TwitchRewardCounter
        {
            UserId = id.Length > 0 ? id : known?.UserId ?? "",
            UserLogin = known?.UserName.ToLowerInvariant() ?? (id.Length == 0 ? user.ToLowerInvariant() : ""),
            UserName = known?.DisplayName is { Length: > 0 } display ? display : known?.UserName ?? (id.Length == 0 ? user : ""),
            Count = IntArg(counters, "userCounter"),
        };
    }

    // ---- Chat -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replies to a chat message. <paramref name="useBot"/> sends with the bot account; when it is not signed in,
    /// <paramref name="fallback"/> sends with the broadcaster instead. True when Twitch accepted it.
    /// </summary>
    public bool TwitchReplyToMessage(string message, string replyId, bool useBot = true, bool fallback = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var sendAs = !useBot ? "Broadcaster" : fallback ? (TwitchBotSignedIn() ? "Bot" : "Broadcaster") : "Bot";
        return Try(() => Call("twitch.replyToMessage", new { message, messageId = replyId?.Trim() ?? "", sendAs }));
    }

    /// <summary>Deletes a chat message (as the broadcaster; <paramref name="bot"/> is ignored).</summary>
    public bool TwitchDeleteChatMessage(string messageId, bool bot = true) =>
        !string.IsNullOrWhiteSpace(messageId) && Try(() => Call("twitch.deleteMessage", new { messageId = messageId.Trim() }));

    /// <summary>Clears chat (as the broadcaster; <paramref name="bot"/> is ignored).</summary>
    public bool TwitchClearChatMessages(bool bot = true) => Try(() => Call("twitch.clearChat", null));

    /// <summary>Pins a chat message for 30 to 1800 seconds; 0 keeps it pinned until the stream ends.</summary>
    public bool TwitchPinChatMessage(string messageId, int duration = 0, bool bot = false) =>
        !string.IsNullOrWhiteSpace(messageId)
        && Try(() => Call("twitch.pinMessage", new { messageId = messageId.Trim(), duration = Num(Math.Max(0, duration)) }));

    /// <summary>Sends a message as the broadcaster and pins it (see <see cref="TwitchPinChatMessage"/> for the duration).</summary>
    public bool TwitchSendAndPinMessage(string message, int duration = 0, bool bot = false) =>
        !string.IsNullOrWhiteSpace(message) && Try(() => Call("twitch.sendAndPinMessage", new { message, duration = Num(Math.Max(0, duration)) }));

    /// <summary>Unpins the message pinned now.</summary>
    public bool TwitchUnpinMessage() => Try(() => Call("twitch.unpinMessage", new { messageId = "" }));

    /// <summary>Changes how long the pinned message stays, counted from now (0: until the stream ends).</summary>
    public bool TwitchUpdatePinnedMessageDuration(int duration) =>
        Try(() => Call("twitch.updatePinnedDuration", new { messageId = "", duration = Num(Math.Max(0, duration)) }));

    /// <summary>The pinned chat message, or null when none is pinned.</summary>
    public TwitchPinnedMessage? TwitchGetPinnedMessage()
    {
        var pin = TryCallForArgs("twitch.getPinnedMessage", null, warn: true);
        if (pin is null || !Flag(pin, "isPinned")) return null;
        return new TwitchPinnedMessage
        {
            Id = Arg(pin, "pinnedMessageId"),
            Message = Arg(pin, "pinnedMessage"),
            UserId = Arg(pin, "pinnedUserId"),
            UserLogin = Arg(pin, "pinnedUserName"),
            UserName = Arg(pin, "pinnedUser"),
            PinnedBy = Arg(pin, "pinnedBy"),
            StartsAt = ArgTime(pin, "pinnedAt"),
            EndsAt = ArgTime(pin, "pinnedUntil"),
        };
    }

    // ---- Moderation -----------------------------------------------------------------------------------------------

    /// <summary>Warns a user (they must acknowledge it before chatting again).</summary>
    public bool TwitchWarnUser(string userName, string reason, bool useBot = false) =>
        !string.IsNullOrWhiteSpace(userName) && Try(() => Call("twitch.warn", new { user = Login(userName), reason = reason ?? "" }));

    /// <summary>Puts a user (by id) under active monitoring: their messages are flagged for moderators.</summary>
    public bool TwitchAddActiveMonitoringSuspiciousStatus(string userId) => SuspiciousStatus("twitch.addSuspiciousStatus", userId, "Active Monitoring");

    /// <summary>Restricts a user (by id): their messages are kept out of chat.</summary>
    public bool TwitchAddRestrictedSuspiciousStatus(string userId) => SuspiciousStatus("twitch.addSuspiciousStatus", userId, "Restricted");

    /// <summary>Removes a user's suspicious status (by id).</summary>
    public bool TwitchRemoveSuspiciousStatus(string userId) => SuspiciousStatus("twitch.removeSuspiciousStatus", userId, null);

    private bool SuspiciousStatus(string function, string userId, string? status, [System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        !string.IsNullOrWhiteSpace(userId)
        && Try(() => Call(function, status is null ? (object)new { user = "id:" + userId.Trim() } : new { user = "id:" + userId.Trim(), status }, method), method);

    public bool TwitchAddBlockedTerm(string term) =>
        !string.IsNullOrWhiteSpace(term) && Try(() => CallForArgs("twitch.addBlockedTerm", new { term = term.Trim() }));

    /// <summary>Removes a blocked term by its text; false when the channel has no such term.</summary>
    public bool TwitchRemoveBlockedTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        return TryCall("twitch.removeBlockedTerm", new { term = term.Trim() }) is { ValueKind: JsonValueKind.True };
    }

    /// <summary>The channel's blocked terms; empty when Twitch refused.</summary>
    public List<string> TwitchGetBlockedTerms() =>
        TryCall("twitch.getBlockedTerms", null) is { ValueKind: JsonValueKind.Array } terms
            ? terms.EnumerateArray().Select(t => Prop(t, "text")).Where(t => t.Length > 0).ToList()
            : [];

    /// <summary>Allows a message AutoMod held for review.</summary>
    public bool TwitchApproveAutoHeldMessage(string messageId) => AutoMod(messageId, "Allow");

    /// <summary>Denies a message AutoMod held for review.</summary>
    public bool TwitchDenyAutoHeldMessage(string messageId) => AutoMod(messageId, "Deny");

    private bool AutoMod(string messageId, string decision, [System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        !string.IsNullOrWhiteSpace(messageId)
        && Try(() => Call("twitch.resolveAutomod", new { messageId = messageId.Trim(), decision }, method), method);

    // ---- Moderators and VIPs --------------------------------------------------------------------------------------

    /// <summary>Every moderator of the channel; empty when Twitch refused.</summary>
    public List<TwitchUserInfo> TwitchGetMods() => ChannelUsers("twitch.getModerators");

    /// <summary>Every VIP of the channel; empty when Twitch refused.</summary>
    public List<TwitchUserInfo> TwitchGetVips() => ChannelUsers("twitch.getVips");

    private List<TwitchUserInfo> ChannelUsers(string function, [System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        TryCall(function, null, method) is { ValueKind: JsonValueKind.Array } users
            ? users.EnumerateArray().Select(u => TwitchInfo(Prop(u, "userId"), Prop(u, "userLogin"), Prop(u, "userName"))).ToList()
            : [];

    // ---- Cheermotes -----------------------------------------------------------------------------------------------

    /// <summary>The channel's cheermotes; empty when Twitch refused.</summary>
    public List<Cheermote> GetCheermotes()
    {
        if (TryCall("twitch.getCheermotes", null) is not { ValueKind: JsonValueKind.Array } list) return [];
        return list.EnumerateArray().Select(c => new Cheermote
        {
            Prefix = Prop(c, "prefix"),
            Type = Prop(c, "type"),
            Order = IntProp(c, "order"),
            IsCharitable = Prop(c, "is_charitable") == "True",
            LastUpdated = DateTimeOffset.TryParse(Prop(c, "last_updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.LocalDateTime : default,
            Tiers = Child(c, "tiers") is { ValueKind: JsonValueKind.Array } tiers
                ? tiers.EnumerateArray().Select(t => new CheermoteTier
                {
                    MinBits = IntProp(t, "min_bits"),
                    Id = Prop(t, "id"),
                    Color = Prop(t, "color"),
                    CanCheer = Prop(t, "can_cheer") == "True",
                    ShowInBitsCard = Prop(t, "show_in_bits_card") == "True",
                    Images = new CheermoteImages
                    {
                        Dark = CheermoteTheme(Child(Child(t, "images"), "dark")),
                        Light = CheermoteTheme(Child(Child(t, "images"), "light")),
                    },
                }).ToList()
                : [],
        }).ToList();
    }

    private static CheermoteImage CheermoteTheme(JsonElement theme) => new()
    {
        Animated = Urls(Child(theme, "animated")),
        Static = Urls(Child(theme, "static")),
    };

    private static Dictionary<string, string> Urls(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "")
            : [];

    /// <summary>Not supported: Cuebot does not request Twitch's user:manage:blocked_users scope. Returns false.</summary>
    public bool TwitchBlockUser(string userLogin, TwitchBlockContext context = TwitchBlockContext.None, TwitchBlockReason reason = TwitchBlockReason.None) =>
        NotSupported(false, "Cuebot does not request Twitch's user:manage:blocked_users scope");

    /// <summary>Not supported: Cuebot does not request Twitch's user:manage:blocked_users scope. Returns false.</summary>
    public bool TwitchBlockUserById(string userId, TwitchBlockContext context = TwitchBlockContext.None, TwitchBlockReason reason = TwitchBlockReason.None) =>
        NotSupported(false, "Cuebot does not request Twitch's user:manage:blocked_users scope");

    // ---- Clips (most viewed first, as Twitch sorts them; at most 1000) ---------------------------------------------

    /// <summary>Every clip of the broadcaster's channel (up to Twitch's 1000).</summary>
    public List<ClipData> GetAllClips(bool? isFeatured = null) => Clips(null, null, null, null, 0, isFeatured);

    /// <summary>The broadcaster's <paramref name="count"/> most viewed clips.</summary>
    public List<ClipData> GetClips(int count, bool? isFeatured = null) => Clips(null, null, null, null, Count(count), isFeatured);

    public List<ClipData> GetClipsForGame(int gameId, bool? isFeatured = null) => Clips(null, Num(gameId), null, null, 0, isFeatured);
    public List<ClipData> GetClipsForGame(int gameId, int count, bool? isFeatured = null) => Clips(null, Num(gameId), null, null, Count(count), isFeatured);
    public List<ClipData> GetClipsForGame(int gameId, DateTime start, DateTime end, bool? isFeatured = null) =>
        Clips(null, Num(gameId), start, end, 0, isFeatured);
    public List<ClipData> GetClipsForGame(int gameId, DateTime start, DateTime end, int count, bool? isFeatured = null) =>
        Clips(null, Num(gameId), start, end, Count(count), isFeatured);
    public List<ClipData> GetClipsForGame(int gameId, TimeSpan duration, bool? isFeatured = null) =>
        Clips(null, Num(gameId), DateTime.Now - duration, DateTime.Now, 0, isFeatured);
    public List<ClipData> GetClipsForGame(int gameId, TimeSpan duration, int count, bool? isFeatured = null) =>
        Clips(null, Num(gameId), DateTime.Now - duration, DateTime.Now, Count(count), isFeatured);

    public List<ClipData> GetClipsForUser(string username, bool? isFeatured = null) => Clips(Login(username), null, null, null, 0, isFeatured);
    public List<ClipData> GetClipsForUser(string userName, int count, bool? isFeatured = null) => Clips(Login(userName), null, null, null, Count(count), isFeatured);
    public List<ClipData> GetClipsForUser(string username, DateTime start, DateTime end, bool? isFeatured = null) =>
        Clips(Login(username), null, start, end, 0, isFeatured);
    public List<ClipData> GetClipsForUser(string username, DateTime start, DateTime end, int count, bool? isFeatured = null) =>
        Clips(Login(username), null, start, end, Count(count), isFeatured);
    public List<ClipData> GetClipsForUser(string username, TimeSpan duration, bool? isFeatured = null) =>
        Clips(Login(username), null, DateTime.Now - duration, DateTime.Now, 0, isFeatured);
    public List<ClipData> GetClipsForUser(string username, TimeSpan duration, int count, bool? isFeatured = null) =>
        Clips(Login(username), null, DateTime.Now - duration, DateTime.Now, Count(count), isFeatured);

    public List<ClipData> GetClipsForUser(int userId, bool? isFeatured = null) => GetClipsForUserById(Num(userId), isFeatured);
    public List<ClipData> GetClipsForUser(int userId, int count, bool? isFeatured = null) => GetClipsForUserById(Num(userId), count, isFeatured);
    public List<ClipData> GetClipsForUser(int userId, DateTime start, DateTime end, bool? isFeatured = null) =>
        GetClipsForUserById(Num(userId), start, end, isFeatured);
    public List<ClipData> GetClipsForUser(int userId, DateTime start, DateTime end, int count, bool? isFeatured = null) =>
        GetClipsForUserById(Num(userId), start, end, count, isFeatured);
    public List<ClipData> GetClipsForUser(int userId, TimeSpan duration, bool? isFeatured = null) =>
        GetClipsForUserById(Num(userId), duration, isFeatured);
    public List<ClipData> GetClipsForUser(int userId, TimeSpan duration, int count, bool? isFeatured = null) =>
        GetClipsForUserById(Num(userId), duration, count, isFeatured);

    public List<ClipData> GetClipsForUserById(string userId, bool? isFeatured = null) => Clips(UserId(userId), null, null, null, 0, isFeatured);
    public List<ClipData> GetClipsForUserById(string userId, int count, bool? isFeatured = null) =>
        Clips(UserId(userId), null, null, null, Count(count), isFeatured);
    public List<ClipData> GetClipsForUserById(string userId, DateTime start, DateTime end, bool? isFeatured = null) =>
        Clips(UserId(userId), null, start, end, 0, isFeatured);
    public List<ClipData> GetClipsForUserById(string userId, DateTime start, DateTime end, int count, bool? isFeatured = null) =>
        Clips(UserId(userId), null, start, end, Count(count), isFeatured);
    public List<ClipData> GetClipsForUserById(string userId, TimeSpan duration, bool? isFeatured = null) =>
        Clips(UserId(userId), null, DateTime.Now - duration, DateTime.Now, 0, isFeatured);
    public List<ClipData> GetClipsForUserById(string userId, TimeSpan duration, int count, bool? isFeatured = null) =>
        Clips(UserId(userId), null, DateTime.Now - duration, DateTime.Now, Count(count), isFeatured);

    private List<ClipData> Clips(string? user, string? gameId, DateTime? start, DateTime? end, int count, bool? featured,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (user is "" or "id:") return [];
        var args = new Dictionary<string, object?>
        {
            ["user"] = user ?? "",
            ["gameId"] = gameId ?? "",
            ["startedAt"] = start is { } s ? s.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : "",
            ["endedAt"] = end is { } e ? e.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : "",
            ["count"] = Num(count),
        };
        if (featured is { } f) args["featured"] = f;
        if (TryCall("twitch.getClips", args, method) is not { ValueKind: JsonValueKind.Array } clips) return [];
        return clips.EnumerateArray().Select(c => new ClipData
        {
            Id = Prop(c, "id"),
            Url = Prop(c, "url"),
            EmbedUrl = Prop(c, "embedUrl"),
            BroadcasterId = IntProp(c, "broadcasterId"),
            BroadcasterName = Prop(c, "broadcasterName"),
            CreatorId = IntProp(c, "creatorId"),
            CreatorName = Prop(c, "creatorName"),
            VideoId = Prop(c, "videoId"),
            GameId = Prop(c, "gameId"),
            Language = Prop(c, "language"),
            Title = Prop(c, "title"),
            ViewCount = IntProp(c, "viewCount"),
            CreatedAt = DateTimeOffset.TryParse(Prop(c, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.LocalDateTime : default,
            ThumbnailUrl = Prop(c, "thumbnailUrl"),
            Duration = float.TryParse(Prop(c, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
            IsFeatured = Prop(c, "isFeatured") == "True",
        }).ToList();
    }

    private static int Count(int count) => Math.Max(1, count);

    private static string UserId(string? userId) => "id:" + (userId?.Trim() ?? "");

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>Calls a function; null (with a warning) when it failed. A missing module still throws.</summary>
    private JsonElement? TryCall(string function, object? args, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        try
        {
            return Call(function, args, method);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            LogWarn($"{method}: {ex.Message}");
            return null;
        }
    }

    private static int IntProp(JsonElement element, string name) =>
        int.TryParse(Prop(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static int IntArg(Dictionary<string, object?> args, string name) =>
        CphConvert.TryTo(args.GetValueOrDefault(name), out int n) ? n : 0;

    private static DateTime ArgTime(Dictionary<string, object?> args, string name) =>
        DateTimeOffset.TryParse(Arg(args, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.LocalDateTime : default;

    /// <summary>A child of a JSON object by name (ignoring case); default when missing.</summary>
    private static JsonElement Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return default;
    }
}
