using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Users;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Chat;

/// <summary>
/// Identity links, notes and linked bans and timeouts in the mod portal (docs/UNIFIED_MOD_CHAT_PLAN.md, phases 4 and 5),
/// through <see cref="IdentityLinks"/> and <see cref="ModerationService"/> acting as the member. Every op is refused
/// while the streamer's Identity links setting is off. Accounts, names, chat roles and the messages attached as evidence
/// are read from Cuebot's own chat and link records, never taken from the device; a preview is kept here and the device
/// only says which of its rows are ticked. The "links" push topic tells devices that links, notes or the later-arrival
/// prompts changed, only for roles that may see links. Undoing "everything a member did in the last hour" stays with the
/// streamer and is not offered here.
/// </summary>
public sealed partial class PortalChat
{
    public const string LinksTopic = "links";
    public const int MaxNoteLength = 2000;
    public const int MaxLinkNoteLength = 500;
    public const int MaxEvidenceMessages = 20;
    public const int MaxCandidates = 100;

    private const string LinksOffMessage = "Identity links are turned off. The streamer can turn them on in Cuebot's chat settings.";
    private const string PreviewGone = "That preview has expired. Open Ban or Timeout again.";
    private const string IncidentGone = "Cuebot no longer has that action's details. Do it again from the user card, or undo it from the mod log.";
    private static readonly TimeSpan PreviewLife = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IncidentLife = TimeSpan.FromHours(24);
    private const int MaxPending = 500;

    private sealed class PendingPreview(LinkedPreview preview, Guid memberId, DateTimeOffset at)
    {
        public LinkedPreview Preview { get; } = preview;
        public Guid MemberId { get; } = memberId;
        public DateTimeOffset At { get; } = at;
        public bool Used { get; set; }
    }

