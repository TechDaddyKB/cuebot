namespace Cuebot.Kick.Auth;

/// <summary>OAuth scopes requested from Kick (docs.kick.com/getting-started/scopes).</summary>
internal static class KickScopes
{
    public const string UserRead = "user:read";
    public const string ChannelRead = "channel:read";
    public const string ChannelWrite = "channel:write";
    public const string ChatWrite = "chat:write";
    public const string EventsSubscribe = "events:subscribe";
    public const string ModerationBan = "moderation:ban";
    public const string ModerationChat = "moderation:chat_message:manage";
    public const string KicksRead = "kicks:read";
    public const string RewardsRead = "channel:rewards:read";
    public const string RewardsWrite = "channel:rewards:write";

    /// <summary>Adding a scope here means existing broadcaster sign-ins must sign in again to grant it.</summary>
    public static IReadOnlyList<string> Broadcaster { get; } =
        [UserRead, ChannelRead, ChannelWrite, ChatWrite, EventsSubscribe, ModerationBan, ModerationChat, KicksRead, RewardsRead, RewardsWrite];

    /// <summary>A bot account only chats and (when it is a moderator) moderates.</summary>
    public static IReadOnlyList<string> Bot { get; } = [UserRead, ChatWrite, ModerationBan, ModerationChat];

    public static IReadOnlyList<string> For(KickAccount account) => account == KickAccount.Bot ? Bot : Broadcaster;
}

/// <summary>Which signed-in account a request uses.</summary>
public enum KickAccount { Broadcaster, Bot }
