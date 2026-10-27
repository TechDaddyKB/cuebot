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