    private sealed class PendingIncident(LinkedPreview preview, Guid memberId, string reason, DateTimeOffset at)
    {
        public LinkedPreview Preview { get; } = preview;
        public Guid MemberId { get; } = memberId;
        public string Reason { get; } = reason;
        public DateTimeOffset At { get; } = at;
        public HashSet<string> AwaitingManual { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingIncident> _incidents = new(StringComparer.Ordinal);
    private IdentityLinks? _subscribedLinks;

    private static bool IsLinkOp(string op) => op is "chat.person" or "chat.link.propose" or "chat.link.confirm" or "chat.link.reject"
        or "chat.link.unlinkPreview" or "chat.link.unlink" or "chat.link.verify" or "chat.note.add" or "chat.note.edit" or "chat.note.delete"
        or "chat.linked.preview" or "chat.linked.execute" or "chat.linked.manual" or "chat.linked.retry" or "chat.incident.undo"
        or "chat.laterArrivals";

    private bool LinksOn => _moderation?.Links is { Enabled: true };

    private static bool IsLinkRow(ModLogEntry e) => ModLog.LinkActions.Contains(e.Action);

    /// <summary>Rows about links are for roles that may see links, while links are on; every other row for everyone with the log.</summary>
    private bool MaySeeRow(PortalRole? role, ModLogEntry e) => !IsLinkRow(e) || (LinksOn && PortalModerationPermissions.CanViewLinks(role));

    private JsonObject LinksCapabilities(PortalRole? role)
    {
        if (!LinksOn) return new JsonObject { ["enabled"] = false };
        return new JsonObject
        {
            ["enabled"] = true,
            ["codeMinutes"] = IdentityLinks.CodeMinutes,
            ["rights"] = Rights(role),
        };
    }

    private static JsonObject Rights(PortalRole? role) => new()
    {
        ["view"] = PortalModerationPermissions.CanViewLinks(role),
        ["propose"] = PortalPolicy.Has(role, PortalPermission.ProposeLinks),
        ["confirm"] = PortalPolicy.Has(role, PortalPermission.ConfirmLinks),
        ["notesRead"] = PortalPolicy.Has(role, PortalPermission.NotesRead),
        ["notesAdd"] = PortalPolicy.Has(role, PortalPermission.NotesAdd),
        ["notesDelete"] = PortalPolicy.Has(role, PortalPermission.NotesDelete),
        ["ban"] = PortalPolicy.Has(role, PortalPermission.ChatBan),
        ["timeout"] = PortalPolicy.Has(role, PortalPermission.ChatDeleteTimeout),
        ["linkedBans"] = PortalPolicy.Has(role, PortalPermission.LinkedBans),
        ["linkedTimeouts"] = PortalPolicy.Has(role, PortalPermission.LinkedTimeouts),
    };

    private async Task<JsonObject> HandleLinksAsync(PortalMember member, PortalRole? role, string op, JsonObject request, CancellationToken ct)
    {
        if (!PortalPolicy.Has(role, PortalPermission.ViewChat)) return NotIncluded();
        if (_moderation is null || _moderation.Links is not { Enabled: true } links) return Error(LinksOffMessage);
        var actor = ModActor.Member(member.Id.ToString(), member.Name, Str(request, PortalSignInHandler.DeviceKey) is { Length: > 0 } d ? d : null);
        return op switch
        {
            "chat.person" => Person(role, links, actor, request),
            "chat.link.propose" => await ProposeAsync(links, actor, request, ct).ConfigureAwait(false),
            "chat.link.confirm" => Result(links.Confirm(LinkIdOf(request), actor), actor),
            "chat.link.reject" => Result(links.Reject(LinkIdOf(request), actor, Limit(Str(request, "reason").Trim(), MaxReasonLength)), actor),
            "chat.link.unlinkPreview" => UnlinkPreview(role, links, actor, request),
            "chat.link.unlink" => await UnlinkAsync(role, actor, request, ct).ConfigureAwait(false),
            "chat.link.verify" => await VerifyAsync(links, actor, request, ct).ConfigureAwait(false),
            "chat.note.add" => AddNote(links, actor, request),
            "chat.note.edit" => Str(request, "text").Trim().Length > MaxNoteLength
                ? Error($"Keep the note under {MaxNoteLength} characters.")
                : NoteAnswer(links.EditNote(Limit(Str(request, "noteId"), 100), Str(request, "text"), actor)),
            "chat.note.delete" => NoteAnswer(links.DeleteNote(Limit(Str(request, "noteId"), 100), actor)),
            "chat.linked.preview" => await LinkedPreviewAsync(member, role, actor, request, ct).ConfigureAwait(false),
            "chat.linked.execute" => await LinkedExecuteAsync(member, actor, request, ct).ConfigureAwait(false),
            "chat.linked.manual" => LinkedManual(member, role, actor, request),
            "chat.linked.retry" => await LinkedRetryAsync(member, actor, request, ct).ConfigureAwait(false),
            "chat.incident.undo" => await UndoIncidentAsync(role, actor, request, ct).ConfigureAwait(false),
            "chat.laterArrivals" => await LaterArrivalsAsync(role, actor, request, ct).ConfigureAwait(false),
            _ => Error($"Unknown request '{op}'."),
        };
    }

    // ---------------------------------------------------------------- the person

    /// <summary>
    /// A user card's linked accounts (with how sure each link is, from the account shown), the evidence, notes on this
    /// account and the linked ones, same-name hints, and the recent chatters a mod can pick for "Same person as...".
    /// </summary>
    private JsonObject Person(PortalRole? role, IdentityLinks links, ModActor actor, JsonObject request)
    {
        if (PortalModerationPermissions.RefuseLink(role, LinkOperation.View) is { } no) return Error(no);
        var platform = Str(request, "platform");
        var userId = Str(request, "userId");
        if (platform.Length == 0 || userId.Length == 0) return Error("Pick someone in the chat first.");
        var key = IdentityLinks.ActorKey(actor);
        var person = links.PersonOf(platform, userId);
        var me = person?.Account(LinkPlatforms.Canon(platform), userId);
        var name = LastMessageOf(platform, userId)?.User ?? me?.Name ?? "";
        var body = new JsonObject
        {
            ["platform"] = platform,
            ["userId"] = userId,
            ["name"] = Limit(name, 100),
            ["rights"] = Rights(role),
            ["codeMinutes"] = IdentityLinks.CodeMinutes,
        };
        if (person is not null && me is not null)
        {
            body["person"] = new JsonObject
            {
                ["note"] = Limit(person.Note, MaxLinkNoteLength),
                ["accounts"] = new JsonArray([.. person.Accounts.OrderBy(a => a.LinkId == me.LinkId ? 0 : 1)
                    .ThenByDescending(a => IdentityLinks.Between(me, a)).Select(a => (JsonNode)Account(a, me, key))]),
                ["evidence"] = new JsonArray([.. person.Evidence.OrderByDescending(e => e.At).Take(50).Select(e => (JsonNode)Evidence(e))]),
            };
        }
        if (PortalPolicy.Has(role, PortalPermission.NotesRead))
            body["notes"] = new JsonArray([.. links.NotesFor(platform, userId, actor).Take(100).Select(n => (JsonNode)Note(n, key))]);
        body["hints"] = new JsonArray([.. (name.Length > 0 ? links.SameNameHints(platform, userId, name) : [])
            .Select(h => (JsonNode)new JsonObject { ["platform"] = h.Platform, ["userId"] = Limit(h.UserId, 100), ["name"] = Limit(h.UserName, 100) })]);
        if (PortalPolicy.Has(role, PortalPermission.ProposeLinks))
            body["candidates"] = new JsonArray([.. Candidates(platform, userId, person).Select(c => (JsonNode)c)]);
        body["laterArrivals"] = new JsonArray([.. _moderation!.LaterArrivals.Where(p => person is not null && p.PersonId == person.Id)
            .Select(p => (JsonNode)Later(p))]);
        return Ok(body);
    }

    /// <summary>Recent chatters other than this account and its person, newest first, without the broadcaster, mods and VIPs.</summary>
    private IEnumerable<JsonObject> Candidates(string platform, string userId, IdentityPerson? person)
    {
        if (_hub is null) yield break;
        var seen = new HashSet<(string, string)>();
        var count = 0;
        foreach (var m in _hub.RecentMessages().Reverse())
        {
            if (m.UserId.Length == 0 || (Same(m.Platform, platform) && m.UserId == userId)) continue;
            if (!seen.Add((m.Platform.ToLowerInvariant(), m.UserId))) continue;
            if (person?.Account(LinkPlatforms.Canon(m.Platform), m.UserId) is not null) continue;
            if ((m.Roles & (ChatRoles.Broadcaster | ChatRoles.Moderator | ChatRoles.Vip)) != 0) continue;
            yield return new JsonObject
            {
                ["platform"] = m.Platform,
                ["userId"] = Limit(m.UserId, 100),
                ["name"] = Limit(m.User, 100),
                ["messages"] = new JsonArray([.. _hub.RecentMessages().Where(x => Same(x.Platform, m.Platform) && x.UserId == m.UserId).TakeLast(5)
                    .Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["text"] = Limit(x.Text, 300), ["time"] = x.Time })]),
            };
            if (++count >= MaxCandidates) yield break;
        }
    }

