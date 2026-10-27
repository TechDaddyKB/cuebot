namespace Cuebot.Twitch.Triggers;

/// <summary>Chat permission levels, lowest to highest. A user has every level up to their highest role.</summary>
internal enum ChatRole
{
    Everyone,
    Subscriber,
    Vip,
    Moderator,
    Broadcaster,
}

/// <summary>Resolves the role of a chatter from the flags on a <c>twitch.chatMessage</c> event.</summary>
internal static class ChatRoles
{
    public static IReadOnlyList<string> Choices { get; } = ["Everyone", "Subscriber", "VIP", "Moderator", "Broadcaster"];

    public static ChatRole Parse(string choice) =>
        Enum.TryParse<ChatRole>(choice, ignoreCase: true, out var role) ? role : ChatRole.Everyone;

    public static ChatRole Of(IReadOnlyDictionary<string, object?> args) =>
        args.GetBool("isBroadcaster") ? ChatRole.Broadcaster
        : args.GetBool("isModerator") ? ChatRole.Moderator
        : args.GetBool("isVip") ? ChatRole.Vip
        : args.GetBool("isSubscriber") ? ChatRole.Subscriber
        : ChatRole.Everyone;
}
