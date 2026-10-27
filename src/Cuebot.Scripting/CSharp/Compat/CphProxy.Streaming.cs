using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cuebot.Core.Chat;
using Streamer.bot.Plugin.Interface.Model;
using Twitch.Common.Models.Api;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's Twitch, YouTube and Kick account, moderation and channel methods, mapped onto the platform
/// modules' script functions (twitch.*, youtube.*, kick.*). Like Streamer.bot, methods that return bool or data
/// report a failed request as false or null and log a warning; only a missing module throws.
/// Cuebot moderates and edits the channel with the broadcaster account, so the <c>bot</c>/<c>useBot</c> arguments
/// of those methods are ignored.
/// </summary>
public sealed partial class CphProxy
{
    // ---- Twitch chat ----------------------------------------------------------------------------------------------

    /// <summary>Whispers <paramref name="userName"/>. True when Twitch accepted it (it may still drop it silently).</summary>
    public bool SendWhisper(string userName, string message, bool bot = true)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(message)) return false;
        var sendAs = bot && TwitchBotSignedIn() ? "Bot" : "Broadcaster";
        return Try(() => Call("twitch.sendWhisper", new { user = Login(userName), message, sendAs }));
    }

    /// <summary>Posts a Twitch announcement (sent by the broadcaster). Colours: default/primary, blue, green, orange, purple.</summary>
    public void TwitchAnnounce(string message, bool bot = false, string? color = null, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var normalized = string.IsNullOrWhiteSpace(color) || color.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)
            ? "primary"
            : color.Trim().ToLowerInvariant();
        Call("twitch.sendAnnouncement", new { message, color = normalized });
    }

    // ---- Twitch users ---------------------------------------------------------------------------------------------

    /// <summary>The signed-in broadcaster, or null when Twitch is not signed in.</summary>
    public TwitchUserInfo? TwitchGetBroadcaster() => TwitchAccount("twitch.getBroadcaster");

    /// <summary>The signed-in bot account, or null when there is none.</summary>
    public TwitchUserInfo? TwitchGetBot() => TwitchAccount("twitch.getBot");

    /// <summary>Looks a user up by login; null when not found.</summary>
    public TwitchUserInfo? TwitchGetUserInfoByLogin(string userLogin) => TwitchUser(Login(userLogin));

    /// <summary>Looks a user up by id; null when not found.</summary>
    public TwitchUserInfo? TwitchGetUserInfoById(string userId) =>
        string.IsNullOrWhiteSpace(userId) ? null : TwitchUser("id:" + userId.Trim());

    /// <summary>Profile, channel, follow and subscription details of a user by login; null when not found.</summary>
    public TwitchUserInfoEx? TwitchGetExtendedUserInfoByLogin(string userLogin) => TwitchUserEx(Login(userLogin));

    /// <summary>Profile, channel, follow and subscription details of a user by id; null when not found.</summary>
    public TwitchUserInfoEx? TwitchGetExtendedUserInfoById(string userId) =>
        string.IsNullOrWhiteSpace(userId) ? null : TwitchUserEx("id:" + userId.Trim());

    private TwitchUserInfo? TwitchAccount(string function, [CallerMemberName] string method = "")
    {
        var account = Call(function, null, method);
        var id = Prop(account, "userId");
        if (id.Length == 0) return null;
        var display = "";
        try
        {
            display = Prop(Call("twitch.getUser", new { user = "id:" + id }, method), "displayName");
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            // The display name is a nicety; the account itself is known.
        }
        return TwitchInfo(id, Prop(account, "userName"), display);
    }

    private TwitchUserInfo? TwitchUser(string user, [CallerMemberName] string method = "")
    {
        if (user.Length == 0) return null;
        JsonElement found;
        try
        {
            found = Call("twitch.getUser", new { user }, method);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            LogWarn($"{method}: {ex.Message}");
            return null;
        }
        var id = Prop(found, "id");
        return id.Length == 0 ? null : TwitchInfo(id, Prop(found, "login"), Prop(found, "displayName"));
    }

    private TwitchUserInfo TwitchInfo(string id, string login, string display)
    {
        var known = KnownTwitchUser(id, login);
        var roles = known?.Roles ?? ChatRoles.None;
        return new TwitchUserInfo
        {
            UserId = id,
            UserLogin = login,
            UserName = display.Length > 0 ? display : known?.DisplayName is { Length: > 0 } name ? name : login,
            LastActive = LocalTime(known?.LastSeen),
            IsSubscribed = roles.HasFlag(ChatRoles.Subscriber),
            IsModerator = roles.HasFlag(ChatRoles.Moderator),
            IsVip = roles.HasFlag(ChatRoles.Vip),
        };
    }

    private TwitchUserInfoEx? TwitchUserEx(string user, [CallerMemberName] string method = "")
    {
        if (user.Length == 0) return null;
        var info = TryCallForArgs("twitch.getUserInfo", new { user }, method, warn: true);
        if (info is null || !Flag(info, "targetFound")) return null;

        var id = Arg(info, "targetUserId");
        var login = Arg(info, "targetUserName");
        var known = KnownTwitchUser(id, login);
        var roles = known?.Roles ?? ChatRoles.None;
        DateTimeOffset? created = DateTimeOffset.TryParse(Arg(info, "targetCreatedAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var c) ? c : null;
        var result = new TwitchUserInfoEx
        {
            UserId = id,
            UserLogin = login,
            UserName = Arg(info, "targetUser"),
            Description = Arg(info, "targetDescription"),
            ProfileImageUrl = Arg(info, "targetProfileImageUrl"),
            UserType = Arg(info, "targetBroadcasterType"),
            Game = Arg(info, "targetGame"),
            ChannelTitle = Arg(info, "targetTitle"),
            CreatedAt = LocalTime(created),
            AccountAge = created is { } at ? Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds) : 0,
            LastActive = LocalTime(known?.LastSeen),
            IsSubscribed = roles.HasFlag(ChatRoles.Subscriber),
            IsModerator = roles.HasFlag(ChatRoles.Moderator),
            IsVip = roles.HasFlag(ChatRoles.Vip),
        };

        // Follow and subscription need extra scopes; without them those fields keep what chat showed.
        var target = "id:" + id;
        if (TryCallForArgs("twitch.getFollowAge", new { user = target }, method) is { } follow) result.IsFollowing = Flag(follow, "isFollowing");
        if (TryCallForArgs("twitch.getSubStatus", new { user = target }, method) is { } sub)
        {
            result.IsSubscribed = Flag(sub, "isSubscribed");
            result.SubscriptionTier = Arg(sub, "subTier");
        }
        return result;
    }

    private Core.Users.UserRecord? KnownTwitchUser(string id, string login) =>
        (id.Length > 0 ? _services.Users?.Get("Twitch", id) : null)
        ?? (login.Length > 0 ? _services.Users?.FindByName("Twitch", login) : null);

    private bool TwitchBotSignedIn()
    {
        try
        {
            return HasFunction("twitch.getBot") && Prop(Call("twitch.getBot", null), "userId").Length > 0;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return false;
        }
    }

    // ---- Twitch moderation ----------------------------------------------------------------------------------------

    /// <summary>Times a user out for <paramref name="duration"/> seconds. False when Twitch refused.</summary>
    public bool TwitchTimeoutUser(string userName, int duration, string? reason = null, bool useBot = false) =>
        !string.IsNullOrWhiteSpace(userName)
        && Try(() => Call("twitch.timeout", new { user = Login(userName), duration = Math.Max(1, duration), reason = reason ?? "" }));

    public bool TwitchBanUser(string userName, string? reason = null, bool useBot = false) =>
        !string.IsNullOrWhiteSpace(userName) && Try(() => Call("twitch.ban", new { user = Login(userName), reason = reason ?? "" }));

    /// <summary>Lifts a ban or timeout.</summary>
    public bool TwitchUnbanUser(string userName, bool useBot = false) =>
        !string.IsNullOrWhiteSpace(userName) && Try(() => Call("twitch.unban", new { user = Login(userName) }));

    // ---- Twitch stream --------------------------------------------------------------------------------------------

    /// <summary>Runs an ad of 30 to 180 seconds. True when it started.</summary>
    public bool TwitchRunCommercial(int length) => Try(() => Call("twitch.runAd", new { length = Math.Clamp(length, 30, 180) }));

    /// <summary>Marks the current position of the live stream; null when Twitch refused (for example while offline).</summary>
    public StreamMarker? CreateStreamMarker(string description)
    {
        var created = TryCallForArgs("twitch.createMarker", new { description = description ?? "" }, warn: true);
        if (created is null) return null;
        return new StreamMarker
        {
            Id = Arg(created, "markerId"),
            Position = CphConvert.TryTo(created.GetValueOrDefault("markerPosition"), out int position) ? position : 0,
            Description = description ?? "",
            CreatedAt = DateTime.Now,
        };
    }

    /// <summary>Clips the live stream; null when Twitch refused. Twitch picks the clip's length and title.</summary>
    public ClipData? CreateClip() => Clip("");

    /// <summary>
    /// Clips the live stream. Twitch's clip API takes no title or length, so <paramref name="title"/> is only copied
    /// to the result and <paramref name="duration"/> is ignored.
    /// </summary>
    public ClipData? CreateClip(string? title, int duration) => Clip(title ?? "");

    private ClipData? Clip(string title, [CallerMemberName] string method = "")
    {
        var created = TryCallForArgs("twitch.createClip", new { hasDelay = false }, method, warn: true);
        if (created is null) return null;
        var id = Arg(created, "clipId");
        var broadcaster = Call("twitch.getBroadcaster", null, method);
        var broadcasterId = int.TryParse(Prop(broadcaster, "userId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
        var broadcasterName = Prop(broadcaster, "userName");
        return new ClipData
        {
            Id = id,
            Url = Arg(created, "clipUrl"),
            EditUrl = Arg(created, "clipEditUrl"),
            EmbedUrl = id.Length > 0 ? "https://clips.twitch.tv/embed?clip=" + Uri.EscapeDataString(id) : "",
            BroadcasterId = broadcasterId,
            BroadcasterName = broadcasterName,
            CreatorId = broadcasterId,
            CreatorName = broadcasterName,
            Title = title,
            CreatedAt = DateTime.Now,
        };
    }

    public bool SetChannelTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && Try(() => Call("twitch.setTitle", new { title }));

    /// <summary>Sets the channel's category by name; returns it, or null when no category matches.</summary>
    public GameInfo? SetChannelGame(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;
        var set = TryCallForArgs("twitch.setGame", new { game }, warn: true);
        if (set is null) return null;
        return new GameInfo
        {
            Id = int.TryParse(Arg(set, "gameId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
            Name = Arg(set, "gameName"),
        };
    }

    // ---- YouTube --------------------------------------------------------------------------------------------------

    /// <summary>The most recent broadcast Cuebot monitors, or null when there is none.</summary>
    public YouTubeBroadcastInfo? YouTubeGetLatestMonitoredBroadcast()
    {
        var latest = Call("youtube.getLatestMonitoredBroadcast", null);
        return latest.ValueKind == JsonValueKind.Object ? Broadcast(latest, YouTubeChannelId()) : null;
    }

    /// <summary>Same as <see cref="YouTubeGetLatestMonitoredBroadcast"/>.</summary>
    public YouTubeBroadcastInfo? YouTubeGetLatestBroadcast() => YouTubeGetLatestMonitoredBroadcast();

    /// <summary>Every broadcast Cuebot monitors.</summary>
    public List<YouTubeBroadcastInfo> YouTubeGetMonitoredBroadcasts()
    {
        var all = Call("youtube.getMonitoredBroadcasts", null);
        if (all.ValueKind != JsonValueKind.Array) return [];
        var channelId = YouTubeChannelId();
        return all.EnumerateArray().Select(b => Broadcast(b, channelId)).ToList();
    }

    private string YouTubeChannelId()
    {
        try
        {
            return HasFunction("youtube.getBroadcaster") ? Prop(Call("youtube.getBroadcaster", null), "userId") : "";
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return "";
        }
    }

    private static YouTubeBroadcastInfo Broadcast(JsonElement b, string channelId) => new()
    {
        Id = Prop(b, "id"),
        ChannelId = channelId,
        LiveChatId = Prop(b, "liveChatId"),
        Title = Prop(b, "title"),
        Description = Prop(b, "description"),
        Privacy = Prop(b, "privacy"),
        Status = Prop(b, "status"),
        Url = Prop(b, "url"),
    };

    // ---- Kick -----------------------------------------------------------------------------------------------------

    /// <summary>The Kick broadcaster account, or null when Kick is not signed in.</summary>
    public KickUserInfo? KickGetBroadcaster()
    {
        var info = Call("kick.getBroadcaster", null);
        var name = Prop(info, "broadcastUser");
        return name.Length == 0 ? null : new KickUserInfo
        {
            UserName = name,
            UserLogin = Prop(info, "broadcastUserName") is { Length: > 0 } login ? login : name,
            UserId = Prop(info, "broadcastUserId"),
            ChannelSlug = Prop(info, "broadcasterChannelSlug"),
        };
    }

    /// <summary>The Kick bot account, or null when none is signed in.</summary>
    public KickUserInfo? KickGetBot()
    {
        var info = Call("kick.getBroadcaster", null);
        var name = Prop(info, "botUser");
        return name.Length == 0 ? null : new KickUserInfo { UserName = name, UserLogin = name, UserId = Prop(info, "botUserId") };
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static string Login(string? user) => user?.Trim().TrimStart('@') ?? "";

    /// <summary>Failures a bool/data method reports as false/null: not a missing module and not cancellation.</summary>
    private static bool Recoverable(Exception ex) => ex is not NotSupportedException and not OperationCanceledException;

    /// <summary>Runs a request; false (with a warning) when it failed.</summary>
    private bool Try(Action request, [CallerMemberName] string method = "")
    {
        try
        {
            request();
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            LogWarn($"{method}: {ex.Message}");
            return false;
        }
    }

    private Dictionary<string, object?>? TryCallForArgs(string function, object? args, string method, bool warn = false)
    {
        try
        {
            return CallForArgs(function, args, method);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            if (warn) LogWarn($"{method}: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, object?>? TryCallForArgs(string function, object? args, bool warn, [CallerMemberName] string method = "") =>
        TryCallForArgs(function, args, method, warn);

    /// <summary>A property of a JSON object as text (name matched ignoring case); "" when missing.</summary>
    private static string Prop(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => property.Value.ToString(),
            };
        }
        return "";
    }

    private static string Arg(Dictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) && value is not null ? Text(value) : "";

    private static bool Flag(Dictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) && CphConvert.TryTo(value, out bool on) && on;

    private static DateTime LocalTime(DateTimeOffset? time) =>
        time is { } t && t != default ? t.LocalDateTime : default;
}
