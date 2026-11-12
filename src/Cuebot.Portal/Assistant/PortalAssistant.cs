using System.Text.Json.Nodes;
using Cuebot.Assistant;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Rewards;
using Cuebot.Portal.Uploads;
using Cuebot.Server.Api;
using Cuebot.Server.Mcp;

namespace Cuebot.Portal.Assistant;

/// <summary>
/// The streamer's Assistant in the mod portal. Each member with <see cref="PortalPermission.UseAssistant"/> gets their own
/// conversation (<see cref="ScopedAssistant"/>) whose only tools are <see cref="Tools"/>: each one is a portal request made
/// as that member, so the AI can do exactly what the member could do on the portal's own pages, checked the same way
/// (role, level, groups, uploads only, approval). The portal follows a turn by asking for new lines
/// (<c>assistant.poll</c>), because the relay link answers one request at a time.
/// <list type="bullet">
/// <item><c>assistant.status</c>: whether it can be used, today's count and limit, and the conversation's lines.</item>
/// <item><c>assistant.send</c>: <c>text</c>, optional <c>attachments</c> (upload ids). Starts a turn.</item>
/// <item><c>assistant.poll</c>: <c>after</c> (a seq). The lines added or changed since, and whether a turn runs.</item>
/// <item><c>assistant.answer</c>: <c>line</c>, <c>approve</c>. Approve or deny what the AI asked to do.</item>
/// <item><c>assistant.stop</c>, <c>assistant.reset</c>.</item>
/// </list>
/// </summary>
public sealed class PortalAssistant(PortalDirectory directory, ScopedAssistant? assistant, Func<Guid, JsonObject, Task<JsonObject>> api,
    PortalMediaStore? media = null, PortalChanges? changes = null, ProfileService? profile = null, CommandStore? commands = null,
    AppLog? log = null, TimeProvider? time = null, RewardArea? rewards = null)
{
    private const string LogSource = "Mod portal";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, (DateOnly Day, int Count)> _used = [];
    private readonly object _gate = new();

    public static bool Handles(string op) => op.StartsWith("assistant.", StringComparison.Ordinal);

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request)
    {
        if (!PortalPolicy.Has(role, PortalPermission.UseAssistant)) return Error("Your role does not include the assistant.");
        if (assistant is null) return Error("This Cuebot has no assistant.");
        if (directory.AssistantMessagesPerDay <= 0) return Error("The streamer has turned the assistant off for mods.");
        var key = member.Id.ToString("N");
        switch (op)
        {
            case "assistant.status":
            {
                var conversation = assistant.Find(key);
                return Ok(new JsonObject
                {
                    ["limit"] = directory.AssistantMessagesPerDay,
                    ["used"] = UsedToday(member.Id),
                    ["busy"] = conversation?.Busy ?? false,
                    ["seq"] = conversation?.Seq ?? 0,
                    ["lines"] = Lines(conversation?.Since(0) ?? []),
                });
            }
            case "assistant.send":
            {
                var text = (string?)request["text"] ?? "";
                var (attached, names) = Attachments(member, request["attachments"] as JsonArray);
                // The AI gets the files' upload ids; the conversation shows the mod's words and the file names.
                var shown = names.Count > 0 ? $"{text.Trim()}\n📎 {string.Join(", ", names)}" : null;
                if (attached.Length > 0) text = text.Trim() + "\n\n" + attached;
                var conversation = assistant.For(key, () => new McpScope(Tools(member.Id), AssistantPrompt.BuildForMod(member.Name)),
                    () => AssistantPrompt.BuildForMod(member.Name));
                lock (_gate)
                {
                    var used = UsedToday(member.Id);
                    if (used >= directory.AssistantMessagesPerDay)
                        return Error($"You have used today's {directory.AssistantMessagesPerDay} messages. The count starts again tomorrow (UTC).");
                    if (conversation.Send(text, shown) is { } problem) return Error(problem);
                    _used[member.Id] = (Today, used + 1);
                }
                log?.Info(LogSource, $"{member.Name} asked the assistant something.");
                return Ok(new JsonObject { ["used"] = UsedToday(member.Id), ["limit"] = directory.AssistantMessagesPerDay });
            }
            case "assistant.poll":
            {
                var conversation = assistant.Find(key);
                var after = request["after"] is JsonValue v && v.TryGetValue<long>(out var a) ? a : 0;
                return Ok(new JsonObject
                {
                    ["busy"] = conversation?.Busy ?? false,
                    ["seq"] = conversation?.Seq ?? 0,
                    ["lines"] = Lines(conversation?.Since(after) ?? []),
                });
            }
            case "assistant.answer":
            {
                var line = request["line"] is JsonValue l && l.TryGetValue<int>(out var id) ? id : -1;
                var approve = request["approve"] is JsonValue b && b.TryGetValue<bool>(out var yes) && yes;
                return assistant.Find(key)?.Answer(line, approve) == true ? Ok(new JsonObject()) : Error("That question was already answered.");
            }
            case "assistant.stop":
                assistant.Find(key)?.Stop();
                return Ok(new JsonObject());
            case "assistant.reset":
                if (assistant.Find(key) is { } c) await c.ResetAsync().ConfigureAwait(false);
                return Ok(new JsonObject());
            default:
                return Error("Unknown request.");
        }
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    private int UsedToday(Guid member)
    {
        lock (_gate) return _used.TryGetValue(member, out var u) && u.Day == Today ? u.Count : 0;
    }

    /// <summary>A note for the AI about the uploads the member attached: ones in the library, or their own waiting for approval.</summary>
    private (string Note, List<string> Names) Attachments(PortalMember member, JsonArray? ids)
    {
        var notes = new List<string>();
        var names = new List<string>();
        if (ids is null || ids.Count == 0) return ("", names);
        foreach (var node in ids.Take(10))
        {
            if (!Guid.TryParse((string?)node, out var id)) continue;
            if (media?.Get(id) is { } item)
            {
                notes.Add($"- \"{item.Name}\" ({item.Kind.ToString().ToLowerInvariant()}): ready, use \"{PortalFiles.Prefix}{item.Id}\"");
                names.Add(item.Name);
            }
            else if (changes?.Pending.FirstOrDefault(c => c.Area == UploadArea.AreaName && c.TargetId == id && c.MemberId == member.Id) is { } pending)
            {
                notes.Add($"- \"{pending.TargetName}\": waiting for the streamer's approval, so it cannot be used in a step yet");
                names.Add(pending.TargetName);
            }
        }
        return (notes.Count == 0 ? "" : "Files I attached (already uploaded):\n" + string.Join("\n", notes), names);
    }

    private static JsonArray Lines(IEnumerable<ScopedLine> lines) => new([.. lines.Select(l => (JsonNode)new JsonObject
    {
        ["id"] = l.Id,
        ["seq"] = l.Seq,
        ["kind"] = l.Kind,
        ["text"] = l.Text,
        ["state"] = l.State,
    })]);

    // ---- the member's tools -----------------------------------------------------------------------------------------

    /// <summary>
    /// The tools of a member's conversation. Each is one portal request made as the member (<paramref name="memberId"/>),
    /// so the portal's checks apply to every call; a refusal comes back to the AI as an error it can explain.
    /// </summary>
    internal IReadOnlyList<McpTool> Tools(Guid memberId)
    {
        var read = new McpToolHints(ReadOnly: true, Idempotent: true);
        var write = new McpToolHints(ReadOnly: false);
        var destructive = new McpToolHints(ReadOnly: false, Destructive: true);
        Func<JsonObject, CancellationToken, Task<JsonNode>> Op(string op, Func<JsonObject, JsonObject>? body = null, Func<JsonObject, JsonNode>? shape = null) =>
            async (args, _) =>
            {
                var request = body?.Invoke(args) ?? [];
                request["op"] = op;
                var answer = await api(memberId, request).ConfigureAwait(false);
                if (answer["ok"]?.GetValue<bool>() != true)
                    throw ApiException.BadRequest((string?)answer["error"] ?? "Refused.", answer["problems"]?.DeepClone() as JsonArray);
                answer.Remove("ok");
                return shape?.Invoke(answer) ?? answer;
            };
        JsonObject Id(JsonObject a) => new() { ["id"] = Str(a, "id") };

        return
        [
            new("my_role", "My role", "What the mod's role allows: highest step level (1-4), permissions, groups, and whether changes go live or wait for the streamer.",
                Schema([]), read, Op("me")),
            new("list_actions", "List actions", "The actions the mod's role can see: id, name, group, steps, and the highest step level (5 = only the streamer may change it).",
                Schema([]), read, Op("actions.list")),
            new("get_action", "Get action", "One action in full (triggers, steps, branches). Change it and pass it whole to save_action.",
                Schema(["id"], ("id", Str("Action id"))), read, Op("actions.get", Id)),
            new("save_action", "Save action",
                "Creates (no id) or replaces (with id) an action. Shape: {\"id\"?, \"name\", \"group\", \"enabled\", \"triggers\": [{\"type\", "
                + "\"settings\": {key: string}}], \"subActions\": [{\"type\", \"parameters\": {key: string}, \"branches\": {name: [steps]}}]}. "
                + "Only step and trigger types from list_step_types. Files: \"upload:<id>\" from list_uploads, or \"upload:sounds\" for the "
                + "sound folder. Answers status live or pending (waiting for the streamer's approval).",
                Schema(["action"], ("action", new JsonObject { ["type"] = "object", ["description"] = "The whole action" })), write,
                Op("actions.save", a => new JsonObject { ["action"] = a["action"]?.DeepClone() })),
            new("delete_action", "Delete action", "Deletes an action (the mod is asked first).",
                Schema(["id"], ("id", Str("Action id"))), destructive, Op("actions.delete", Id))
            {
                Confirmation = a => $"Delete the action {ActionName(a)}",
            },
            new("run_action", "Run action", "Runs an action now, on stream (the mod is asked first). Use it to test an alert.",
                Schema(["id"], ("id", Str("Action id"))), destructive, Op("actions.run", Id))
            {
                Confirmation = a => $"Run the action {ActionName(a)} on stream now",
            },
            new("list_commands", "List commands", "The chat commands the mod's role can see, in full.",
                Schema([]), read, Op("commands.list")),
            new("save_command", "Save command",
                "Creates (no id) or replaces (with id) a chat command: {\"id\"?, \"name\", \"group\", \"enabled\", \"commands\": [\"!text\"], "
                + "\"globalCooldownSeconds\", \"userCooldownSeconds\", \"minimumRole\"}. Copy the rest from list_commands. An action runs it "
                + "through a trigger: see how an existing command's action is set up.",
                Schema(["command"], ("command", new JsonObject { ["type"] = "object", ["description"] = "The whole command" })), write,
                Op("commands.save", a => new JsonObject { ["command"] = a["command"]?.DeepClone() })),
            new("delete_command", "Delete command", "Deletes a chat command (the mod is asked first).",
                Schema(["id"], ("id", Str("Command id"))), destructive, Op("commands.delete", Id))
            {
                Confirmation = a => $"Delete the command {CommandName(a)}",
            },
            new("list_step_types", "List step types", "The step (sub-action) and trigger types the mod's role may use: type, name, category.",
                Schema([], ("search", Str("Words in the type, name or category, e.g. 'sound' or 'reward'"))), read,
                Op("steps.catalog", shape: StepList)),
            new("describe_step_type", "Describe step type", "One step or trigger type's settings (key, label, kind, default, choices, help) and branches.",
                Schema(["type"], ("type", Str("Type id, e.g. audio.playSound or twitch.rewardRedemption"))), read,
                async (args, ct) => DescribeStep(await Op("steps.catalog")(args, ct).ConfigureAwait(false), Str(args, "type"))),
            new("list_uploads", "List uploads", "Uploaded sounds, pictures and videos a step can use, as \"upload:<id>\".",
                Schema([], ("kind", Str("Sound, Image or Video"))), read,
                async (args, ct) => Uploads(await Op("uploads.list")(args, ct).ConfigureAwait(false), Str(args, "kind"))),
            new("list_rewards", "List rewards",
                "The channel point rewards on Twitch and Kick: per platform whether the streamer is signed in, how many of the maximum are used, "
                + "the title and description length limits, and each reward (id, title, description, cost, color, enabled, paused, userInput, "
                + "skipQueue, maxPerStream, maxPerUserPerStream, cooldownSeconds, manageable). manageable false: made outside Cuebot, read-only.",
                Schema([]), read, Op("rewards.list")),
            new("save_reward", "Save reward",
                "Creates (no id) or changes (with id) a channel point reward on \"twitch\" or \"kick\": {\"id\"?, \"title\", \"description\", "
                + "\"cost\", \"color\" (#RRGGBB), \"enabled\", \"paused\", \"userInput\", \"skipQueue\", and on Twitch only \"maxPerStream\", "
                + "\"maxPerUserPerStream\", \"cooldownSeconds\" (0 = off)}. Send every field when changing one (start from list_rewards). Answers live or pending.",
                Schema(["platform", "reward"], ("platform", Str("twitch or kick")),
                    ("reward", new JsonObject { ["type"] = "object", ["description"] = "The whole reward" })), write,
                Op("rewards.save", a => new JsonObject { ["platform"] = Str(a, "platform"), ["reward"] = a["reward"]?.DeepClone() })),
            new("delete_reward", "Delete reward", "Deletes a channel point reward (the mod is asked first).",
                Schema(["platform", "id"], ("platform", Str("twitch or kick")), ("id", Str("Reward id from list_rewards"))), destructive,
                Op("rewards.delete", a => new JsonObject { ["platform"] = Str(a, "platform"), ["id"] = Str(a, "id") }))
            {
                Confirmation = a => $"Delete the {RewardName(a)}",
            },
            new("my_changes", "My changes", "The mod's recent changes and whether each is live, waiting for approval or rejected.",
                Schema([]), read, Op("changes.mine", shape: a => new JsonObject { ["changes"] = new JsonArray([.. (a["changes"] as JsonArray ?? []).TakeLast(20).Select(c => c?.DeepClone())]) })),
        ];
    }

    private string ActionName(JsonObject args) =>
        Guid.TryParse(Str(args, "id"), out var id) && profile?.Snapshot.Actions.FirstOrDefault(a => a.Id == id) is { } action
            ? $"\"{action.Name}\"" : Str(args, "id");

    private string RewardName(JsonObject args)
    {
        var platform = rewards?.Platform(Str(args, "platform"));
        var reward = platform?.Cached.FirstOrDefault(r => r.Id == Str(args, "id"));
        return reward is null ? $"{Str(args, "platform")} reward {Str(args, "id")}" : $"{platform!.Name} reward \"{reward.Title}\"";
    }

    private string CommandName(JsonObject args) =>
        Guid.TryParse(Str(args, "id"), out var id) && commands?.Get(id) is { } command ? $"\"{command.Name}\"" : Str(args, "id");

    private static JsonNode StepList(JsonObject catalog) => new JsonObject
    {
        ["steps"] = Compact(catalog["steps"] as JsonArray),
        ["triggers"] = Compact(catalog["triggers"] as JsonArray),
    };

    private static JsonArray Compact(JsonArray? list) => new([.. (list ?? []).OfType<JsonObject>().Select(t => (JsonNode)new JsonObject
    {
        ["type"] = t["type"]?.DeepClone(),
        ["name"] = t["name"]?.DeepClone(),
        ["category"] = t["category"]?.DeepClone(),
    })]);

    private static JsonNode DescribeStep(JsonNode catalog, string type)
    {
        foreach (var kind in new[] { "steps", "triggers" })
            if ((catalog[kind] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(t => (string?)t["type"] == type) is { } found)
            {
                var copy = (JsonObject)found.DeepClone();
                copy["is"] = kind == "steps" ? "step" : "trigger";
                return copy;
            }
        throw ApiException.NotFound($"'{type}' is not a step or trigger type the mod's role may use. Use list_step_types.");
    }

    private static JsonNode Uploads(JsonNode list, string kind) => new JsonObject
    {
        ["items"] = new JsonArray([.. (list["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(i => kind.Length == 0 || string.Equals((string?)i["kind"], kind, StringComparison.OrdinalIgnoreCase))
            .Select(i => (JsonNode)new JsonObject
            {
                ["use"] = PortalFiles.Prefix + (string?)i["id"],
                ["name"] = i["name"]?.DeepClone(),
                ["kind"] = i["kind"]?.DeepClone(),
                ["seconds"] = Math.Round(((long?)i["durationMs"] ?? 0) / 1000.0, 1),
                ["by"] = i["by"]?.DeepClone(),
            })]),
        ["soundFolder"] = PortalFiles.SoundFolder,
    };

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)p.Schema))),
        ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static string Str(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
