namespace Cuebot.Rumble.Triggers;

/// <summary>
/// Who may use a chat command, lowest to highest. A chatter has every level up to their highest role. Rumble has
/// no VIP role, so the levels are those of <c>twitch.command</c> without it.
/// </summary>
internal enum ChatPermission
{
    Everyone,
    Subscriber,
    Moderator,
    Broadcaster,
}

/// <summary>Resolves the permission level of a chatter from the flags on a <c>rumble.chatMessage</c> event.</summary>
internal static class ChatPermissions
{
    public static IReadOnlyList<string> Choices { get; } = Enum.GetNames<ChatPermission>();

    public static ChatPermission Parse(string choice) =>
        Enum.TryParse<ChatPermission>(choice, ignoreCase: true, out var level) ? level : ChatPermission.Everyone;

    public static ChatPermission Of(IReadOnlyDictionary<string, object?> args) =>
        args.GetBool("isBroadcaster") ? ChatPermission.Broadcaster
        : args.GetBool("isModerator") ? ChatPermission.Moderator
        : args.GetBool("isSubscriber") ? ChatPermission.Subscriber
        : ChatPermission.Everyone;
}