    private static JsonObject Account(IdentityAccount a, IdentityAccount? me, string key)
    {
        var isThis = me is not null && a.LinkId == me.LinkId;
        var between = me is null || isThis ? a.Status : IdentityLinks.Between(me, a);
        return new JsonObject
        {
            ["linkId"] = a.LinkId,
            ["platform"] = a.Platform,
            ["userId"] = Limit(a.UserId, 100),
            ["name"] = Limit(a.Name, 100),
            ["nameAtLink"] = Limit(a.NameAtLink, 100),
            ["isThis"] = isThis,
            ["status"] = a.Status.ToString(),
            ["between"] = between.ToString(),
            ["linkText"] = IdentityLinks.Describe(a, between),
            ["addedBy"] = Limit(a.AddedBy, 100),
            ["addedAt"] = a.AddedAt,
            ["statusAt"] = a.StatusAt,
            ["mine"] = a.AddedByKey == key,
            ["needsStreamer"] = a.NeedsStreamer,
            ["confirmedBy"] = new JsonArray([.. a.ConfirmedBy.Select(c => (JsonNode)Limit(c.By, 100))]),
        };
    }

    private static JsonObject Evidence(LinkEvidence e) => new()
    {
        ["kind"] = e.Kind,
        ["quote"] = Limit(e.Quote, MaxLinkNoteLength),
        ["addedBy"] = Limit(e.AddedBy, 100),
        ["at"] = e.At,
        ["messages"] = e.MessageIds.Count,
    };

    private static JsonObject Note(ModNote n, string key) => new()
    {
        ["id"] = n.Id,
        ["platform"] = n.Platform,
        ["userId"] = Limit(n.UserId, 100),
        ["text"] = Limit(n.Text, MaxNoteLength),
        ["author"] = Limit(n.Author, 100),
        ["mine"] = n.AuthorMemberId == key,
        ["createdAt"] = n.CreatedAt,
        ["editedAt"] = n.EditedAt,
    };

    // ---------------------------------------------------------------- links

