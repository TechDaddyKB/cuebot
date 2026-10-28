using System.Text.RegularExpressions;
using static Cuebot.Core.Variables.StreamerBotArgs;
using K = Cuebot.Kick.KickEventTypes;

namespace Cuebot.Kick.Compat;

/// <summary>The broadcaster account, for Streamer.bot's <c>broadcastUser*</c> arguments on every Kick event.</summary>
internal sealed record KickBroadcasterArgs(string UserName, string UserId, string ProfileImage = "");

/// <summary>
/// Adds Streamer.bot's documented trigger argument names (docs: <c>3.api/2.triggers/kick/**</c> and the
/// <c>KickUser</c>, <c>KickChat</c>, <c>KickBroadcaster</c> variable sets) to Kick events, derived from the names
/// Cuebot already sets. Cuebot's own names are kept; nothing is overwritten. Values that need memory across events
/// (previous title and category, stream start) are added by the event hub.
/// </summary>
internal static partial class StreamerBotVariables
{
    public static void Apply(string type, Dictionary<string, object?> a, KickBroadcasterArgs? broadcaster, DateTimeOffset at)
    {
        if (!type.StartsWith("kick.", StringComparison.Ordinal)) return;

        // KickBroadcaster
        if (broadcaster is { UserId.Length: > 0 })
        {
            Set(a, "broadcastUser", broadcaster.UserName);
            Set(a, "broadcastUsername", broadcaster.UserName);
            Set(a, "broadcastUserId", broadcaster.UserId);
            if (broadcaster.ProfileImage.Length > 0) Set(a, "broadcastUserProfileImage", broadcaster.ProfileImage);
        }

        // KickUser
        if (Text(a, "user").Length > 0 || Text(a, "userId").Length > 0)
        {
            Set(a, "userType", "kick");
            Alias(a, "isSubscribed", "isSubscriber");
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
                Alias(a, "kicks.amount", "kicks");
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
                break;
            case K.StreamOnline:
                Alias(a, "category.name", "categoryName");
                Alias(a, "category.id", "categoryId");
                Alias(a, "category.thumbnail", "categoryThumbnail");
                break;
            case K.StreamOffline:
                SetTime(a, "endedAt", at);
                break;
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
