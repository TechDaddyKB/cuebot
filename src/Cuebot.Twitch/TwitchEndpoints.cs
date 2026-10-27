namespace Cuebot.Twitch;

/// <summary>Base addresses of the Twitch services; overridable so tests can point at fakes.</summary>
internal sealed record TwitchEndpoints(Uri Auth, Uri Helix, Uri EventSub)
{
    public static TwitchEndpoints Production { get; } = new(
        new Uri("https://id.twitch.tv/oauth2/"),
        new Uri("https://api.twitch.tv/helix/"),
        new Uri("wss://eventsub.wss.twitch.tv/ws"));
}
