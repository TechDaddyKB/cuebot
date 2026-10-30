using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Server.Api;
using Cuebot.Server.Mcp;

namespace Cuebot.Server.Tests;

public sealed class RestApiTests : IAsyncLifetime
{
    private ApiHost _host = null!;
    private TestBot Bot => _host.Bot;

    public async Task InitializeAsync() => _host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static JsonObject LogAction(string name, string level = "Info") => new()
    {
        ["name"] = name,
        ["group"] = "API",
        ["triggers"] = new JsonArray(new JsonObject { ["type"] = "command.triggered", ["settings"] = new JsonObject { ["command"] = "Hello" } }),
        ["subActions"] = new JsonArray(
            new JsonObject { ["type"] = "core.log", ["parameters"] = new JsonObject { ["message"] = "Hi %user%", ["level"] = level } },
            new JsonObject { ["type"] = CaptureSubAction.Id }),
    };

    // ---- security ----

    [Fact]
    public async Task The_shared_listener_logs_what_it_serves()
    {
        await using var bot = new TestBot();
        var s = bot.Manager.Settings;
        (s.Api.Enabled, s.Mcp.Enabled) = (true, true);
        Assert.Equal("REST API and MCP", bot.Manager.ListenerName(ServerKind.Api));
        s.Mcp.Enabled = false;
        Assert.Equal("REST API", bot.Manager.ListenerName(ServerKind.Api));
        (s.Api.Enabled, s.Mcp.Enabled) = (false, true);
        Assert.Equal("MCP (REST API off)", bot.Manager.ListenerName(ServerKind.Api));
        Assert.Equal("WebSocket", bot.Manager.ListenerName(ServerKind.WebSocket));
    }

    [Fact]
    public async Task Every_request_needs_the_token()
    {
        using var anonymous = new HttpClient { BaseAddress = _host.Http.BaseAddress };
        foreach (var path in new[] { "/api/v1/status", "/api/v1/actions", "/api/v1/openapi.json", "/mcp" })
        {
            using var none = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.Contains("Bearer", none.Headers.WwwAuthenticate.ToString());
        }
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await anonymous.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", ApiHost.Token);
        using var basic = await anonymous.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, basic.StatusCode);

        var (status, body) = await _host.GetAsync("/api/v1/status");
        Assert.Equal(200, status);
        Assert.Equal("Cuebot", body!["app"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(ApiHost.Token, body.ToJsonString());
    }

    [Fact]
    public async Task Foreign_origins_are_refused_and_local_ones_allowed()
    {
        using var evil = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        evil.Headers.Add("Origin", "https://evil.example");
        using var refused = await _host.Http.SendAsync(evil);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/actions");
        preflight.Headers.Add("Origin", "https://evil.example");
        using var preflightRefused = await _host.Http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.Forbidden, preflightRefused.StatusCode);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossRefused = await _host.Http.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, crossRefused.StatusCode);

