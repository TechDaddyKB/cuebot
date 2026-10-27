namespace Cuebot.Kick.Events;

/// <summary>
/// Builds <see cref="KickEvent"/>s from already-decoded values so the websocket decoder and the webhook parser
/// produce identical arguments and identical de-duplication keys for the same occurrence.
/// </summary>
internal static class KickEventFactory
{
    public sealed record ChatInput(string MessageId, KickActor Sender, string Content, long BroadcasterUserId, DateTimeOffset? SentAt,
        string? ReplyToMessageId = null, string? ReplyToContent = null, KickActor? ReplyToSender = null);

    public static KickEvent Chat(string source, ChatInput c)
    {
        var args = KickArgs.New(source).WithUser(c.Sender).WithBadges(c.Sender, c.BroadcasterUserId);
        args["messageId"] = c.MessageId;
        args["msgId"] = c.MessageId;
        args["message"] = KickArgs.PlainText(c.Content);
        args["rawMessage"] = c.Content;
        args["emoteCount"] = KickArgs.EmoteCount(c.Content);
        args["isReply"] = !string.IsNullOrEmpty(c.ReplyToMessageId);
        if (!string.IsNullOrEmpty(c.ReplyToMessageId))
        {
            args["replyToMessageId"] = c.ReplyToMessageId;
            args["replyToMessage"] = KickArgs.PlainText(c.ReplyToContent ?? "");
            if (c.ReplyToSender is { } r) args.WithUser(r, "replyToUser");
        }
        if (c.SentAt is { } at) args["sentAt"] = at.ToString("o");
        return new KickEvent(KickEventTypes.ChatMessage, args, "chat:" + (c.MessageId.Length > 0 ? c.MessageId : Guid.NewGuid().ToString("N")));
    }

    public static KickEvent Follow(string source, KickActor follower, long followerCount = 0)
    {
        var args = KickArgs.New(source).WithUser(follower);
        if (followerCount > 0) args["followerCount"] = followerCount;
        return new KickEvent(KickEventTypes.Follow, args, "follow:" + Key(follower));
    }

    /// <summary>A new subscription (1 month) or a renewal (more months).</summary>
    public static KickEvent Subscription(string source, KickActor subscriber, int months, bool? isRenewal = null)
    {
        var args = KickArgs.New(source).WithUser(subscriber);
        args["months"] = months;
        args["cumulativeMonths"] = months;
        var renewal = isRenewal ?? months > 1;
        return new KickEvent(renewal ? KickEventTypes.Resubscription : KickEventTypes.Subscription, args, "sub:" + Key(subscriber));
    }

    /// <summary>
    /// Gifted subscriptions: one <c>giftSubscription</c> per recipient, plus a <c>massGiftSubscription</c> first
    /// when several were gifted at once.
    /// </summary>
    public static IReadOnlyList<KickEvent> Gifts(string source, KickActor gifter, IReadOnlyList<KickActor> recipients, long gifterTotal = 0)
    {
        var events = new List<KickEvent>();
        var mass = recipients.Count > 1;
        if (mass)
        {
            var args = KickArgs.New(source).WithUser(gifter);
            args["gifts"] = recipients.Count;
            args["recipients"] = string.Join(", ", recipients.Select(r => r.UserName));
            if (gifterTotal > 0) args["totalGifted"] = gifterTotal;
            events.Add(new KickEvent(KickEventTypes.MassGiftSubscription, args,
                "gifts:" + Key(gifter) + ":" + string.Join(',', recipients.Select(Key).Order(StringComparer.Ordinal))));
        }
        foreach (var recipient in recipients)
        {
            var args = KickArgs.New(source).WithUser(gifter).WithUser(recipient, "recipient");
            args["fromMassGift"] = mass;
            args["gifts"] = recipients.Count;
            if (gifterTotal > 0) args["totalGifted"] = gifterTotal;
            events.Add(new KickEvent(KickEventTypes.GiftSubscription, args, "gift:" + Key(recipient)));
        }
        return events;
    }

    public static KickEvent Kicks(string source, KickActor sender, long amount, string giftName, string giftType, string giftTier, string message)
    {
        var args = KickArgs.New(source).WithUser(sender);
        args["kicks"] = amount;
        args["amount"] = amount;
        args["giftName"] = giftName;
        args["giftType"] = giftType;
        args["giftTier"] = giftTier;
        args["message"] = message;
        return new KickEvent(KickEventTypes.KicksGifted, args, $"kicks:{Key(sender)}:{amount}:{giftName}");
    }

