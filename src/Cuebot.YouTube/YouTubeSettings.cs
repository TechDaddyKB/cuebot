using Cuebot.Core.Storage;
namespace Cuebot.YouTube;

/// <summary>Persisted YouTube configuration and sign-in state (youtube.json). Tokens and the client secret are never logged.</summary>
public sealed class YouTubeSettings
{
    /// <summary>OAuth client ID from the user's own Google Cloud project (Advanced); empty uses Cuebot's built-in client.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// OAuth client secret of that client. Google issues one even to installed apps and requires it at the token
    /// endpoint; it is not confidential for installed apps, but Cuebot still treats it like a secret.
    /// </summary>
    [Secret]
    public string ClientSecret { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>The channel that streams; required. Its broadcasts are monitored and moderation runs as this account.</summary>
    public YouTubeAccount Broadcaster { get; set; } = new();

    /// <summary>Optional second account that can post chat messages ("useBot").</summary>
    public YouTubeAccount Bot { get; set; } = new();

    /// <summary>
    /// Receive live chat as a stream (liveChatMessages.streamList): messages arrive as they are posted, for one quota
    /// charge per connection. Off, or when the stream cannot be opened, chat is polled instead.
    /// </summary>
    public bool StreamChat { get; set; } = true;

    /// <summary>
    /// Lower bound for the time between live chat polls. YouTube suggests an interval with each response (often
    /// only a few seconds); every poll costs quota, so Cuebot waits at least this long. Applies when chat is polled.
    /// </summary>
    public int MinimumChatPollSeconds { get; set; } = 8;

    /// <summary>Seconds between checks for a new active broadcast.</summary>
    public int BroadcastPollSeconds { get; set; } = 120;

    /// <summary>Seconds between broadcast statistics/info refreshes while live.</summary>
    public int LivePollSeconds { get; set; } = 60;

    /// <summary>Also watch upcoming (scheduled) broadcasts for the Added/Removed triggers. Costs one more list call per check.</summary>
    public bool MonitorUpcoming { get; set; }

    /// <summary>Poll the channel's recent subscribers (only public subscriptions are visible) for the New Subscriber trigger.</summary>
    public bool PollNewSubscribers { get; set; } = true;

    public int SubscriberPollSeconds { get; set; } = 300;

    /// <summary>The Google Cloud project's daily YouTube Data API quota (default 10,000 units), for estimates.</summary>
    public int DailyQuota { get; set; } = 10_000;

    /// <summary>Quota day (yyyy-MM-dd, Pacific time, when Google resets quotas) that <see cref="QuotaUsed"/> belongs to.</summary>
    public string QuotaDay { get; set; } = "";

    /// <summary>Units Cuebot estimates it has spent on <see cref="QuotaDay"/>.</summary>
    public int QuotaUsed { get; set; }
}

/// <summary>A signed-in Google account / YouTube channel.</summary>
public sealed class YouTubeAccount
{
    [Secret]
    public string AccessToken { get; set; } = "";
    [Secret]
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public List<string> Scopes { get; set; } = [];
    public string ChannelId { get; set; } = "";
    public string ChannelTitle { get; set; } = "";
    public string CustomUrl { get; set; } = "";
    public string ProfileImageUrl { get; set; } = "";

    /// <summary>
    /// Which client issued the tokens: "builtin" (Cuebot's browser client), "builtin-device" (Cuebot's code client)
    /// or empty (the user's own client).
    /// </summary>
    public string SignedInWith { get; set; } = "";

    public bool IsSignedIn => RefreshToken.Length > 0 || AccessToken.Length > 0;

    internal void Clear()
    {
        AccessToken = "";
        RefreshToken = "";
        AccessTokenExpiresAt = default;
        Scopes = [];
        ChannelId = "";
        ChannelTitle = "";
        CustomUrl = "";
        ProfileImageUrl = "";
        SignedInWith = "";
    }
}

/// <summary>Which of the two accounts an operation uses.</summary>
public enum YouTubeAccountKind { Broadcaster, Bot }
