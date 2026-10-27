namespace Cuebot.Twitch;

/// <summary>Persisted Twitch configuration and sign-in state (twitch.json).</summary>
public sealed class TwitchSettings
{
    /// <summary>Client ID of the user's own Twitch application (public client, no secret).</summary>
    public string ClientId { get; set; } = "";

    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>Twitch user id of the signed-in account (also the broadcaster whose channel is joined).</summary>
    public string UserId { get; set; } = "";

    public string Login { get; set; } = "";
    public bool AutoConnect { get; set; } = true;

    /// <summary>Scopes actually granted to the current token.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Scopes to request at the next broadcaster sign-in; empty means every scope Cuebot can use.</summary>
    public List<string> RequestedScopes { get; set; } = [];

    /// <summary>Optional second account that sends chat messages and whispers.</summary>
    public TwitchBotAccount Bot { get; set; } = new();

    /// <summary>Send chat messages from the bot account when it is signed in (otherwise from the broadcaster).</summary>
    public bool SendChatAsBot { get; set; } = true;

    /// <summary>Send whispers from the bot account when it is signed in.</summary>
    public bool SendWhispersAsBot { get; set; } = true;

    /// <summary>How often the viewer count is checked while live (seconds; 0 disables).</summary>
    public int ViewerCountIntervalSeconds { get; set; } = 60;

    /// <summary>How often present viewers are listed (seconds; 0 disables). Needs moderator:read:chatters.</summary>
    public int PresentViewersIntervalSeconds { get; set; } = 300;

    /// <summary>Publish "upcoming ad" warnings each minute from 5 minutes before a scheduled ad. Needs channel:read:ads.</summary>
    public bool UpcomingAdWarnings { get; set; } = true;

    /// <summary>The sub counter (Streamer.bot's Twitch sub counter with rollover).</summary>
    public TwitchSubCounter SubCounter { get; set; } = new();

    /// <summary>Cuebot-side reward settings and counters, by reward id.</summary>
    public Dictionary<string, TwitchRewardState> Rewards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The most recent subscription event (Helix has no "latest subscriber" lookup).</summary>
    public TwitchLatestSubscriber LatestSubscriber { get; set; } = new();
}

/// <summary>Counts new subs (and optionally resubs and gifted subs); every <see cref="Rollover"/> subs it starts over.</summary>
public sealed class TwitchSubCounter
{
    public int Count { get; set; }

    /// <summary>Subs per round; 0 means the counter never rolls over.</summary>
    public int Rollover { get; set; }

    /// <summary>How many times the counter reached <see cref="Rollover"/>.</summary>
    public int RolloverCount { get; set; }

    public bool CountResubs { get; set; } = true;
    public bool CountGiftedSubs { get; set; } = true;
}

/// <summary>A reward's group and redemption counters (reset when the stream goes online unless persisted).</summary>
public sealed class TwitchRewardState
{
    /// <summary>Reward group name for the group sub-actions; empty means "not configured".</summary>
    public string Group { get; set; } = "";

    public bool PersistCounter { get; set; }
    public bool PersistUserCounter { get; set; }
    public int Counter { get; set; }
    public Dictionary<string, int> UserCounters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TwitchLatestSubscriber
{
    public string UserId { get; set; } = "";
    public string UserLogin { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Tier { get; set; } = "";
    public bool IsGift { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>Tokens and identity of the optional bot account.</summary>
public sealed class TwitchBotAccount
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}
