using System.Globalization;
using System.Text.RegularExpressions;
using static Cuebot.Core.Variables.StreamerBotArgs;
using K = Cuebot.Kick.KickEventTypes;

namespace Cuebot.Kick.Compat;

/// <summary>
/// The broadcaster account, for Streamer.bot's <c>broadcastUser*</c> and <c>broadcasterChannel.*</c> arguments on every
/// Kick event. The profile picture is read once per connection (not per event).
/// </summary>
internal sealed record KickBroadcasterArgs(string UserName, string UserId, string ProfileImage = "")
{
    /// <summary>The channel's current title and category; null until read.</summary>
    public KickChannelArgs? Channel { get; init; }
}

/// <summary>The broadcaster's channel, for Streamer.bot's <c>broadcasterChannel.*</c> arguments.</summary>
internal sealed record KickChannelArgs(string Title, string CategoryId, string CategoryName, string CategoryThumbnail);

/// <summary>A user's roles in the broadcaster's chat as last seen (chat badges).</summary>
internal readonly record struct KickUserRoles(bool IsModerator, bool IsSubscriber);

/// <summary>
/// Adds Streamer.bot's documented trigger argument names (docs: <c>3.api/2.triggers/kick/**</c> and the
/// <c>KickUser</c>, <c>KickChat</c>, <c>KickBroadcaster</c> variable sets) to Kick events, derived from the names
/// Cuebot already sets. Cuebot's own names are kept; nothing is overwritten. Values that need memory across events
/// (previous title and category, stream start) are added by the event hub.
/// </summary>
internal static partial class StreamerBotVariables
{
    /// <param name="roles">The roles last seen for a user id (null when unknown), for events without chat badges.</param>
    public static void Apply(string type, Dictionary<string, object?> a, KickBroadcasterArgs? broadcaster, DateTimeOffset at,
        Func<string, KickUserRoles?>? roles = null)
    {
        if (!type.StartsWith("kick.", StringComparison.Ordinal)) return;

        // KickBroadcaster. Streamer.bot's runs spell it broadcastUserName (its docs: broadcastUsername; Cuebot's argument
        // names are case-insensitive, so both resolve).
        if (broadcaster is { UserId.Length: > 0 })
        {
            Set(a, "broadcastUser", broadcaster.UserName);
            Set(a, "broadcastUserName", broadcaster.UserName);
            Set(a, "broadcastUserId", broadcaster.UserId);
            if (broadcaster.ProfileImage.Length > 0) Set(a, "broadcastUserProfileImage", broadcaster.ProfileImage);
            // The account block Streamer.bot adds once an account is connected: the channel's title and category.
            var channel = broadcaster.Channel;
            Set(a, "broadcasterChannel.categoryId", Int(channel?.CategoryId) ?? 0);
            Set(a, "broadcasterChannel.categoryName", channel?.CategoryName ?? "");
            Set(a, "broadcasterChannel.categoryThumbnail", channel?.CategoryThumbnail ?? "");
            Set(a, "broadcasterChannel.title", channel?.Title ?? "");
        }

        // KickUser. Events without chat badges get the roles last seen for the user (false when not seen).
        if (Text(a, "user").Length > 0 || Text(a, "userId").Length > 0)
        {
            Set(a, "userType", "kick");
            Alias(a, "isSubscribed", "isSubscriber");
            var known = Text(a, "userId") is { Length: > 0 } userId ? roles?.Invoke(userId) : null;
            Set(a, "isModerator", known?.IsModerator ?? false);
            Set(a, "isSubscribed", known?.IsSubscriber ?? false);
        }

        switch (type)
        {
            case K.ChatMessage:
                Chat(a);
                break;
            case K.RewardRedemption:
                Alias(a, "reward.id", "rewardId");
                Alias(a, "reward.title", "rewardTitle");
                Alias(a, "reward.cost", "rewardCost");
                Alias(a, "reward.description", "rewardDescription");
                Alias(a, "reward.backgroundColor", "rewardBackgroundColor");
                Alias(a, "reward.skipsQueue", "rewardSkipsQueue");
                Alias(a, "reward.requiresUserInput", "rewardRequiresUserInput");
                AddInput(a, Text(a, "userInput"), words: false);
                break;
            case K.KicksGifted:
                // Streamer.bot types the amount as a decimal.
                if (a.ContainsKey("kicks")) Set(a, "kicks.amount", (decimal)Number(a, "kicks"));
                Alias(a, "kicks.name", "giftName");
                Alias(a, "kicks.type", "giftType");
                Alias(a, "kicks.tier", "giftTier");
                Alias(a, "kicks.pinnedTimeSeconds", "pinnedTimeSeconds");
                Alias(a, "kicks.createdAt", "createdAt");
                Input(a, Text(a, "message"));
                break;
            case K.Subscription or K.Resubscription:
                Alias(a, "monthsSubscribed", "months");
                Alias(a, "duration", "months");
                break;
            case K.GiftSubscription:
                Alias(a, "recipient.userName", "recipient");
                Alias(a, "recipient.userLogin", "recipientUserName");
                Alias(a, "recipient.userId", "recipientId");
                Alias(a, "recipient.profilePicture", "recipientProfilePicture");
                Set(a, "recipient.platform", "kick");
                break;
            case K.MassGiftSubscription:
                Alias(a, "count", "gifts");
                break;
            case K.UserBanned or K.UserTimedOut or K.UserUnbanned:
                Alias(a, "createdById", "moderatorId");
                Alias(a, "createdByUsername", "moderatorUserName");
                Alias(a, "createdByDisplayName", "moderator");
                SetTime(a, "createdAt", a.GetValueOrDefault("bannedAt") is string { Length: > 0 } banned ? banned : at);
                break;
            case K.ChannelUpdate:
                Alias(a, "categoryName", "category");
                Alias(a, "status", "title");
                // Streamer.bot reports whether the title changed as text ("True" / "False").
                if (a.GetValueOrDefault("statusUpdated") is bool changed) Set(a, "statusUpdate", changed.ToString());
                break;
            case K.StreamOnline:
                ToTime(a, "startedAt");
                Alias(a, "category.name", "categoryName");
                // Streamer.bot types the category id as a number here (and as text on Channel Update).
                if (Int(Text(a, "categoryId")) is { } category) Set(a, "category.id", category);
                Alias(a, "category.thumbnail", "categoryThumbnail");
                break;
            case K.StreamOffline:
                ToTime(a, "startedAt");
                SetTime(a, "endedAt", at);
                break;
            case K.ViewerCountUpdate:
                ToInt(a, "viewerCount");
                break;
        }

        Placeholders(type, a, broadcaster is { UserId.Length: > 0 });
    }

