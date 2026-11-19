using System.Globalization;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;

namespace Cuebot.Twitch;

/// <summary>
/// Twitch's moderation queues for the combined chat (docs/design/UNIFIED_MOD_CHAT_PLAN.md, phase 3): AutoMod held messages
/// (automod.message.hold / update v2), Shield Mode (channel.shield_mode.begin / end and Helix), suspicious users
/// (channel.suspicious_user.update / message), unban requests and blocked terms (Helix lists). The state comes from the
/// Twitch events Cuebot publishes (<see cref="OnEvent"/>, subscribed in DI); actions arrive through
/// <see cref="ModerationService"/> as queue tools.
/// </summary>
internal sealed partial class TwitchChatModerator : IModerationQueues
{
    /// <summary>Decided held messages kept for the lane, so a mod sees what happened to them.</summary>
    private const int KeepDecided = 30;

    private const int MaxHeld = 300;

    private readonly object _gate = new();
    private readonly List<HeldChatMessage> _held = [];
    private readonly Dictionary<string, SuspiciousUserInfo> _suspicious = new(StringComparer.Ordinal);
    private ShieldModeState? _shield;

    public event Action<ModerationQueueChange>? Changed;

    public IReadOnlyList<HeldChatMessage> HeldMessages
    {
        get
        {
            lock (_gate) return _held.ToArray();
        }
    }

    public ShieldModeState? ShieldMode
    {
        get
        {
            lock (_gate) return _shield;
        }
    }

