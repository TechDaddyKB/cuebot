namespace Cuebot.Kick;

/// <summary>Tokens and identity of one signed-in Kick account. Tokens are secrets and are never logged.</summary>
public sealed class KickAccountTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>When the access token expires (UTC); it is refreshed shortly before.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public List<string> Scopes { get; set; } = [];
    public long UserId { get; set; }
    public string UserName { get; set; } = "";

    public bool IsSignedIn => RefreshToken.Length > 0 || AccessToken.Length > 0;

    public void Clear()
    {
        AccessToken = "";
        RefreshToken = "";
        ExpiresAt = default;
        Scopes = [];
        UserId = 0;
        UserName = "";
    }
}

/// <summary>Persisted Kick configuration (kick.json).</summary>
public sealed class KickSettings
{
    /// <summary>Client ID of the user's own Kick app (kick.com → Settings → Developer).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret of that app. Kick's token endpoint requires it even with PKCE. Never logged.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Port of the loopback redirect http://localhost:{port}/callback, which must be registered in the app.</summary>
    public int RedirectPort { get; set; } = KickEndpoints.DefaultRedirectPort;

    public bool AutoConnect { get; set; } = true;

    /// <summary>The broadcaster account: reads the channel, sets title/category, moderates, sends chat.</summary>
    public KickAccountTokens Broadcaster { get; set; } = new();

    /// <summary>Optional second account used for sending chat when "use bot" is chosen.</summary>
    public KickAccountTokens Bot { get; set; } = new();

    /// <summary>Channel slug (from the official API after sign-in), e.g. "cuebotdev".</summary>
    public string ChannelSlug { get; set; } = "";

    /// <summary>
    /// Kick's internal channel id (not the broadcaster user id) for real-time channel events. Filled automatically
    /// from kick.com when possible; can be entered manually when that lookup is blocked.
    /// </summary>
    public long ChannelId { get; set; }

    /// <summary>Kick's internal chatroom id for real-time chat. Filled automatically or entered manually.</summary>
    public long ChatroomId { get; set; }

    /// <summary>Read chat and channel events in real time from kick.com's public (unofficial) real-time websocket.</summary>
    public bool UseRealtimeChat { get; set; } = true;

    /// <summary>Seconds between official API polls of the channel (live state, viewers, title, category).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>
    /// When true, connecting also registers Kick webhook subscriptions. Webhook events reach Cuebot only through
    /// the user-hosted relay (configured separately, see PROTOCOL.md).
    /// </summary>
    public bool UseWebhookRelay { get; set; }

    /// <summary>Watch the channel's 7TV emote set in real time (7TV EventAPI) for the 7TV emote added/removed triggers.</summary>
    public bool UseSevenTvEvents { get; set; } = true;

    /// <summary>Cuebot-side reward settings and counters (groups, redemption counters), by reward id.</summary>
    public Dictionary<string, KickRewardState> Rewards { get; set; } = new(StringComparer.Ordinal);
}
