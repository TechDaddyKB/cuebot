using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Speech;

/// <summary>
/// A chat message as speech sees it, read from a "chat.message" event (or a platform chat event with the same
/// argument names). Required args: platform, user, message. Optional: userId, userName (login), messageId,
/// isBroadcaster/isModerator/isVip/isSubscriber, emotes (Twitch ranges "id:0-4,6-10/id2:12-16", in code points),
/// emoteNames (comma or space separated), isSharedChat or sourceBroadcasterId/sourceRoomId vs broadcasterId/roomId,
/// isHighlighted.
/// </summary>
public sealed record ChatContext(string Platform, string User, string UserId, string Message)
{
    public string UserName { get; init; } = "";
    public string MessageId { get; init; } = "";
    public bool IsBroadcaster { get; init; }
    public bool IsModerator { get; init; }
    public bool IsVip { get; init; }
    public bool IsSubscriber { get; init; }
    public bool IsHighlighted { get; init; }

    /// <summary>Message comes from another channel through Twitch shared chat.</summary>
    public bool IsSharedChat { get; init; }

    /// <summary>Twitch-style emote ranges, or empty.</summary>
    public string EmoteRanges { get; init; } = "";

    /// <summary>Emote names the platform reported for this message.</summary>
    public IReadOnlyList<string> EmoteNames { get; init; } = [];

    /// <summary>All event arguments, for templates.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } = new Dictionary<string, object?>();

    public bool IsModeratorOrBroadcaster => IsModerator || IsBroadcaster;

    public static ChatContext? FromEvent(BotEvent e)
    {
        var args = e.Args;
        string Get(string key) => VariableResolver.Format(args.GetValueOrDefault(key));
        bool Flag(string key) => args.GetValueOrDefault(key) switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var b) && b,
            _ => false,
        };

        var message = Get("message");
        if (message.Length == 0) message = Get("rawInput");
        var user = Get("user");
        if (user.Length == 0) user = Get("userName");
        if (message.Length == 0 && user.Length == 0) return null;
        var platform = Get("platform");
        if (platform.Length == 0) platform = e.Type.Split('.')[0];

        return new ChatContext(platform, user, Get("userId"), message)
        {
            UserName = Get("userName"),
            MessageId = Get("messageId") is { Length: > 0 } id ? id : Get("msgId"),
            IsBroadcaster = Flag("isBroadcaster"),
            IsModerator = Flag("isModerator"),
            IsVip = Flag("isVip"),
            IsSubscriber = Flag("isSubscriber"),
            IsHighlighted = Flag("isHighlighted") || Get("msgType").Equals("highlighted-message", StringComparison.OrdinalIgnoreCase),
            IsSharedChat = IsShared(Flag("isSharedChat"), Get("sourceBroadcasterId") is { Length: > 0 } s ? s : Get("sourceRoomId"),
                Get("broadcasterId") is { Length: > 0 } b ? b : Get("roomId")),
            EmoteRanges = Get("emotes"),
            EmoteNames = Get("emoteNames").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Args = args,
        };
    }

    /// <summary>Shared chat from another channel: flagged, or the source channel differs from this channel.</summary>
    internal static bool IsShared(bool flagged, string sourceChannel, string channel) =>
        flagged || (sourceChannel.Length > 0 && channel.Length > 0 && !string.Equals(sourceChannel, channel, StringComparison.Ordinal));

    /// <summary>Names that identify this user in ignore lists and commands.</summary>
    public IEnumerable<string> Names()
    {
        if (User.Length > 0) yield return User;
        if (UserName.Length > 0) yield return UserName;
    }
}
