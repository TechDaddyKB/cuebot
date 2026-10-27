namespace Cuebot.Twitch;

/// <summary>OAuth scopes Cuebot requests when signing in.</summary>
public static class TwitchScopes
{
    public const string ReadChat = "user:read:chat";
    public const string WriteChat = "user:write:chat";
    public const string ReadFollowers = "moderator:read:followers";
    public const string ReadSubscriptions = "channel:read:subscriptions";
    public const string ReadBits = "bits:read";
    public const string ReadRedemptions = "channel:read:redemptions";

    public static IReadOnlyList<string> All { get; } =
        [ReadChat, WriteChat, ReadFollowers, ReadSubscriptions, ReadBits, ReadRedemptions];
}
