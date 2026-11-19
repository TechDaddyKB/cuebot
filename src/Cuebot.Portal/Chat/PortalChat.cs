using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Chat;

/// <summary>
/// The member's role decides which moderation tools they may use (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 8).
/// <see cref="ModerationService"/> asks before every command a member sends; the streamer and Cuebot's own rules are
/// never asked about.
/// </summary>
public sealed class PortalModerationPermissions(PortalDirectory directory) : IModerationPermissions
{
    /// <summary>
    /// The permission a tool needs. The queue tools follow the plan's table: Shield Mode goes with chat modes, blocked
    /// terms have their own right; allowing or denying a held message and monitoring a chatter go with delete and
    /// timeout (the same weight as removing one message); restricting a chatter, clearing their status and answering
    /// unban requests go with ban and unban.
    /// </summary>
    public static PortalPermission For(ModerationAction action) => action switch
    {
        ModerationAction.Delete or ModerationAction.Timeout or ModerationAction.Warn
            or ModerationAction.ApproveHeld or ModerationAction.DenyHeld or ModerationAction.MonitorUser => PortalPermission.ChatDeleteTimeout,
        ModerationAction.Ban or ModerationAction.Unban or ModerationAction.RestrictUser or ModerationAction.ClearSuspicious
            or ModerationAction.ApproveUnbanRequest or ModerationAction.DenyUnbanRequest => PortalPermission.ChatBan,
        ModerationAction.AddBlockedTerm or ModerationAction.RemoveBlockedTerm => PortalPermission.BlockedTerms,
        ModerationAction.ShieldMode => PortalPermission.ChatModes,
        _ => PortalPermission.ChatModes,
    };

    public string? Refuse(ModActor actor, ModerationCommand command)
    {
        if (actor.Kind != ModActorKind.Member) return null;
        if (!Guid.TryParse(actor.MemberId, out var id) || directory.Member(id) is not { Suspended: false } member)
            return "Your access was removed or paused by the streamer.";
        return Refuse(directory.RoleOf(member), command);
    }

    /// <summary>Why a member with <paramref name="role"/> may not send <paramref name="command"/>, or null.</summary>
    public static string? Refuse(PortalRole? role, ModerationCommand command)
    {
        if (!PortalPolicy.Has(role, For(command.Action)))
            return $"Your role does not include {ModerationCapability.Label(command.Action).ToLowerInvariant()}.";
        if (command.ViaLinkId is not null)
        {
            if (command.Action == ModerationAction.Timeout && !PortalPolicy.Has(role, PortalPermission.LinkedTimeouts))
                return "Your role does not include timeouts through linked accounts.";
            if (command.Action == ModerationAction.Ban && !PortalPolicy.Has(role, PortalPermission.LinkedBans))
                return "Your role does not include bans through linked accounts.";
        }
        return null;
    }

    /// <summary>
    /// Identity links and notes (phase 4): the member's role decides, like moderation. Seeing linked accounts needs
    /// Propose links, Confirm links or Notes: read; confirming, rejecting and unlinking need Confirm links; notes follow
    /// the three Notes rights. That a member never confirms their own suggestion is <see cref="IdentityLinks"/>' rule.
    /// </summary>
    public string? RefuseLink(ModActor actor, LinkOperation operation)
    {
        if (actor.Kind == ModActorKind.Streamer) return null;
        if (actor.Kind != ModActorKind.Member) return "Only the streamer and mods can do this.";
        if (!Guid.TryParse(actor.MemberId, out var id) || directory.Member(id) is not { Suspended: false } member)
            return "Your access was removed or paused by the streamer.";
        return RefuseLink(directory.RoleOf(member), operation);
    }

    /// <summary>Why a member with <paramref name="role"/> may not do <paramref name="operation"/>, or null.</summary>
    public static string? RefuseLink(PortalRole? role, LinkOperation operation) => operation switch
    {
        LinkOperation.View => CanViewLinks(role) ? null : "Your role does not include seeing linked accounts.",
        LinkOperation.Propose => PortalPolicy.Has(role, PortalPermission.ProposeLinks) ? null : "Your role does not include suggesting links.",
        LinkOperation.Confirm => PortalPolicy.Has(role, PortalPermission.ConfirmLinks) ? null : "Your role does not include confirming links.",
        LinkOperation.Reject => PortalPolicy.Has(role, PortalPermission.ConfirmLinks) ? null : "Your role does not include rejecting links.",
        LinkOperation.Unlink => PortalPolicy.Has(role, PortalPermission.ConfirmLinks) ? null : "Your role does not include unlinking accounts.",
        LinkOperation.ReadNotes => PortalPolicy.Has(role, PortalPermission.NotesRead) ? null : "Your role does not include reading notes.",
        LinkOperation.AddNote => PortalPolicy.Has(role, PortalPermission.NotesAdd) ? null : "Your role does not include writing notes.",
        LinkOperation.DeleteNote => PortalPolicy.Has(role, PortalPermission.NotesDelete) ? null
            : "Your role does not include changing or deleting other people's notes.",
        _ => "Your role does not include this.",
    };