    /// <summary>
    /// "Same person as...": both accounts must be ones Cuebot has seen (recent chat, an existing link, or a same-name
    /// hint); their names and chat roles come from there. Attached messages are looked up in the recent chat and must be
    /// from one of the two accounts; their text is the evidence.
    /// </summary>
    private async Task<JsonObject> ProposeAsync(IdentityLinks links, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (links.Refusal(actor, LinkOperation.Propose) is { } refused) return Error(refused);
        if (Known(links, Str(request, "platform"), Str(request, "userId")) is not { } from)
            return Error("Cuebot has not seen that account in the recent chat. Open the user card from a chat line.");
        var hints = links.SameNameHints(from.Account.Platform, from.Account.UserId, from.Account.UserName);
        var otherPlatform = Str(request, "otherPlatform");
        var otherUserId = Str(request, "otherUserId");
        var to = Known(links, otherPlatform, otherUserId);
        if (to is null && hints.FirstOrDefault(h => IdentityLinks.SamePlatform(h.Platform, otherPlatform) && h.UserId == otherUserId) is { } hint)
            to = (hint, ChatRoles.None);
        if (to is null) return Error("Cuebot has not seen the other account in the recent chat. Pick it from the list.");

        var note = Str(request, "note").Trim();
        if (note.Length > MaxLinkNoteLength) return Error($"Keep the note under {MaxLinkNoteLength} characters.");
        var evidence = new List<LinkEvidenceInput>();
        foreach (var id in Strings(request, "messageIds").Distinct(StringComparer.Ordinal))
        {
            if (evidence.Count >= MaxEvidenceMessages) return Error($"Attach at most {MaxEvidenceMessages} messages.");
            var message = _hub?.RecentMessages().LastOrDefault(m => m.Id == id
                && ((Same(m.Platform, from.Account.Platform) && m.UserId == from.Account.UserId)
                    || (Same(m.Platform, to.Value.Account.Platform) && m.UserId == to.Value.Account.UserId)));
            if (message is null) return Error("A message you attached is no longer in Cuebot's recent chat, or is not from these two accounts.");
            evidence.Add(new LinkEvidenceInput(LinkEvidenceKinds.ViewerSaid, Limit($"{message.Platform}, {message.User}: {message.Text}", MaxLinkNoteLength))
            {
                MessageIds = [message.Id],
            });
        }
        var choice = Str(request, "choice") switch
        {
            "Merge" => LinkConflictChoice.Merge,
            "Move" => LinkConflictChoice.Move,
            _ => LinkConflictChoice.Ask,
        };
        var result = await links.ProposeAsync(from.Account, to.Value.Account, actor, note, evidence, choice, from.Roles, to.Value.Roles, ct)
            .ConfigureAwait(false);
        return Result(result, actor);
    }

    /// <summary>An account Cuebot knows: from the recent chat (name and every role it showed), else from a link record.</summary>
    private (ViewerRef Account, ChatRoles Roles)? Known(IdentityLinks links, string platform, string userId)
    {
        if (platform.Length == 0 || userId.Length == 0 || userId.Length > 200) return null;
        if (LastMessageOf(platform, userId) is { } m) return (new ViewerRef(m.Platform, m.UserId, m.User), RolesOf(m.Platform, m.UserId));
        if (links.PersonOf(platform, userId)?.Account(LinkPlatforms.Canon(platform), userId) is { } a)
            return (new ViewerRef(a.Platform, a.UserId, a.Name), RolesOf(platform, userId));
        return null;
    }

    private ChatMessage? LastMessageOf(string platform, string userId) =>
        _hub?.RecentMessages().LastOrDefault(m => Same(m.Platform, platform) && m.UserId == userId);

    private static string LinkIdOf(JsonObject request) => Limit(Str(request, "linkId"), 100);

    private static JsonObject Result(IdentityLinkResult r, ModActor actor)
    {
        var key = IdentityLinks.ActorKey(actor);
        var answer = new JsonObject { ["ok"] = r.Ok, ["message"] = r.Message };
        if (!r.Ok) answer["error"] = r.Message;
        if (r.NeedsChoice && r.Conflict is { } conflict)
        {
            answer["needsChoice"] = true;
            answer["conflict"] = new JsonArray([.. conflict.Accounts.Select(a => (JsonNode)Account(a, null, key))]);
        }
        return answer;
    }

    /// <summary>What unlinking would reverse: the actions made through the link that are still in force.</summary>
    private JsonObject UnlinkPreview(PortalRole? role, IdentityLinks links, ModActor actor, JsonObject request)
    {
        var linkId = LinkIdOf(request);
        var dissolve = Bool(request, "dissolve");
        var account = links.PersonOfLink(linkId)?.AccountByLink(linkId);
        if (account is null) return Error("That link no longer exists.");
        var own = account.Status == LinkStatus.Suggested && account.AddedByKey == IdentityLinks.ActorKey(actor) && !dissolve;
        if (!own && links.Refusal(actor, LinkOperation.Unlink) is { } refused) return Error(refused);
        if (_moderation!.PreviewUnlink(linkId, dissolve, Bool(request, "wrong")) is not { } preview) return Error("That link no longer exists.");
        var key = IdentityLinks.ActorKey(actor);
        return Ok(new JsonObject
        {
            ["leaving"] = new JsonArray([.. preview.Leaving.Select(a => (JsonNode)Account(a, null, key))]),
            ["inForce"] = new JsonArray([.. preview.InForce.Select(r => (JsonNode)Row(r))]),
            ["question"] = preview.Question,
            ["reverseByDefault"] = preview.ReverseByDefault,
            ["canReverse"] = PortalPolicy.Has(role, PortalPermission.ChatBan),
        });
    }

