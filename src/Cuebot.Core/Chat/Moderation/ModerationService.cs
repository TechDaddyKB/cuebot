using System.Globalization;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Chat.Moderation;

/// <summary>moderation.json: the mod log's retention, members' rate limits and soft warnings.</summary>
public sealed class ModerationSettings
{
    /// <summary>Days the text of a deleted message is kept (at most 60).</summary>
    public int MessageTextDays { get; set; } = ModLogRetention.Default.MessageTextDays;

    /// <summary>Days timeouts, warnings and deletions keep the person's details (at most 180).</summary>
    public int TimeoutDays { get; set; } = ModLogRetention.Default.TimeoutDays;

    /// <summary>Months a ban keeps the person's details after it was lifted (at most 24; an active ban keeps them).</summary>
    public int BanMonthsAfterEnd { get; set; } = ModLogRetention.Default.BanMonthsAfterEnd;

    /// <summary>Bans one portal member may make per <see cref="RateWindowMinutes"/>.</summary>
    public int MemberBansPerWindow { get; set; } = 10;

    /// <summary>Timeouts one portal member may make per <see cref="RateWindowMinutes"/>.</summary>
    public int MemberTimeoutsPerWindow { get; set; } = 30;

    public int RateWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Warn on YouTube and Kick (which have no warnings): post a chat message that @-mentions the viewer, logged as a
    /// warning.
    /// </summary>
    public bool SoftWarnings { get; set; } = true;

    public ModLogRetention Retention => new ModLogRetention(MessageTextDays, TimeoutDays, BanMonthsAfterEnd).Clamped();

    /// <summary>
    /// Identity links (phase 4) and linked actions (phase 5): linked accounts, notes and <c>!link</c> verification on
    /// user cards, the linked ban and timeout preview. Off by default: the viewer wording, the GDPR position and the
    /// retention periods are waiting for a legal and privacy review (docs/UNIFIED_MOD_CHAT_PLAN.md, decision 12). While
    /// off nothing about links is shown, <c>!link</c> gets no answer, no action goes through a link and nothing is
    /// suggested; links already stored stay (and keep their retention) until the streamer turns it back on or erases them.
    /// </summary>
    public bool IdentityLinksEnabled { get; set; }

    /// <summary>
    /// Automatic mirroring (decision 6): a ban seen on any platform, including one made on the platform's website, is
    /// applied to the person's verified and confirmed links without a preview. Off by default; needs
    /// <see cref="IdentityLinksEnabled"/>. Never goes through suggested links.
    /// </summary>
    public bool AutomaticMirroring { get; set; }

    /// <summary>Linked bans (one incident touching more than one account) one portal member may start per hour.</summary>
    public int MemberLinkedBansPerHour { get; set; } = 5;

    /// <summary>Days a suggested link nobody confirmed is kept (at most 60).</summary>
    public int UnconfirmedLinkDays { get; set; } = IdentityRetention.Default.UnconfirmedLinkDays;

    /// <summary>Months a note is kept after its last edit (at most 24).</summary>
    public int NoteMonthsAfterEdit { get; set; } = IdentityRetention.Default.NoteMonthsAfterEdit;

    public IdentityRetention LinkRetention =>
        new IdentityRetention(UnconfirmedLinkDays, BanMonthsAfterEnd, NoteMonthsAfterEdit).Clamped();
}

/// <summary>
/// How long identity data is kept (section 9.2, decision 8; pending the review of decision 12). Links stand while
/// they are active; rejected suggestions and the link rows of the mod log go <see cref="LinkMonthsAfterEnd"/> months
/// after the link ended. YouTube accounts follow the 30-day refresh rule on top of this.
/// </summary>
public sealed record IdentityRetention(int UnconfirmedLinkDays = 60, int LinkMonthsAfterEnd = 24, int NoteMonthsAfterEdit = 24)
{
    public static IdentityRetention Default { get; } = new();

    /// <summary>YouTube's developer policies: API data is refreshed or deleted within 30 days.</summary>
    public const int YouTubeRefreshDays = 30;

    /// <summary>Refreshing starts this many days in, so a failed lookup is retried before the 30 days are up.</summary>
    public const int YouTubeRefreshStartDays = 25;

    public IdentityRetention Clamped() => new(
        Math.Clamp(UnconfirmedLinkDays, 1, Default.UnconfirmedLinkDays),
        Math.Clamp(LinkMonthsAfterEnd, 1, Default.LinkMonthsAfterEnd),
        Math.Clamp(NoteMonthsAfterEdit, 1, Default.NoteMonthsAfterEdit));
}

