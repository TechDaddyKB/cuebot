using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Server.Mcp;

namespace Cuebot.Server.Tests;

public sealed class McpTests : IAsyncLifetime
{
    private static readonly string[] RequiredTools =
    [
        "list_actions", "get_action", "create_action", "update_action", "delete_action", "run_action", "set_action_enabled",
        "list_trigger_types", "list_subaction_types", "describe_subaction_type", "list_commands", "create_command", "update_command",
        "list_timers", "create_timer", "get_globals", "set_global", "get_status", "get_recent_chat", "send_chat_message", "get_history",
        "preview_import", "apply_import", "export_bundle",
    ];

    private ApiHost _host = null!;
    private TestBot Bot => _host.Bot;

    public async Task InitializeAsync()
    {
        _host = await ApiHost.StartAsync();
        await _host.InitializeAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static JsonObject Args(params (string Key, JsonNode? Value)[] values)
    {
        var args = new JsonObject();
        foreach (var (key, value) in values) args[key] = value;
        return args;
    }

    private static JsonObject NewAction(string name) => new()
    {
        ["name"] = name,
        ["triggers"] = new JsonArray(new JsonObject { ["type"] = "command.triggered", ["settings"] = new JsonObject { ["command"] = "Hello" } }),
        ["subActions"] = new JsonArray(
            new JsonObject { ["type"] = "core.log", ["parameters"] = new JsonObject { ["message"] = "Hello %user%" } },
            new JsonObject { ["type"] = CaptureSubAction.Id }),
    };

    /// <summary>Calls a destructive tool through the confirmation flow.</summary>
    private async Task<(bool IsError, JsonObject Content)> ConfirmedAsync(string tool, JsonObject args)
    {
        var (isError, first) = await _host.CallAsync(tool, args);
        Assert.False(isError, first.ToJsonString());
        Assert.True(first["confirmationRequired"]!.GetValue<bool>());
        var confirmed = (JsonObject)args.DeepClone();
        confirmed[McpConfirmations.ArgumentName] = first["confirmationToken"]!.GetValue<string>();
        return await _host.CallAsync(tool, confirmed);
    }

    // ---- handshake and protocol ----

    [Fact]
    public async Task Initialize_negotiates_version_and_capabilities()
    {
        var result = await _host.InitializeAsync();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("cuebot", result["serverInfo"]!["name"]!.GetValue<string>());
        var capabilities = result["capabilities"]!.AsObject();
        Assert.True(capabilities.ContainsKey("tools"));
        Assert.True(capabilities.ContainsKey("resources"));
        Assert.True(capabilities.ContainsKey("prompts"));
        Assert.False(string.IsNullOrEmpty(result["instructions"]?.GetValue<string>()));

        Assert.Equal("2024-11-05", (await _host.InitializeAsync("2024-11-05"))["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpServer.LatestProtocolVersion, (await _host.InitializeAsync("1999-01-01"))["protocolVersion"]!.GetValue<string>());
        Assert.Empty(await _host.RequestAsync("ping"));
    }

    [Fact]
    public async Task Sessions_unknown_ids_delete_and_get()
    {
        var session = _host.Session!;
        _host.Session = "not-a-session";
        var (unknown, _, _) = await _host.McpPostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" });
        Assert.Equal(404, unknown);
        _host.Session = session;

        using var get = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        using var getResponse = await _host.Http.SendAsync(get);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Add(McpServer.SessionHeader, session);
        using var deleted = await _host.Http.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var (gone, _, _) = await _host.McpPostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "ping" });
        Assert.Equal(404, gone);

        _host.Session = null;   // requests without a session are accepted
        Assert.Empty(await _host.RequestAsync("ping"));
    }

    [Fact]
    public async Task Json_rpc_errors_batches_and_headers()
    {
        var unknownMethod = await _host.RpcAsync("nope/nothing");
        Assert.Equal(-32601, unknownMethod["error"]!["code"]!.GetValue<int>());

        var unknownTool = await _host.RpcAsync("tools/call", new JsonObject { ["name"] = "no_such_tool" });
        Assert.Equal(-32602, unknownTool["error"]!["code"]!.GetValue<int>());

        using var garbage = new StringContent("{oops", Encoding.UTF8, "application/json");
        using var parse = await _host.Http.PostAsync("/mcp", garbage);
        Assert.Equal(HttpStatusCode.BadRequest, parse.StatusCode);
        Assert.Equal(-32700, JsonNode.Parse(await parse.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<int>());

        var (status, batch, _) = await _host.McpPostAsync(new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 10, ["method"] = "ping" },
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 3 } },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 11, ["method"] = "tools/list" }));
        Assert.Equal(200, status);
        Assert.Equal(2, batch!.AsArray().Count);

        var (invalid, body, _) = await _host.McpPostAsync(new JsonObject { ["id"] = 1, ["method"] = "ping" });
        Assert.Equal(200, invalid);
        Assert.Equal(-32600, body!["error"]!["code"]!.GetValue<int>());

        using var badVersion = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        badVersion.Headers.Add(McpServer.ProtocolHeader, "1999-01-01");
        using var badVersionResponse = await _host.Http.SendAsync(badVersion);
        Assert.Equal(HttpStatusCode.BadRequest, badVersionResponse.StatusCode);

        using var sseOnly = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":5,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        sseOnly.Headers.Accept.ParseAdd("text/event-stream");
        using var sse = await _host.Http.SendAsync(sseOnly);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"id\":5", await sse.Content.ReadAsStringAsync());
    }

    // ---- tools ----

    [Fact]
    public async Task Tools_list_has_every_tool_with_schemas_and_annotations()
    {
        var tools = (await _host.RequestAsync("tools/list"))["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var names = tools.Select(t => t["name"]!.GetValue<string>()).ToList();
        foreach (var required in RequiredTools) Assert.Contains(required, names);
        Assert.Equal(names.Count, names.Distinct().Count());

        foreach (var tool in tools)
        {
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            var annotations = tool["annotations"]!;
            var name = tool["name"]!.GetValue<string>();
            var destructive = annotations["destructiveHint"]!.GetValue<bool>();
            Assert.Equal(name.StartsWith("delete_", StringComparison.Ordinal) || name is "apply_import" or "send_chat_message", destructive);
            if (annotations["readOnlyHint"]!.GetValue<bool>()) Assert.False(destructive);
            if (destructive) Assert.True(tool["inputSchema"]!["properties"]!.AsObject().ContainsKey(McpConfirmations.ArgumentName));
            if (tool["inputSchema"]!["required"] is JsonArray required)
            {
                foreach (var key in required) Assert.True(tool["inputSchema"]!["properties"]!.AsObject().ContainsKey(key!.GetValue<string>()));
            }
        }
        var create = tools.Single(t => t["name"]!.GetValue<string>() == "create_action");
        Assert.NotNull(create["inputSchema"]!["$defs"]!["subAction"]);
        Assert.True(tools.Single(t => t["name"]!.GetValue<string>() == "list_actions")["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Action_tools_happy_paths()
    {
        var (createError, created) = await _host.CallAsync("create_action", Args(("action", NewAction("Greet"))));
        Assert.False(createError, created.ToJsonString());
        var id = created["action"]!["id"]!.GetValue<string>();
        Assert.NotNull(Bot.Profile.Snapshot.FindAction("Greet"));

        var (_, list) = await _host.CallAsync("list_actions", Args(("search", "gre")));
        Assert.Equal(1, list["count"]!.GetValue<int>());
        var (_, got) = await _host.CallAsync("get_action", Args(("action", "Greet")));
        Assert.Equal(id, got["id"]!.GetValue<string>());

        var (updateError, _) = await _host.CallAsync("update_action", Args(("action", id), ("changes", new JsonObject { ["group"] = "Chat" })));
        Assert.False(updateError);
        Assert.Equal("Chat", Bot.Profile.Snapshot.FindAction("Greet")!.Group);
        Assert.Equal(2, Bot.Profile.Snapshot.FindAction("Greet")!.SubActions.Count);

        var (runError, run) = await _host.CallAsync("run_action", Args(("action", "Greet"), ("args", new JsonObject { ["user"] = "ai" })));
        Assert.False(runError, run.ToJsonString());
        Assert.True(run["completed"]!.GetValue<bool>());
        Assert.Equal("MCP", run["run"]!["source"]!.GetValue<string>());
        Assert.Equal("ai", Bot.Capture.ByAction["Greet"]["user"]);

        var (_, history) = await _host.CallAsync("get_history", Args(("action", "Greet")));
        Assert.Equal(1, history["count"]!.GetValue<int>());

        var (_, disabled) = await _host.CallAsync("set_action_enabled", Args(("action", "Greet"), ("enabled", false)));
        Assert.False(disabled["enabled"]!.GetValue<bool>());
        var (disabledRun, error) = await _host.CallAsync("run_action", Args(("action", "Greet")));
        Assert.True(disabledRun);
        Assert.Contains("disabled", error["error"]!.GetValue<string>());

        var (deleteError, deleted) = await ConfirmedAsync("delete_action", Args(("action", "Greet")));
        Assert.False(deleteError, deleted.ToJsonString());
        Assert.Null(Bot.Profile.Snapshot.FindAction("Greet"));
    }

    [Fact]
    public async Task Tool_errors_are_reported_as_tool_results()
    {
        var bad = NewAction("Broken");
        bad["subActions"]!.AsArray().Add(new JsonObject { ["type"] = "does.not.exist" });
        var (isError, result) = await _host.CallAsync("create_action", Args(("action", bad)));
        Assert.True(isError);
        Assert.Equal("subActions[2].type", result["details"]![0]!["path"]!.GetValue<string>());

        var (missing, missingResult) = await _host.CallAsync("get_action", Args(("action", "Nope")));
        Assert.True(missing);
        Assert.Contains("not found", missingResult["error"]!.GetValue<string>());

        var (noArg, noArgResult) = await _host.CallAsync("get_action");
        Assert.True(noArg);
        Assert.Contains("'action' is required", noArgResult["error"]!.GetValue<string>());

        var (wrongType, _) = await _host.CallAsync("set_action_enabled", Args(("action", "x"), ("enabled", "maybe")));
        Assert.True(wrongType);
    }

    [Fact]
    public async Task Catalogue_tools()
    {
        var (_, triggers) = await _host.CallAsync("list_trigger_types", Args(("search", "command")));
        Assert.Contains(triggers["triggers"]!.AsArray(), t => t!["typeId"]!.GetValue<string>() == "command.triggered");
        var (_, trigger) = await _host.CallAsync("describe_trigger_type", Args(("type", "core.timer")));
        Assert.NotNull(trigger["settingsSchema"]);

        var (_, subs) = await _host.CallAsync("list_subaction_types", Args(("category", "Logic")));
        Assert.All(subs["subActions"]!.AsArray(), s => Assert.Equal("Logic", s!["category"]!.GetValue<string>()));
        Assert.Contains(subs["subActions"]!.AsArray(), s => s!["typeId"]!.GetValue<string>() == "core.if");

        var (_, delay) = await _host.CallAsync("describe_subaction_type", Args(("type", "core.delay")));
        Assert.Equal("string", delay["parametersSchema"]!["properties"]!["milliseconds"]!["type"]!.GetValue<string>());
        var (unknown, _) = await _host.CallAsync("describe_subaction_type", Args(("type", "x.y")));
        Assert.True(unknown);

        var (_, functions) = await _host.CallAsync("list_script_functions");
        Assert.True(functions["count"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Command_timer_and_global_tools()
    {
        var (commandError, command) = await _host.CallAsync("create_command", Args(("command", new JsonObject { ["name"] = "Dice", ["commands"] = new JsonArray("!dice") })));
        Assert.False(commandError, command.ToJsonString());
        Assert.NotNull(Bot.Commands.Find("Dice"));
        var (_, updated) = await _host.CallAsync("update_command", Args(("command", "Dice"), ("changes", new JsonObject { ["userCooldownSeconds"] = 10 })));
        Assert.Equal(10, updated["command"]!["userCooldownSeconds"]!.GetValue<int>());
        var (_, commands) = await _host.CallAsync("list_commands");
        Assert.Equal(1, commands["count"]!.GetValue<int>());
        var (_, one) = await _host.CallAsync("get_command", Args(("command", "Dice")));
        Assert.Equal("Dice", one["name"]!.GetValue<string>());

        var (timerError, timer) = await _host.CallAsync("create_timer", Args(("timer", new JsonObject { ["name"] = "Ads", ["intervalSeconds"] = 900 })));
        Assert.False(timerError, timer.ToJsonString());
        var (_, timers) = await _host.CallAsync("list_timers");
        Assert.Equal(900, timers["timers"]![0]!["intervalSeconds"]!.GetValue<int>());
        await _host.CallAsync("update_timer", Args(("timer", "Ads"), ("changes", new JsonObject { ["intervalSeconds"] = 1200 })));
        Assert.Equal(1200, Bot.Profile.Snapshot.Timers.Single().IntervalSeconds);
        var (_, state) = await _host.CallAsync("set_timer_state", Args(("timer", "Ads"), ("enabled", false)));
        Assert.False(state["state"]!["enabled"]!.GetValue<bool>());

        await _host.CallAsync("set_global", Args(("name", "secretish"), ("value", "SECRET-VALUE-123"), ("persisted", false)));
        Assert.Equal("SECRET-VALUE-123", Bot.Globals.Get("secretish"));
        var (_, globals) = await _host.CallAsync("get_globals");
        Assert.Equal(1, globals["count"]!.GetValue<int>());
        var (_, global) = await _host.CallAsync("get_globals", Args(("name", "secretish")));
        Assert.Equal("SECRET-VALUE-123", global["value"]!.GetValue<string>());
        Assert.DoesNotContain(Bot.Log.Recent(), e => e.Message.Contains("SECRET-VALUE-123", StringComparison.Ordinal));
        Assert.Contains(Bot.Log.Recent(), e => e.Source == "MCP" && e.Message == "Tool call: set_global");

        Assert.False((await ConfirmedAsync("delete_global", Args(("name", "secretish")))).IsError);
        Assert.False((await ConfirmedAsync("delete_command", Args(("command", "Dice")))).IsError);
        Assert.False((await ConfirmedAsync("delete_timer", Args(("timer", "Ads")))).IsError);
        Assert.Empty(Bot.Commands.All());
        Assert.Empty(Bot.Profile.Snapshot.Timers);
        Assert.False(Bot.Globals.Contains("secretish"));
    }

    [Fact]
    public async Task Status_chat_and_import_export_tools()
    {
        var (_, status) = await _host.CallAsync("get_status");
        Assert.Contains(status["connections"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Twitch");
        Assert.DoesNotContain(ApiHost.Token, status.ToJsonString());

        var (_, chat) = await _host.CallAsync("get_recent_chat", Args(("limit", 5)));
        Assert.NotNull(chat["messages"]);

        var (sendError, sent) = await ConfirmedAsync("send_chat_message", Args(("message", "Hi from the AI"), ("platform", "Twitch")));
        Assert.False(sendError, sent.ToJsonString());
        Assert.Contains("Hi from the AI", Bot.Sender.Sent);

        await _host.CallAsync("create_action", Args(("action", NewAction("Bundle me"))));
        var (_, exported) = await _host.CallAsync("export_bundle", Args(("actions", new JsonArray("Bundle me"))));
        var data = exported["data"]!.GetValue<string>();
        Assert.StartsWith("CUEBOT1:", data);

        var (_, preview) = await _host.CallAsync("preview_import", Args(("data", data)));
        Assert.Equal("SameId", preview["items"]![0]!["conflict"]!.GetValue<string>());

        var (applyError, applied) = await ConfirmedAsync("apply_import", Args(("data", data), ("mode", "addAsNew")));
        Assert.False(applyError, applied.ToJsonString());
        Assert.Equal(2, Bot.Profile.Snapshot.Actions.Count(a => a.Name.StartsWith("Bundle me", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Every_tool_is_exercised_by_the_tests()
    {
        // Guards against adding a tool without a test: the names here must match tools/list exactly.
        var tested = RequiredTools.Concat(["describe_trigger_type", "list_script_functions", "get_command", "delete_command", "update_timer",
            "set_timer_state", "delete_timer", "delete_global"]).Order().ToArray();
        var listed = (await _host.RequestAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToArray();
        Assert.Equal(tested, listed);
    }

    [Fact]
    public async Task Confirmation_tokens_are_single_use_and_bound_to_the_arguments()
    {
        Bot.AddAction("Keep");
        Bot.AddAction("Other");
        var (_, first) = await _host.CallAsync("delete_action", Args(("action", "Keep")));
        Assert.True(first["confirmationRequired"]!.GetValue<bool>());
        Assert.Contains("Keep", first["action"]!.GetValue<string>());
        Assert.NotNull(Bot.Profile.Snapshot.FindAction("Keep"));
        var token = first["confirmationToken"]!.GetValue<string>();

        var (wrongArgs, _) = await _host.CallAsync("delete_action", Args(("action", "Other"), (McpConfirmations.ArgumentName, token)));
        Assert.True(wrongArgs);
        Assert.NotNull(Bot.Profile.Snapshot.FindAction("Other"));

        var (forged, _) = await _host.CallAsync("delete_action", Args(("action", "Keep"), (McpConfirmations.ArgumentName, "made-up")));
        Assert.True(forged);
        Assert.NotNull(Bot.Profile.Snapshot.FindAction("Keep"));

        var (ok, _) = await _host.CallAsync("delete_action", Args(("action", "Keep"), (McpConfirmations.ArgumentName, token)));
        Assert.False(ok);
        Assert.Null(Bot.Profile.Snapshot.FindAction("Keep"));

        var (reused, _) = await _host.CallAsync("delete_action", Args(("action", "Keep"), (McpConfirmations.ArgumentName, token)));
        Assert.True(reused);

        var (missingAction, _) = await _host.CallAsync("delete_action", Args(("action", "Never existed")));
        Assert.True(missingAction);   // no token for something that cannot be done

        Bot.Manager.Settings.Mcp.RequireConfirmation = false;
        var (direct, result) = await _host.CallAsync("delete_action", Args(("action", "Other")));
        Assert.False(direct);
        Assert.True(result["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public void Confirmation_tokens_expire()
    {
        var time = new ManualTime();
        var confirmations = new McpConfirmations(time);
        var args = Args(("action", "x"));
        var token = confirmations.Issue("delete_action", args);
        time.Advance(McpConfirmations.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(confirmations.TryConsume(token, "delete_action", args));
        var fresh = confirmations.Issue("delete_action", Args(("b", 1), ("a", new JsonObject { ["y"] = 1, ["x"] = 2 })));
        Assert.True(confirmations.TryConsume(fresh, "delete_action", Args(("a", new JsonObject { ["x"] = 2, ["y"] = 1 }), ("b", 1))));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // ---- resources and prompts ----

    [Fact]
    public async Task Resources_list_read_and_templates()
    {
        var action = Bot.AddAction("Readable");
        var resources = (await _host.RequestAsync("resources/list"))["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()).ToList();
        Assert.Contains(McpCatalog.ActionsUri, resources);
        Assert.Contains(McpCatalog.SubActionsUri, resources);
        Assert.Contains(McpCatalog.VariablesUri, resources);
        Assert.Contains($"{McpCatalog.ActionsUri}/{action.Id}", resources);

        var templates = await _host.RequestAsync("resources/templates/list");
        Assert.Equal("cuebot://actions/{id}", templates["resourceTemplates"]![0]!["uriTemplate"]!.GetValue<string>());

        foreach (var uri in resources)
        {
            var read = await _host.RequestAsync("resources/read", new JsonObject { ["uri"] = uri });
            var content = read["contents"]![0]!;
            Assert.Equal(uri, content["uri"]!.GetValue<string>());
            var text = content["text"]!.GetValue<string>();
            if (content["mimeType"]!.GetValue<string>() == "application/json") Assert.NotNull(JsonNode.Parse(text));
            else Assert.Contains("#", text);
        }

        var catalog = await _host.RequestAsync("resources/read", new JsonObject { ["uri"] = McpCatalog.SubActionsUri });
        Assert.Contains("parametersSchema", catalog["contents"]![0]!["text"]!.GetValue<string>());
        var variables = await _host.RequestAsync("resources/read", new JsonObject { ["uri"] = McpCatalog.VariablesUri });
        Assert.Contains("~name~", variables["contents"]![0]!["text"]!.GetValue<string>());

        var missing = await _host.RpcAsync("resources/read", new JsonObject { ["uri"] = "cuebot://nothing" });
        Assert.Equal(-32002, missing["error"]!["code"]!.GetValue<int>());
        var missingAction = await _host.RpcAsync("resources/read", new JsonObject { ["uri"] = $"{McpCatalog.ActionsUri}/{Guid.NewGuid()}" });
        Assert.Equal(-32002, missingAction["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Prompts_list_and_get()
    {
        var prompts = (await _host.RequestAsync("prompts/list"))["prompts"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "create_action_from_description", "troubleshoot_action" }, prompts);

        var create = await _host.RequestAsync("prompts/get", new JsonObject
        {
            ["name"] = "create_action_from_description",
            ["arguments"] = new JsonObject { ["description"] = "roll a dice on !dice" },
        });
        var text = create["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("roll a dice on !dice", text);
        Assert.Contains("list_subaction_types", text);
        Assert.Equal("user", create["messages"]![0]!["role"]!.GetValue<string>());

        Bot.AddAction("Broken one");
        await Bot.Engine.RunAsync(Bot.Profile.Snapshot.FindAction("Broken one")!);
        var trouble = await _host.RequestAsync("prompts/get", new JsonObject
        {
            ["name"] = "troubleshoot_action",
            ["arguments"] = new JsonObject { ["action"] = "Broken one", ["problem"] = "nothing happens" },
        });
        var troubleText = trouble["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("nothing happens", troubleText);
        Assert.Contains(CaptureSubAction.Id, troubleText);
        Assert.Contains("\"state\": \"Completed\"", troubleText);

        var missingArg = await _host.RpcAsync("prompts/get", new JsonObject { ["name"] = "troubleshoot_action" });
        Assert.Equal(-32602, missingArg["error"]!["code"]!.GetValue<int>());
        var unknown = await _host.RpcAsync("prompts/get", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, unknown["error"]!["code"]!.GetValue<int>());
    }

    // ---- stdio bridge ----

    [Fact]
    public async Task Stdio_bridge_forwards_to_the_running_instance()
    {
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            "",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_status","arguments":{}}}""",
            "not json",
            """{"jsonrpc":"2.0","id":"four","method":"ping"}""",
        };
        using var input = new StringReader(string.Join("\n", lines) + "\n");
        using var output = new StringWriter();
        using var log = new StringWriter();
        var code = await McpStdioBridge.RunAsync(input, output, log, new McpBridgeTarget(_host.McpUri, ApiHost.Token));
        Assert.Equal(0, code);

        var answers = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(5, answers.Count);   // 4 requests + 1 parse error; nothing for the notification
        JsonNode ById(string id) => answers.Single(a => a["id"]?.ToJsonString().Trim('"') == id);
        Assert.Equal("2025-06-18", ById("1")["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.True(ById("2")["result"]!["tools"]!.AsArray().Count >= RequiredTools.Length);
        Assert.False(ById("3")["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(ById("four")["result"]!.AsObject());
        Assert.Contains(answers, a => a["error"]?["code"]?.GetValue<int>() == -32700);
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public async Task Stdio_bridge_reports_unreachable_cuebot_and_bad_tokens()
    {
        // Port 1 (tcpmux) is never served here, so the connection is refused; a "free" port picked at runtime could be
        // taken by another test running in parallel before the bridge connects.
        const int freePort = 1;

        using var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n" + """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");
        using var output = new StringWriter();
        using var log = new StringWriter();
        await McpStdioBridge.RunAsync(input, output, log, new McpBridgeTarget(new Uri($"http://127.0.0.1:{freePort}/mcp"), "x"));
        var answer = JsonNode.Parse(output.ToString().Trim())!;
        Assert.Equal(1, answer["id"]!.GetValue<int>());
        Assert.Equal(-32000, answer["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Cannot reach Cuebot", answer["error"]!["message"]!.GetValue<string>());
        Assert.NotEqual("", log.ToString());

        using var input2 = new StringReader("""{"jsonrpc":"2.0","id":7,"method":"ping"}""" + "\n");
        using var output2 = new StringWriter();
        await McpStdioBridge.RunAsync(input2, output2, TextWriter.Null, new McpBridgeTarget(_host.McpUri, "wrong-token"));
        var rejected = JsonNode.Parse(output2.ToString().Trim())!;
        Assert.Contains("token", rejected["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Stdio_bridge_finds_the_endpoint_in_the_data_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cuebot-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(McpStdioBridge.ResolveTarget(folder, _ => null, out var noSettings));
            Assert.Contains("Servers page", noSettings);

            File.WriteAllText(Path.Combine(folder, "servers.json"),
                """{"api": {"enabled": true, "port": 7999, "token": "cb_file"}, "mcp": {"enabled": true}}""");
            var target = McpStdioBridge.ResolveTarget(folder, _ => null, out var warning)!;
            Assert.Equal("http://127.0.0.1:7999/mcp", target.Endpoint.ToString());
            Assert.Equal("cb_file", target.Token);
            Assert.Equal("", warning);

            var overridden = McpStdioBridge.ResolveTarget(folder,
                n => n switch { McpStdioBridge.UrlVariable => "http://127.0.0.1:1234/mcp", McpStdioBridge.TokenVariable => "env-token", _ => null }, out _)!;
            Assert.Equal(1234, overridden.Endpoint.Port);
            Assert.Equal("env-token", overridden.Token);

            File.WriteAllText(Path.Combine(folder, "servers.json"), """{"api": {"port": 7999, "token": "cb_file"}, "mcp": {"enabled": false}}""");
            Assert.NotNull(McpStdioBridge.ResolveTarget(folder, _ => null, out var off));
            Assert.Contains("MCP server is off", off);

            Assert.True(McpStdioBridge.IsRequested(["--data", "x", "--MCP-STDIO"]));
            Assert.False(McpStdioBridge.IsRequested(["--headless"]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Client_config_snippets()
    {
        var url = McpClientConfig.Url(7475);
        Assert.Equal("http://127.0.0.1:7475/mcp", url);
        Assert.Equal("claude mcp add --transport http cuebot http://127.0.0.1:7475/mcp --header \"Authorization: Bearer TOKEN\"",
            McpClientConfig.ClaudeCodeCommand(url, "TOKEN"));
        var http = JsonNode.Parse(McpClientConfig.HttpJson(url, "TOKEN"))!;
        Assert.Equal("Bearer TOKEN", http["mcpServers"]!["cuebot"]!["headers"]!["Authorization"]!.GetValue<string>());

        var (command, args) = McpClientConfig.StdioLaunch("/usr/bin/dotnet", "/app/Cuebot.dll", "/data");
        Assert.Equal("/usr/bin/dotnet", command);
        Assert.Equal(new[] { "/app/Cuebot.dll", "--mcp-stdio", "--data", "/data" }, args);
        var stdio = JsonNode.Parse(McpClientConfig.StdioJson("C:\\Cuebot\\Cuebot.exe", ["--mcp-stdio"]))!;
        Assert.Equal("C:\\Cuebot\\Cuebot.exe", stdio["mcpServers"]!["cuebot"]!["command"]!.GetValue<string>());
    }
}