    /// <summary>Linked accounts are shown to roles that may suggest or confirm links, or read notes.</summary>
    public static bool CanViewLinks(PortalRole? role) =>
        PortalPolicy.Has(role, PortalPermission.ProposeLinks) || PortalPolicy.Has(role, PortalPermission.ConfirmLinks)
                                                                || PortalPolicy.Has(role, PortalPermission.NotesRead);
}

/// <summary>
/// The combined chat in the mod portal (docs/design/UNIFIED_MOD_CHAT_PLAN.md, phase 2): the recent chat, every moderation tool
/// through <see cref="ModerationService"/> (acting as the member), the mod log, and two push topics, "chat" (messages,
/// deletions, bans and timeouts as the platforms report them) and "modlog" (new and changed log rows). Moderation is
/// not a change: it applies at once, nothing waits for approval, and the mod log records who did it. Members' rate
/// limits and protected targets are <see cref="ModerationService"/>'s; its refusals come back as the answer's error.
/// Mods cannot send chat messages from here (the streamer's and bot's accounts are the streamer's).
/// Phase 3 adds the platforms' moderation queues (<see cref="IModerationQueues"/>, Twitch): the "chat.queues" op (held
/// messages, Shield Mode, and on request unban requests and blocked terms, each only for roles with the right) and the
/// "modqueue" push topic (a message held or decided, Shield Mode on or off, a chatter flagged, an unban request or the
/// blocked terms changed), filtered by the same rights.
/// Phases 4 and 5 add identity links, notes and linked bans and timeouts (PortalChat.Links.cs), all behind the
/// streamer's Identity links setting.
/// </summary>
public sealed partial class PortalChat : IDisposable
{
    public const string ChatTopic = "chat";
    public const string LogTopic = "modlog";
    public const string QueueTopic = "modqueue";
    public const int MaxHistory = 500;
    public const int MaxLogRows = 200;

    /// <summary>The deepest page of the mod log the portal reads (offset plus page size).</summary>
    public const int MaxLogDepth = 2000;

    public const int MaxReasonLength = 500;

    /// <summary>Longest blocked term (Twitch's limit).</summary>
    public const int MaxTermLength = 500;

    /// <summary>Most held messages (pending first, then recently decided) and unban requests or terms sent in one answer.</summary>
    public const int MaxHeld = 100;

    public const int MaxQueueItems = 1000;

    /// <summary>Longest message text sent to a device; longer ones are cut (every platform's limit is below it).</summary>
    public const int MaxTextLength = 2000;

    /// <summary>The longest timeout any platform allows (Twitch: 14 days).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(14);

    /// <summary>Timeout choices the page offers, with what each platform will really apply.</summary>
    public static readonly int[] TimeoutPresetSeconds = [60, 300, 600, 3600, 86400, 604800];

    private const string LogSource = "Mod portal";

    private static readonly PortalPermission[] ActPermissions =
        [PortalPermission.ChatDeleteTimeout, PortalPermission.ChatBan, PortalPermission.ChatModes, PortalPermission.BlockedTerms];

    private readonly PortalDirectory _directory;
    private readonly ChatHub? _hub;
    private readonly ModerationService? _moderation;
    private readonly AppLog? _log;
    private IPortalPush? _push;

    public PortalChat(PortalDirectory directory, ChatHub? hub, ModerationService? moderation, AppLog? log = null)
    {
        _directory = directory;
        _hub = hub;
        _moderation = moderation;
        _log = log;
        if (_moderation is not null) _moderation.Permissions = new PortalModerationPermissions(directory);
        if (_hub is not null)
        {
            _hub.MessageReceived += OnMessage;
            _hub.ModerationSeen += OnModeration;
        }
        if (_moderation?.Log is { } modLog) modLog.Changed += OnLogRow;
        if (_moderation is not null) _moderation.QueuesChanged += OnQueuesChanged;
        SubscribeLinks();
    }

    /// <summary>The ops this class answers, for PortalApi's dispatch.</summary>
    public static bool Handles(string op) =>
        op is "chat.history" or "chat.capabilities" or "chat.act" or "chat.log" or "chat.user" or "chat.queues" || IsLinkOp(op);