/// <summary>One moderation decision, from any client (desktop Chat page, mod portal, action steps).</summary>
public sealed record ModerationCommand(string Platform, ModerationAction Action)
{
    public string TargetUserId { get; init; } = "";
    public string TargetName { get; init; } = "";

    /// <summary>The login name, when it differs from the display name; platforms that act by name get it.</summary>
    public string TargetLogin { get; init; } = "";

    /// <summary>Roles the chat showed for the target (broadcaster, moderator, VIP are protected).</summary>
    public ChatRoles TargetRoles { get; init; }

    public string MessageId { get; init; } = "";

    /// <summary>The message being deleted, kept in the log so a reviewer can judge the action.</summary>
    public string MessageText { get; init; } = "";

    /// <summary>The chat the message came from (the YouTube live chat id).</summary>
    public string? ChannelId { get; init; }

    public TimeSpan? Duration { get; init; }
    public string? Reason { get; init; }

    /// <summary>Chat modes and pins: on or off.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Slow mode seconds or followers-only minutes; null for the platform's default.</summary>
    public int? ModeValue { get; init; }

    /// <summary>Groups several rows into one decision; a new incident when null.</summary>
    public string? IncidentId { get; init; }

    public string? ViaLinkId { get; init; }

    /// <summary>The queue item acted on: an unban request id, a blocked term id.</summary>
    public string ItemId { get; init; } = "";

    /// <summary>The blocked term to add, or the text of the one removed (for the log).</summary>
    public string Term { get; init; } = "";

    /// <summary>For an unban: the mod log row it lifts (undoing an incident); the newest ban or timeout in force when null.</summary>
    public string? UndoesRowId { get; init; }

    /// <summary>Allow or deny a message the platform held.</summary>
    public static ModerationCommand ForHeld(HeldChatMessage held, bool allow) =>
        new(held.Platform, allow ? ModerationAction.ApproveHeld : ModerationAction.DenyHeld)
        {
            TargetUserId = held.UserId,
            TargetName = held.UserName,
            TargetLogin = held.UserLogin,
            MessageId = held.Id,
            MessageText = held.Text,
        };

    /// <summary>Approve (which lifts the ban) or deny an unban request, with an optional answer to the viewer.</summary>
    public static ModerationCommand ForUnbanRequest(UnbanRequestInfo request, bool approve, string? answer = null) =>
        new(request.Platform, approve ? ModerationAction.ApproveUnbanRequest : ModerationAction.DenyUnbanRequest)
        {
            TargetUserId = request.UserId,
            TargetName = request.UserName,
            TargetLogin = request.UserLogin,
            ItemId = request.Id,
            MessageText = request.Text,
            Reason = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim(),
        };

    /// <summary>Add a blocked term.</summary>
    public static ModerationCommand BlockTerm(string platform, string term) =>
        new(platform, ModerationAction.AddBlockedTerm) { Term = term.Trim() };

    /// <summary>Remove a blocked term.</summary>
    public static ModerationCommand UnblockTerm(BlockedTermInfo term) =>
        new(term.Platform, ModerationAction.RemoveBlockedTerm) { ItemId = term.Id, Term = term.Text };

    /// <summary>A command about a chat message and its author.</summary>
    public static ModerationCommand For(ChatMessage message, ModerationAction action) => new(message.Platform, action)
    {
        TargetUserId = message.UserId,
        TargetName = message.User,
        TargetRoles = message.Roles,
        MessageId = action == ModerationAction.Delete || action == ModerationAction.Pin ? message.Id : "",
        MessageText = action == ModerationAction.Delete ? message.Text : "",
        ChannelId = message.ChannelId,
    };
}

/// <summary>What a moderation command did, in words for the person who clicked.</summary>
public sealed record ModerationResult(ModResult Result, string Message)
{
    public bool Ok => Result == ModResult.Ok;

    /// <summary>The log row, when one was written.</summary>
    public ModLogEntry? Entry { get; init; }

    public TimeSpan? AppliedDuration { get; init; }

    /// <summary>Set when the tool is done on the platform's website: the page to open.</summary>
    public string? WebsiteUrl { get; init; }
}

/// <summary>
/// The portal's per-member permissions (phase 2): null allows the command, otherwise the reason it is refused. The
/// streamer is never asked about.
/// </summary>
public interface IModerationPermissions
{
    string? Refuse(ModActor actor, ModerationCommand command);

    /// <summary>
    /// Identity links and notes (phase 4): null allows <paramref name="operation"/>, otherwise the reason it is refused.
    /// The default lets members view, propose and write notes, and keeps confirming, rejecting, unlinking and deleting
    /// others' notes for the streamer until the portal maps them to its roles ("Confirm links", "Notes: delete").
    /// </summary>
    string? RefuseLink(ModActor actor, Users.LinkOperation operation) => LinkPermissionDefaults.Refuse(actor, operation);
}