    private async Task<JsonObject> UnlinkAsync(PortalRole? role, ModActor actor, JsonObject request, CancellationToken ct)
    {
        var reverse = Strings(request, "reverse").Take(200).ToArray();
        if (reverse.Length > 0 && !PortalPolicy.Has(role, PortalPermission.ChatBan))
            return Error("Your role does not include lifting bans. Untick the actions to reverse, or ask someone who can.");
        var reason = Str(request, "reason").Trim();
        if (reason.Length > MaxReasonLength) return Error($"Keep the reason under {MaxReasonLength} characters.");
        var (unlink, reversed) = await _moderation!.UnlinkAsync(LinkIdOf(request), actor, Bool(request, "dissolve"), Bool(request, "wrong"), reason,
            reverse, ct).ConfigureAwait(false);
        var answer = Result(unlink, actor);
        if (reversed is not null) answer["reversed"] = Undo(reversed);
        return answer;
    }

    /// <summary>Starts a <c>!link</c> verification: the one-time code goes to this mod only, who sends it to the viewer privately.</summary>
    private async Task<JsonObject> VerifyAsync(IdentityLinks links, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (links.Refusal(actor, LinkOperation.Propose) is { } refused) return Error(refused);
        if (Known(links, Str(request, "platform"), Str(request, "userId")) is not { } known)
            return Error("Cuebot has not seen that account in the recent chat. Open the user card from a chat line.");
        var (verification, message) = await links.StartVerificationAsync(known.Account, actor, known.Roles, ct).ConfigureAwait(false);
        if (verification is null) return Error(message);
        _log?.Info(LogSource, $"{actor.Name} started a !link verification on {known.Account.Platform}.");
        return Ok(new JsonObject
        {
            ["code"] = verification.Code,
            ["expiresAt"] = verification.ExpiresAt,
            ["minutes"] = IdentityLinks.CodeMinutes,
            ["instructions"] = verification.Instructions,
        });
    }

    // ---------------------------------------------------------------- notes

    private JsonObject AddNote(IdentityLinks links, ModActor actor, JsonObject request)
    {
        if (links.Refusal(actor, LinkOperation.AddNote) is { } refused) return Error(refused);
        var text = Str(request, "text").Trim();
        if (text.Length > MaxNoteLength) return Error($"Keep the note under {MaxNoteLength} characters.");
        if (Known(links, Str(request, "platform"), Str(request, "userId")) is not { } known)
            return Error("Cuebot has not seen that account in the recent chat. Open the user card from a chat line.");
        var (note, message) = links.AddNote(known.Account, text, actor);
        if (note is null) return Error(message);
        return Ok(new JsonObject { ["message"] = message, ["note"] = Note(note, IdentityLinks.ActorKey(actor)) });
    }

    private static JsonObject NoteAnswer((bool Ok, string Message) r) => r.Ok ? Ok(new JsonObject { ["message"] = r.Message }) : Error(r.Message);

    // ---------------------------------------------------------------- linked bans and timeouts

    /// <summary>
    /// The dry run of a ban or timeout on an account that may be linked (section 7.4): nothing is sent. The command is
    /// built from Cuebot's own chat like "chat.act"; the preview is kept here for 30 minutes and only this member can
    /// carry it out. A role without the linked right (or without seeing links) gets only the account clicked.
    /// </summary>
    private async Task<JsonObject> LinkedPreviewAsync(PortalMember member, PortalRole? role, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (!Enum.TryParse<ModerationAction>(Str(request, "action"), ignoreCase: true, out var action)
            || action is not (ModerationAction.Ban or ModerationAction.Timeout))
            return Error("Only bans and timeouts have a linked preview.");
        if (!PortalPolicy.Has(role, PortalModerationPermissions.For(action))) return NotIncluded();
        var platform = Str(request, "platform");
        if (platform.Length == 0) return Error("Say which platform.");
        var copy = (JsonObject)request.DeepClone();
        copy.Remove("reason");
        var (command, problem) = CommandFor(platform, action, copy);
        if (command is null) return Error(problem ?? "That request was not understood.");
        if (command.TargetUserId.Length == 0) return Error("Pick who this is for.");

        var preview = await _moderation!.BuildPreviewAsync(command, actor, ct).ConfigureAwait(false);
        string? linkedNote = null;
        var linkedRight = action == ModerationAction.Ban ? PortalPermission.LinkedBans : PortalPermission.LinkedTimeouts;
        if (preview.Rows.Count > 1 && (!PortalPolicy.Has(role, linkedRight) || !PortalModerationPermissions.CanViewLinks(role)))
        {
            linkedNote = $"This account is linked to other accounts. Your role does not include {(action == ModerationAction.Ban ? "bans" : "timeouts")} " +
                         "through linked accounts, so only this account is shown.";
            preview = preview with { Rows = [preview.Rows[0]] };
        }

        var now = DateTimeOffset.UtcNow;
        lock (_pendingGate)
        {
            Prune(now);
            _previews[preview.Id] = new PendingPreview(preview, member.Id, now);
        }
        var answer = new JsonObject
        {
            ["previewId"] = preview.Id,
            ["action"] = action.ToString(),
            ["platform"] = command.Platform,
            ["name"] = Limit(command.TargetName, 100),
            ["linked"] = preview.IsLinked,
            ["rows"] = new JsonArray([.. preview.Rows.Select(r => (JsonNode)PreviewRow(r))]),
            ["defaultTicked"] = new JsonArray([.. preview.DefaultTicked.Select(t => (JsonNode)t)]),
            ["maxReason"] = MaxReasonLength,
        };
        if (command.Duration is { } duration) answer["requestedSeconds"] = (int)duration.TotalSeconds;
        if (linkedNote is not null) answer["linkedNote"] = linkedNote;
        return Ok(answer);
    }

