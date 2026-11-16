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
}

/// <summary>
/// The single way into chat moderation for the desktop Chat page, the mod portal and action steps
/// (docs/UNIFIED_MOD_CHAT_PLAN.md, section 5.4): asks the permission hook, refuses protected targets (the broadcaster,
/// platform moderators and VIPs, portal members, bot accounts) unless the streamer acts, applies members' rate limits,
/// prefixes members' reasons with their name on Twitch and Kick, checks the YouTube quota, calls the platform, and
/// writes the mod log. It also records deletions, bans and timeouts made outside Cuebot (on a website) as
/// <see cref="ModActorKind.External"/>.
/// </summary>
public sealed class ModerationService : IDisposable
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
        if (_hub is not null && Log is not null) _hub.ModerationSeen += RecordExternal;
    }

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

    /// <summary>Runs one command as <paramref name="actor"/>. Never throws for platform errors; the result says what happened.</summary>
    public async Task<ModerationResult> ExecuteAsync(ModerationCommand command, ModActor actor, CancellationToken ct = default)
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
        if (command.Action == ModerationAction.Unban && command.TargetUserId.Length > 0)
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
        };

        ModerationOutcome outcome;
        try
        {
            outcome = await moderator.ExecuteAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            outcome = ModerationOutcome.Failed(ex.Message);
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
        if (_hub is not null) _hub.ModerationSeen -= RecordExternal;
    }

    // ---------------------------------------------------------------- safety rules

    private async Task<string?> WhyProtectedAsync(ModerationCommand command, CancellationToken ct)
    {
        if (!ModerationCapability.TargetsUser(command.Action) && command.Action != ModerationAction.Delete) return null;
        if (command.Action == ModerationAction.Unban) return null;
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
        Reason = command.Action switch
        {
            ModerationAction.SlowMode or ModerationAction.FollowersOnly or ModerationAction.SubscribersOnly or ModerationAction.EmoteOnly =>
                $"{ModerationCapability.Label(command.Action)} {(command.Enabled ? "on" : "off")}"
                + (command.ModeValue is { } v && command.Enabled ? $" ({v.ToString(CultureInfo.InvariantCulture)})" : "")
                + (reason.Length > 0 ? ": " + reason : ""),
            _ => reason,
        },
        ViaLinkId = command.ViaLinkId,
        Result = result,
        Error = error,
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

    private void RecordExternal(ChatModerationNotice notice)
    {
        if (Log is not { } log) return;
        try
        {
            var action = notice.Kind switch
            {
                ChatModerationKind.MessageDeleted => "delete",
                ChatModerationKind.UserBanned => "ban",
                _ => "timeout",
            };
            if (log.FindOwnSince(notice.Platform, action, notice.UserId, notice.MessageId, _time.GetUtcNow() - EchoWindow) is not null) return;
            var text = notice.MessageId.Length > 0
                ? _hub?.RecentMessages().LastOrDefault(m => m.Platform == notice.Platform && m.Id == notice.MessageId)?.Text ?? ""
                : "";
            log.Append(new ModLogEntry
            {
                ActorKind = ModActorKind.External,
                ActorName = notice.Moderator.Length > 0 ? notice.Moderator : $"Someone on {notice.Platform}",
                Platform = notice.Platform,
                Action = action,
                TargetUserId = notice.UserId,
                TargetName = notice.User,
                MessageId = notice.MessageId,
                MessageText = text,
                DurationRequestedSeconds = notice.DurationSeconds,
                DurationAppliedSeconds = notice.DurationSeconds,
                Reason = notice.Reason,
                Result = ModResult.Ok,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Moderation", $"Could not record a {notice.Platform} moderation event in the mod log: {ex.Message}");
        }
    }
}
