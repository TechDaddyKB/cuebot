namespace Cuebot.Twitch;

/// <summary>One OAuth scope Cuebot can request, with what it unlocks (shown on the Settings tab).</summary>
public sealed record TwitchScopeInfo(string Scope, string Group, string Description);

/// <summary>OAuth scopes Cuebot requests when signing in.</summary>
public static class TwitchScopes
{
    public const string ReadChat = "user:read:chat";
    public const string WriteChat = "user:write:chat";
    public const string ReadFollowers = "moderator:read:followers";
    public const string ReadSubscriptions = "channel:read:subscriptions";
    public const string ReadBits = "bits:read";
    public const string ReadRedemptions = "channel:read:redemptions";
    public const string ManageRedemptions = "channel:manage:redemptions";
    public const string ManageBroadcast = "channel:manage:broadcast";
    public const string EditCommercial = "channel:edit:commercial";
    public const string ReadAds = "channel:read:ads";
    public const string ManageAds = "channel:manage:ads";
    public const string ManageRaids = "channel:manage:raids";
    public const string ManageShoutouts = "moderator:manage:shoutouts";
    public const string ManageAnnouncements = "moderator:manage:announcements";
    public const string ManageWhispers = "user:manage:whispers";
    public const string ManagePolls = "channel:manage:polls";
    public const string ManagePredictions = "channel:manage:predictions";
    public const string ManageBannedUsers = "moderator:manage:banned_users";
    public const string ManageChatMessages = "moderator:manage:chat_messages";
    public const string ManageWarnings = "moderator:manage:warnings";
    public const string ManageVips = "channel:manage:vips";
    public const string ManageModerators = "channel:manage:moderators";
    public const string ModerationRead = "moderation:read";
    public const string ChannelModerate = "channel:moderate";
    public const string ManageChatSettings = "moderator:manage:chat_settings";
    public const string ManageShieldMode = "moderator:manage:shield_mode";
    public const string EditClips = "clips:edit";
    public const string ManageClips = "channel:manage:clips";

    /// <summary>The editor form of <see cref="ManageClips"/>; Cuebot requests the channel one but accepts either.</summary>
    public const string EditorManageClips = "editor:manage:clips";

    public const string ReadHypeTrain = "channel:read:hype_train";
    public const string ReadGoals = "channel:read:goals";
    public const string ReadCharity = "channel:read:charity";
    public const string ManageUnbanRequests = "moderator:manage:unban_requests";
    public const string ReadSuspiciousUsers = "moderator:read:suspicious_users";
    public const string ManageSuspiciousUsers = "moderator:manage:suspicious_users";
    public const string ManageAutomod = "moderator:manage:automod";
    public const string ManageBlockedTerms = "moderator:manage:blocked_terms";
    public const string ReadChatters = "moderator:read:chatters";
    public const string ReadModerators = "moderator:read:moderators";
    public const string ReadVips = "moderator:read:vips";
    public const string ManageGuestStar = "channel:manage:guest_star";

    /// <summary>Every scope Cuebot knows, grouped for the Settings tab.</summary>
    public static IReadOnlyList<TwitchScopeInfo> Catalog { get; } =
    [
        new(ReadChat, "Chat", "Read chat, chat notifications, deletions and chat mode changes"),
        new(WriteChat, "Chat", "Send chat messages"),
        new(ManageAnnouncements, "Chat", "Send announcements"),
        new(ManageWhispers, "Chat", "Send and receive whispers"),
        new(ReadChatters, "Chat", "List present viewers"),
        new(ReadFollowers, "Community", "Follow events, follower count and follow age"),
        new(ReadSubscriptions, "Community", "Subscription events, sub count and sub status"),
        new(ReadBits, "Community", "Cheers, bits and power-ups"),
        new(ReadHypeTrain, "Community", "Hype train events"),
        new(ReadGoals, "Community", "Creator goal events"),
        new(ReadCharity, "Community", "Charity campaign events"),
        new(ReadRedemptions, "Channel points", "Read reward redemptions"),
        new(ManageRedemptions, "Channel points", "Manage rewards and fulfil or cancel redemptions"),
        new(ManagePolls, "Channel points", "Polls: events, create and end"),
        new(ManagePredictions, "Channel points", "Predictions: events, create, lock and resolve"),
        new(ManageBroadcast, "Channel", "Title, category, tags, content labels and stream markers"),
        new(EditCommercial, "Channel", "Run ads"),
        new(ReadAds, "Channel", "Ad break events and ad schedule"),
        new(ManageAds, "Channel", "Snooze the next ad"),
        new(ManageRaids, "Channel", "Start and cancel raids"),
        new(EditClips, "Channel", "Create clips"),
        new(ManageClips, "Channel", "Clip download URLs"),
        new(ManageShoutouts, "Moderation", "Send shoutouts and shoutout events"),
        new(ChannelModerate, "Moderation", "Ban and unban events"),
        new(ModerationRead, "Moderation", "Moderator added and removed events"),
        new(ManageBannedUsers, "Moderation", "Ban, unban and time out users"),
        new(ManageChatMessages, "Moderation", "Delete messages, clear chat and pin messages"),
        new(ManageWarnings, "Moderation", "Warn users and warning events"),
        new(ManageVips, "Moderation", "Add and remove VIPs, VIP events"),
        new(ManageModerators, "Moderation", "Add and remove moderators"),
        new(ManageChatSettings, "Moderation", "Slow, follower, subscriber, emote-only and unique chat modes"),
        new(ManageShieldMode, "Moderation", "Shield mode: events and toggle"),
        new(ManageUnbanRequests, "Moderation", "Unban request events"),
        new(ReadSuspiciousUsers, "Moderation", "Suspicious user events"),
        new(ManageSuspiciousUsers, "Moderation", "Add and remove a chatter's suspicious status"),
        new(ManageAutomod, "Moderation", "AutoMod held messages and term list events"),
        new(ManageBlockedTerms, "Moderation", "Add and remove blocked terms"),
        new(ReadModerators, "Moderation", "Shared chat moderation events (with the ban, chat message, chat mode, term, unban request and warning scopes)"),
        new(ReadVips, "Moderation", "Shared chat moderation events (see above)"),
        new(ManageGuestStar, "Channel", "Guest Star: events, sessions, invites and slots (Twitch beta)"),
    ];

    /// <summary>Everything Cuebot can use (the default).</summary>
    public static IReadOnlyList<string> All { get; } = Catalog.Select(s => s.Scope).ToArray();

    /// <summary>The minimal set: chat plus the classic alerts (follows, subs, bits, redemptions).</summary>
    public static IReadOnlyList<string> Minimal { get; } =
        [ReadChat, WriteChat, ReadFollowers, ReadSubscriptions, ReadBits, ReadRedemptions];

    /// <summary>What the optional bot account needs: send chat, send and receive whispers.</summary>
    public static IReadOnlyList<string> Bot { get; } = [ReadChat, WriteChat, ManageWhispers];

    /// <summary>
    /// Scopes to request for the broadcaster: <paramref name="requested"/> limited to known scopes (empty means
    /// <see cref="All"/>), always including chat read and write.
    /// </summary>
    public static IReadOnlyList<string> ForBroadcaster(IReadOnlyCollection<string> requested)
    {
        if (requested.Count == 0) return All;
        var wanted = requested.ToHashSet(StringComparer.Ordinal);
        wanted.Add(ReadChat);
        wanted.Add(WriteChat);
        return All.Where(wanted.Contains).ToArray();
    }
}
