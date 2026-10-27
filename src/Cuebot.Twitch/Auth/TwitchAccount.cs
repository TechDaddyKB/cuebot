namespace Cuebot.Twitch.Auth;

/// <summary>Where one signed-in account's tokens and identity live inside <see cref="TwitchSettings"/>.</summary>
internal interface ITwitchAccount
{
    string AccessToken { get; set; }
    string RefreshToken { get; set; }
    string UserId { get; set; }
    string Login { get; set; }
    List<string> Scopes { get; set; }
}

/// <summary>The broadcaster account, stored in the top-level fields of <see cref="TwitchSettings"/>.</summary>
internal sealed class BroadcasterAccount(TwitchSettings settings) : ITwitchAccount
{
    public string AccessToken { get => settings.AccessToken; set => settings.AccessToken = value; }
    public string RefreshToken { get => settings.RefreshToken; set => settings.RefreshToken = value; }
    public string UserId { get => settings.UserId; set => settings.UserId = value; }
    public string Login { get => settings.Login; set => settings.Login = value; }
    public List<string> Scopes { get => settings.Scopes; set => settings.Scopes = value; }
}

/// <summary>The optional bot account (<see cref="TwitchSettings.Bot"/>).</summary>
internal sealed class BotAccount(TwitchSettings settings) : ITwitchAccount
{
    public string AccessToken { get => settings.Bot.AccessToken; set => settings.Bot.AccessToken = value; }
    public string RefreshToken { get => settings.Bot.RefreshToken; set => settings.Bot.RefreshToken = value; }
    public string UserId { get => settings.Bot.UserId; set => settings.Bot.UserId = value; }
    public string Login { get => settings.Bot.Login; set => settings.Bot.Login = value; }
    public List<string> Scopes { get => settings.Bot.Scopes; set => settings.Bot.Scopes = value; }
}
