using Cuebot.Core.Events;

namespace Cuebot.Core.Chat;

/// <summary>Roles a chatter can have, normalized across platforms.</summary>
[Flags]
public enum ChatRoles
{
    None = 0,
    Broadcaster = 1,
    Moderator = 2,
    Vip = 4,
    Subscriber = 8,
    Verified = 16,
}

/// <summary>A chat message from any platform, in one shape for the combined chat.</summary>
public sealed record ChatMessage(
    string Platform,
    string Id,
    string User,
    string UserId,
    string Text,
    DateTimeOffset Time)
{
    public ChatRoles Roles { get; init; }

    /// <summary>Platform-provided name colour (#RRGGBB), when there is one.</summary>
    public string? Color { get; init; }

    /// <summary>Paid highlight (Twitch bits, Rumble rant): a display amount such as "100 bits" or "$5.00".</summary>
    public string? Amount { get; init; }
}

/// <summary>Kinds of activity shown in the event feed.</summary>
public enum FeedKind { Follow, Subscription, GiftSubscription, Tip, Raid, Redemption, StreamOnline, StreamOffline, Other }

/// <summary>Something that happened on a platform, for the activity feed ("Alice followed", "Bob tipped $5").</summary>
public sealed record FeedItem(string Platform, FeedKind Kind, string User, string Title, DateTimeOffset Time)
{
    public string? Detail { get; init; }
    public string? Amount { get; init; }
}

/// <summary>
/// Implemented by each platform module: recognizes that module's own <see cref="BotEvent"/>s and translates
/// them into the shared chat and feed shapes. Modules keep their detailed events for triggers.
/// </summary>
public interface IChatEventAdapter
{
    string Platform { get; }
    ChatMessage? ToChatMessage(BotEvent e);
    FeedItem? ToFeedItem(BotEvent e);
}

/// <summary>Implemented by modules that can post chat messages.</summary>
public interface IChatSender
{
    string Platform { get; }

    /// <summary>True when connected (and, where needed, signed in) so a send can succeed.</summary>
    bool CanSend { get; }

    Task SendAsync(string text, CancellationToken ct = default);
}