/// <summary>What members may do with identity links when no portal decides (<see cref="IModerationPermissions.RefuseLink"/>).</summary>
public static class LinkPermissionDefaults
{
    public static string? Refuse(ModActor actor, Users.LinkOperation operation)
    {
        if (actor.Kind == ModActorKind.Streamer) return null;
        if (actor.Kind != ModActorKind.Member) return "Only the streamer and mods can do this.";
        return operation switch
        {
            Users.LinkOperation.Confirm => "Your role does not include confirming links.",
            Users.LinkOperation.Reject => "Your role does not include rejecting links.",
            Users.LinkOperation.Unlink => "Your role does not include unlinking accounts.",
            Users.LinkOperation.DeleteNote => "Your role does not include deleting other people's notes.",
            _ => null,
        };
    }
}

/// <summary>
/// The single way into chat moderation for the desktop Chat page, the mod portal and action steps
/// (docs/UNIFIED_MOD_CHAT_PLAN.md, section 5.4): asks the permission hook, refuses protected targets (the broadcaster,
/// platform moderators and VIPs, portal members, bot accounts) unless the streamer acts, applies members' rate limits,
/// prefixes members' reasons with their name on Twitch and Kick, checks the YouTube quota, calls the platform, and
/// writes the mod log. It also records deletions, bans and timeouts made outside Cuebot (on a website) as
/// <see cref="ModActorKind.External"/>.
/// </summary>
public sealed partial class ModerationService : IDisposable
{
    /// <summary>A platform event this long after Cuebot's own action on the same target is that action echoed back.</summary>
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMinutes(2);

    private static readonly string[] ReasonPrefixPlatforms = ["Twitch", "Kick"];

    private readonly ChatModerationService _moderators;
    private readonly ChatHub? _hub;
    private readonly IReadOnlyList<IProtectedAccounts> _protected;
    private readonly IReadOnlyList<IPlatformQuota> _quotas;
    private readonly IReadOnlyList<IChannelRoles> _roles;
    private readonly Func<ModerationSettings> _settings;
    private readonly AppLog? _appLog;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rate = new(StringComparer.Ordinal);

    // Echo matching: Cuebot's commands still waiting for the platform, and own rows that already absorbed an echo.
    private readonly object _echoGate = new();
    private readonly List<PendingEcho> _pending = [];
    private readonly HashSet<string> _absorbed = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IModerationQueues> _queues;

    public ModerationService(ChatModerationService moderators, ModLog? log = null, ChatHub? hub = null,
        IEnumerable<IProtectedAccounts>? protectedAccounts = null, IEnumerable<IPlatformQuota>? quotas = null,
        IEnumerable<IChannelRoles>? roles = null, Func<ModerationSettings>? settings = null, AppLog? appLog = null, TimeProvider? time = null)
    {
        _moderators = moderators;
        Log = log;
        _hub = hub;
        _protected = protectedAccounts?.ToArray() ?? [];
        _quotas = quotas?.ToArray() ?? [];
        _roles = roles?.ToArray() ?? [];
        var defaults = new ModerationSettings();
        _settings = settings ?? (() => defaults);
        _appLog = appLog;
        _time = time ?? TimeProvider.System;
        if (_hub is not null && Log is not null)
        {
            _hub.ModerationSeen += RecordExternal;
            _hub.ModerationActionSeen += RecordExternal;
        }
        _queues = moderators.Moderators.OfType<IModerationQueues>().ToArray();
        foreach (var queues in _queues) queues.Changed += OnQueuesChanged;
    }

    /// <summary>Raised (on a background thread) when any platform's moderation queues change.</summary>
    public event Action<ModerationQueueChange>? QueuesChanged;

    /// <summary>The platforms that have moderation queues (held messages, Shield Mode...).</summary>
    public IReadOnlyList<IModerationQueues> Queues => _queues;

    /// <summary>The moderation queues of <paramref name="platform"/>; null when it has none.</summary>
    public IModerationQueues? QueuesFor(string platform) =>
        _queues.FirstOrDefault(q => string.Equals(q.Platform, platform, StringComparison.OrdinalIgnoreCase));

    /// <summary>Held messages still waiting for a decision, on every platform, oldest first.</summary>
    public IReadOnlyList<HeldChatMessage> PendingHeldMessages() =>
        _queues.SelectMany(SafeHeld).Where(h => h.IsPending).OrderBy(h => h.HeldAt).ToArray();