    private static JsonObject PreviewRow(LinkedPreviewRow r)
    {
        var node = new JsonObject
        {
            ["rowId"] = r.RowId,
            ["platform"] = r.Platform,
            ["userId"] = Limit(r.UserId, 100),
            ["name"] = Limit(r.Name, 100),
            ["isOrigin"] = r.IsOrigin,
            ["status"] = r.Status?.ToString(),
            ["linkText"] = r.LinkText,
            ["state"] = r.State.ToString(),
            ["willHappen"] = r.WillHappen,
            ["quotaCost"] = r.QuotaCost,
            ["notes"] = r.Notes,
            ["ticked"] = r.Ticked,
            ["canTick"] = r.CanTick,
            ["byHand"] = r.ByHand,
            ["alreadyActioned"] = r.AlreadyActioned,
        };
        if (r.AppliedDuration is { } applied) node["appliedSeconds"] = (int)Math.Ceiling(applied.TotalSeconds);
        if (r.QuotaLeft is { } left) node["quotaLeft"] = left;
        if (r.WebsiteUrl is { Length: > 0 } url && IsWebLink(url)) node["websiteUrl"] = url;
        return node;
    }

    /// <summary>
    /// Carries out a kept preview with the rows the mod ticked (only rows that may be ticked count, and a by-hand row
    /// through a suggested link never does for a ban), a required reason and, for a permanent ban on more than one
    /// platform, the second confirmation. Each platform runs on its own; the answer has a result per row.
    /// </summary>
    private async Task<JsonObject> LinkedExecuteAsync(PortalMember member, ModActor actor, JsonObject request, CancellationToken ct)
    {
        var reason = Str(request, "reason").Trim();
        if (reason.Length == 0) return Error("A reason is required.");
        if (reason.Length > MaxReasonLength) return Error($"Keep the reason under {MaxReasonLength} characters.");
        var previewId = Limit(Str(request, "previewId"), 100);
        PendingPreview? pending;
        lock (_pendingGate)
        {
            Prune(DateTimeOffset.UtcNow);
            pending = _previews.GetValueOrDefault(previewId);
            if (pending is null || pending.MemberId != member.Id || pending.Used) return Error(PreviewGone);
        }
        var preview = pending.Preview;
        var ban = preview.Command.Action == ModerationAction.Ban;
        var wanted = Strings(request, "ticked").ToHashSet(StringComparer.Ordinal);
        var ticked = preview.Rows.Where(r => wanted.Contains(r.RowId)
                                             && (r.CanTick || (r.ByHand && (r.IsOrigin || r.Status >= LinkStatus.Confirmed || !ban))))
            .Select(r => r.RowId).ToArray();

        var result = await _moderation!.ExecuteLinkedAsync(new LinkedExecution(preview, ticked, reason) { Confirmed = Bool(request, "confirmed") }, actor, ct)
            .ConfigureAwait(false);
        if (result.Refused is { } refused) return Error(refused);
        if (result.NeedsConfirmation)
            return new JsonObject { ["ok"] = false, ["needsConfirmation"] = true, ["confirmText"] = result.ConfirmText, ["error"] = result.ConfirmText };

        var incident = new PendingIncident(preview, member.Id, reason, DateTimeOffset.UtcNow);
        foreach (var row in result.Rows)
        {
            if (row.AwaitingManual) incident.AwaitingManual.Add(row.Row.RowId);
            else if (row.Result == ModResult.Failed) incident.Failed.Add(row.Row.RowId);
        }
        lock (_pendingGate)
        {
            pending.Used = true;
            _incidents[result.IncidentId] = incident;
        }
        _log?.Info(LogSource, $"{member.Name}: {result.Summary}");
        return Ok(new JsonObject
        {
            ["incidentId"] = result.IncidentId,
            ["summary"] = result.Summary,
            ["partial"] = result.Partial,
            ["rows"] = new JsonArray([.. result.Rows.Select(r => (JsonNode)RowResult(r))]),
        });
    }

