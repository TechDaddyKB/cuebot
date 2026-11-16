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
/// <c>IChatModerator</c>. Version 2 adds <see cref="Capability"/> (what each tool does on this platform) and
/// <see cref="ExecuteAsync"/> (every tool, with the platform's reference in the result); both have defaults built on
/// the first three methods, so older moderators keep working. Callers should go through <see cref="ModerationService"/>.
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

    /// <summary>What <paramref name="action"/> does on this platform right now.</summary>
    ModerationCapability Capability(ModerationAction action) => ModerationCapability.FromLegacy(action, Available);

    /// <summary>
    /// Runs one tool. Returns a failed outcome (not an exception) for refusals the caller should show as text;
    /// exceptions are platform errors, which <see cref="ModerationService"/> logs as failed.
    /// </summary>
    async Task<ModerationOutcome> ExecuteAsync(ModerationRequest request, CancellationToken ct = default)
    {
        var capability = Capability(request.Action);
        if (!capability.CanRun) return ModerationOutcome.Failed(capability.Note);
        switch (request.Action)
        {
            case ModerationAction.Delete:
                await DeleteMessageAsync(request.MessageId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Timeout:
                var applied = capability.Apply(request.Duration ?? TimeSpan.FromMinutes(10));
                await TimeoutAsync(request.UserId, request.UserName, applied, request.Reason, ct).ConfigureAwait(false);
                return ModerationOutcome.Done(applied);
            case ModerationAction.Ban:
                await BanAsync(request.UserId, request.UserName, request.Reason, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            default:
                return ModerationOutcome.Failed($"{Platform} cannot do this from Cuebot.");
        }
    }
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

/// <summary>
/// Implemented by platform modules that can tell whether a user moderates the channel, for messages whose events
/// carry no badges (a Twitch whisper). Optional; Speech uses it for Speaker.bot's "Allow Moderator Whispers".
/// </summary>
public interface IChannelRoles
{
    /// <summary>Same name as the module's <see cref="IChatEventAdapter.Platform"/>.</summary>
    string Platform { get; }

    /// <summary>True when the user moderates the channel (the broadcaster counts). False when unknown or the lookup failed.</summary>
    Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="IsModeratorAsync"/>, but null when Cuebot cannot tell (signed out, missing permission, the lookup
    /// failed), so a check that removes access does not act on an error.
    /// </summary>
    async Task<bool?> CheckModeratorAsync(string userId, CancellationToken ct = default) =>
        await IsModeratorAsync(userId, ct).ConfigureAwait(false) ? true : null;
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