        using var local = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        local.Headers.Add("Origin", "http://localhost:3000");
        using var allowed = await _host.Http.SendAsync(local);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("http://localhost:3000", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var mcpEvil = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}") };
        mcpEvil.Headers.Add("Origin", "https://evil.example");
        using var mcpRefused = await _host.Http.SendAsync(mcpEvil);
        Assert.Equal(HttpStatusCode.Forbidden, mcpRefused.StatusCode);
    }

    [Fact]
    public async Task Off_by_default_and_token_generated_on_first_start()
    {
        await using (var bot = new TestBot())
        {
            Assert.False(bot.Manager.Settings.Api.Enabled);
            Assert.False(bot.Manager.Settings.Mcp.Enabled);
            Assert.Equal(ApiServerSettings.DefaultPort, bot.Manager.Settings.Api.Port);
            await bot.Manager.StartAsync();
            Assert.False(bot.Manager.Api.IsRunning);
            Assert.Equal("Off", bot.Manager.Statuses.Single(s => s.Kind == ServerKind.Api).Message);
            Assert.Equal("", bot.Manager.Settings.Api.Token);
        }

        await using var fresh = new TestBot(new ServerSettings { Api = new ApiServerSettings { Enabled = true, Port = 0 } });
        await fresh.Manager.StartAsync();
        Assert.True(fresh.Manager.Api.IsRunning);
        var token = fresh.Manager.Settings.Api.Token;
        Assert.StartsWith(ApiToken.Prefix, token);
        Assert.True(token.Length > 40);
        Assert.NotEqual(token, fresh.Manager.RegenerateApiToken());
        Assert.DoesNotContain(fresh.Log.Recent(), e => e.Message.Contains(fresh.Manager.Settings.Api.Token, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, false, true)]    // version 1: off was only the default, MCP is switched on once
    [InlineData(2, false, false)]   // version 2: the user turned it off, it stays off
    [InlineData(2, true, true)]
    public async Task Saved_settings_start_the_MCP_server_from_version_2(int version, bool enabled, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuebot-mcp-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "servers.json");
            File.WriteAllText(path, $$$"""{"version":{{{version}}},"mcp":{"enabled":{{{(enabled ? "true" : "false")}}}}}""");
            await using var bot = new TestBot();
            using var file = new JsonFile<ServerSettings>(path, bot.Log);
            var manager = new ServerManager(file, bot.Services);

            Assert.Equal(expected, manager.Settings.Mcp.Enabled);
            Assert.Equal(ServerSettings.CurrentVersion, manager.Settings.Version);
            Assert.False(manager.Settings.Api.Enabled);
            file.Flush();
            using var reloaded = new JsonFile<ServerSettings>(path, bot.Log);
            Assert.Equal(ServerSettings.CurrentVersion, reloaded.Value.Version);
            Assert.Equal(expected, reloaded.Value.Mcp.Enabled);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(2, false, true)]    // version 2: off was only the default, the overlay server is switched on once
    [InlineData(3, false, false)]   // version 3: the user turned it off, it stays off
    [InlineData(3, true, true)]
    public async Task Saved_settings_start_the_overlay_server_from_version_3(int version, bool enabled, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuebot-overlay-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "servers.json");
            File.WriteAllText(path, $$$"""{"version":{{{version}}},"mcp":{"enabled":true},"overlays":{"enabled":{{{(enabled ? "true" : "false")}}}}}""");
            await using var bot = new TestBot();
            using var file = new JsonFile<ServerSettings>(path, bot.Log);
            var manager = new ServerManager(file, bot.Services);

            Assert.Equal(expected, manager.Settings.Overlays.Enabled);
            Assert.True(manager.Settings.Mcp.Enabled);
            Assert.Equal(ServerSettings.CurrentVersion, manager.Settings.Version);
            file.Flush();
            using var reloaded = new JsonFile<ServerSettings>(path, bot.Log);
            Assert.Equal(expected, reloaded.Value.Overlays.Enabled);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Lan_addresses_are_downgraded_unless_allowed()
    {
        await using var bot = new TestBot(new ServerSettings { Api = new ApiServerSettings { Enabled = true, Port = 0, Address = "0.0.0.0", Token = "t" } });
        await bot.Manager.StartAsync();
        var status = bot.Manager.Statuses.Single(s => s.Kind == ServerKind.Api);
        Assert.True(status.Running);
        Assert.Equal("127.0.0.1", status.Address);
        Assert.Contains(bot.Log.Recent(), e => e.Message.Contains("LAN access is off"));
        Assert.StartsWith("http://127.0.0.1:", bot.Manager.ApiBaseUrl);
    }

    [Fact]
    public async Task Api_and_mcp_halves_can_be_switched_off_separately()
    {
        await using (var apiOnly = await ApiHost.StartAsync(api: true, mcp: false))
        {
            Assert.Equal(200, (await apiOnly.GetAsync("/api/v1/status")).Status);
            Assert.Equal(404, (await apiOnly.PostAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" })).Status);
        }
        await using var mcpOnly = await ApiHost.StartAsync(api: false, mcp: true);
        Assert.Equal(404, (await mcpOnly.GetAsync("/api/v1/status")).Status);
        Assert.Equal(200, (await mcpOnly.PostAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" })).Status);
    }

    [Fact]
    public void Token_comparison_and_header_parsing()
    {
        Assert.True(ApiToken.Matches("abc", "abc"));
        Assert.False(ApiToken.Matches("abc", "abd"));
        Assert.False(ApiToken.Matches("abc", "abcd"));
        Assert.False(ApiToken.Matches("", ""));
        Assert.False(ApiToken.Matches("abc", null));
        Assert.Equal("xyz", ApiToken.FromAuthorizationHeader("Bearer xyz"));
        Assert.Equal("xyz", ApiToken.FromAuthorizationHeader("bearer  xyz "));
        Assert.Null(ApiToken.FromAuthorizationHeader("Basic xyz"));
        Assert.Null(ApiToken.FromAuthorizationHeader(null));
    }

    // ---- actions ----

    [Fact]
    public async Task Action_crud_round_trip_is_visible_in_the_profile()
    {
        var changes = 0;
        Bot.Profile.Changed += () => Interlocked.Increment(ref changes);

        var (created, body) = await _host.PostAsync("/api/v1/actions", LogAction("Greeter"));
        Assert.Equal(201, created);
        var id = Guid.Parse(body!["action"]!["id"]!.GetValue<string>());
        var stored = Bot.Profile.Snapshot.FindAction(id)!;
        Assert.Equal("Greeter", stored.Name);
        Assert.Equal("command.triggered", stored.Triggers.Single().Type);
        Assert.Equal("Hi %user%", stored.SubActions[0].Parameters["message"]);
        Assert.NotEqual(Guid.Empty, stored.SubActions[0].Id);

        var (got, action) = await _host.GetAsync("/api/v1/actions/greeter");
        Assert.Equal(200, got);
        Assert.Equal(id.ToString(), action!["id"]!.GetValue<string>());

        var (patched, _) = await _host.SendAsync(HttpMethod.Patch, $"/api/v1/actions/{id}", new JsonObject { ["name"] = "Greeter 2", ["enabled"] = false });
        Assert.Equal(200, patched);
        Assert.Equal("Greeter 2", Bot.Profile.Snapshot.FindAction(id)!.Name);
        Assert.False(Bot.Profile.Snapshot.FindAction(id)!.Enabled);
        Assert.Single(Bot.Profile.Snapshot.FindAction(id)!.Triggers);   // untouched fields kept

        var replacement = LogAction("Replaced");
        replacement["triggers"] = new JsonArray();
        var (put, _) = await _host.SendAsync(HttpMethod.Put, $"/api/v1/actions/{id}", replacement);
        Assert.Equal(200, put);
        Assert.Empty(Bot.Profile.Snapshot.FindAction(id)!.Triggers);
        Assert.Equal("Replaced", Bot.Profile.Snapshot.FindAction(id)!.Name);

        var (listed, list) = await _host.GetAsync("/api/v1/actions?group=API");
        Assert.Equal(200, listed);
        Assert.Equal(1, list!["count"]!.GetValue<int>());
        Assert.Equal(2, list["actions"]![0]!["subActionCount"]!.GetValue<int>());

        var (duplicate, _) = await _host.PostAsync("/api/v1/actions", new JsonObject { ["id"] = id.ToString(), ["name"] = "Dup" });
        Assert.Equal(409, duplicate);

        var (deleted, _) = await _host.SendAsync(HttpMethod.Delete, $"/api/v1/actions/{id}");
        Assert.Equal(200, deleted);
        Assert.Null(Bot.Profile.Snapshot.FindAction(id));
        Assert.Equal(404, (await _host.GetAsync($"/api/v1/actions/{id}")).Status);
        Assert.True(changes >= 4);
    }

    [Fact]
    public async Task Invalid_actions_are_rejected_with_details()
    {
        var action = LogAction("Bad", level: "Loud");
        action["subActions"]!.AsArray().Add(new JsonObject { ["type"] = "nope.unknown" });
        action["subActions"]!.AsArray().Add(new JsonObject { ["type"] = "core.delay", ["parameters"] = new JsonObject { ["milliseconds"] = "soon" } });
        action["subActions"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "core.if",
            ["parameters"] = new JsonObject { ["left"] = "%x%", ["operator"] = "equals", ["right"] = "1" },
            ["branches"] = new JsonObject { ["True"] = new JsonArray(new JsonObject { ["type"] = "missing.type" }) },
        });
        action["triggers"]!.AsArray().Add(new JsonObject { ["type"] = "bogus.trigger" });

        var (status, body) = await _host.PostAsync("/api/v1/actions", action);
        Assert.Equal(400, status);
        var paths = body!["details"]!.AsArray().Select(d => d!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("subActions[0].parameters.level", paths);
        Assert.Contains("subActions[2].type", paths);
        Assert.Contains("subActions[3].parameters.milliseconds", paths);
        Assert.Contains("subActions[4].branches.True[0].type", paths);
        Assert.Contains("triggers[1].type", paths);
        Assert.Empty(Bot.Profile.Snapshot.Actions);

        var (badJson, error) = await _host.PostAsync("/api/v1/actions", new JsonObject { ["name"] = "X", ["subActions"] = "not a list" });
        Assert.Equal(400, badJson);
        Assert.Contains("subActions", error!["details"]![0]!["path"]!.GetValue<string>());

        using var raw = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        using var rawResponse = await _host.Http.PostAsync("/api/v1/actions", raw);
        Assert.Equal(HttpStatusCode.BadRequest, rawResponse.StatusCode);

        var (noName, _) = await _host.PostAsync("/api/v1/actions", new JsonObject { ["name"] = " " });
        Assert.Equal(400, noName);
    }

    [Fact]
    public async Task Templates_pass_validation_and_unknown_keys_and_branches_only_warn()
    {
        var action = new JsonObject
        {
            ["name"] = "Templated",
            ["subActions"] = new JsonArray(
                new JsonObject { ["type"] = "core.delay", ["parameters"] = new JsonObject { ["milliseconds"] = "%wait%", ["extra"] = "x" } },
                new JsonObject { ["type"] = "core.log", ["branches"] = new JsonObject { ["Nowhere"] = new JsonArray() } }),
        };
        var (status, body) = await _host.PostAsync("/api/v1/actions", action);
        Assert.Equal(201, status);
        var warnings = body!["warnings"]!.AsArray().Select(w => w!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("subActions[0].parameters.extra", warnings);
        Assert.Contains("subActions[1].branches.Nowhere", warnings);
    }

    [Fact]
    public async Task Run_action_runs_it_in_the_engine()
    {
        var action = Bot.AddAction("Runner");
        var (status, body) = await _host.PostAsync($"/api/v1/actions/{action.Id}/run", new { args = new { user = "tester", count = 3 }, wait = true });
        Assert.Equal(200, status);
        Assert.True(body!["completed"]!.GetValue<bool>());
        Assert.True(body["success"]!.GetValue<bool>());
        Assert.Equal("API", body["run"]!["source"]!.GetValue<string>());
        Assert.Equal("tester", Bot.Capture.ByAction["Runner"]["user"]);
        Assert.Equal(3L, Bot.Capture.ByAction["Runner"]["count"]);

        var (queued, _) = await _host.PostAsync("/api/v1/actions/Runner/run", new { args = new { user = "second" } });
        Assert.Equal(202, queued);
        await WaitUntil(() => Bot.Runs.Count(r => r.ActionName == "Runner") >= 2);

        var (history, runs) = await _host.GetAsync("/api/v1/history?action=Runner&includeArgs=true");
        Assert.Equal(200, history);
        Assert.Equal(2, runs!["count"]!.GetValue<int>());
        Assert.NotNull(runs["runs"]![0]!["args"]);

        Assert.Equal(200, (await _host.PostAsync("/api/v1/actions/Runner/disable")).Status);
        Assert.False(Bot.Profile.Snapshot.FindAction(action.Id)!.Enabled);
        Assert.Equal(409, (await _host.PostAsync("/api/v1/actions/Runner/run")).Status);
        Assert.Equal(200, (await _host.PostAsync("/api/v1/actions/Runner/enable")).Status);
        Assert.True(Bot.Profile.Snapshot.FindAction(action.Id)!.Enabled);
        Assert.Equal(404, (await _host.PostAsync("/api/v1/actions/Nope/run")).Status);
    }

    // ---- catalogue ----

    [Fact]
    public async Task Catalogue_lists_types_with_descriptors_and_schemas()
    {
        var (_, subs) = await _host.GetAsync("/api/v1/subactions?search=log");
        Assert.Contains(subs!["subActions"]!.AsArray(), s => s!["typeId"]!.GetValue<string>() == "core.log");
        Assert.DoesNotContain(subs["subActions"]!.AsArray(), s => s!["typeId"]!.GetValue<string>() == "core.delay");

        var (_, log) = await _host.GetAsync("/api/v1/subactions/core.log");
        var level = log!["parametersSchema"]!["properties"]!["level"]!;
        Assert.Equal(new[] { "Debug", "Info", "Warning", "Error" }, level["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("choice", log["parameters"]![1]!["kind"]!.GetValue<string>());

        var (_, ifType) = await _host.GetAsync("/api/v1/subactions/core.if");
        Assert.Equal(new[] { "True", "False" }, ifType!["branches"]!.AsArray().Select(v => v!.GetValue<string>()));

        var (_, triggers) = await _host.GetAsync("/api/v1/triggers?category=Commands");
        Assert.All(triggers!["triggers"]!.AsArray(), t => Assert.Equal("Commands", t!["category"]!.GetValue<string>()));
        Assert.Contains(triggers["triggers"]!.AsArray(), t => t!["typeId"]!.GetValue<string>() == "command.triggered");

        var (_, timer) = await _host.GetAsync("/api/v1/triggers/core.timer");
        Assert.NotNull(timer!["settingsSchema"]!["properties"]!["timer"]);
        Assert.Equal(404, (await _host.GetAsync("/api/v1/triggers/none.such")).Status);

        var (_, functions) = await _host.GetAsync("/api/v1/scriptfunctions?search=obs");
        Assert.Contains(functions!["functions"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "obs.request");
    }

    // ---- commands, timers, globals ----

    [Fact]
    public async Task Commands_crud_and_linked_actions()
    {
        var (created, body) = await _host.PostAsync("/api/v1/commands", new JsonObject { ["name"] = "Hello", ["commands"] = new JsonArray("!hello") });
        Assert.Equal(201, created);
        var id = body!["command"]!["id"]!.GetValue<string>();
        Assert.Equal("!hello", Bot.Commands.Find("Hello")!.Commands.Single());

        await _host.PostAsync("/api/v1/actions", LogAction("Hello action"));
        var (_, command) = await _host.GetAsync("/api/v1/commands/hello");
        Assert.Equal("Hello action", command!["linkedActions"]![0]!["name"]!.GetValue<string>());

        var (patched, _) = await _host.SendAsync(HttpMethod.Patch, $"/api/v1/commands/{id}", new JsonObject { ["globalCooldownSeconds"] = 30 });
        Assert.Equal(200, patched);
        Assert.Equal(30, Bot.Commands.Find(id)!.GlobalCooldownSeconds);
        Assert.Equal("!hello", Bot.Commands.Find(id)!.Commands.Single());

        Assert.Equal(400, (await _host.PostAsync("/api/v1/commands", new JsonObject { ["name"] = "Re", ["mode"] = "Regex", ["commands"] = new JsonArray("([") })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/commands", new JsonObject { ["name"] = "Mode", ["mode"] = "Telepathy" })).Status);

        var (_, list) = await _host.GetAsync("/api/v1/commands");
        Assert.Equal(1, list!["count"]!.GetValue<int>());
        Assert.Equal(200, (await _host.SendAsync(HttpMethod.Delete, $"/api/v1/commands/{id}")).Status);
        Assert.Empty(Bot.Commands.All());
    }

    [Fact]
    public async Task Timers_crud_and_state()
    {
        var (created, body) = await _host.PostAsync("/api/v1/timers", new JsonObject { ["name"] = "Socials", ["intervalSeconds"] = 600 });
        Assert.Equal(201, created);
        Assert.Contains("core.timer", body!["hint"]!.GetValue<string>());
        Assert.Equal(600, Bot.Profile.Snapshot.Timers.Single().IntervalSeconds);
        Assert.Equal(600, body["timer"]!["state"]!["intervalSeconds"]!.GetValue<int>());

        var (state, after) = await _host.PostAsync("/api/v1/timers/Socials/state", new { enabled = false, intervalSeconds = 90 });
        Assert.Equal(200, state);
        Assert.False(after!["state"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(90, Bot.Timers.GetState("Socials")!.IntervalSeconds);
        Assert.True(Bot.Profile.Snapshot.Timers.Single().Enabled);   // runtime only

        Assert.Equal(400, (await _host.PostAsync("/api/v1/timers", new JsonObject { ["name"] = "Bad", ["cron"] = "not a cron" })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/timers", new JsonObject { ["name"] = "Zero", ["intervalSeconds"] = 0 })).Status);

        Assert.Equal(200, (await _host.SendAsync(HttpMethod.Patch, "/api/v1/timers/Socials", new JsonObject { ["cron"] = "*/15 * * * *" })).Status);
        Assert.Equal("*/15 * * * *", Bot.Profile.Snapshot.Timers.Single().Cron);
        Assert.Equal(200, (await _host.SendAsync(HttpMethod.Delete, "/api/v1/timers/Socials")).Status);
        Assert.Empty(Bot.Profile.Snapshot.Timers);
    }

    [Fact]
    public async Task Globals_get_put_delete()
    {
        Assert.Equal(200, (await _host.SendAsync(HttpMethod.Put, "/api/v1/globals/score", new { value = 42, persisted = false })).Status);
        Assert.Equal(42L, Bot.Globals.GetValue("score"));
        Assert.False(Bot.Globals.IsPersisted("score"));

        var (_, one) = await _host.GetAsync("/api/v1/globals/score");
        Assert.Equal(42, one!["value"]!.GetValue<int>());
        var (_, all) = await _host.GetAsync("/api/v1/globals?persisted=false");
        Assert.Equal(1, all!["count"]!.GetValue<int>());

        await _host.SendAsync(HttpMethod.Put, "/api/v1/globals/greeting", new { value = "hello" });
        Assert.Equal("hello", Bot.Globals.Get("greeting"));
        Assert.True(Bot.Globals.IsPersisted("greeting"));

        Assert.Equal(200, (await _host.SendAsync(HttpMethod.Delete, "/api/v1/globals/score")).Status);
        Assert.False(Bot.Globals.Contains("score"));
        Assert.Equal(404, (await _host.GetAsync("/api/v1/globals/score")).Status);
    }

    // ---- polls ----

    [Fact]
    public async Task Chat_polls_are_started_voted_and_ended_over_the_api()
    {
        var (status, started) = await _host.PostAsync("/api/v1/polls", new { title = "Next game?", choices = new[] { "Horror", "Racing" }, duration = 0 });
        Assert.Equal(200, status);
        Assert.Equal(("cuebot", "active", "!vote"), (started!["source"]!.GetValue<string>(), started["status"]!.GetValue<string>(),
            started["command"]!.GetValue<string>()));
        Assert.Equal(409, (await _host.PostAsync("/api/v1/polls", new { title = "Again", choices = new[] { "A", "B" } })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/predictions/resolve", new { winner = "x" })).Status);

        Bot.Bus.Publish(new BotEvent(Cuebot.Core.Chat.ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Kick", ["user"] = "Bob", ["userId"] = "b1", ["message"] = "!vote racing",
        }));
        var (_, polls) = await _host.GetAsync("/api/v1/polls");
        Assert.Equal(1, polls!["polls"]![0]!["choices"]![1]!["votes"]!.GetValue<int>());

        var (_, ended) = await _host.PostAsync("/api/v1/polls/end", new { });
        Assert.Equal(("ended", 1), (ended!["status"]!.GetValue<string>(), ended["winner"]!.GetValue<int>()));
        Assert.Equal(404, (await _host.PostAsync("/api/v1/polls/end", new { })).Status);
        // Predictions take Cuebot points; this test bot has none.
        Assert.Equal(409, (await _host.PostAsync("/api/v1/predictions", new { title = "Win?", outcomes = new[] { "Yes", "No" } })).Status);
        Assert.Equal(404, (await _host.PostAsync("/api/v1/predictions/cancel", new { })).Status);
    }

    // ---- speech and alert queues ----

    [Fact]
    public async Task Speech_and_alert_queues_are_controlled_over_the_api()
    {
        var (status, speech) = await _host.GetAsync("/api/v1/speech");
        Assert.Equal(200, status);
        Assert.False(speech!["paused"]!.GetValue<bool>());

        var (_, paused) = await _host.PostAsync("/api/v1/speech/pause", new { });
        Assert.True(paused!["paused"]!.GetValue<bool>());
        var (_, stepped) = await _host.PostAsync("/api/v1/speech/next", new { });
        Assert.True(stepped!["stepped"]!.GetValue<bool>());
        await _host.PostAsync("/api/v1/speech/skip", new { });
        var (_, resumed) = await _host.PostAsync("/api/v1/speech/resume", new { });
        Assert.False(resumed!["paused"]!.GetValue<bool>());
        Assert.Equal(["next", "stop"], Bot.Speaker.Log.Where(l => l is "next" or "stop"));
        Assert.Equal(404, (await _host.PostAsync("/api/v1/speech/explode", new { })).Status);
        Assert.Equal(405, (await _host.GetAsync("/api/v1/speech/next")).Status);

        var sent = new List<string>();
        using var feed = Bot.OverlayFeed.Subscribe(m => sent.Add(m.Json));
        var (_, alerts) = await _host.PostAsync("/api/v1/alerts/pause", new { });
        Assert.Equal((true, false), (alerts!["paused"]!.GetValue<bool>(), alerts["muted"]!.GetValue<bool>()));
        await _host.PostAsync("/api/v1/alerts/mute", new { });
        await _host.PostAsync("/api/v1/alerts/skip", new { });
        var (_, now) = await _host.GetAsync("/api/v1/alerts");
        Assert.Equal((true, true), (now!["paused"]!.GetValue<bool>(), now["muted"]!.GetValue<bool>()));
        Assert.Equal(["pause", "mute", "skip"], sent.Select(j => JsonNode.Parse(j)!["command"]!.GetValue<string>()));
        Assert.Equal(404, (await _host.PostAsync("/api/v1/alerts/toggle", new { })).Status);
    }

    // ---- chat, import/export, events ----

    [Fact]
    public async Task Chat_get_and_send()
    {
        var (status, sent) = await _host.PostAsync("/api/v1/chat", new { message = "Hello chat", platform = "Twitch" });
        Assert.Equal(200, status);
        Assert.Equal("Twitch", sent!["platforms"]![0]!.GetValue<string>());
        Assert.Contains("Hello chat", Bot.Sender.Sent);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/chat", new { message = " " })).Status);
        Assert.Equal(409, (await _host.PostAsync("/api/v1/chat", new { message = "x", platform = "Nowhere" })).Status);

        var (_, chat) = await _host.GetAsync("/api/v1/chat?limit=5");
        Assert.NotNull(chat!["messages"]);
        Assert.Contains(chat["sendablePlatforms"]!.AsArray(), p => p!.GetValue<string>() == "Twitch");
    }

    [Fact]
    public async Task Export_preview_and_apply_import()
    {
        await _host.PostAsync("/api/v1/actions", LogAction("Exported"));
        await _host.PostAsync("/api/v1/commands", new JsonObject { ["name"] = "Hello", ["commands"] = new JsonArray("!hello") });
        var (exported, bundle) = await _host.GetAsync("/api/v1/export?actions=Exported&commands=Hello&name=Test");
        Assert.Equal(200, exported);
        var data = bundle!["data"]!.GetValue<string>();
        Assert.StartsWith("CUEBOT1:", data);
        Assert.Equal(1, bundle["actions"]!.GetValue<int>());

        await _host.SendAsync(HttpMethod.Delete, "/api/v1/actions/Exported");
        var (previewed, plan) = await _host.PostAsync("/api/v1/import/preview", new { data });
        Assert.Equal(200, previewed);
        var items = plan!["items"]!.AsArray();
        Assert.Contains(items, i => i!["kind"]!.GetValue<string>() == "Action" && i["conflict"]!.GetValue<string>() == "None");
        Assert.Contains(items, i => i!["kind"]!.GetValue<string>() == "Command" && i["conflict"]!.GetValue<string>() == "SameId");
        Assert.Null(Bot.Profile.Snapshot.FindAction("Exported"));

        var (applied, result) = await _host.PostAsync("/api/v1/import/apply", new { data, items = new[] { new { kind = "command", name = "Hello", include = false } } });
        Assert.Equal(200, applied);
        Assert.Equal(1, result!["added"]!.GetValue<int>());
        Assert.NotNull(Bot.Profile.Snapshot.FindAction("Exported"));

        Assert.Equal(400, (await _host.PostAsync("/api/v1/import/preview", new { data = "garbage" })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/import/apply", new { data, mode = "explode" })).Status);
        Assert.Equal(400, (await _host.GetAsync("/api/v1/export")).Status);
        Assert.Equal(404, (await _host.GetAsync("/api/v1/export?actions=Missing")).Status);
    }

    [Fact]
    public async Task Events_stream_as_server_sent_events()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events?types=custom.*,action.runCompleted");
        using var response = await _host.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        Bot.Bus.Publish(new BotEvent("other.ignored", new Dictionary<string, object?> { ["x"] = 1 }));
        Bot.Bus.Publish(new BotEvent("custom.test", new Dictionary<string, object?> { ["message"] = "hi", ["n"] = 5 }));
        Bot.AddAction("Streamed");
        await Bot.Engine.RunAsync(Bot.Profile.Snapshot.FindAction("Streamed")!);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<(string Type, JsonNode Data)>();
        string? type = null;
        while (events.Count < 2)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) events.Add((type!, JsonNode.Parse(line[6..])!));
        }
        Assert.Equal("custom.test", events[0].Type);
        Assert.Equal("hi", events[0].Data["args"]!["message"]!.GetValue<string>());
        Assert.Equal(5, events[0].Data["args"]!["n"]!.GetValue<int>());
        Assert.Equal("action.runCompleted", events[1].Type);
        Assert.Equal("Streamed", events[1].Data["args"]!["actionName"]!.GetValue<string>());
    }

    // ---- OpenAPI and routing ----

    [Fact]
    public async Task OpenApi_document_parses_and_matches_the_routes()
    {
        var (status, doc) = await _host.GetAsync("/api/v1/openapi.json");
        Assert.Equal(200, status);
        Assert.Equal("3.1.0", doc!["openapi"]!.GetValue<string>());
        Assert.EndsWith("/api/v1", doc["servers"]![0]!["url"]!.GetValue<string>());
        var schemas = doc["components"]!["schemas"]!.AsObject();

        // Every $ref resolves.
        foreach (var reference in Refs(doc))
        {
            Assert.StartsWith("#/components/schemas/", reference);
            Assert.True(schemas.ContainsKey(reference["#/components/schemas/".Length..]), $"Unresolved {reference}");
        }

        // Every documented operation exists (answers something other than "no such endpoint" / 405).
        var ids = new HashSet<string>();
        foreach (var (path, item) in doc["paths"]!.AsObject())
        {
            foreach (var (method, op) in item!.AsObject().Where(p => p.Key != "parameters"))
            {
                Assert.True(ids.Add(op!["operationId"]!.GetValue<string>()));
                if (path == "/events") continue;   // a stream; covered above
                var concrete = path.Replace("{id}", "does-not-exist").Replace("{type}", "none.such").Replace("{name}", "nothing");
                var (code, body) = await _host.SendAsync(new HttpMethod(method.ToUpperInvariant()), "/api/v1" + concrete,
                    method is "get" or "delete" ? null : new JsonObject());
                Assert.NotEqual(405, code);
                Assert.False(code == 404 && body?["error"]?.GetValue<string>().StartsWith("No such endpoint") == true, $"{method} {path} is not routed");
            }
        }
        Assert.True(ids.Count >= 30);

        Assert.Equal(404, (await _host.GetAsync("/api/v1/nothing")).Status);
        Assert.Equal(405, (await _host.SendAsync(HttpMethod.Delete, "/api/v1/status")).Status);
    }

    private static IEnumerable<string> Refs(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Key == "$ref" ? [p.Value!.GetValue<string>()] : Refs(p.Value)),
        JsonArray a => a.SelectMany(Refs),
        _ => [],
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