    private IEnumerable<HeldChatMessage> SafeHeld(IModerationQueues queues)
    {
        try
        {
            return queues.HeldMessages;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Moderation", $"{queues.Platform} could not list held messages: {ex.Message}");
            return [];
        }
    }

    private void OnQueuesChanged(ModerationQueueChange change) => QueuesChanged?.Invoke(change);

    /// <summary>The mod log; null when this Cuebot keeps none (tests, a page without one).</summary>
    public ModLog? Log { get; }

    /// <summary>Set by the mod portal; asked before every command a member sends.</summary>
    public IModerationPermissions? Permissions { get; set; }

    public ChatModerationService Moderators => _moderators;

    /// <summary>What <paramref name="action"/> does on <paramref name="platform"/> now.</summary>
    public ModerationCapability Capability(string platform, ModerationAction action)
    {
        if (_moderators.ModeratorFor(platform) is not { } moderator)
            return ModerationCapability.NotAvailable(action, $"Cuebot cannot moderate {platform} chat.");
        ModerationCapability capability;
        try
        {
            capability = moderator.Capability(action);
        }
        catch (Exception ex)
        {
            _appLog?.Warn("Moderation", $"{platform} could not describe {action}: {ex.Message}");
            return ModerationCapability.NotAvailable(action, $"{platform} is not ready.");
        }
        // Moderators written before the queue tools existed answer them with their default; they cannot do them.
        if (ModerationCapability.IsQueueTool(action) && moderator is not IModerationQueues)
            return ModerationCapability.NotAvailable(action, $"Cuebot cannot do this on {platform}.");
        if (action == ModerationAction.Warn && capability.State == ModerationToolState.Closest && !_settings().SoftWarnings)
            return ModerationCapability.NotAvailable(action, "Soft warnings (a chat message to the viewer) are turned off.");
        return capability;
    }

    /// <summary>Every tool on <paramref name="platform"/>, in toolbar order.</summary>
    public IReadOnlyList<ModerationCapability> Capabilities(string platform) =>
        ModerationCapability.AllActions.Select(a => Capability(platform, a)).ToArray();