    private static JsonObject RowResult(LinkedRowResult r)
    {
        var node = new JsonObject
        {
            ["rowId"] = r.Row.RowId,
            ["platform"] = r.Row.Platform,
            ["name"] = Limit(r.Row.Name, 100),
            ["result"] = r.Result.ToString(),
            ["message"] = r.Message,
            ["awaitingManual"] = r.AwaitingManual,
            ["canRetry"] = r.Result == ModResult.Failed,
        };
        if ((r.WebsiteUrl ?? r.Row.WebsiteUrl) is { Length: > 0 } url && IsWebLink(url)) node["websiteUrl"] = url;
        if (r.Entry is { } entry) node["row"] = Row(entry);
        return node;
    }

    /// <summary>"I did it": records a by-hand row of this member's incident as done by hand.</summary>
    private JsonObject LinkedManual(PortalMember member, PortalRole? role, ModActor actor, JsonObject request)
    {
        if (FindIncident(member, request, out var incidentId) is not { } incident) return Error(IncidentGone);
        var rowId = Limit(Str(request, "rowId"), 100);
        var row = incident.Preview.Rows.FirstOrDefault(r => r.RowId == rowId);
        lock (_pendingGate)
            if (row is null || !incident.AwaitingManual.Contains(rowId)) return Error("That row is not waiting to be done by hand.");
        var action = incident.Preview.Command.Action;
        if (!PortalPolicy.Has(role, PortalModerationPermissions.For(action))
            || (!row.IsOrigin && !PortalPolicy.Has(role, action == ModerationAction.Ban ? PortalPermission.LinkedBans : PortalPermission.LinkedTimeouts)))
            return NotIncluded();
        if (_moderation!.RecordManual(incidentId, incident.Preview, rowId, incident.Reason, actor) is not { } entry)
            return Error("This Cuebot keeps no mod log.");
        lock (_pendingGate) incident.AwaitingManual.Remove(rowId);
        return Ok(new JsonObject { ["message"] = $"Recorded: done by hand on {row.Platform}.", ["row"] = Row(entry) });
    }

    /// <summary>Tries a failed row of this member's incident again.</summary>
    private async Task<JsonObject> LinkedRetryAsync(PortalMember member, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (FindIncident(member, request, out var incidentId) is not { } incident) return Error(IncidentGone);
        var rowId = Limit(Str(request, "rowId"), 100);
        lock (_pendingGate)
            if (!incident.Failed.Contains(rowId)) return Error("That row did not fail, so there is nothing to try again.");
        var result = await _moderation!.RetryRowAsync(incidentId, incident.Preview, rowId, incident.Reason, actor, ct).ConfigureAwait(false);
        if (result.Result != ModResult.Failed)
            lock (_pendingGate) incident.Failed.Remove(rowId);
        var answer = RowResult(result);
        answer["ok"] = result.Result == ModResult.Ok;
        if (result.Result != ModResult.Ok) answer["error"] = result.Message;
        return answer;
    }

    private PendingIncident? FindIncident(PortalMember member, JsonObject request, out string incidentId)
    {
        incidentId = Limit(Str(request, "incidentId"), 100);
        lock (_pendingGate)
        {
            Prune(DateTimeOffset.UtcNow);
            return _incidents.GetValueOrDefault(incidentId) is { } found && found.MemberId == member.Id ? found : null;
        }
    }

    /// <summary>
    /// Undo incident (section 7.4, step 7): lifts every ban and timeout of the incident still in force. Lifting needs
    /// the ban and unban right; each lift goes through <see cref="ModerationService"/> as this member and is logged.
    /// </summary>
    private async Task<JsonObject> UndoIncidentAsync(PortalRole? role, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (!PortalPolicy.Has(role, PortalPermission.ChatBan)) return Error("Your role does not include lifting bans and timeouts.");
        var incidentId = Limit(Str(request, "incidentId"), 100);
        if (incidentId.Length == 0) return Error("Pick the action to undo in the mod log.");
        if (_moderation!.IncidentRows(incidentId).Count == 0) return Error("That incident is not in the mod log.");
        if (!_moderation.CanUndoIncident(incidentId)) return Error("Nothing in that incident is still in force.");
        var result = await _moderation.UndoIncidentAsync(incidentId, actor, ct).ConfigureAwait(false);
        if (result.Refused is { } refused) return Error(refused);
        var answer = Undo(result);
        answer["ok"] = true;
        return answer;
    }

    private static JsonObject Undo(IncidentUndoResult r) => new()
    {
        ["summary"] = r.Summary,
        ["rows"] = new JsonArray([.. r.Rows.Select(u =>
        {
            var node = new JsonObject
            {
                ["platform"] = u.Row.Platform,
                ["name"] = Limit(u.Row.TargetName, 100),
                ["action"] = u.Row.Action,
                ["result"] = u.Result.ToString(),
                ["message"] = u.Message,
            };
            if (u.WebsiteUrl is { Length: > 0 } url && IsWebLink(url)) node["websiteUrl"] = url;
            return (JsonNode)node;
        })]),
        ["cannotUndo"] = new JsonArray([.. r.CannotUndo.Select(c => (JsonNode)c)]),
    };

