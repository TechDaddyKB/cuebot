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
}
