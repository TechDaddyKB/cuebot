using System.Text.Json.Nodes;

namespace Cuebot.Server.Api;

/// <summary>The OpenAPI 3.1 description of <c>/api/v1</c>, built by hand to match <see cref="ApiServer"/>'s routes.</summary>
public static class OpenApiDocument
{
    public static JsonObject Build(string serverUrl, string version)
    {
        var paths = new JsonObject
        {
            ["/status"] = new JsonObject { ["get"] = Op("getStatus", "System", "App version, platform connections, servers and counts", responses: Ok(Ref("Status"))) },
            ["/openapi.json"] = new JsonObject { ["get"] = Op("getOpenApi", "System", "This document", responses: Ok(new JsonObject { ["type"] = "object" })) },
            ["/actions"] = new JsonObject
            {
                ["get"] = Op("listActions", "Actions", "Action summaries", [Query("group", "Only this group"), Query("search", "Name or group contains")],
                    responses: Ok(Obj(("actions", Arr(Ref("ActionSummary"))), ("count", Int())))),
                ["post"] = Op("createAction", "Actions", "Create an action (validated against the trigger and sub-action catalogue)",
                    body: Ref("Action"), responses: Responses(("201", "Created", Saved("action", "Action")), Bad(), Err("409", "An action with this id exists"))),
            },
            ["/actions/{id}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Action id or name")),
                ["get"] = Op("getAction", "Actions", "The full action: triggers and sub-actions (with branches)", responses: Responses(("200", "OK", Ref("Action")), NotFound())),
                ["put"] = Op("replaceAction", "Actions", "Replace the action (the id in the path wins)", body: Ref("Action"),
                    responses: Responses(("200", "OK", Saved("action", "Action")), Bad(), NotFound())),
                ["patch"] = Op("patchAction", "Actions", "Change only the given top-level fields (e.g. {\"name\": ...} or {\"subActions\": [...]})",
                    body: new JsonObject { ["type"] = "object" }, responses: Responses(("200", "OK", Saved("action", "Action")), Bad(), NotFound())),
                ["delete"] = Op("deleteAction", "Actions", "Delete the action", responses: Responses(("200", "Deleted", Ref("Deleted")), NotFound())),
            },
            ["/actions/{id}/run"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Action id or name")),
                ["post"] = Op("runAction", "Actions", "Run the action. Without wait it is queued (202); with wait the response has the run result",
                    body: Obj(("args", new JsonObject { ["type"] = "object", ["description"] = "Arguments, available as %name% in the sub-actions" }),
                        ("wait", Bool()), ("timeoutSeconds", Num())),
                    responses: Responses(("202", "Queued", Ref("RunResult")), ("200", "Finished (wait)", Ref("RunResult")), NotFound(), Err("409", "The action is disabled"))),
            },
            ["/actions/{id}/enable"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Action id or name")),
                ["post"] = Op("enableAction", "Actions", "Enable the action", responses: Responses(("200", "OK", Ref("ActionSummary")), NotFound())),
            },
            ["/actions/{id}/disable"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Action id or name")),
                ["post"] = Op("disableAction", "Actions", "Disable the action", responses: Responses(("200", "OK", Ref("ActionSummary")), NotFound())),
            },
            ["/triggers"] = new JsonObject
            {
                ["get"] = Op("listTriggers", "Catalogue", "Trigger types", [Query("category", "Exact category"), Query("search", "Words in type id, name or category")],
                    responses: Ok(Obj(("triggers", Arr(Ref("TriggerType"))), ("count", Int()), ("categories", Arr(Str()))))),
            },
            ["/triggers/{type}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("type", "Trigger type id")),
                ["get"] = Op("describeTrigger", "Catalogue", "One trigger type with its settings JSON Schema", responses: Responses(("200", "OK", Ref("TriggerType")), NotFound())),
            },
            ["/subactions"] = new JsonObject
            {
                ["get"] = Op("listSubActions", "Catalogue", "Sub-action types", [Query("category", "Exact category"), Query("search", "Words in type id, name or category")],
                    responses: Ok(Obj(("subActions", Arr(Ref("SubActionType"))), ("count", Int()), ("categories", Arr(Str()))))),
            },
            ["/subactions/{type}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("type", "Sub-action type id")),
                ["get"] = Op("describeSubAction", "Catalogue", "One sub-action type with its parameters JSON Schema", responses: Responses(("200", "OK", Ref("SubActionType")), NotFound())),
            },
            ["/scriptfunctions"] = new JsonObject
            {
                ["get"] = Op("listScriptFunctions", "Catalogue", "Functions callable from C#/Python code sub-actions", [Query("search", "Name or description contains")],
                    responses: Ok(Obj(("functions", Arr(Obj(("name", Str()), ("description", Str()), ("parameters", Arr(Str()))))), ("count", Int())))),
            },
            ["/commands"] = new JsonObject
            {
                ["get"] = Op("listCommands", "Commands", "All chat commands", responses: Ok(Obj(("commands", Arr(Ref("Command"))), ("count", Int())))),
                ["post"] = Op("createCommand", "Commands", "Create a chat command. Link it to an action with a 'command.triggered' trigger",
                    body: Ref("Command"), responses: Responses(("201", "Created", Saved("command", "Command")), Bad(), Err("409", "A command with this id exists"))),
            },
            ["/commands/{id}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Command id or name")),
                ["get"] = Op("getCommand", "Commands", "The command and the actions it runs (linkedActions)", responses: Responses(("200", "OK", Ref("Command")), NotFound())),
                ["put"] = Op("replaceCommand", "Commands", "Replace the command", body: Ref("Command"), responses: Responses(("200", "OK", Saved("command", "Command")), Bad(), NotFound())),
                ["patch"] = Op("patchCommand", "Commands", "Change only the given fields", body: new JsonObject { ["type"] = "object" },
                    responses: Responses(("200", "OK", Saved("command", "Command")), Bad(), NotFound())),
                ["delete"] = Op("deleteCommand", "Commands", "Delete the command", responses: Responses(("200", "Deleted", Ref("Deleted")), NotFound())),
            },
            ["/timers"] = new JsonObject
            {
                ["get"] = Op("listTimers", "Timers", "Timers with their runtime state", responses: Ok(Obj(("timers", Arr(Ref("Timer"))), ("count", Int())))),
                ["post"] = Op("createTimer", "Timers", "Create a timer. Actions run from it with a 'core.timer' trigger", body: Ref("Timer"),
                    responses: Responses(("201", "Created", Obj(("timer", Ref("Timer")), ("hint", Str()))), Bad(), Err("409", "A timer with this id exists"))),
            },
            ["/timers/{id}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Timer id or name")),
                ["get"] = Op("getTimer", "Timers", "One timer", responses: Responses(("200", "OK", Ref("Timer")), NotFound())),
                ["put"] = Op("replaceTimer", "Timers", "Replace the timer", body: Ref("Timer"), responses: Responses(("200", "OK", Obj(("timer", Ref("Timer")))), Bad(), NotFound())),
                ["patch"] = Op("patchTimer", "Timers", "Change only the given fields", body: new JsonObject { ["type"] = "object" },
                    responses: Responses(("200", "OK", Obj(("timer", Ref("Timer")))), Bad(), NotFound())),
                ["delete"] = Op("deleteTimer", "Timers", "Delete the timer", responses: Responses(("200", "Deleted", Ref("Deleted")), NotFound())),
            },
            ["/timers/{id}/state"] = new JsonObject
            {
                ["parameters"] = new JsonArray(IdParam("Timer id or name")),
                ["post"] = Op("setTimerState", "Timers", "Runtime enable/disable or interval change (not saved, like the 'Set timer state' sub-action)",
                    body: Obj(("enabled", Bool()), ("intervalSeconds", Int())), responses: Responses(("200", "OK", Ref("Timer")), Bad(), NotFound())),
            },
            ["/globals"] = new JsonObject
            {
                ["get"] = Op("listGlobals", "Globals", "Global variables (~name~)", [Query("persisted", "true: persisted only, false: temporary only", Bool())],
                    responses: Ok(Obj(("variables", Arr(Ref("GlobalVariable"))), ("count", Int())))),
            },
            ["/globals/{name}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("name", "Variable name")),
                ["get"] = Op("getGlobal", "Globals", "One global variable", responses: Responses(("200", "OK", Ref("GlobalVariable")), NotFound())),
                ["put"] = Op("setGlobal", "Globals", "Create or change a global variable", body: Obj(("value", new JsonObject { ["description"] = "string, number, boolean or null" }), ("persisted", Bool())),
                    responses: Responses(("200", "OK", Ref("GlobalVariable")), Bad())),
                ["delete"] = Op("deleteGlobal", "Globals", "Delete a global variable", responses: Responses(("200", "Deleted", Ref("Deleted")), NotFound())),
            },
            ["/history"] = new JsonObject
            {
                ["get"] = Op("getHistory", "Runs", "Recent and pending action runs, newest first",
                    [Query("limit", "Maximum runs (default 50)", Int()), Query("action", "Only this action (id or name)"), Query("includeArgs", "Include the arguments", Bool())],
                    responses: Ok(Obj(("runs", Arr(Ref("Run"))), ("count", Int())))),
            },
            ["/polls"] = new JsonObject
            {
                ["get"] = Op("getPolls", "Polls", "The latest poll and prediction from each source (twitch, youtube, cuebot) and Cuebot's vote and bet commands",
                    responses: Ok(Obj(("polls", Arr(Ref("Poll"))), ("voteCommand", Str()), ("betCommand", Str())))),
                ["post"] = Op("startChatPoll", "Polls", "Start a Cuebot chat poll, voted with the vote command on every platform (duration 0: until ended)",
                    body: Obj(("title", Str()), ("choices", Arr(Str())), ("duration", Int())),
                    responses: Responses(("200", "Started", Ref("Poll")), Bad(), Err("409", "A chat poll is already running"))),
            },
            ["/polls/end"] = new JsonObject
            {
                ["post"] = Op("endChatPoll", "Polls", "End the running Cuebot chat poll", responses: Responses(("200", "Ended", Ref("Poll")), NotFound())),
            },
            ["/predictions"] = new JsonObject
            {
                ["post"] = Op("startChatPrediction", "Polls", "Start a Cuebot prediction: viewers bet Cuebot points with the bet command (window 0: until locked)",
                    body: Obj(("title", Str()), ("outcomes", Arr(Str())), ("window", Int()), ("currency", Str())),
                    responses: Responses(("200", "Started", Ref("Poll")), Bad(), Err("409", "A prediction is still open, or points are not available"))),
            },
            ["/predictions/{command}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("command", "lock, resolve (body {winner: number from 1 or title}) or cancel (refunds every bet)")),
                ["post"] = Op("controlChatPrediction", "Polls", "Lock, resolve or cancel the Cuebot prediction", body: Obj(("winner", Str())),
                    responses: Responses(("200", "OK", Ref("Poll")), Bad(), NotFound())),
            },
            ["/speech"] = new JsonObject
            {
                ["get"] = Op("getSpeech", "Speech", "Cuebot speech's state", responses: Responses(("200", "OK", SpeechState()), Err("503", "Speech is not available"))),
            },
            ["/speech/{command}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("command", "next, pause, resume or skip")),
                ["post"] = Op("controlSpeech", "Speech",
                    "Control the speech queue. next stops the text playing and, while the queue is paused, lets exactly one waiting text play: pause, then call next to step through the queue one text at a time",
                    responses: Responses(("200", "OK", SpeechState()), NotFound(), Err("503", "Speech is not available"))),
            },
            ["/alerts"] = new JsonObject
            {
                ["get"] = Op("getAlerts", "Overlays", "Whether overlay alert queues are paused or muted",
                    responses: Responses(("200", "OK", Obj(("paused", Bool()), ("muted", Bool()))), Err("503", "Overlays are not available"))),
            },
            ["/alerts/{command}"] = new JsonObject
            {
                ["parameters"] = new JsonArray(PathParam("command", "pause, resume, skip, mute or unmute")),
                ["post"] = Op("controlAlerts", "Overlays", "Pause, resume, skip or mute the alert queue on every overlay page",
                    responses: Responses(("200", "OK", Obj(("paused", Bool()), ("muted", Bool()))), NotFound(), Err("503", "Overlays are not available"))),
            },
            ["/chat"] = new JsonObject
            {
                ["get"] = Op("getChat", "Chat", "Recent combined chat messages and activity feed", [Query("limit", "Maximum items (default 50)", Int())],
                    responses: Ok(Obj(("messages", Arr(new JsonObject { ["type"] = "object" })), ("feed", Arr(new JsonObject { ["type"] = "object" })),
                        ("platforms", Arr(Str())), ("sendablePlatforms", Arr(Str()))))),
                ["post"] = Op("sendChat", "Chat", "Send a chat message (platform: Twitch, YouTube, Kick... or All)", body: Obj(("message", Str()), ("platform", Str())),
                    responses: Responses(("200", "Sent", Obj(("sent", Bool()), ("platforms", Arr(Str())))), Bad(), Err("409", "No platform could send"))),
            },
            ["/import/preview"] = new JsonObject
            {
                ["post"] = Op("previewImport", "Import/export", "What an import would add or overwrite; changes nothing", body: Obj(("data", Str())),
                    responses: Responses(("200", "OK", Ref("ImportPlan")), Bad())),
            },
            ["/import/apply"] = new JsonObject
            {
                ["post"] = Op("applyImport", "Import/export", "Import a Cuebot bundle (CUEBOT1:...) or a Streamer.bot export. Nothing in it is run",
                    body: Obj(("data", Str()), ("mode", Enum("overwrite", "addAsNew", "skip")),
                        ("items", Arr(Obj(("kind", Enum("action", "queue", "command", "timer", "global")), ("name", Str()), ("sourceId", Str()), ("include", Bool()), ("mode", Enum("overwrite", "addAsNew", "skip")))))),
                    responses: Responses(("200", "Imported", Obj(("summary", Str()), ("added", Int()), ("overwritten", Int()), ("skipped", Int()), ("renamed", Arr(Str())))), Bad())),
            },
            ["/export"] = new JsonObject
            {
                ["get"] = Op("export", "Import/export", "A Cuebot import string for the named items (comma separated ids or names)",
                    [Query("actions", "Actions"), Query("commands", "Commands"), Query("timers", "Timers"), Query("globals", "Global variables"),
                        Query("includeDependencies", "Include referenced actions, queues... (default true)", Bool()), Query("name", "Bundle name")],
                    responses: Responses(("200", "OK", Obj(("data", Str()), ("actions", Int()), ("commands", Int()), ("timers", Int()), ("globals", Int()), ("queues", Int()))), Bad(), NotFound())),
            },
            ["/events"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["operationId"] = "events",
                    ["tags"] = new JsonArray("Events"),
                    ["summary"] = "Server-Sent Events: every Cuebot event ('event:' is its type, e.g. twitch.chatMessage) plus action.runCompleted",
                    ["parameters"] = new JsonArray(Query("types", "Comma separated event types; a trailing * matches a prefix (twitch.*)")),
                    ["responses"] = new JsonObject
                    {
                        ["200"] = new JsonObject
                        {
                            ["description"] = "text/event-stream; data is {type, time, args}",
                            ["content"] = new JsonObject { ["text/event-stream"] = new JsonObject { ["schema"] = Str() } },
                        },
                        ["401"] = ErrorResponse("Missing or wrong token"),
                        ["429"] = ErrorResponse("Too many open event streams"),
                    },
                },
            },
        };

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Cuebot local API",
                ["version"] = version,
                ["description"] = "Manage Cuebot's actions, commands, timers and variables. Every request needs 'Authorization: Bearer <token>' "
                                  + "(Servers page). Action, command and timer JSON has the same shape as actions.json / commands.json. "
                                  + "Parameter values are strings and may use %arguments%, ~globals~ and $functions()$.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = serverUrl }),
            ["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject { ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } },
                ["schemas"] = Schemas(),
            },
        };
    }

    private static JsonObject Schemas() => new()
    {
        ["Error"] = Obj(("error", Str()), ("details", Arr(Ref("Problem")))),
        ["Poll"] = Obj(("source", Str("twitch, youtube or cuebot")), ("kind", Str("poll or prediction")), ("id", Str()), ("title", Str()),
            ("status", Str("active, locked, ended or canceled")),
            ("choices", Arr(Obj(("id", Str()), ("title", Str()), ("votes", Int()), ("points", Int()), ("percent", new JsonObject { ["type"] = "number" })))),
            ("winner", Int()), ("endsAt", Str()), ("totalVotes", Int()), ("totalPoints", Int()), ("currency", Str()), ("command", Str())),
        ["Problem"] = Obj(("path", Str()), ("message", Str())),
        ["Deleted"] = Obj(("deleted", Bool()), ("id", Str()), ("name", Str())),
        ["Status"] = Obj(("app", Obj(("name", Str()), ("version", Str()), ("runtime", Str()), ("os", Str()), ("instanceId", Str()), ("apiVersion", Str()))),
            ("connections", Arr(Obj(("name", Str()), ("state", Str()), ("status", Str())))),
            ("servers", Arr(Obj(("kind", Str()), ("name", Str()), ("enabled", Bool()), ("running", Bool()), ("port", Int()), ("status", Str())))),
            ("counts", new JsonObject { ["type"] = "object", ["additionalProperties"] = Int() }),
            ("chat", Obj(("platforms", Arr(Str())), ("sendablePlatforms", Arr(Str()))))),
        ["ActionSummary"] = Obj(("id", Str()), ("name", Str()), ("group", Str()), ("enabled", Bool()), ("queue", Str()),
            ("triggers", Arr(Obj(("type", Str()), ("enabled", Bool())))), ("subActionCount", Int())),
        ["Action"] = Obj(
            ("id", Str("uuid; generated when missing")), ("name", Str()), ("group", Str()), ("enabled", Bool()),
            ("queue", Str("Action queue name; empty = Default")), ("concurrent", Bool()), ("randomAction", Bool()),
            ("concurrentSubActions", Bool()), ("alwaysRun", Bool()), ("excludeFromHistory", Bool()),
            ("triggers", Arr(Ref("Trigger"))), ("subActions", Arr(Ref("SubAction")))),
        ["Trigger"] = Obj(("id", Str()), ("type", Str("A trigger type id from GET /triggers")), ("enabled", Bool()),
            ("settings", new JsonObject { ["type"] = "object", ["additionalProperties"] = Str() })),
        ["SubAction"] = Obj(("id", Str()), ("type", Str("A sub-action type id from GET /subactions")), ("enabled", Bool()),
            ("parameters", new JsonObject { ["type"] = "object", ["additionalProperties"] = Str() }),
            ("branches", new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Child steps per branch name (e.g. True/False for If, Steps for Group)",
                ["additionalProperties"] = Arr(Ref("SubAction")),
            }),
            ("weight", Num())),
        ["Parameter"] = Obj(("key", Str()), ("label", Str()), ("kind", Enum("text", "multiline", "number", "boolean", "choice", "code")),
            ("default", Str()), ("choices", Arr(Str())), ("language", Str()), ("help", Str()), ("suggestions", Arr(Str()))),
        ["TriggerType"] = Obj(("typeId", Str()), ("name", Str()), ("category", Str()), ("eventTypes", Arr(Str())), ("parameters", Arr(Ref("Parameter"))),
            ("settingsSchema", new JsonObject { ["type"] = "object" })),
        ["SubActionType"] = Obj(("typeId", Str()), ("name", Str()), ("category", Str()), ("branches", Arr(Str())), ("dynamicBranches", Bool()),
            ("container", Bool()), ("parameters", Arr(Ref("Parameter"))), ("parametersSchema", new JsonObject { ["type"] = "object" })),
        ["Command"] = Obj(("id", Str()), ("name", Str()), ("group", Str()), ("enabled", Bool()), ("mode", Enum("Basic", "Regex")),
            ("location", Enum("Start", "Exact", "Anywhere")), ("commands", Arr(Str("e.g. !hello"))), ("sources", Arr(Str())), ("caseSensitive", Bool()),
            ("globalCooldownSeconds", Num()), ("userCooldownSeconds", Num()), ("minimumRole", Enum("Everyone", "Subscriber", "Vip", "Moderator", "Broadcaster")),
            ("linkedActions", Arr(Obj(("id", Str()), ("name", Str()))))),
        ["Timer"] = Obj(("id", Str()), ("name", Str()), ("enabled", Bool()), ("intervalSeconds", Int()), ("repeat", Bool()),
            ("randomInterval", Bool()), ("minIntervalSeconds", Int()), ("maxIntervalSeconds", Int()), ("minChatLines", Int()),
            ("cron", Str("5-field cron, local time")),
            ("state", Obj(("enabled", Bool()), ("intervalSeconds", Int()), ("nextFire", Str()), ("chatLines", Int())))),
        ["GlobalVariable"] = Obj(("name", Str()), ("value", new JsonObject()), ("type", Str()), ("persisted", Bool()), ("lastWrite", Str())),
        ["Run"] = Obj(("runId", Str()), ("actionId", Str()), ("actionName", Str()), ("queue", Str()), ("source", Str()),
            ("state", Enum("Pending", "Running", "Completed", "Cancelled", "Failed")), ("queuedAt", Str()), ("startedAt", Str()),
            ("completedAt", Str()), ("durationMs", Int()), ("error", Str()), ("depth", Int()),
            ("args", new JsonObject { ["type"] = "object" }), ("argsAfterRun", new JsonObject { ["type"] = "object" })),
        ["RunResult"] = Obj(("queued", Bool()), ("completed", Bool()), ("success", Bool()), ("action", Obj(("id", Str()), ("name", Str()))),
            ("run", Ref("Run")), ("message", Str())),
        ["ImportPlan"] = Obj(("source", Enum("Cuebot", "StreamerBot")), ("header", new JsonObject { ["type"] = "object" }),
            ("items", Arr(Obj(("kind", Str()), ("sourceId", Str()), ("name", Str()), ("group", Str()), ("conflict", Enum("None", "SameId", "SameName")),
                ("conflictTargetName", Str()), ("include", Bool()), ("mode", Str()), ("containsCode", Bool()), ("placeholderCount", Int()),
                ("details", Arr(Str())), ("warnings", Arr(Str()))))),
            ("warnings", Arr(Str())), ("mapped", Arr(Str())), ("placeholders", Int()), ("containsCode", Bool())),
    };

    // ---- builders --------------------------------------------------------------------------------------------------

    private static JsonObject Op(string id, string tag, string summary, JsonObject[]? parameters = null, JsonObject? body = null, JsonObject? responses = null)
    {
        var op = new JsonObject { ["operationId"] = id, ["tags"] = new JsonArray(tag), ["summary"] = summary };
        if (parameters is { Length: > 0 }) op["parameters"] = new JsonArray([.. parameters]);
        if (body is not null)
        {
            op["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = body } },
            };
        }
        var r = responses ?? Ok(new JsonObject { ["type"] = "object" });
        r["401"] = ErrorResponse("Missing or wrong token");
        r["403"] = ErrorResponse("Origin not allowed");
        op["responses"] = r;
        return op;
    }

    private static JsonObject Ok(JsonObject schema) => Responses(("200", "OK", schema));

    private static JsonObject Responses(params (string Code, string Description, JsonObject? Schema)[] items)
    {
        var r = new JsonObject();
        foreach (var (code, description, schema) in items)
        {
            var response = new JsonObject { ["description"] = description };
            if (schema is not null) response["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } };
            r[code] = response;
        }
        return r;
    }

    private static (string, string, JsonObject?) Bad() => ("400", "Invalid request (details lists each problem)", Ref("Error"));
    private static (string, string, JsonObject?) NotFound() => ("404", "Not found", Ref("Error"));
    private static (string, string, JsonObject?) Err(string code, string description) => (code, description, Ref("Error"));

    private static JsonObject ErrorResponse(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref("Error") } },
    };

    private static JsonObject SpeechState() =>
        Obj(("enabled", Bool()), ("paused", Bool()), ("queueLength", Int()), ("current", Str("The text playing, or null")), ("stepped", Bool()));

    private static JsonObject Saved(string key, string schema) => Obj((key, Ref(schema)), ("warnings", Arr(Ref("Problem"))));

    private static JsonObject IdParam(string description) => PathParam("id", description);

    private static JsonObject PathParam(string name, string description) => new()
    {
        ["name"] = name, ["in"] = "path", ["required"] = true, ["description"] = description, ["schema"] = Str(),
    };

    private static JsonObject Query(string name, string description, JsonObject? schema = null) => new()
    {
        ["name"] = name, ["in"] = "query", ["required"] = false, ["description"] = description, ["schema"] = schema ?? Str(),
    };

    private static JsonObject Ref(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };
    private static JsonObject Str(string? description = null) => description is null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int() => new() { ["type"] = "integer" };
    private static JsonObject Num() => new() { ["type"] = "number" };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };
    private static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = CatalogJson.Strings(values) };

    private static JsonObject Obj(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }
}