    public static KickEvent Reward(string source, KickActor redeemer, string rewardId, string title, long cost, string userInput,
        string status, string redemptionId)
    {
        var args = KickArgs.New(source).WithUser(redeemer);
        args["rewardId"] = rewardId;
        args["rewardTitle"] = title;
        args["rewardCost"] = cost;
        args["userInput"] = userInput;
        args["rawInput"] = userInput;
        args["status"] = status;
        args["redemptionId"] = redemptionId;
        return new KickEvent(KickEventTypes.RewardRedemption, args, $"reward:{Key(redeemer)}:{title.ToLowerInvariant()}:{status}");
    }

    public static KickEvent Host(string source, string hostName, long viewers, string message)
    {
        var args = KickArgs.New(source).WithUser(new KickActor(0, hostName));
        args["viewers"] = viewers;
        args["message"] = message;
        return new KickEvent(KickEventTypes.Host, args, "host:" + hostName.ToLowerInvariant());
    }

    public static KickEvent StreamOnline(string source, string title, DateTimeOffset? startedAt, long livestreamId = 0)
    {
        var args = KickArgs.New(source);
        args["title"] = title;
        if (startedAt is { } t) args["startedAt"] = t.ToString("o");
        if (livestreamId > 0) args["livestreamId"] = KickArgs.Id(livestreamId);
        return new KickEvent(KickEventTypes.StreamOnline, args, "stream:online");
    }

    public static KickEvent StreamOffline(string source, DateTimeOffset? endedAt = null)
    {
        var args = KickArgs.New(source);
        if (endedAt is { } t) args["endedAt"] = t.ToString("o");
        return new KickEvent(KickEventTypes.StreamOffline, args, "stream:offline");
    }

    public static KickEvent ChannelUpdate(string source, string title, string category, long categoryId, string language, bool isMature)
    {
        var args = KickArgs.New(source);
        args["title"] = title;
        args["category"] = category;
        args["gameName"] = category;
        args["categoryId"] = categoryId > 0 ? KickArgs.Id(categoryId) : "";
        args["language"] = language;
        args["isMature"] = isMature;
        return new KickEvent(KickEventTypes.ChannelUpdate, args, $"meta:{title}:{categoryId}");
    }

    public static KickEvent ViewerCount(string source, long viewers)
    {
        var args = KickArgs.New(source);
        args["viewers"] = viewers;
        args["viewerCount"] = viewers;
        return new KickEvent(KickEventTypes.ViewerCountUpdate, args, "viewers:" + viewers);
    }

    /// <summary>A ban (no duration) or a timeout (<paramref name="durationSeconds"/> &gt; 0).</summary>
    public static KickEvent Ban(string source, KickActor target, KickActor? moderator, long durationSeconds, string reason,
        DateTimeOffset? expiresAt)
    {
        var args = KickArgs.New(source).WithUser(target);
        if (moderator is not null) args.WithUser(moderator, "moderator");
        args["reason"] = reason;
        var timeout = durationSeconds > 0;
        args["duration"] = timeout ? durationSeconds : 0;
        args["isPermanent"] = !timeout;
        if (expiresAt is { } t) args["expiresAt"] = t.ToString("o");
        return new KickEvent(timeout ? KickEventTypes.UserTimedOut : KickEventTypes.UserBanned, args, "ban:" + Key(target));
    }

    public static KickEvent Unban(string source, KickActor target, KickActor? moderator)
    {
        var args = KickArgs.New(source).WithUser(target);
        if (moderator is not null) args.WithUser(moderator, "moderator");
        return new KickEvent(KickEventTypes.UserUnbanned, args, "unban:" + Key(target));
    }

    public static KickEvent MessageDeleted(string source, string messageId)
    {
        var args = KickArgs.New(source);
        args["messageId"] = messageId;
        args["targetMessageId"] = messageId;
        return new KickEvent(KickEventTypes.MessageDeleted, args, "deleted:" + messageId);
    }

    public static KickEvent ChatCleared(string source, string id) =>
        new(KickEventTypes.ChatCleared, KickArgs.New(source), "cleared:" + id);

    /// <summary>Lower-case name when known (some sources send no ids), otherwise the id.</summary>
    private static string Key(KickActor actor) => actor.UserName.Length > 0 ? actor.UserName.ToLowerInvariant() : KickArgs.Id(actor.Id);
}