    public SuspiciousUserInfo? SuspiciousUser(string userId)
    {
        lock (_gate) return _suspicious.GetValueOrDefault(userId);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!IsConnected || !Api.HasBroadcasterScope(TwitchScopes.ManageShieldMode)) return;
        try
        {
            var shield = await Api.GetShieldModeAsync(ct).ConfigureAwait(false);
            SetShield(new ShieldModeState(shield.IsActive) { ModeratorName = shield.ModeratorName, Since = shield.LastActivatedAt });
        }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException or InvalidOperationException)
        {
            // keeps the last known state; the toolbar still offers the switch
        }
    }

    public async Task<IReadOnlyList<UnbanRequestInfo>> GetUnbanRequestsAsync(CancellationToken ct = default)
    {
        var requests = await Api.GetUnbanRequestsAsync("pending", ct).ConfigureAwait(false);
        return requests.Select(r => new UnbanRequestInfo(Platform, r.Id, r.UserId, r.CreatedAt ?? DateTimeOffset.MinValue)
        {
            UserName = r.UserName,
            UserLogin = r.UserLogin,
            Text = r.Text,
            Status = RequestStatus(r.Status),
            ResolvedBy = r.ResolvedBy,
            ResolutionText = r.ResolutionText,
        }).OrderBy(r => r.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<BlockedTermInfo>> GetBlockedTermsAsync(CancellationToken ct = default)
    {
        var terms = await Api.GetBlockedTermsAsync(ct).ConfigureAwait(false);
        return terms.Select(t => new BlockedTermInfo(Platform, t.Id, t.Text)).OrderBy(t => t.Text, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static UnbanRequestStatus RequestStatus(string status) => status.ToLowerInvariant() switch
    {
        "pending" => UnbanRequestStatus.Pending,
        "approved" => UnbanRequestStatus.Approved,
        "denied" => UnbanRequestStatus.Denied,
        _ => UnbanRequestStatus.Closed,
    };

    // ---------------------------------------------------------------- actions

    private async Task<ModerationOutcome> ExecuteQueueToolAsync(TwitchApi twitch, ModerationRequest r, CancellationToken ct)
    {
        switch (r.Action)
        {
            case ModerationAction.ShieldMode:
                var shield = await twitch.SetShieldModeAsync(r.Enabled, ct).ConfigureAwait(false);
                SetShield(new ShieldModeState(shield.IsActive)
                {
                    ModeratorName = shield.ModeratorName,
                    Since = shield.LastActivatedAt ?? Clock.GetUtcNow(),
                });
                return ModerationOutcome.Done();
            case ModerationAction.ApproveHeld or ModerationAction.DenyHeld:
                if (r.MessageId.Length == 0) return ModerationOutcome.Failed("No held message given.");
                var allow = r.Action == ModerationAction.ApproveHeld;
                await twitch.ResolveAutomodMessageAsync(r.MessageId, allow, ct).ConfigureAwait(false);
                Decide(r.MessageId, allow ? HeldMessageStatus.Approved : HeldMessageStatus.Denied, "");
                return ModerationOutcome.Done();
            case ModerationAction.MonitorUser or ModerationAction.RestrictUser:
                var restricted = r.Action == ModerationAction.RestrictUser;
                await twitch.AddSuspiciousStatusAsync(r.UserId, restricted, ct).ConfigureAwait(false);
                SetSuspicious(r.UserId, r.UserName, restricted ? SuspiciousLevel.Restricted : SuspiciousLevel.Monitored, "", "");
                return ModerationOutcome.Done();
            case ModerationAction.ClearSuspicious:
                await twitch.RemoveSuspiciousStatusAsync(r.UserId, ct).ConfigureAwait(false);
                SetSuspicious(r.UserId, r.UserName, SuspiciousLevel.None, "", "");
                return ModerationOutcome.Done();
            case ModerationAction.ApproveUnbanRequest or ModerationAction.DenyUnbanRequest:
                if (r.ItemId.Length == 0) return ModerationOutcome.Failed("No unban request given.");
                await twitch.ResolveUnbanRequestAsync(r.ItemId, r.Action == ModerationAction.ApproveUnbanRequest, r.Reason, ct).ConfigureAwait(false);
                Raise(ModerationQueueKind.UnbanRequests, r.ItemId);
                return ModerationOutcome.Done();
            case ModerationAction.AddBlockedTerm:
                if (r.Term.Trim().Length < 2) return ModerationOutcome.Failed("A blocked term needs at least 2 characters.");
                var id = await twitch.AddBlockedTermAsync(r.Term, ct).ConfigureAwait(false);
                Raise(ModerationQueueKind.BlockedTerms, id);
                return ModerationOutcome.Done(platformRef: id);
            case ModerationAction.RemoveBlockedTerm:
                if (r.ItemId.Length == 0) return ModerationOutcome.Failed("No blocked term given.");
                await twitch.RemoveBlockedTermAsync(r.ItemId, ct).ConfigureAwait(false);
                Raise(ModerationQueueKind.BlockedTerms, r.ItemId);
                return ModerationOutcome.Done();
            default:
                return ModerationOutcome.Failed($"{Platform} cannot do this from Cuebot.");
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Updates the queues from a published Twitch event (any thread).</summary>
    internal void OnEvent(BotEvent e)
    {
        switch (e.Type)
        {
            case TwitchEventTypes.AutomodMessageHeld:
                Hold(e);
                break;
            case TwitchEventTypes.AutomodMessageUpdated:
                var status = Str(e, "status").ToLowerInvariant() switch
                {
                    "approved" => HeldMessageStatus.Approved,
                    "denied" => HeldMessageStatus.Denied,
                    "expired" => HeldMessageStatus.Expired,
                    _ => HeldMessageStatus.Pending,
                };
                if (status != HeldMessageStatus.Pending) Decide(Str(e, "messageId"), status, Str(e, "moderator"));
                break;
            case TwitchEventTypes.ShieldModeBegin or TwitchEventTypes.ShieldModeEnd:
                var on = e.Type == TwitchEventTypes.ShieldModeBegin;
                SetShield(new ShieldModeState(on)
                {
                    ModeratorName = Str(e, "user"),
                    Since = Time(e, on ? "startedAt" : "endedAt") ?? e.Time,
                });
                break;
            case TwitchEventTypes.SuspiciousUserUpdate:
                SetSuspicious(Str(e, "userId"), Str(e, "user"), Level(Str(e, "lowTrustStatus")), "", Str(e, "moderator"));
                break;
            case TwitchEventTypes.SuspiciousUserMessage:
                SetSuspicious(Str(e, "userId"), Str(e, "user"), Level(Str(e, "lowTrustStatus")), Why(e), null);
                break;
            case TwitchEventTypes.UnbanRequestCreated or TwitchEventTypes.UnbanRequestApproved or TwitchEventTypes.UnbanRequestDenied:
                Raise(ModerationQueueKind.UnbanRequests, Str(e, "requestId"));
                break;
            case TwitchEventTypes.BlockedTermsAdded or TwitchEventTypes.BlockedTermsDeleted:
                Raise(ModerationQueueKind.BlockedTerms);
                break;
        }
    }

    private void Hold(BotEvent e)
    {
        var id = Str(e, "messageId");
        if (id.Length == 0) return;
        var held = new HeldChatMessage(Platform, id, Time(e, "heldAt") ?? e.Time)
        {
            UserId = Str(e, "userId"),
            UserName = Str(e, "user"),
            UserLogin = Str(e, "userName"),
            Text = Str(e, "message"),
            Reason = Str(e, "reason") switch
            {
                "blocked_term" => "Blocked term",
                "blocked_link" => "Link not allowed",
                _ => Str(e, "category") is { Length: > 0 } category
                    ? $"AutoMod: {category.Replace('_', ' ')}" + (Str(e, "level") is { Length: > 0 } level and not "0" ? $", level {level}" : "")
                    : "AutoMod",
            },
        };
        lock (_gate)
        {
            _held.RemoveAll(h => h.Id == id);
            _held.Add(held);
            Trim();
        }
        Raise(ModerationQueueKind.HeldMessages, id);
    }

    private void Decide(string id, HeldMessageStatus status, string by)
    {
        lock (_gate)
        {
            var index = _held.FindIndex(h => h.Id == id);
            if (index < 0) return;
            _held[index] = _held[index] with { Status = status, DecidedBy = by.Length > 0 ? by : _held[index].DecidedBy };
            Trim();
        }
        Raise(ModerationQueueKind.HeldMessages, id);
    }

    /// <summary>Keeps every pending message (up to a limit) and the newest decided ones.</summary>
    private void Trim()
    {
        var decided = _held.Where(h => !h.IsPending).ToList();
        foreach (var old in decided.Take(Math.Max(0, decided.Count - KeepDecided))) _held.Remove(old);
        while (_held.Count > MaxHeld) _held.RemoveAt(0);
    }

    private void SetShield(ShieldModeState state)
    {
        lock (_gate)
        {
            if (_shield == state) return;
            _shield = state;
        }
        Raise(ModerationQueueKind.ShieldMode);
    }

    private void SetSuspicious(string userId, string userName, SuspiciousLevel level, string why, string? moderator)
    {
        if (userId.Length == 0) return;
        lock (_gate)
        {
            if (level == SuspiciousLevel.None)
            {
                if (!_suspicious.Remove(userId)) return;
            }
            else
            {
                var old = _suspicious.GetValueOrDefault(userId);
                if (_suspicious.Count > 5000) _suspicious.Clear();
                _suspicious[userId] = new SuspiciousUserInfo(Platform, userId, level)
                {
                    UserName = userName.Length > 0 ? userName : old?.UserName ?? "",
                    Why = why.Length > 0 ? why : old?.Why ?? "",
                    ModeratorName = moderator ?? old?.ModeratorName ?? "",
                };
            }
        }
        Raise(ModerationQueueKind.SuspiciousUsers, userId);
    }

    private static SuspiciousLevel Level(string lowTrustStatus) => lowTrustStatus switch
    {
        "active_monitoring" => SuspiciousLevel.Monitored,
        "restricted" => SuspiciousLevel.Restricted,
        _ => SuspiciousLevel.None,
    };

    /// <summary>"possible ban evader, banned in 2 shared channels" from a suspicious user message.</summary>
    private static string Why(BotEvent e)
    {
        var parts = new List<string>();
        switch (Str(e, "banEvasionEvaluation"))
        {
            case "likely": parts.Add("likely ban evader"); break;
            case "possible": parts.Add("possible ban evader"); break;
        }
        var shared = Str(e, "sharedBanChannelIds").Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        if (shared > 0) parts.Add(shared == 1 ? "banned in 1 shared channel" : $"banned in {shared} shared channels");
        if (Str(e, "types").Contains("manually_added", StringComparison.Ordinal)) parts.Add("added by a mod");
        return string.Join(", ", parts);
    }

    private void Raise(ModerationQueueKind kind, string itemId = "") => Changed?.Invoke(new ModerationQueueChange(Platform, kind, itemId));

    private static string Str(BotEvent e, string key) =>
        e.Args.GetValueOrDefault(key) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static DateTimeOffset? Time(BotEvent e, string key) =>
        DateTimeOffset.TryParse(Str(e, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
