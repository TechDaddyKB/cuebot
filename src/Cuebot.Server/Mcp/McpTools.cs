using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

/// <summary>MCP tool annotations (hints for clients; not security boundaries).</summary>
public sealed record McpToolHints(bool ReadOnly, bool Destructive = false, bool Idempotent = false, bool OpenWorld = false);

/// <summary>One MCP tool: its listing (name, schema, annotations) and its handler.</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, McpToolHints Hints,
    Func<JsonObject, CancellationToken, Task<JsonNode>> Invoke)
{
    /// <summary>Set on destructive tools: what the call will do, shown when a confirmation is required. May throw.</summary>
    public Func<JsonObject, string>? Confirmation { get; init; }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = Hints.ReadOnly,
            ["destructiveHint"] = Hints.Destructive,
            ["idempotentHint"] = Hints.Idempotent,
            ["openWorldHint"] = Hints.OpenWorld,
        },
    };
}

/// <summary>The tools Cuebot offers over MCP, all backed by <see cref="ApiOperations"/>.</summary>
public static class McpTools
{
    private const string ActionRef = "Action id or name";

    public static IReadOnlyList<McpTool> Create(ApiOperations ops)
    {
        var readOnly = new McpToolHints(ReadOnly: true, Idempotent: true);
        var write = new McpToolHints(ReadOnly: false);
        var idempotentWrite = new McpToolHints(ReadOnly: false, Idempotent: true);
        var destructive = new McpToolHints(ReadOnly: false, Destructive: true, Idempotent: true);

        return
        [
            // ---- actions ----
            new("list_actions", "List actions",
                "List Cuebot actions (id, name, group, enabled, trigger types, number of sub-actions). Use get_action for the full definition.",
                Schema([], ("group", Str("Only actions in this group")), ("search", Str("Name or group contains this text"))),
                readOnly, (a, _) => Done(ops.ListActions(OptStr(a, "group"), OptStr(a, "search")))),
            new("get_action", "Get action",
                "The full definition of one action: settings, triggers (type + settings) and sub-actions (type + parameters, with child steps per branch).",
                Schema(["action"], ("action", Str(ActionRef))), readOnly, (a, _) => Done(ops.GetAction(ReqStr(a, "action")))),
            new("create_action", "Create action",
                "Create an action. Pick trigger and sub-action types with list_trigger_types / list_subaction_types and check their parameters with "
                + "describe_subaction_type first. Every parameter value is a string and may use %arguments%, ~globals~ and $functions()$ "
                + "(resource cuebot://docs/variables). Unknown types or invalid values are rejected with a list of problems.",
                ActionSchema(), write, async (a, _) => await ops.CreateActionAsync(a["action"] ?? a).ConfigureAwait(false)),
            new("update_action", "Update action",
                "Change an action. 'changes' holds the top-level fields to replace (name, group, enabled, queue, triggers, subActions...); with "
                + "replace=true it is the complete new definition. Lists such as subActions are replaced as a whole, so send the full list.",
                Schema(["action", "changes"], ("action", Str(ActionRef)), ("changes", new JsonObject { ["type"] = "object", ["description"] = "Fields of the action to set" }),
                    ("replace", Bool("Replace the whole definition instead of merging the given fields"))),
                write, async (a, _) => await ops.UpdateActionAsync(ReqStr(a, "action"), a["changes"], merge: OptBool(a, "replace") != true).ConfigureAwait(false)),
            new("delete_action", "Delete action", "Delete an action permanently.",
                Schema(["action"], ("action", Str(ActionRef)), Confirm()), destructive,
                async (a, _) => await ops.DeleteActionAsync(ReqStr(a, "action")).ConfigureAwait(false))
            {
                Confirmation = a => $"Delete the action '{ops.FindAction(ReqStr(a, "action")).Name}'",
            },
            new("run_action", "Run action",
                "Run an action now with optional arguments (available as %name% in its sub-actions). By default waits up to timeoutSeconds and "
                + "returns the run (state, error, arguments after the run). The action may do anything it is built to do (chat, OBS...).",
                Schema(["action"], ("action", Str(ActionRef)),
                    ("args", new JsonObject { ["type"] = "object", ["description"] = "Arguments, e.g. {\"user\": \"tester\", \"message\": \"hi\"}" }),
                    ("wait", Bool("Wait for the run to finish (default true)")), ("timeoutSeconds", Num("How long to wait (default 30, max 300)"))),
                new McpToolHints(ReadOnly: false, OpenWorld: true),
                async (a, ct) => await ops.RunActionAsync(ReqStr(a, "action"), a["args"], OptBool(a, "wait") ?? true, OptNum(a, "timeoutSeconds") ?? 30, "MCP", ct).ConfigureAwait(false)),
            new("set_action_enabled", "Enable or disable action", "Enable or disable an action.",
                Schema(["action", "enabled"], ("action", Str(ActionRef)), ("enabled", Bool("true to enable"))), idempotentWrite,
                async (a, _) => await ops.SetActionEnabledAsync(ReqStr(a, "action"), OptBool(a, "enabled") ?? throw Missing("enabled")).ConfigureAwait(false)),

            // ---- catalogue ----
            new("list_trigger_types", "List trigger types",
                "Trigger types (what can start an action), with their settings. Filter by category or search words.",
                Schema([], ("category", Str("Exact category, e.g. Twitch")), ("search", Str("Words in the type id, name or category, e.g. 'chat message'"))),
                readOnly, (a, _) => Done(ops.ListTriggers(OptStr(a, "category"), OptStr(a, "search")))),
            new("describe_trigger_type", "Describe trigger type", "One trigger type with a JSON Schema of its settings and current suggestions.",
                Schema(["type"], ("type", Str("Trigger type id, e.g. twitch.chatMessage"))), readOnly, (a, _) => Done(ops.DescribeTrigger(ReqStr(a, "type")))),
            new("list_subaction_types", "List sub-action types",
                "Sub-action types (the steps an action runs), with their parameters and branches. Filter by category or search words.",
                Schema([], ("category", Str("Exact category, e.g. Logic")), ("search", Str("Words in the type id, name or category, e.g. 'send message'"))),
                readOnly, (a, _) => Done(ops.ListSubActions(OptStr(a, "category"), OptStr(a, "search")))),
            new("describe_subaction_type", "Describe sub-action type",
                "One sub-action type with a JSON Schema of its parameters, its branches and current suggestions (scene names, voices...).",
                Schema(["type"], ("type", Str("Sub-action type id, e.g. core.if"))), readOnly, (a, _) => Done(ops.DescribeSubAction(ReqStr(a, "type")))),
            new("list_script_functions", "List script functions", "Functions C# and Python code sub-actions can call (Cb.Call / cb.call).",
                Schema([], ("search", Str("Name or description contains"))), readOnly, (a, _) => Done(ops.ListScriptFunctions(OptStr(a, "search")))),

            // ---- commands ----
            new("list_commands", "List commands", "All chat commands (trigger texts, permissions, cooldowns).", Schema([]), readOnly,
                (_, _) => Done(ops.ListCommands())),
            new("get_command", "Get command", "One chat command and the actions it runs.",
                Schema(["command"], ("command", Str("Command id or name"))), readOnly, (a, _) => Done(ops.GetCommand(ReqStr(a, "command")))),
            new("create_command", "Create command",
                "Create a chat command, e.g. {\"name\": \"Hello\", \"commands\": [\"!hello\"]}. To make it do something, add a trigger "
                + "{\"type\": \"command.triggered\", \"settings\": {\"command\": \"Hello\"}} to an action.",
                Schema(["command"], ("command", CommandSchema())), write, (a, _) => Done(ops.CreateCommand(a["command"] ?? a))),
            new("update_command", "Update command", "Change the given fields of a chat command.",
                Schema(["command", "changes"], ("command", Str("Command id or name")), ("changes", new JsonObject { ["type"] = "object" })),
                write, (a, _) => Done(ops.UpdateCommand(ReqStr(a, "command"), a["changes"], merge: true))),
            new("delete_command", "Delete command", "Delete a chat command permanently.",
                Schema(["command"], ("command", Str("Command id or name")), Confirm()), destructive, (a, _) => Done(ops.DeleteCommand(ReqStr(a, "command"))))
            {
                Confirmation = a => $"Delete the command '{ops.FindCommand(ReqStr(a, "command")).Name}'",
            },

            // ---- timers ----
            new("list_timers", "List timers", "Timers with their runtime state (enabled, next fire).", Schema([]), readOnly, (_, _) => Done(ops.ListTimers())),
            new("create_timer", "Create timer",
                "Create a timer, e.g. {\"name\": \"Socials\", \"intervalSeconds\": 600}. A timer only fires: run an action from it with a trigger "
                + "{\"type\": \"core.timer\", \"settings\": {\"timer\": \"Socials\"}}.",
                Schema(["timer"], ("timer", TimerSchema())), write, async (a, _) => await ops.CreateTimerAsync(a["timer"] ?? a).ConfigureAwait(false)),
            new("update_timer", "Update timer", "Change the given fields of a timer (saved).",
                Schema(["timer", "changes"], ("timer", Str("Timer id or name")), ("changes", new JsonObject { ["type"] = "object" })),
                write, async (a, _) => await ops.UpdateTimerAsync(ReqStr(a, "timer"), a["changes"], merge: true).ConfigureAwait(false)),
            new("set_timer_state", "Set timer state", "Enable/disable a timer or change its interval until restart (not saved).",
                Schema(["timer"], ("timer", Str("Timer id or name")), ("enabled", Bool("Enable or disable")), ("intervalSeconds", Int("New interval"))),
                idempotentWrite, (a, _) => Done(ops.SetTimerState(ReqStr(a, "timer"), OptBool(a, "enabled"), (int?)OptNum(a, "intervalSeconds")))),
            new("delete_timer", "Delete timer", "Delete a timer permanently.",
                Schema(["timer"], ("timer", Str("Timer id or name")), Confirm()), destructive,
                async (a, _) => await ops.DeleteTimerAsync(ReqStr(a, "timer")).ConfigureAwait(false))
            {
                Confirmation = a => $"Delete the timer '{ops.FindTimer(ReqStr(a, "timer")).Name}'",
            },

            // ---- globals ----
            new("get_globals", "Get global variables", "Global variables (used as ~name~ in parameters). Give a name for one variable.",
                Schema([], ("name", Str("One variable")), ("persisted", Bool("true: persisted only, false: temporary only"))), readOnly,
                (a, _) => Done(OptStr(a, "name") is { Length: > 0 } name ? ops.GetGlobal(name) : ops.GetGlobals(OptBool(a, "persisted")))),
            new("set_global", "Set global variable", "Create or change a global variable.",
                Schema(["name", "value"], ("name", Str("Variable name")),
                    ("value", new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null"), ["description"] = "The value" }),
                    ("persisted", Bool("Keep it across restarts (default true)"))),
                idempotentWrite, (a, _) => Done(ops.SetGlobal(ReqStr(a, "name"), a["value"], OptBool(a, "persisted") ?? true))),
            new("delete_global", "Delete global variable", "Delete a global variable.",
                Schema(["name"], ("name", Str("Variable name")), Confirm()), destructive, (a, _) => Done(ops.DeleteGlobal(ReqStr(a, "name"))))
            {
                Confirmation = a => $"Delete the global variable '{ReqStr(a, "name")}'",
            },

            // ---- status, history, chat ----
            new("get_status", "Get status", "Cuebot version, platform connections (Twitch, YouTube, Kick, OBS...), servers and counts.",
                Schema([]), readOnly, (_, _) => Done(ops.Status())),
            new("get_history", "Get run history",
                "Recent and pending action runs (newest first) with state, source, duration and errors; includeArgs adds the arguments.",
                Schema([], ("limit", Int("Maximum runs (default 20)")), ("action", Str("Only this action (id or name)")),
                    ("includeArgs", Bool("Include the arguments of each run"))),
                readOnly, (a, _) => Done(ops.History((int?)OptNum(a, "limit") ?? 20, OptStr(a, "action"), OptBool(a, "includeArgs") ?? false))),
            new("get_recent_chat", "Get recent chat", "Recent messages from the combined chat (all platforms) and the activity feed.",
                Schema([], ("limit", Int("Maximum messages (default 30)"))), new McpToolHints(ReadOnly: true, OpenWorld: true),
                (a, _) => Done(ops.Chat((int?)OptNum(a, "limit") ?? 30))),
            new("send_chat_message", "Send chat message", "Send a message to the stream chat, as the connected account.",
                Schema(["message"], ("message", Str("The text")), ("platform", Str("Twitch, YouTube, Kick, ... or All (default)")), Confirm()),
                new McpToolHints(ReadOnly: false, Destructive: true, OpenWorld: true),
                async (a, ct) => await ops.SendChatAsync(OptStr(a, "platform"), ReqStr(a, "message"), ct).ConfigureAwait(false))
            {
                Confirmation = a => $"Send \"{ReqStr(a, "message")}\" to {OptStr(a, "platform") ?? "every connected platform"}",
            },

            // ---- import / export ----
            new("preview_import", "Preview import",
                "Show what importing a Cuebot bundle (CUEBOT1:...) or a Streamer.bot export would add or overwrite. Changes nothing.",
                Schema(["data"], ("data", Str("The import string or bundle JSON"))), readOnly, (a, _) => Done(ops.PreviewImport(ReqStr(a, "data")))),
            new("apply_import", "Apply import",
                "Import a bundle. mode decides what happens to items that clash with existing ones (overwrite, addAsNew, skip); items can "
                + "override single entries. Run preview_import first. Nothing in the import is run.",
                Schema(["data"], ("data", Str("The import string or bundle JSON")), ("mode", Enum("overwrite", "addAsNew", "skip")),
                    ("items", new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Schema(["kind"], ("kind", Enum("action", "queue", "command", "timer", "global")), ("name", Str("Item name")),
                            ("sourceId", Str("Item id in the import")), ("include", Bool("Import it")), ("mode", Enum("overwrite", "addAsNew", "skip"))),
                    }), Confirm()),
                new McpToolHints(ReadOnly: false, Destructive: true),
                async (a, _) => await ops.ApplyImportAsync(ReqStr(a, "data"), OptStr(a, "mode"), a["items"] as JsonArray).ConfigureAwait(false))
            {
                Confirmation = a =>
                {
                    var plan = ops.PreviewImport(ReqStr(a, "data"));
                    var items = plan["items"]!.AsArray();
                    var conflicts = items.Count(i => i!["conflict"]!.GetValue<string>() != "None");
                    return $"Import {items.Count} item(s) ({conflicts} clash with existing ones, mode {OptStr(a, "mode") ?? "overwrite"})";
                },
            },
            new("export_bundle", "Export bundle",
                "Build a Cuebot import string (CUEBOT1:...) for actions, commands, timers and globals (ids or names). Referenced items are included.",
                Schema([], ("actions", StrArray("Actions")), ("commands", StrArray("Commands")), ("timers", StrArray("Timers")), ("globals", StrArray("Global variables")),
                    ("includeDependencies", Bool("Include referenced actions, queues, commands... (default true)")), ("name", Str("Bundle name"))),
                readOnly, (a, _) => Done(ops.Export(StrList(a, "actions"), StrList(a, "commands"), StrList(a, "timers"), StrList(a, "globals"),
                    OptBool(a, "includeDependencies") ?? true, OptStr(a, "name")))),
        ];
    }