    /// <summary>
    /// Documented by Streamer.bot but not available from Kick's API: present with a neutral value so imported actions
    /// that read them keep working. Runs last, so real values win.
    /// </summary>
    private static void Placeholders(string type, Dictionary<string, object?> a, bool hasBroadcaster)
    {
        if (hasBroadcaster) Placeholder(a, "broadcastUserProfileImage", "");
        if (a.ContainsKey("userType"))
        {
            Placeholder(a, "userGroups", "");          // KickUser: comma-delimited groups (Streamer.bot user database)
            Placeholder(a, "userPreviousActive", default(DateTime));
        }
        if (type is K.Subscription or K.Resubscription)
        {
            Placeholder(a, "badgeCount", 0);            // subscription events carry no badges
            Placeholder(a, "badge", new List<Dictionary<string, object?>>());
        }
    }

    /// <summary>KickChat: messageStripped, colorR/G/B, emotes.#.*, badge.#.* (set by the factory) and the input words.</summary>
    private static void Chat(Dictionary<string, object?> a)
    {
        var raw = a.ContainsKey("rawMessage") ? Text(a, "rawMessage") : Text(a, "message");
        Set(a, "messageStripped", Whitespace().Replace(EmoteToken().Replace(raw, ""), " ").Trim());
        AddColor(a, Text(a, "color"));
        var i = 0;
        foreach (Match m in EmoteToken().Matches(raw))
        {
            var id = m.Groups["id"].Value;
            Set(a, $"emotes.{i}.text", m.Groups["name"].Value);
            Set(a, $"emotes.{i}.id", id);
            Set(a, $"emotes.{i}.url", $"https://files.kick.com/emotes/{id}/fullsize");
            i++;
        }
        Input(a, a.ContainsKey("rawInput") ? Text(a, "rawInput") : Text(a, "message"));
    }

    /// <summary>An id or count given as text, as Streamer.bot's Int32; null when it is not a whole number.</summary>
    private static int? Int(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>rawInput, its escaped forms, input.count and input#, inputEscaped#, inputUrlEncoded#.</summary>
    private static void Input(Dictionary<string, object?> a, string text)
    {
        AddInput(a, text);
        Set(a, "input.count", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [GeneratedRegex(@"\[emote:(?<id>\d+):(?<name>[^\]]*)\]")]
    private static partial Regex EmoteToken();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