    // ---------------------------------------------------------------- later arrivals

    /// <summary>
    /// "This person is banned on Twitch and Kick. Apply here too?": list (roles that see links), apply (a ban through
    /// the link: ban and linked-ban rights, reason required) or dismiss (ban right).
    /// </summary>
    private async Task<JsonObject> LaterArrivalsAsync(PortalRole? role, ModActor actor, JsonObject request, CancellationToken ct)
    {
        if (PortalModerationPermissions.RefuseLink(role, LinkOperation.View) is { } no) return Error(no);
        var moderation = _moderation!;
        var id = Limit(Str(request, "id"), 100);
        switch (Str(request, "mode"))
        {
            case "" or "list":
                return Ok(new JsonObject { ["items"] = LaterList() });
            case "apply":
            {
                if (!PortalPolicy.Has(role, PortalPermission.ChatBan) || !PortalPolicy.Has(role, PortalPermission.LinkedBans))
                    return Error("Your role does not include bans through linked accounts.");
                var reason = Str(request, "reason").Trim();
                if (reason.Length == 0) return Error("A reason is required.");
                if (reason.Length > MaxReasonLength) return Error($"Keep the reason under {MaxReasonLength} characters.");
                if (moderation.LaterArrivals.All(p => p.Id != id)) return Error("That was already answered.");
                var result = await moderation.ApplyLaterArrivalAsync(id, actor, reason, ct).ConfigureAwait(false);
                var answer = new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["items"] = LaterList() };
                if (!result.Ok) answer["error"] = result.Message;
                if (result.Entry is { } entry) answer["row"] = Row(entry);
                return answer;
            }
            case "dismiss":
                if (!PortalPolicy.Has(role, PortalPermission.ChatBan)) return NotIncluded();
                return moderation.DismissLaterArrival(id)
                    ? Ok(new JsonObject { ["message"] = "Dismissed.", ["items"] = LaterList() })
                    : Error("That was already answered.");
            default:
                return Error("Say list, apply or dismiss.");
        }
    }

    private JsonArray LaterList() => new([.. (_moderation?.LaterArrivals ?? []).Select(p => (JsonNode)Later(p))]);

    private static JsonObject Later(LaterArrivalPrompt p) => new()
    {
        ["id"] = p.Id,
        ["platform"] = p.Account.Platform,
        ["userId"] = Limit(p.Account.UserId, 100),
        ["name"] = Limit(p.Account.UserName, 100),
        ["bannedOn"] = new JsonArray([.. p.BannedOn.Select(b => (JsonNode)b)]),
        ["at"] = p.At,
        ["text"] = p.Text,
    };

    // ---------------------------------------------------------------- push and housekeeping

    private void SubscribeLinks()
    {
        if (_moderation is null) return;
        _moderation.LaterArrivalsChanged += OnLaterArrivals;
        if (_moderation.Links is { } links)
        {
            _subscribedLinks = links;
            links.Changed += OnLinksChanged;
        }
    }

    private void UnsubscribeLinks()
    {
        if (_moderation is not null) _moderation.LaterArrivalsChanged -= OnLaterArrivals;
        if (_subscribedLinks is not null) _subscribedLinks.Changed -= OnLinksChanged;
        _subscribedLinks = null;
    }

    /// <summary>Links or notes changed: devices that may see links reload the user card they show.</summary>
    private void OnLinksChanged(IdentityLinkChange change)
    {
        if (!LinksOn) return;
        Publish(LinksTopic, null, new JsonObject { ["type"] = "changed", ["kind"] = change.Kind }, PortalModerationPermissions.CanViewLinks);
    }

    private void OnLaterArrivals()
    {
        if (!LinksOn) return;
        Publish(LinksTopic, null, new JsonObject { ["type"] = "later", ["items"] = LaterList() }, PortalModerationPermissions.CanViewLinks);
    }

    /// <summary>Drops old previews and incidents; keeps at most <see cref="MaxPending"/> of each. Call with the gate held.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (id, p) in _previews.ToArray())
            if (now - p.At > PreviewLife) _previews.Remove(id);
        foreach (var (id, i) in _incidents.ToArray())
            if (now - i.At > IncidentLife) _incidents.Remove(id);
        foreach (var (id, _) in _previews.OrderBy(p => p.Value.At).Take(Math.Max(0, _previews.Count - MaxPending)).ToArray()) _previews.Remove(id);
        foreach (var (id, _) in _incidents.OrderBy(p => p.Value.At).Take(Math.Max(0, _incidents.Count - MaxPending)).ToArray()) _incidents.Remove(id);
    }

    private static IEnumerable<string> Strings(JsonObject o, string name) =>
        o[name] is JsonArray a
            ? a.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>())
                .Where(s => s.Length is > 0 and <= 200).Take(500)
            : [];
}