    private static Task<JsonNode> Done(JsonNode node) => Task.FromResult(node);

    // ---- argument helpers ------------------------------------------------------------------------------------------

    private static ApiException Missing(string name) => ApiException.BadRequest($"'{name}' is required");

    internal static string ReqStr(JsonObject args, string name) =>
        OptStr(args, name) is { Length: > 0 } value ? value : throw Missing(name);

    internal static string? OptStr(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => throw ApiException.BadRequest($"'{name}' must be a string"),
    };

    internal static bool? OptBool(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var b) => b,
        _ => throw ApiException.BadRequest($"'{name}' must be true or false"),
    };

    internal static double? OptNum(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => throw ApiException.BadRequest($"'{name}' must be a number"),
    };

    private static IEnumerable<string>? StrList(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString() ?? "").Where(s => s.Length > 0).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => throw ApiException.BadRequest($"'{name}' must be a list of names"),
    };

    // ---- schema helpers --------------------------------------------------------------------------------------------

    internal static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        var json = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) json["required"] = CatalogJson.Strings(required);
        return json;
    }

    private static (string, JsonObject) Confirm() =>
        (McpConfirmations.ArgumentName, Str("Only when a previous call answered confirmationRequired: the token it returned, after the user agreed"));

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = CatalogJson.Strings(values) };
    private static JsonObject StrArray(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private static JsonObject StringMap(string description) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    /// <summary>The create_action input: an action as in actions.json, with the sub-action tree as a recursive definition.</summary>
    internal static JsonObject ActionSchema()
    {
        var subAction = Schema(["type"],
            ("type", Str("Sub-action type id (list_subaction_types)")),
            ("enabled", Bool("Default true")),
            ("parameters", StringMap("Parameter values by key (describe_subaction_type); all values are strings")),
            ("branches", new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Child steps per branch name, e.g. {\"True\": [...], \"False\": [...]} for core.if or {\"Steps\": [...]} for core.group",
                ["additionalProperties"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/subAction" } },
            }),
            ("weight", Num("Chance when the parent picks one random child (default 1)")));
        var trigger = Schema(["type"],
            ("type", Str("Trigger type id (list_trigger_types)")),
            ("enabled", Bool("Default true")),
            ("settings", StringMap("Trigger settings by key; empty values mean 'any'")));
        var action = Schema(["name"],
            ("name", Str("Unique, descriptive name")),
            ("group", Str("Group shown in the actions list")),
            ("enabled", Bool("Default true")),
            ("queue", Str("Action queue name; empty = Default")),
            ("concurrent", Bool("Allow runs of this action to overlap")),
            ("randomAction", Bool("Run only one random top-level sub-action")),
            ("triggers", new JsonObject { ["type"] = "array", ["items"] = trigger }),
            ("subActions", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/subAction" } }));
        action["description"] = "The action. Example: {\"name\": \"Greet\", \"triggers\": [{\"type\": \"command.triggered\", \"settings\": {\"command\": \"Hello\"}}], "
                                + "\"subActions\": [{\"type\": \"chat.send\", \"parameters\": {\"message\": \"Hi %user%!\"}}]}";
        var schema = Schema(["action"], ("action", action));
        schema["$defs"] = new JsonObject { ["subAction"] = subAction };
        return schema;
    }

    private static JsonObject CommandSchema() => Schema(["name", "commands"],
        ("name", Str("Name (used by the command.triggered trigger)")),
        ("commands", StrArray("Trigger texts, e.g. [\"!hello\", \"!hi\"]")),
        ("group", Str("Group")),
        ("enabled", Bool("Default true")),
        ("mode", Enum("Basic", "Regex")),
        ("location", Enum("Start", "Exact", "Anywhere")),
        ("caseSensitive", Bool("Default false")),
        ("globalCooldownSeconds", Num("Cooldown for everyone")),
        ("userCooldownSeconds", Num("Cooldown per user")),
        ("minimumRole", Enum("Everyone", "Subscriber", "Vip", "Moderator", "Broadcaster")),
        ("sources", StrArray("Platforms it listens on (empty = all)")));

    private static JsonObject TimerSchema() => Schema(["name"],
        ("name", Str("Name (used by the core.timer trigger)")),
        ("enabled", Bool("Default true")),
        ("intervalSeconds", Int("Fire every N seconds (default 300)")),
        ("repeat", Bool("false: fire once after being enabled")),
        ("randomInterval", Bool("Pick each interval between min and max")),
        ("minIntervalSeconds", Int("Random interval minimum")),
        ("maxIntervalSeconds", Int("Random interval maximum")),
        ("minChatLines", Int("Only fire after this many chat messages")),
        ("cron", Str("5-field cron schedule in local time (overrides the interval), e.g. */15 * * * *")));
}
