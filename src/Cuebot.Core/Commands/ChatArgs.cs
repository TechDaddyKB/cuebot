using System.Globalization;
using Cuebot.Core.Chat;

namespace Cuebot.Core.Commands;

/// <summary>Typed reads of event arguments, tolerant of numbers and bools stored as strings.</summary>
public static class ChatArgs
{
    public static string GetString(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

    public static bool GetBool(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };

    /// <summary>The highest role flagged on a chat event (isBroadcaster, isModerator, isVip, isSubscriber).</summary>
    public static ChatPermission RoleOf(IReadOnlyDictionary<string, object?> args) =>
        args.GetBool("isBroadcaster") ? ChatPermission.Broadcaster
        : args.GetBool("isModerator") ? ChatPermission.Moderator
        : args.GetBool("isVip") ? ChatPermission.Vip
        : args.GetBool("isSubscriber") ? ChatPermission.Subscriber
        : ChatPermission.Everyone;

    /// <summary>All role flags on a chat event.</summary>
    public static ChatRoles RolesOf(IReadOnlyDictionary<string, object?> args)
    {
        var roles = ChatRoles.None;
        if (args.GetBool("isBroadcaster")) roles |= ChatRoles.Broadcaster;
        if (args.GetBool("isModerator")) roles |= ChatRoles.Moderator;
        if (args.GetBool("isVip")) roles |= ChatRoles.Vip;
        if (args.GetBool("isSubscriber")) roles |= ChatRoles.Subscriber;
        return roles;
    }

    /// <summary>Parses "VIP", "moderator"... falling back to Everyone.</summary>
    public static ChatPermission ParseRole(string text) =>
        Enum.TryParse<ChatPermission>(text, ignoreCase: true, out var role) ? role : ChatPermission.Everyone;

    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
