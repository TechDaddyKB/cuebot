using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.Server.Api;

/// <summary>
/// The combined chat for the companion app's Chat tab: recent messages (with what was deleted), sending as the
/// broadcaster or the bot account, what each moderation tool does per platform, moderating, and the mod log. Every call
/// comes with the REST API token, which only the streamer's own paired devices hold, so moderation runs as
/// <see cref="ModActor.Streamer"/>; mods use the mod portal, with its own per-member rights.
/// </summary>
public sealed partial class ApiOperations
{
    /// <summary>Timeout lengths the Chat tab offers, in seconds (1 min, 10 min, 1 h, 1 day, 1 week).</summary>
    internal static readonly int[] TimeoutPresets = [60, 600, 3600, 86400, 604800];

    private ChatHub ChatHub => services.Chat ?? throw ApiException.Unavailable("Chat is not available");
    private ModerationService Moderation => services.Moderation ?? throw ApiException.Unavailable("Chat moderation is not available");

    // ---- Reading -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The latest <paramref name="limit"/> messages (oldest first, each with <c>deleted</c>), the activity feed, the
    /// latest deletions, bans and timeouts, and which platforms can send (and send as the bot account).
    /// </summary>
    public JsonObject Chat(int limit = 50, string? platform = null)
    {
        var chat = ChatHub;
        limit = Math.Clamp(limit, 1, 500);
        bool Wanted(string p) => string.IsNullOrWhiteSpace(platform) || platform.Equals(ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(p, platform.Trim(), StringComparison.OrdinalIgnoreCase);
        var messages = chat.RecentMessages().Where(m => Wanted(m.Platform)).TakeLast(limit).ToList();
        var feed = chat.RecentFeed().Where(f => Wanted(f.Platform)).TakeLast(limit).ToList();
        var moderation = chat.RecentModeration().Where(n => Wanted(n.Platform)).TakeLast(limit).ToList();
        var sendable = chat.SendablePlatforms;
        return new JsonObject
        {
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)ChatMessageJson(m, chat.IsDeleted(m)))]),
            ["feed"] = new JsonArray([.. feed.Select(f => (JsonNode)new JsonObject
            {
                ["platform"] = f.Platform,
                ["kind"] = f.Kind.ToString(),
                ["user"] = f.User,
                ["title"] = f.Title,
                ["detail"] = f.Detail,
                ["amount"] = f.Amount,
                ["time"] = f.Time.ToString("o"),
            })]),
            ["moderation"] = new JsonArray([.. moderation.Select(n => (JsonNode)ModerationNoticeJson(n))]),
            ["platforms"] = CatalogJson.Strings(chat.Platforms),
            ["sendablePlatforms"] = CatalogJson.Strings(sendable),
            ["botPlatforms"] = CatalogJson.Strings(sendable.Where(chat.CanSendAsBot)),
        };
    }

    /// <summary>One chat line as the API and the event stream ("chat.received") send it. Text only: no markup.</summary>
    internal static JsonObject ChatMessageJson(ChatMessage m, bool deleted) => new()
    {
        ["platform"] = m.Platform,
        ["id"] = m.Id,
        ["user"] = m.User,
        ["userId"] = m.UserId,
        ["text"] = m.Text,
        ["time"] = m.Time.ToString("o"),
        ["roles"] = m.Roles.ToString(),
        ["color"] = m.Color,
        ["amount"] = m.Amount,
        ["channelId"] = m.ChannelId,
        ["canDelete"] = m.CanDelete,
        ["deleted"] = deleted,
    };

    /// <summary>A deletion, ban or timeout a platform reported ("chat.moderated" on the event stream).</summary>
    internal static JsonObject ModerationNoticeJson(ChatModerationNotice n) => new()
    {
        ["platform"] = n.Platform,
        ["kind"] = n.Kind switch
        {
            ChatModerationKind.MessageDeleted => "messageDeleted",
            ChatModerationKind.UserBanned => "userBanned",
            _ => "userTimedOut",
        },
        ["time"] = n.Time.ToString("o"),
        ["messageId"] = n.MessageId,
        ["userId"] = n.UserId,
        ["user"] = n.User,
        ["durationSeconds"] = n.DurationSeconds,
        ["moderator"] = n.Moderator,
        ["reason"] = n.Reason,
    };

    // ---- Sending -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends <paramref name="message"/> to one platform or "All". <paramref name="sendAs"/>: "default" (the platform's
    /// setting), "broadcaster" or "bot" (409 when no bot account is signed in where it is sent).
    /// </summary>
    public async Task<JsonObject> SendChatAsync(string? platform, string? message, CancellationToken ct = default, string? sendAs = null)
    {
        var chat = ChatHub;
        var text = message ?? "";
        if (text.Trim().Length == 0) throw ApiException.BadRequest("text is required");
        var target = string.IsNullOrWhiteSpace(platform) ? ChatHub.AllPlatforms : platform.Trim();
        var options = (sendAs?.Trim().ToLowerInvariant() ?? "") switch
        {
            "" or "default" => ChatSendOptions.Default,
            "broadcaster" => new ChatSendOptions(Bot: false),
            "bot" => new ChatSendOptions(Bot: true),
            _ => throw ApiException.BadRequest($"Unknown as '{sendAs}' (default, broadcaster or bot)"),
        };
        if (options.Bot == true && !chat.CanSendAsBot(target))
            throw ApiException.Conflict(target.Equals(ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
                ? "No platform has a bot account signed in"
                : $"No bot account is signed in on {target}");
        try
        {
            var sent = await chat.SendAsync(target, text, options, ct).ConfigureAwait(false);
            services.Log.Info(LogSource, $"Chat message sent to {string.Join(", ", sent)}");
            return new JsonObject
            {
                ["sent"] = true,
                ["platforms"] = CatalogJson.Strings(sent),
                ["as"] = options.Bot switch { true => "bot", false => "broadcaster", null => "default" },
            };
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.Conflict(ex.Message);
        }
    }

    // ---- Moderation --------------------------------------------------------------------------------------------------

    /// <summary>
    /// What every moderation tool does on each chat platform now (or only <paramref name="platform"/>), with the
    /// timeout lengths the platform will really apply for the Chat tab's presets.
    /// </summary>
    public JsonObject ChatCapabilities(string? platform = null)
    {
        var chat = ChatHub;
        var moderation = Moderation;
        var platforms = string.IsNullOrWhiteSpace(platform) ? chat.Platforms : [platform.Trim()];
        return new JsonObject
        {
            ["platforms"] = new JsonArray([.. platforms.Select(p => (JsonNode)new JsonObject
            {
                ["platform"] = p,
                ["canSend"] = chat.SendablePlatforms.Contains(p, StringComparer.OrdinalIgnoreCase),
                ["canSendAsBot"] = chat.CanSendAsBot(p),
                ["actions"] = new JsonArray([.. moderation.Capabilities(p).Select(c => (JsonNode)CapabilityJson(c))]),
                ["timeoutPresets"] = new JsonArray([.. TimeoutPresets.Select(seconds =>
                {
                    var (applied, note) = moderation.PreviewDuration(p, TimeSpan.FromSeconds(seconds));
                    return (JsonNode)new JsonObject
                    {
                        ["requestedSeconds"] = seconds,
                        ["appliedSeconds"] = (int)Math.Round(applied.TotalSeconds),
                        ["note"] = note,
                    };
                })]),
            })]),
        };
    }

    private static JsonObject CapabilityJson(ModerationCapability c) => new()
    {
        ["action"] = ActionName(c.Action),
        ["label"] = ModerationCapability.Label(c.Action),
        ["state"] = c.State switch
        {
            ModerationToolState.Works => "works",
            ModerationToolState.Closest => "closest",
            ModerationToolState.OnWebsite => "onWebsite",
            _ => "notAvailable",
        },
        ["canRun"] = c.CanRun,
        ["note"] = c.Note,
        ["minDurationSeconds"] = c.MinDuration is { } min ? (int)min.TotalSeconds : null,
        ["maxDurationSeconds"] = c.MaxDuration is { } max ? (int)max.TotalSeconds : null,
        ["durationUnitSeconds"] = (int)Math.Max(1, c.DurationUnit.TotalSeconds),
        ["maxReasonLength"] = c.MaxReasonLength,
        ["quotaCost"] = c.QuotaCost,
        ["websiteUrl"] = c.WebsiteUrl,
    };

    /// <summary>The API's name of an action: camelCase ("delete", "timeout", "slowMode"...).</summary>
    internal static string ActionName(ModerationAction action)
    {
        var name = action.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static ModerationAction ParseAction(string? text)
    {
        var name = (text ?? "").Replace("-", "").Replace("_", "").Trim();
        if (name.Length > 0 && Enum.TryParse<ModerationAction>(name, ignoreCase: true, out var action) && Enum.IsDefined(action)
            && !int.TryParse(name, out _))
            return action;
        throw ApiException.BadRequest($"Unknown action '{text}' ({string.Join(", ", ModerationCapability.AllActions.Select(ActionName))})");
    }

    /// <summary>
    /// Runs one moderation tool as the streamer: <c>{platform, action, messageId?, userId, userName, durationSeconds?,
    /// reason?, enabled?, value?}</c>. A message id of a recent message fills in its author, roles and chat. The
    /// answer says what happened (<c>result</c>: ok, failed or skipped, with <c>websiteUrl</c> when it is done on the
    /// platform's website); only a bad request is an HTTP error.
    /// </summary>
    public async Task<JsonObject> ModerateChatAsync(JsonObject body, CancellationToken ct = default)
    {
        var moderation = Moderation;
        var platform = BodyText(body["platform"])?.Trim() ?? "";
        if (platform.Length == 0 || platform.Equals(ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("platform is required (one platform, like Twitch)");
        var action = ParseAction(BodyText(body["action"]));
        var messageId = BodyText(body["messageId"])?.Trim() ?? "";
        var userId = BodyText(body["userId"])?.Trim() ?? "";
        var userName = BodyText(body["userName"])?.Trim() ?? "";
        var reason = BodyText(body["reason"]);
        TimeSpan? duration = null;
        if (body["durationSeconds"] is JsonValue d)
        {
            if (!d.TryGetValue<double>(out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
                throw ApiException.BadRequest("durationSeconds must be a positive number");
            duration = TimeSpan.FromSeconds(seconds);
        }
        bool? enabled = body["enabled"] is JsonValue e && e.TryGetValue<bool>(out var ev) ? ev : null;
        int? value = body["value"] is JsonValue v && v.TryGetValue<int>(out var vv) ? vv : null;

        var message = messageId.Length == 0 ? null : services.Chat?.RecentMessages().LastOrDefault(m =>
            m.Id == messageId && string.Equals(m.Platform, platform, StringComparison.OrdinalIgnoreCase));
        var command = message is not null
            ? ModerationCommand.For(message, action)
            : new ModerationCommand(platform, action)
            {
                TargetUserId = userId,
                TargetName = userName,
                MessageId = messageId,
            };
        if (message is null && action == ModerationAction.Delete && messageId.Length == 0) throw ApiException.BadRequest("messageId is required to delete");
        if (message is not null && action == ModerationAction.Delete && !message.CanDelete)
            throw ApiException.Conflict($"{platform} gave this message no id of its own, so it cannot be deleted");
        if (ModerationCapability.TargetsUser(action) && command.TargetUserId.Length == 0 && command.TargetName.Length == 0)
            throw ApiException.BadRequest("userId or userName is required");
        command = command with
        {
            Duration = action == ModerationAction.Timeout ? duration : null,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Enabled = enabled ?? true,
            ModeValue = value,
        };

        var result = await moderation.ExecuteAsync(command, ModActor.Streamer, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = result.Ok,
            ["result"] = result.Result.ToString().ToLowerInvariant(),
            ["message"] = result.Message,
            ["appliedDurationSeconds"] = result.AppliedDuration is { } applied ? (int)Math.Round(applied.TotalSeconds) : null,
            ["websiteUrl"] = result.WebsiteUrl,
            ["entry"] = result.Entry is { } entry ? ModLogJson(entry) : null,
        };
    }

    /// <summary>
    /// The mod log, newest first: about one account (<paramref name="target"/>, a user id, on <paramref name="platform"/>
    /// or every chat platform) or everything (optionally one platform).
    /// </summary>
    public JsonObject ChatModLog(string? target, string? platform, int limit = 100)
    {
        var log = Moderation.Log ?? throw ApiException.Unavailable("The mod log is not available");
        limit = Math.Clamp(limit, 1, 500);
        var onePlatform = string.IsNullOrWhiteSpace(platform) || platform.Equals(ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase) ? null : platform.Trim();
        IEnumerable<ModLogEntry> entries;
        if (string.IsNullOrWhiteSpace(target))
        {
            entries = log.Recent(limit, onePlatform);
        }
        else
        {
            var platforms = onePlatform is not null ? [onePlatform] : services.Chat?.Platforms ?? [];
            entries = platforms.SelectMany(p => log.ForTarget(p, target.Trim(), limit)).OrderByDescending(e => e.At).Take(limit);
        }
        return new JsonObject { ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode)ModLogJson(e))]) };
    }

    /// <summary>One mod log row ("chat.modLog" on the event stream), without its hash chain.</summary>
    internal static JsonObject ModLogJson(ModLogEntry e) => new()
    {
        ["id"] = e.Id,
        ["incidentId"] = e.IncidentId,
        ["at"] = e.At.ToString("o"),
        ["actorKind"] = e.ActorKind.ToString(),
        ["actorName"] = e.ActorName,
        ["platform"] = e.Platform,
        ["action"] = e.Action,
        ["targetUserId"] = e.TargetUserId,
        ["targetName"] = e.TargetName,
        ["messageId"] = e.MessageId,
        ["messageText"] = e.MessageText,
        ["durationRequestedSeconds"] = e.DurationRequestedSeconds,
        ["durationAppliedSeconds"] = e.DurationAppliedSeconds,
        ["reason"] = e.Reason,
        ["result"] = e.Result.ToString(),
        ["error"] = e.Error,
        ["undoes"] = e.Undoes,
        ["undoneBy"] = e.UndoneBy,
        ["undoneAt"] = e.UndoneAt?.ToString("o"),
        ["redacted"] = e.Redacted,
    };

    private static string? BodyText(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString();
}