    /// <summary>The quota <paramref name="platform"/> charges, if any.</summary>
    public IPlatformQuota? QuotaFor(string platform) =>
        _quotas.FirstOrDefault(q => string.Equals(q.Platform, platform, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The duration a timeout will really get, and a note when it differs from the request ("Kick: 10 min, rounded up
    /// from 9 min 30 s").
    /// </summary>
    public (TimeSpan Applied, string Note) PreviewDuration(string platform, TimeSpan requested)
    {
        var capability = Capability(platform, ModerationAction.Timeout);
        if (!capability.CanRun) return (requested, "");
        var applied = capability.Apply(requested);
        if (applied == requested) return (applied, "");
        var rounded = (capability with { MinDuration = null, MaxDuration = null }).Apply(requested);
        var how = applied < requested ? "the most it allows, not" : rounded == applied ? "rounded up from" : "the least it allows, not";
        return (applied, $"{platform}: {FormatDuration(applied)}, {how} {FormatDuration(requested)}");
    }

    /// <summary>
    /// Runs one command as <paramref name="actor"/>. Never throws for platform errors; the result says what happened.
    /// A ban that worked is mirrored to the person's verified and confirmed links when the streamer turned automatic
    /// mirroring on.
    /// </summary>
    public Task<ModerationResult> ExecuteAsync(ModerationCommand command, ModActor actor, CancellationToken ct = default) =>
        ExecuteCoreAsync(command, actor, mirror: true, ct);

    private async Task<ModerationResult> ExecuteCoreAsync(ModerationCommand command, ModActor actor, bool mirror, CancellationToken ct)
    {
        var platform = command.Platform;
        var capability = Capability(platform, command.Action);
        if (capability.State == ModerationToolState.OnWebsite)
            return new ModerationResult(ModResult.Skipped, $"{Name(command.Action)} is done on {platform}'s website. {capability.Note}".Trim())
            {
                WebsiteUrl = capability.WebsiteUrl,
            };
        if (!capability.CanRun) return new ModerationResult(ModResult.Skipped, capability.Note);

        var moderator = _moderators.ModeratorFor(platform)!;
        var reason = command.Reason?.Trim() ?? "";
        if (actor.Kind != ModActorKind.Streamer)
        {
            if (Permissions?.Refuse(actor, command) is { } refused) return Skip(command, actor, reason, capability, refused);
            if (await WhyProtectedAsync(command, ct).ConfigureAwait(false) is { } protectedWhy)
                return Skip(command, actor, reason, capability, protectedWhy);
            if (actor.Kind == ModActorKind.Member && RateLimited(command.Action, actor) is { } limited)
                return Skip(command, actor, reason, capability, limited);
        }

        reason = ReasonFor(platform, actor, reason, capability);
        if (capability.QuotaCost > 0 && QuotaFor(platform)?.Refuse(capability.QuotaCost) is { } noQuota)
            return Skip(command, actor, reason, capability, noQuota);

        ModLogEntry? undoes = null;
        if (command.Action is ModerationAction.Unban && command.UndoesRowId is { } undoesId)
            undoes = Log?.Get(undoesId);
        else if (command.Action is ModerationAction.Unban or ModerationAction.ApproveUnbanRequest && command.TargetUserId.Length > 0)
            undoes = Log?.FindInForce(platform, command.TargetUserId);

        var requested = command.Action == ModerationAction.Timeout ? command.Duration ?? TimeSpan.FromMinutes(10) : (TimeSpan?)null;
        var request = new ModerationRequest(command.Action)
        {
            UserId = command.TargetUserId,
            UserName = command.TargetLogin.Length > 0 ? command.TargetLogin : command.TargetName,
            MessageId = command.MessageId,
            ChannelId = command.ChannelId ?? undoes?.ChannelId,
            Duration = requested,
            Reason = reason.Length > 0 ? reason : null,
            Enabled = command.Enabled,
            ModeValue = command.ModeValue,
            PlatformRef = undoes?.PlatformRef,
            ItemId = command.ItemId,
            Term = command.Term.Trim(),
        };

        // The platform may report the action (channel.moderate) before its answer arrives: remember it as pending.
        var pending = new PendingEcho(platform, ModerationCapability.LogName(command.Action), command.TargetUserId, command.MessageId,
            RowReason(command, reason), _time.GetUtcNow());
        lock (_echoGate) _pending.Add(pending);
        ModerationOutcome outcome;
        try
        {
            outcome = await moderator.ExecuteAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            outcome = ModerationOutcome.Failed(ex.Message);
        }
        finally
        {
            lock (_echoGate) _pending.Remove(pending);
        }
        if (!outcome.Ok) _appLog?.Warn("Moderation", $"{Name(command.Action)} on {platform} failed: {outcome.Error}");

        var applied = outcome.AppliedDuration ?? (requested is { } r ? capability.Apply(r) : null);
        var entry = Log?.Append(Row(command, actor, reason, outcome.Ok ? ModResult.Ok : ModResult.Failed, outcome.Error ?? "") with
        {
            DurationRequestedSeconds = Seconds(requested),
            DurationAppliedSeconds = Seconds(outcome.Ok ? applied : null),
            PlatformRef = outcome.PlatformRef,
            Undoes = outcome.Ok ? undoes?.Id : null,
        });
        if (outcome.Ok && undoes is not null && entry is not null) Log!.MarkUndone(undoes.Id, entry.Id);
        // The echo already came: this row must not absorb a later, real outside action.
        if (pending.Consumed && entry is not null) Absorb(entry.Id);
        if (mirror && outcome.Ok && entry is not null && command.Action == ModerationAction.Ban && command.ViaLinkId is null)
            await MirrorAsync(entry, ct).ConfigureAwait(false);

        return new ModerationResult(outcome.Ok ? ModResult.Ok : ModResult.Failed,
            outcome.Ok ? Done(command, applied, requested) : outcome.Error ?? "The platform refused it.")
        {
            Entry = entry,
            AppliedDuration = applied,
        };
    }

    /// <summary>Blanks personal details in the mod log past the retention periods (at start, from <c>DataRetention</c>).</summary>
    public int ApplyRetention(DateTimeOffset now) => Log?.ApplyRetention(_settings().Retention, now) ?? 0;

    public void Dispose()
    {
        if (_links is not null) _links.Changed -= OnLinksChanged;
        if (_hub is not null)
        {
            _hub.ModerationSeen -= RecordExternal;
            _hub.ModerationActionSeen -= RecordExternal;
        }
        foreach (var queues in _queues) queues.Changed -= OnQueuesChanged;
    }

    // ---------------------------------------------------------------- safety rules

    private async Task<string?> WhyProtectedAsync(ModerationCommand command, CancellationToken ct)
    {
        if (!ModerationCapability.TargetsUser(command.Action) && command.Action != ModerationAction.Delete) return null;
        if (command.Action is ModerationAction.Unban or ModerationAction.ClearSuspicious) return null;
        var name = command.TargetName.Length > 0 ? command.TargetName : "This person";
        if (command.TargetRoles.HasFlag(ChatRoles.Broadcaster)) return $"{name} is the broadcaster. Only the streamer can do this.";
        if (command.TargetRoles.HasFlag(ChatRoles.Moderator)) return $"{name} is a moderator. Only the streamer can do this.";
        if (command.TargetRoles.HasFlag(ChatRoles.Vip)) return $"{name} is a VIP. Only the streamer can do this.";
        foreach (var accounts in _protected)
            if (accounts.WhyProtected(command.Platform, command.TargetUserId, command.TargetName) is { } why) return why;
        if (command.TargetUserId.Length > 0
            && _roles.FirstOrDefault(r => string.Equals(r.Platform, command.Platform, StringComparison.OrdinalIgnoreCase)) is { } roles)
        {
            try
            {
                if (await roles.CheckModeratorAsync(command.TargetUserId, ct).ConfigureAwait(false) == true)
                    return $"{name} is a moderator. Only the streamer can do this.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _appLog?.Warn("Moderation", $"Could not check whether {name} moderates {command.Platform}: {ex.Message}");
            }
        }
        return null;
    }

    private string? RateLimited(ModerationAction action, ModActor actor)
    {
        var s = _settings();
        var limit = action switch
        {
            ModerationAction.Ban => s.MemberBansPerWindow,
            ModerationAction.Timeout => s.MemberTimeoutsPerWindow,
            _ => 0,
        };
        if (limit <= 0) return null;
        var window = TimeSpan.FromMinutes(Math.Max(1, s.RateWindowMinutes));
        var now = _time.GetUtcNow();
        var key = (actor.MemberId ?? actor.Name) + "|" + action;
        lock (_rate)
        {
            if (!_rate.TryGetValue(key, out var times)) _rate[key] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && times.Peek() <= now - window) times.Dequeue();
            if (times.Count >= limit)
                return $"{actor.Name} has made {limit} {(action == ModerationAction.Ban ? "bans" : "timeouts")} in the last " +
                       $"{FormatDuration(window)}. Wait a little, or ask the streamer.";
            times.Enqueue(now);
            return null;
        }
    }

    /// <summary>"[mod: Sam] spam" for members on Twitch and Kick (their logs show the streamer), trimmed to the platform's limit.</summary>
    private static string ReasonFor(string platform, ModActor actor, string reason, ModerationCapability capability)
    {
        if (actor.Kind == ModActorKind.Member && ReasonPrefixPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase)
            && HasReason(capability.Action))
            reason = $"[mod: {actor.Name}] {reason}".TrimEnd();
        if (capability.MaxReasonLength is { } max && reason.Length > max) reason = reason[..max];
        return reason;

        static bool HasReason(ModerationAction a) => a is ModerationAction.Ban or ModerationAction.Timeout or ModerationAction.Warn;
    }

    private ModerationResult Skip(ModerationCommand command, ModActor actor, string reason, ModerationCapability capability, string why)
    {
        var entry = Log?.Append(Row(command, actor, reason, ModResult.Skipped, why) with
        {
            DurationRequestedSeconds = Seconds(command.Action == ModerationAction.Timeout ? command.Duration : null),
        });
        return new ModerationResult(ModResult.Skipped, why) { Entry = entry, WebsiteUrl = capability.WebsiteUrl };
    }

    private static ModLogEntry Row(ModerationCommand command, ModActor actor, string reason, ModResult result, string error) => new()
    {
        IncidentId = command.IncidentId ?? "",
        ActorKind = actor.Kind,
        ActorMemberId = actor.MemberId,
        ActorName = actor.Name,
        ActorDeviceId = actor.DeviceId,
        Platform = command.Platform,
        ChannelId = command.ChannelId,
        Action = ModerationCapability.LogName(command.Action),
        TargetUserId = command.TargetUserId,
        TargetName = command.TargetName,
        MessageId = command.MessageId,
        MessageText = command.MessageText,
        Reason = RowReason(command, reason),
        ViaLinkId = command.ViaLinkId,
        Result = result,
        Error = error,
    };

    /// <summary>The log row's reason: what changed for chat modes and Shield Mode, the term for blocked terms.</summary>
    private static string RowReason(ModerationCommand command, string reason) => command.Action switch
    {
        ModerationAction.SlowMode or ModerationAction.FollowersOnly or ModerationAction.SubscribersOnly or ModerationAction.EmoteOnly
            or ModerationAction.ShieldMode =>
            $"{ModerationCapability.Label(command.Action)} {(command.Enabled ? "on" : "off")}"
            + (command.ModeValue is { } v && command.Enabled && command.Action != ModerationAction.ShieldMode
                ? $" ({v.ToString(CultureInfo.InvariantCulture)})" : "")
            + (reason.Length > 0 ? ": " + reason : ""),
        ModerationAction.AddBlockedTerm or ModerationAction.RemoveBlockedTerm => command.Term.Trim(),
        _ => reason,
    };

    private static string Done(ModerationCommand c, TimeSpan? applied, TimeSpan? requested)
    {
        var who = c.TargetName.Length > 0 ? c.TargetName : "the viewer";
        return c.Action switch
        {
            ModerationAction.Delete => $"Deleted {who}'s message",
            ModerationAction.Timeout => $"Timed out {who} for {FormatDuration(applied ?? TimeSpan.Zero)}"
                                        + (applied != requested && requested is { } r ? $" (asked for {FormatDuration(r)})" : ""),
            ModerationAction.Ban => $"Banned {who}",
            ModerationAction.Unban => $"Lifted the ban or timeout of {who}",
            ModerationAction.Warn => $"Warned {who}",
            ModerationAction.Clear => $"Cleared {c.Platform} chat",
            ModerationAction.Pin => "Pinned the message",
            ModerationAction.Unpin => "Unpinned the message",
            ModerationAction.ShieldMode => $"Turned Shield Mode {(c.Enabled ? "on" : "off")} on {c.Platform}",
            ModerationAction.ApproveHeld => $"Allowed {who}'s held message",
            ModerationAction.DenyHeld => $"Denied {who}'s held message",
            ModerationAction.MonitorUser => $"{who} is now monitored",
            ModerationAction.RestrictUser => $"{who} is now restricted",
            ModerationAction.ClearSuspicious => $"{who} is no longer marked suspicious",
            ModerationAction.ApproveUnbanRequest => $"Approved {who}'s unban request",
            ModerationAction.DenyUnbanRequest => $"Denied {who}'s unban request",
            ModerationAction.AddBlockedTerm => $"Blocked \"{c.Term.Trim()}\" on {c.Platform}",
            ModerationAction.RemoveBlockedTerm => c.Term.Length > 0 ? $"Unblocked \"{c.Term.Trim()}\" on {c.Platform}" : $"Unblocked the term on {c.Platform}",
            _ => $"{ModerationCapability.Label(c.Action)} {(c.Enabled ? "on" : "off")} on {c.Platform}",
        };
    }

    private static string Name(ModerationAction action) => ModerationCapability.Label(action);

    private static int? Seconds(TimeSpan? t) => t is { } d ? (int)Math.Ceiling(d.TotalSeconds) : null;

    /// <summary>"30 s", "9 min 30 s", "2 h", "7 days".</summary>
    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays >= 1 && t.TotalDays == Math.Floor(t.TotalDays)) return t.TotalDays == 1 ? "1 day" : $"{t.TotalDays:0} days";
        if (t.TotalHours >= 1 && t.TotalHours == Math.Floor(t.TotalHours)) return $"{t.TotalHours:0} h";
        if (t.TotalMinutes >= 1)
        {
            var minutes = (int)t.TotalMinutes;
            return t.Seconds > 0 ? $"{minutes} min {t.Seconds} s" : $"{minutes} min";
        }
        return $"{Math.Ceiling(t.TotalSeconds):0} s";
    }

    // ---------------------------------------------------------------- actions made outside Cuebot

    /// <summary>A command sent to a platform whose answer has not come yet; an echo that arrives meanwhile consumes it.</summary>
    private sealed record PendingEcho(string Platform, string Action, string TargetUserId, string MessageId, string Reason, DateTimeOffset At)
    {
        public bool Consumed { get; set; }
    }

    /// <summary>The log names of Cuebot's own actions a platform event of <paramref name="action"/> can be the echo of.</summary>
    private static string[] OwnActionsFor(string action) => action switch
    {
        "unban" => ["unban", "unban-request-approve"],
        _ => [action],
    };

    /// <summary>
    /// True when a platform event is the echo of Cuebot's own action: same action, same person or message, and for chat
    /// modes, Shield Mode and blocked terms the same switch or term.
    /// </summary>
    private static bool SameAction(string ownAction, string ownTarget, string ownMessage, string ownReason, ExternalModerationNotice n)
    {
        if (!OwnActionsFor(n.Action).Contains(ownAction, StringComparer.Ordinal)) return false;
        if (n.MessageId.Length > 0 && ownMessage.Length > 0) return n.MessageId == ownMessage;
        if (n.TargetUserId.Length > 0 || ownTarget.Length > 0) return n.TargetUserId == ownTarget;
        return ownAction switch
        {
            "mode" or "shield" => string.Equals(SwitchName(ownReason), SwitchName(n.Reason), StringComparison.OrdinalIgnoreCase),
            "term-add" or "term-remove" => string.Equals(ownReason.Trim(), n.Reason.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    /// <summary>"Slow mode" from "Slow mode on (30): spam".</summary>
    private static string SwitchName(string reason)
    {
        foreach (var marker in new[] { " on", " off" })
        {
            var at = reason.IndexOf(marker, StringComparison.Ordinal);
            if (at > 0) return reason[..at];
        }
        return reason;
    }

    private void Absorb(string rowId)
    {
        lock (_echoGate)
        {
            if (_absorbed.Count > 2000) _absorbed.Clear();
            _absorbed.Add(rowId);
        }
    }

    /// <summary>True when the event is Cuebot's own action coming back (pending or logged in the last two minutes).</summary>
    private bool IsEcho(ExternalModerationNotice n, ModLog log)
    {
        var since = _time.GetUtcNow() - EchoWindow;
        lock (_echoGate)
        {
            foreach (var p in _pending)
                if (!p.Consumed && string.Equals(p.Platform, n.Platform, StringComparison.OrdinalIgnoreCase)
                    && SameAction(p.Action, p.TargetUserId, p.MessageId, p.Reason, n))
                {
                    p.Consumed = true;
                    return true;
                }
        }
        foreach (var action in OwnActionsFor(n.Action))
            foreach (var row in log.OwnSince(n.Platform, action, since))
            {
                lock (_echoGate)
                    if (!_absorbed.Contains(row.Id) && SameAction(row.Action, row.TargetUserId, row.MessageId, row.Reason, n))
                    {
                        _absorbed.Add(row.Id);
                        return true;
                    }
            }
        return false;
    }

    /// <summary>A deletion, ban or timeout seen in the chat (platforms without a full moderation feed).</summary>
    private void RecordExternal(ChatModerationNotice notice)
    {
        if (notice.LoggedElsewhere) return;
        var text = notice.Kind == ChatModerationKind.MessageDeleted && notice.MessageId.Length > 0
            ? _hub?.RecentMessages().LastOrDefault(m => m.Platform == notice.Platform && m.Id == notice.MessageId)?.Text ?? ""
            : "";
        RecordExternal(new ExternalModerationNotice(notice.Platform, notice.Kind switch
        {
            ChatModerationKind.MessageDeleted => "delete",
            ChatModerationKind.UserBanned => "ban",
            _ => "timeout",
        }, notice.Time)
        {
            Moderator = notice.Moderator,
            TargetUserId = notice.UserId,
            TargetName = notice.User,
            MessageId = notice.MessageId,
            MessageText = text,
            DurationSeconds = notice.DurationSeconds,
            Reason = notice.Reason,
        });
    }

    /// <summary>
    /// Records an action a platform reported (made on its website, by another tool, or by Cuebot), unless it is
    /// Cuebot's own action echoed back. Lifting a ban or approving an unban request marks the ban in the log as lifted.
    /// </summary>
    public void RecordExternal(ExternalModerationNotice notice)
    {
        if (Log is not { } log) return;
        try
        {
            if (IsEcho(notice, log)) return;
            var text = notice.MessageText;
            if (text.Length == 0 && notice.Action == "delete" && notice.MessageId.Length > 0)
                text = _hub?.RecentMessages().LastOrDefault(m => m.Platform == notice.Platform && m.Id == notice.MessageId)?.Text ?? "";
            var undoes = notice.Action is "unban" or "unban-request-approve" && notice.TargetUserId.Length > 0
                ? log.FindInForce(notice.Platform, notice.TargetUserId)
                : null;
            var entry = log.Append(new ModLogEntry
            {
                ActorKind = ModActorKind.External,
                ActorName = notice.Moderator.Length > 0 ? notice.Moderator : $"Someone on {notice.Platform}",
                Platform = notice.Platform,
                ChannelId = notice.ChannelId,
                Action = notice.Action,
                TargetUserId = notice.TargetUserId,
                TargetName = notice.TargetName,
                MessageId = notice.MessageId,
                MessageText = text,
                DurationRequestedSeconds = notice.DurationSeconds,
                DurationAppliedSeconds = notice.DurationSeconds,
                Reason = notice.Reason,
                Result = ModResult.Ok,
                Undoes = undoes?.Id,
            });
            if (undoes is not null) log.MarkUndone(undoes.Id, entry.Id);
            // A ban made on the website (or by another tool) reaches the person's other accounts when mirroring is on.
            if (entry.Action == "ban") LastMirror = Task.Run(() => MirrorAsync(entry, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Moderation", $"Could not record a {notice.Platform} moderation event in the mod log: {ex.Message}");
        }
    }
}