    /// <summary>
    /// Offers the "chat", "modlog" and "modqueue" topics on the portal link. Called once the relay client exists (it needs PortalApi,
    /// which needs this, so the push hub comes later).
    /// </summary>
    public void Attach(IPortalPush push)
    {
        _push = push;
        push.Register(new PortalTopic(ChatTopic) { Permission = PortalPermission.ViewChat, CheckParams = CheckPlatformParam });
        push.Register(new PortalTopic(LogTopic) { Permission = PortalPermission.ViewModLog, CheckParams = CheckPlatformParam });
        push.Register(new PortalTopic(QueueTopic) { Permission = PortalPermission.ViewChat, CheckParams = CheckPlatformParam });
        push.Register(new PortalTopic(LinksTopic) { Permission = PortalPermission.ViewChat, CheckParams = CheckPlatformParam });
    }

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request, CancellationToken ct = default)
    {
        try
        {
            return op switch
            {
                "chat.history" => !PortalPolicy.Has(role, PortalPermission.ViewChat) ? NotIncluded() : History(request),
                "chat.capabilities" => !PortalPolicy.Has(role, PortalPermission.ViewChat) ? NotIncluded() : Capabilities(role, request),
                "chat.log" => !PortalPolicy.Has(role, PortalPermission.ViewModLog) ? NotIncluded() : Log(role, request),
                "chat.user" => !PortalPolicy.Has(role, PortalPermission.ViewChat) ? NotIncluded() : User(role, request),
                "chat.queues" => !PortalPolicy.Has(role, PortalPermission.ViewChat) ? NotIncluded() : await QueuesAsync(role, request, ct).ConfigureAwait(false),
                "chat.act" => await ActAsync(member, role, request, ct).ConfigureAwait(false),
                _ when IsLinkOp(op) => await HandleLinksAsync(member, role, op, request, ct).ConfigureAwait(false),
                _ => Error($"Unknown request '{op}'."),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return Error("That request was not understood: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- reading

    private JsonObject History(JsonObject request)
    {
        if (_hub is null) return Error("This Cuebot has no chat.");
        var platform = Str(request, "platform");
        var limit = Math.Clamp(Int(request, "limit") ?? MaxHistory, 1, MaxHistory);
        var messages = _hub.RecentMessages().Where(m => Matches(m.Platform, platform)).TakeLast(limit);
        var seen = _hub.RecentModeration().Where(n => Matches(n.Platform, platform)).TakeLast(100);
        return Ok(new JsonObject
        {
            ["platforms"] = Platforms(),
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)Message(m))]),
            ["moderation"] = new JsonArray([.. seen.Select(n => (JsonNode)Notice(n))]),
        });
    }

    private JsonObject Capabilities(PortalRole? role, JsonObject request)
    {
        if (_moderation is null) return Error("This Cuebot cannot moderate chat.");
        var only = Str(request, "platform");
        var platforms = only.Length > 0 ? [only] : PlatformNames();
        return Ok(new JsonObject
        {
            ["platforms"] = new JsonArray([.. platforms.Select(p => (JsonNode)PlatformTools(role, p))]),
            ["links"] = LinksCapabilities(role),
        });
    }

    private JsonObject PlatformTools(PortalRole? role, string platform)
    {
        var moderation = _moderation!;
        var node = new JsonObject
        {
            ["platform"] = platform,
            ["tools"] = new JsonArray([.. moderation.Capabilities(platform).Select(c => (JsonNode)Tool(role, c))]),
            ["timeouts"] = new JsonArray([.. TimeoutPresetSeconds.Select(s =>
            {
                var (applied, note) = moderation.PreviewDuration(platform, TimeSpan.FromSeconds(s));
                return (JsonNode)new JsonObject { ["requested"] = s, ["applied"] = (int)Math.Ceiling(applied.TotalSeconds), ["note"] = note };
            })]),
        };
        if (moderation.QuotaFor(platform) is { } quota)
            node["quota"] = new JsonObject { ["remaining"] = quota.Remaining, ["reserve"] = quota.Reserve, ["resetsAt"] = quota.ResetsAt };
        return node;
    }

    private static JsonObject Tool(PortalRole? role, ModerationCapability c)
    {
        var permission = PortalModerationPermissions.For(c.Action);
        var node = new JsonObject
        {
            ["action"] = c.Action.ToString(),
            ["label"] = ModerationCapability.Label(c.Action),
            ["state"] = c.State.ToString(),
            ["note"] = c.Note,
            ["allowed"] = PortalPolicy.Has(role, permission),
        };
        if (c.MinDuration is { } min) node["minSeconds"] = (int)min.TotalSeconds;
        if (c.MaxDuration is { } max) node["maxSeconds"] = (int)max.TotalSeconds;
        if (c.DurationUnit > TimeSpan.FromSeconds(1)) node["unitSeconds"] = (int)c.DurationUnit.TotalSeconds;
        if (c.MaxReasonLength is { } reason) node["maxReason"] = reason;
        if (c.QuotaCost > 0) node["quotaCost"] = c.QuotaCost;
        if (c.WebsiteUrl is { Length: > 0 } url && IsWebLink(url)) node["websiteUrl"] = url;
        return node;
    }

    private JsonObject Log(PortalRole? role, JsonObject request)
    {
        if (_moderation?.Log is not { } log) return Error("This Cuebot keeps no mod log.");
        var platform = Str(request, "platform");
        var userId = Str(request, "userId");
        var limit = Math.Clamp(Int(request, "limit") ?? 50, 1, MaxLogRows);
        var offset = Math.Clamp(Int(request, "offset") ?? 0, 0, MaxLogDepth - limit);
        if (userId.Length > 0 && platform.Length == 0) return Error("Say which platform the account is on.");
        var rows = userId.Length > 0
            ? log.ForTarget(platform, userId, offset + limit + 1)
            : log.Recent(offset + limit + 1, platform.Length > 0 ? platform : null);
        rows = [.. rows.Where(r => MaySeeRow(role, r))];
        var page = rows.Skip(offset).Take(limit).ToList();
        return Ok(new JsonObject
        {
            ["rows"] = new JsonArray([.. page.Select(r => (JsonNode)Row(r))]),
            ["more"] = rows.Count > offset + limit && offset + limit < MaxLogDepth,
        });
    }

    private JsonObject User(PortalRole? role, JsonObject request)
    {
        var platform = Str(request, "platform");
        var userId = Str(request, "userId");
        if (platform.Length == 0 || userId.Length == 0) return Error("Pick someone in the chat first.");
        var messages = _hub?.RecentMessages().Where(m => Same(m.Platform, platform) && m.UserId == userId).TakeLast(50).ToList() ?? [];
        var body = new JsonObject
        {
            ["platform"] = platform,
            ["userId"] = userId,
            ["name"] = messages.LastOrDefault()?.User ?? "",
            ["roles"] = RoleNames(RolesOf(platform, userId)),
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)Message(m))]),
        };
        if (PortalPolicy.Has(role, PortalPermission.ViewModLog) && _moderation?.Log is { } log)
        {
            body["log"] = new JsonArray([.. log.ForTarget(platform, userId, 50).Where(r => MaySeeRow(role, r)).Select(r => (JsonNode)Row(r))]);
            if (log.FindInForce(platform, userId) is { } inForce) body["inForce"] = Row(inForce);
        }
        if (PortalPolicy.Has(role, PortalPermission.ChatDeleteTimeout) && _moderation?.QueuesFor(platform) is { } queues)
        {
            body["suspiciousKnown"] = true;
            if (SafeSuspicious(queues, userId) is { Level: not SuspiciousLevel.None } flagged) body["suspicious"] = Suspicious(flagged);
        }
        return Ok(body);
    }

    /// <summary>
    /// The moderation queues of each platform that has them: Shield Mode for everyone who sees the chat, held messages
    /// for roles that may allow or deny them, and (only when asked, since each is a platform call) unban requests and
    /// blocked terms for roles with those rights.
    /// </summary>
    private async Task<JsonObject> QueuesAsync(PortalRole? role, JsonObject request, CancellationToken ct)
    {
        if (_moderation is null) return Error("This Cuebot cannot moderate chat.");
        var held = PortalPolicy.Has(role, PortalPermission.ChatDeleteTimeout);
        var unban = PortalPolicy.Has(role, PortalPermission.ChatBan);
        var terms = PortalPolicy.Has(role, PortalPermission.BlockedTerms);
        var wantUnban = Bool(request, "unbanRequests");
        var wantTerms = Bool(request, "blockedTerms");
        if ((wantUnban && !unban) || (wantTerms && !terms)) return NotIncluded();
        var only = Str(request, "platform");
        var platforms = new JsonArray();
        foreach (var queues in _moderation.Queues.Where(q => Matches(q.Platform, only)))
        {
            if (Bool(request, "refresh"))
            {
                try
                {
                    await queues.RefreshAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Warn(LogSource, $"{queues.Platform} could not refresh its moderation state: {ex.Message}");
                }
            }
            var node = new JsonObject { ["platform"] = queues.Platform, ["shield"] = Shield(queues.ShieldMode) };
            if (held)
            {
                var all = SafeHeld(queues);
                node["held"] = new JsonArray([.. HeldForPage(all).Select(h => (JsonNode)Held(h))]);
                node["pendingHeld"] = all.Count(h => h.IsPending);
            }
            if (wantUnban)
            {
                try
                {
                    var list = await queues.GetUnbanRequestsAsync(ct).ConfigureAwait(false);
                    node["unbanRequests"] = new JsonArray([.. list.Take(MaxQueueItems).Select(r => (JsonNode)Unban(r))]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    node["unbanRequestsError"] = $"{queues.Platform} did not give the unban requests: {ex.Message}";
                }
            }
            if (wantTerms)
            {
                try
                {
                    var list = await queues.GetBlockedTermsAsync(ct).ConfigureAwait(false);
                    node["blockedTerms"] = new JsonArray([.. list.Take(MaxQueueItems).Select(t => (JsonNode)Term(t))]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    node["blockedTermsError"] = $"{queues.Platform} did not give the blocked terms: {ex.Message}";
                }
            }
            platforms.Add(node);
        }
        return Ok(new JsonObject
        {
            ["platforms"] = platforms,
            ["rights"] = new JsonObject
            {
                ["held"] = held,
                ["shield"] = PortalPolicy.Has(role, PortalPermission.ChatModes),
                ["unbanRequests"] = unban,
                ["blockedTerms"] = terms,
            },
        });
    }

    /// <summary>Pending held messages oldest first, then the most recently decided ones, at most <see cref="MaxHeld"/>.</summary>
    private static IEnumerable<HeldChatMessage> HeldForPage(IReadOnlyList<HeldChatMessage> all) =>
        all.Where(h => h.IsPending).OrderBy(h => h.HeldAt)
            .Concat(all.Where(h => !h.IsPending).OrderByDescending(h => h.HeldAt))
            .Take(MaxHeld);

    private IReadOnlyList<HeldChatMessage> SafeHeld(IModerationQueues queues)
    {
        try
        {
            return queues.HeldMessages.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warn(LogSource, $"{queues.Platform} could not list held messages: {ex.Message}");
            return [];
        }
    }

    private SuspiciousUserInfo? SafeSuspicious(IModerationQueues queues, string userId)
    {
        try
        {
            return queues.SuspiciousUser(userId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warn(LogSource, $"{queues.Platform} could not say whether a chatter is suspicious: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- acting

    private async Task<JsonObject> ActAsync(PortalMember member, PortalRole? role, JsonObject request, CancellationToken ct)
    {
        // Members without any moderation right get a plain no (and no log row); everyone else goes through the service,
        // which logs what it refuses.
        if (!ActPermissions.Any(p => PortalPolicy.Has(role, p))) return NotIncluded();
        if (_moderation is null) return Error("This Cuebot cannot moderate chat.");
        if (!Enum.TryParse<ModerationAction>(Str(request, "action"), ignoreCase: true, out var action) || !Enum.IsDefined(action))
            return Error("Pick a moderation tool.");
        var platform = Str(request, "platform");
        if (platform.Length == 0) return Error("Say which platform.");
        var (command, problem) = IsItemTool(action)
            ? await ItemCommandAsync(platform, action, role, request, ct).ConfigureAwait(false)
            : CommandFor(platform, action, request);
        if (command is null) return Error(problem ?? "That request was not understood.");

        var actor = ModActor.Member(member.Id.ToString(), member.Name, Str(request, PortalSignInHandler.DeviceKey) is { Length: > 0 } d ? d : null);
        var result = await _moderation.ExecuteAsync(command, actor, ct).ConfigureAwait(false);
        if (result.Ok) _log?.Info(LogSource, $"{member.Name}: {result.Message} on {platform}.");
        var answer = new JsonObject
        {
            ["ok"] = result.Ok,
            ["result"] = result.Result.ToString(),
            ["message"] = result.Message,
        };
        if (!result.Ok) answer["error"] = result.Message;
        if (result.AppliedDuration is { } applied) answer["appliedSeconds"] = (int)Math.Ceiling(applied.TotalSeconds);
        if (result.WebsiteUrl is { Length: > 0 } url && IsWebLink(url)) answer["websiteUrl"] = url;
        if (result.Entry is { } entry) answer["row"] = Row(entry);
        return answer;
    }

    /// <summary>
    /// The command for a request. The message, its text and the author's chat roles come from Cuebot's own chat history,
    /// never from the device, so a mod cannot hide a protected target or rewrite what the log keeps.
    /// </summary>
    private (ModerationCommand? Command, string? Problem) CommandFor(string platform, ModerationAction action, JsonObject request)
    {
        var messageId = Str(request, "messageId");
        ChatMessage? message = null;
        if (messageId.Length > 0)
        {
            message = _hub?.RecentMessages().LastOrDefault(m => Same(m.Platform, platform) && m.Id == messageId);
            if (message is null) return Problem("That message is no longer in Cuebot's recent chat.");
        }

        ModerationCommand command;
        if (message is not null && (action == ModerationAction.Delete || ModerationCapability.TargetsUser(action) || action == ModerationAction.Pin))
        {
            if ((action is ModerationAction.Delete or ModerationAction.Pin) && !message.CanDelete)
                return Problem($"{platform} gave this message no id, so it cannot be {(action == ModerationAction.Pin ? "pinned" : "deleted")}.");
            command = ModerationCommand.For(message, action) with { TargetRoles = message.Roles | RolesOf(platform, message.UserId) };
        }
        else if (action is ModerationAction.Delete or ModerationAction.Pin)
        {
            return Problem("Pick the message first.");
        }
        else if (ModerationCapability.TargetsUser(action))
        {
            var userId = Str(request, "userId");
            if (userId.Length == 0) return Problem("Pick who this is for.");
            var known = _hub?.RecentMessages().LastOrDefault(m => Same(m.Platform, platform) && m.UserId == userId);
            command = new ModerationCommand(platform, action)
            {
                TargetUserId = userId,
                TargetName = known?.User ?? Limit(Str(request, "user"), 100),
                TargetRoles = RolesOf(platform, userId),
                ChannelId = known?.ChannelId,
            };
        }
        else
        {
            command = new ModerationCommand(platform, action)
            {
                Enabled = request["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) ? on : true,
                ModeValue = Int(request, "value") is { } v ? Math.Clamp(v, 0, 1_000_000) : null,
            };
        }

        var reason = Str(request, "reason").Trim();
        if (reason.Length > MaxReasonLength) return Problem($"Keep the reason under {MaxReasonLength} characters.");
        command = command with { Reason = reason.Length > 0 ? reason : null };

        if (action == ModerationAction.Timeout)
        {
            var seconds = Int(request, "durationSeconds") ?? 600;
            if (seconds < 1 || seconds > MaxTimeout.TotalSeconds) return Problem("A timeout lasts from 1 second to 14 days.");
            command = command with { Duration = TimeSpan.FromSeconds(seconds) };
        }
        return (command, null);
    }

    private static (ModerationCommand?, string?) Problem(string text) => (null, text);

    private static bool IsItemTool(ModerationAction action) =>
        action is ModerationAction.ApproveHeld or ModerationAction.DenyHeld or ModerationAction.ApproveUnbanRequest
            or ModerationAction.DenyUnbanRequest or ModerationAction.AddBlockedTerm or ModerationAction.RemoveBlockedTerm;

    /// <summary>
    /// The command for a queue item (a held message, an unban request, a blocked term). The item, its text and its
    /// author come from Cuebot's queues or the platform's own list, never from the device. A member whose role does not
    /// include the tool gets a bare command, so <see cref="ModerationService"/> refuses and logs it without Cuebot first
    /// asking the platform for its lists.
    /// </summary>
    private async Task<(ModerationCommand? Command, string? Problem)> ItemCommandAsync(string platform, ModerationAction action,
        PortalRole? role, JsonObject request, CancellationToken ct)
    {
        var reason = Str(request, "reason").Trim();
        if (reason.Length > MaxReasonLength) return Problem($"Keep the reason under {MaxReasonLength} characters.");
        var answer = reason.Length > 0 ? reason : null;
        if (action == ModerationAction.AddBlockedTerm)
        {
            var term = Str(request, "term").Trim();
            if (term.Length == 0) return Problem("Type the word or phrase to block.");
            if (term.Length > MaxTermLength) return Problem($"Keep the term under {MaxTermLength} characters.");
            return (ModerationCommand.BlockTerm(platform, term), null);
        }

        var itemId = Str(request, "itemId").Trim();
        if (itemId.Length == 0 || itemId.Length > 200)
            return Problem(action switch
            {
                ModerationAction.ApproveHeld or ModerationAction.DenyHeld => "Pick the held message first.",
                ModerationAction.RemoveBlockedTerm => "Pick the term first.",
                _ => "Pick the unban request first.",
            });
        var queues = _moderation!.QueuesFor(platform);
        if (queues is null || !PortalPolicy.Has(role, PortalModerationPermissions.For(action)))
            return (new ModerationCommand(platform, action) { ItemId = itemId }, null);

        try
        {
            switch (action)
            {
                case ModerationAction.ApproveHeld or ModerationAction.DenyHeld:
                {
                    var held = SafeHeld(queues).FirstOrDefault(h => h.Id == itemId);
                    if (held is null) return Problem("That held message is no longer waiting.");
                    if (!held.IsPending) return Problem($"That message was already {StatusWord(held.Status)}.");
                    return (ModerationCommand.ForHeld(held, action == ModerationAction.ApproveHeld) with { Reason = answer }, null);
                }
                case ModerationAction.ApproveUnbanRequest or ModerationAction.DenyUnbanRequest:
                {
                    var list = await queues.GetUnbanRequestsAsync(ct).ConfigureAwait(false);
                    var found = list.FirstOrDefault(r => r.Id == itemId);
                    if (found is null || found.Status != UnbanRequestStatus.Pending) return Problem("That unban request is no longer open.");
                    return (ModerationCommand.ForUnbanRequest(found, action == ModerationAction.ApproveUnbanRequest, answer), null);
                }
                default:
                {
                    var list = await queues.GetBlockedTermsAsync(ct).ConfigureAwait(false);
                    var found = list.FirstOrDefault(t => t.Id == itemId);
                    if (found is null) return Problem("That term is no longer blocked.");
                    return (ModerationCommand.UnblockTerm(found), null);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Problem($"{platform} did not answer: {ex.Message}");
        }
    }

    private static string StatusWord(HeldMessageStatus status) => status switch
    {
        HeldMessageStatus.Approved => "allowed",
        HeldMessageStatus.Denied => "denied",
        _ => "dropped by the platform",
    };

    /// <summary>Every role the chat showed for this account in the recent history (a mod who chatted once stays a mod).</summary>
    private ChatRoles RolesOf(string platform, string userId)
    {
        if (_hub is null || userId.Length == 0) return ChatRoles.None;
        var roles = ChatRoles.None;
        foreach (var m in _hub.RecentMessages())
            if (m.UserId == userId && Same(m.Platform, platform)) roles |= m.Roles;
        return roles;
    }

    // ---------------------------------------------------------------- push

    private void OnMessage(ChatMessage message) => PublishChat(message.Platform, new JsonObject { ["type"] = "message", ["message"] = Message(message) });

    private void OnModeration(ChatModerationNotice notice) =>
        PublishChat(notice.Platform, new JsonObject { ["type"] = "moderation", ["notice"] = Notice(notice) });

    private void OnLogRow(ModLogEntry entry)
    {
        // Rows about identity links say who is linked to whom: only for roles that may see links, and only while links are on.
        if (IsLinkRow(entry))
        {
            if (LinksOn) Publish(LogTopic, entry.Platform, new JsonObject { ["type"] = "row", ["row"] = Row(entry) }, PortalModerationPermissions.CanViewLinks);
            return;
        }
        Publish(LogTopic, entry.Platform, new JsonObject { ["type"] = "row", ["row"] = Row(entry) });
    }

    /// <summary>
    /// A queue changed: tells subscribed devices whose role may see that queue. Held messages and suspicious chatters
    /// go to roles with delete and timeout, unban requests to ban and unban, blocked terms to blocked terms; Shield Mode
    /// to everyone who sees the chat.
    /// </summary>
    private void OnQueuesChanged(ModerationQueueChange change)
    {
        if (_push is not { } push || push.Subscribers(QueueTopic) == 0) return;
        try
        {
            var queues = _moderation?.QueuesFor(change.Platform);
            var payload = new JsonObject { ["platform"] = change.Platform };
            PortalPermission needs;
            switch (change.Kind)
            {
                case ModerationQueueKind.HeldMessages:
                    needs = PortalPermission.ChatDeleteTimeout;
                    IReadOnlyList<HeldChatMessage> all = queues is null ? [] : SafeHeld(queues);
                    payload["type"] = "held";
                    payload["id"] = change.ItemId;
                    if (change.ItemId.Length > 0 && all.FirstOrDefault(h => h.Id == change.ItemId) is { } held) payload["item"] = Held(held);
                    payload["pendingHeld"] = all.Count(h => h.IsPending);
                    break;
                case ModerationQueueKind.ShieldMode:
                    needs = PortalPermission.ViewChat;
                    payload["type"] = "shield";
                    payload["shield"] = Shield(queues?.ShieldMode);
                    break;
                case ModerationQueueKind.SuspiciousUsers:
                    needs = PortalPermission.ChatDeleteTimeout;
                    payload["type"] = "suspicious";
                    payload["userId"] = Limit(change.ItemId, 100);
                    payload["suspicious"] = queues is not null && change.ItemId.Length > 0
                                            && SafeSuspicious(queues, change.ItemId) is { Level: not SuspiciousLevel.None } s ? Suspicious(s) : null;
                    break;
                case ModerationQueueKind.UnbanRequests:
                    needs = PortalPermission.ChatBan;
                    payload["type"] = "unbanRequests";
                    payload["id"] = change.ItemId;
                    break;
                case ModerationQueueKind.BlockedTerms:
                    needs = PortalPermission.BlockedTerms;
                    payload["type"] = "blockedTerms";
                    break;
                default:
                    return;
            }
            Publish(QueueTopic, change.Platform, payload, role => PortalPolicy.Has(role, needs));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warn(LogSource, $"Could not send a moderation queue update to mods: {ex.Message}");
        }
    }

    private void PublishChat(string platform, JsonObject payload) => Publish(ChatTopic, platform, payload);

    /// <summary>
    /// Sends a push event to the topic's subscribers whose platform filter matches (a null <paramref name="platform"/>
    /// goes to all of them) and, when <paramref name="allow"/> is given, whose role passes it.
    /// </summary>
    private void Publish(string topic, string? platform, JsonObject payload, Func<PortalRole?, bool>? allow = null)
    {
        if (_push is not { } push || push.Subscribers(topic) == 0) return;
        // The topic's own permission is checked by the hub; a payload that needs more is checked per member, once.
        var allowed = new Dictionary<Guid, bool>();
        try
        {
            push.Publish(topic, payload, sub => (platform is null || sub.Params["platform"] is not JsonValue p || p.GetValue<string>() is not { Length: > 0 } only
                                                 || Same(only, platform))
                                                && (allow is null || MayReceive(allowed, sub.MemberId, allow)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log?.Warn(LogSource, $"Could not send a {topic} update to mods: {ex.Message}");
        }
    }

    private bool MayReceive(Dictionary<Guid, bool> cache, Guid memberId, Func<PortalRole?, bool> allow)
    {
        if (!cache.TryGetValue(memberId, out var may))
            cache[memberId] = may = _directory.Member(memberId) is { Suspended: false } member && allow(_directory.RoleOf(member));
        return may;
    }

    private static string? CheckPlatformParam(JsonObject parameters)
    {
        if (parameters["platform"] is null) return null;
        return parameters["platform"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length <= 40
            ? null
            : "The platform must be a name such as \"Twitch\".";
    }

    // ---------------------------------------------------------------- shapes

    /// <summary>A chat line for the portal. Text only: the page renders it as text, never as markup.</summary>
    internal JsonObject Message(ChatMessage m)
    {
        var node = new JsonObject
        {
            ["platform"] = m.Platform,
            ["id"] = m.Id,
            ["user"] = Limit(m.User, 100),
            ["userId"] = Limit(m.UserId, 100),
            ["text"] = Limit(m.Text, MaxTextLength),
            ["time"] = m.Time,
            ["roles"] = RoleNames(m.Roles),
            ["canDelete"] = m.CanDelete,
            ["deleted"] = _hub?.IsDeleted(m) == true,
        };
        if (m.Amount is { Length: > 0 } amount) node["amount"] = Limit(amount, 40);
        return node;
    }

    private static JsonObject Notice(ChatModerationNotice n)
    {
        var node = new JsonObject
        {
            ["platform"] = n.Platform,
            ["kind"] = n.Kind switch
            {
                ChatModerationKind.MessageDeleted => "deleted",
                ChatModerationKind.UserBanned => "banned",
                _ => "timedOut",
            },
            ["time"] = n.Time,
            ["messageId"] = n.MessageId,
            ["userId"] = Limit(n.UserId, 100),
            ["user"] = Limit(n.User, 100),
            ["moderator"] = Limit(n.Moderator, 100),
        };
        if (n.DurationSeconds is { } s) node["durationSeconds"] = s;
        return node;
    }

    /// <summary>A mod log row for the portal: no device ids, member ids, platform references or hashes.</summary>
    internal static JsonObject Row(ModLogEntry e)
    {
        var node = new JsonObject
        {
            ["id"] = e.Id,
            ["incidentId"] = e.IncidentId,
            ["at"] = e.At,
            ["actorKind"] = e.ActorKind.ToString(),
            ["actorName"] = e.ActorName,
            ["platform"] = e.Platform,
            ["action"] = e.Action,
            ["targetUserId"] = e.TargetUserId,
            ["targetName"] = e.TargetName,
            ["messageText"] = Limit(e.MessageText, MaxTextLength),
            ["reason"] = e.Reason,
            ["result"] = e.Result.ToString(),
            ["error"] = e.Error,
            ["redacted"] = e.Redacted,
        };
        if (e.DurationRequestedSeconds is { } requested) node["requestedSeconds"] = requested;
        if (e.DurationAppliedSeconds is { } applied) node["appliedSeconds"] = applied;
        if (e.UndoneAt is { } undone) node["undoneAt"] = undone;
        if (e.Undoes is { } undoes) node["undoes"] = undoes;
        // Made through an identity link (the link itself is not sent; the incident groups the rows).
        if (e.ViaLinkId is not null && !IsLinkRow(e)) node["linked"] = true;
        return node;
    }

    /// <summary>A held message for the portal. Text only, like chat lines.</summary>
    private static JsonObject Held(HeldChatMessage h) => new()
    {
        ["platform"] = h.Platform,
        ["id"] = h.Id,
        ["userId"] = Limit(h.UserId, 100),
        ["user"] = Limit(h.UserName, 100),
        ["login"] = Limit(h.UserLogin, 100),
        ["text"] = Limit(h.Text, MaxTextLength),
        ["reason"] = Limit(h.Reason, 200),
        ["heldAt"] = h.HeldAt,
        ["status"] = h.Status.ToString(),
        ["decidedBy"] = Limit(h.DecidedBy, 100),
    };

    /// <summary>Shield Mode, or null while Cuebot does not know it yet.</summary>
    private static JsonObject? Shield(ShieldModeState? s)
    {
        if (s is null) return null;
        var node = new JsonObject { ["active"] = s.IsActive, ["moderator"] = Limit(s.ModeratorName, 100) };
        if (s.Since is { } since) node["since"] = since;
        return node;
    }

    private static JsonObject Suspicious(SuspiciousUserInfo s) => new()
    {
        ["level"] = s.Level.ToString(),
        ["user"] = Limit(s.UserName, 100),
        ["why"] = Limit(s.Why, 300),
        ["moderator"] = Limit(s.ModeratorName, 100),
    };

    private static JsonObject Unban(UnbanRequestInfo r) => new()
    {
        ["platform"] = r.Platform,
        ["id"] = r.Id,
        ["userId"] = Limit(r.UserId, 100),
        ["user"] = Limit(r.UserName, 100),
        ["login"] = Limit(r.UserLogin, 100),
        ["text"] = Limit(r.Text, MaxTextLength),
        ["createdAt"] = r.CreatedAt,
        ["status"] = r.Status.ToString(),
        ["resolvedBy"] = Limit(r.ResolvedBy, 100),
        ["resolutionText"] = Limit(r.ResolutionText, MaxReasonLength),
    };

    private static JsonObject Term(BlockedTermInfo t) => new()
    {
        ["platform"] = t.Platform,
        ["id"] = t.Id,
        ["text"] = Limit(t.Text, MaxTermLength),
    };

    private static JsonArray RoleNames(ChatRoles roles) => new([.. Enum.GetValues<ChatRoles>()
        .Where(r => r != ChatRoles.None && roles.HasFlag(r)).Select(r => (JsonNode)r.ToString())]);

    private JsonArray Platforms() => new([.. PlatformNames().Select(p => (JsonNode)p)]);

    private IReadOnlyList<string> PlatformNames() => _hub?.Platforms ?? [];

    // ---------------------------------------------------------------- helpers

    private static bool IsWebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static bool Matches(string platform, string filter) => filter.Length == 0 || Same(platform, filter);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static bool Bool(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int? Int(JsonObject o, string name) => o[name] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && double.IsFinite(d) =>
            (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue),
        JsonValue v when v.GetValueKind() == JsonValueKind.String && int.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject NotIncluded() => Error("Your role does not include this.");

    public void Dispose()
    {
        if (_hub is not null)
        {
            _hub.MessageReceived -= OnMessage;
            _hub.ModerationSeen -= OnModeration;
        }
        if (_moderation?.Log is { } modLog) modLog.Changed -= OnLogRow;
        if (_moderation is not null) _moderation.QueuesChanged -= OnQueuesChanged;
        UnsubscribeLinks();
        if (_moderation?.Permissions is PortalModerationPermissions) _moderation.Permissions = null;
        _push?.Unregister(ChatTopic);
        _push?.Unregister(LogTopic);
        _push?.Unregister(QueueTopic);
        _push?.Unregister(LinksTopic);
    }
}
