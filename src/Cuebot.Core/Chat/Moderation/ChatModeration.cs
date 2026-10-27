namespace Cuebot.Core.Chat.Moderation;

/// <summary>Moderation requests a platform can perform right now.</summary>
[Flags]
public enum ChatModerationActions
{
    None = 0,
    DeleteMessage = 1,
    Timeout = 2,
    Ban = 4,
}

/// <summary>
/// Implemented by platform modules (Twitch, YouTube, Kick...) that can moderate their chat. Register in DI as
/// <c>IChatModerator</c>; the combined chat offers only the actions in <see cref="Available"/>.
/// </summary>
public interface IChatModerator
{
    /// <summary>Same name as the module's <see cref="IChatEventAdapter.Platform"/>.</summary>
    string Platform { get; }

    /// <summary>What can be done now (connected, signed in with the needed scopes, moderator in the channel).</summary>
    ChatModerationActions Available { get; }

    Task DeleteMessageAsync(string messageId, CancellationToken ct = default);

    /// <param name="userId">Platform user id (preferred); <paramref name="userName"/> for platforms that work by name.</param>
    Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default);

    Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default);
}

/// <summary>Kinds of special chat posts.</summary>
public enum ChatAnnouncementKind
{
    /// <summary>A highlighted announcement (Twitch "/announce").</summary>
    Announcement,

    /// <summary>A third-person action line ("/me waves").</summary>
    Action,
}

/// <summary>Implemented by platform modules that can post announcements or /me lines; optional.</summary>
public interface IChatAnnouncer
{
    string Platform { get; }

    bool Supports(ChatAnnouncementKind kind);

    Task AnnounceAsync(string text, ChatAnnouncementKind kind, CancellationToken ct = default);
}

/// <summary>Looks up moderators and announcers by platform for the combined chat.</summary>
public sealed class ChatModerationService(IEnumerable<IChatModerator> moderators, IEnumerable<IChatAnnouncer> announcers)
{
    private readonly IReadOnlyList<IChatModerator> _moderators = moderators.ToArray();
    private readonly IReadOnlyList<IChatAnnouncer> _announcers = announcers.ToArray();

    public IChatModerator? ModeratorFor(string platform) =>
        _moderators.FirstOrDefault(m => string.Equals(m.Platform, platform, StringComparison.OrdinalIgnoreCase));

    public ChatModerationActions AvailableFor(string platform) => ModeratorFor(platform)?.Available ?? ChatModerationActions.None;

    /// <summary>Announcers for one platform, or every platform with <see cref="ChatHub.AllPlatforms"/>, that support the kind.</summary>
    public IReadOnlyList<IChatAnnouncer> AnnouncersFor(string platform, ChatAnnouncementKind kind) =>
        _announcers.Where(a => a.Supports(kind)
                               && (platform == ChatHub.AllPlatforms || string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
}
