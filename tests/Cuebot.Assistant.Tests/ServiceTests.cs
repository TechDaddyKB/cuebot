using System.Runtime.CompilerServices;

namespace Cuebot.Assistant.Tests;

public sealed class ServiceTests
{
    private sealed class FakeProvider(string id, bool ready, AssistantProviderKind kind = AssistantProviderKind.ApiKey) : IAssistantProvider
    {
        public string Id => id;
        public string Name => id.ToUpperInvariant();
        public AssistantProviderKind Kind => kind;
        public bool Ready { get; set; } = ready;
        public List<AssistantContext> Sessions { get; } = [];
        public Exception? FailWith { get; set; }
        public string? ErrorText { get; set; }

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(Ready ? AssistantProviderStatus.Available("ok") : AssistantProviderStatus.Unavailable("not set up"));

        public IAssistantSession StartSession(AssistantContext context)
        {
            Sessions.Add(context);
            return new Session(this, Sessions.Count);
        }

        private sealed class Session(FakeProvider owner, int number) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new AssistantText($"{owner.Id}#{number}: ");
                await Task.Yield();
                if (owner.FailWith is { } failure) throw failure;
                if (owner.ErrorText is { } error)
                {
                    yield return new AssistantError(error);
                    yield break;
                }
                yield return new AssistantText(text);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static AssistantService Service(Bot bot, params IAssistantProvider[] providers) =>
        new(null, providers, bot.Manager, null, bot.Log);

    [Fact]
    public async Task Automatic_choice_takes_the_first_ready_provider_in_order()
    {
        await using var bot = new Bot();
        var openai = new FakeProvider("openai", ready: true);
        var claude = new FakeProvider("claude", ready: true, AssistantProviderKind.Cli);
        var codex = new FakeProvider("codex", ready: false, AssistantProviderKind.Cli);
        await using var service = Service(bot, openai, claude, codex);

        Assert.Equal(["codex", "claude", "openai"], service.Providers.Select(p => p.Id));
        Assert.Same(claude, await service.ResolveProviderAsync());
        Assert.Equal("claude#1: hi", (await Updates.CollectAsync(service.SendAsync("hi"))).Text());
        Assert.Equal("claude", service.ActiveProviderId);

        service.Settings.Provider = "openai";
        Assert.Same(openai, await service.ResolveProviderAsync());
    }

    [Fact]
    public async Task Without_a_ready_provider_the_turn_explains_what_to_set_up()
    {
        await using var bot = new Bot();
        await using var service = Service(bot, new FakeProvider("openai", ready: false));

        var error = Assert.IsType<AssistantError>(Assert.Single(await Updates.CollectAsync(service.SendAsync("hi"))));
        Assert.Contains("API key", error.Message);

        service.Settings.Provider = "openai";
        error = Assert.IsType<AssistantError>(Assert.Single(await Updates.CollectAsync(service.SendAsync("hi"))));
        Assert.Equal("OPENAI: not set up", error.Message);
    }

    [Fact]
    public async Task One_conversation_until_the_model_changes_or_a_new_chat_starts()
    {
        await using var bot = new Bot();
        var openai = new FakeProvider("openai", ready: true);
        await using var service = Service(bot, openai);

        Assert.Equal("openai#1: a", (await Updates.CollectAsync(service.SendAsync("a"))).Text());
        Assert.Equal("openai#1: b", (await Updates.CollectAsync(service.SendAsync("b"))).Text());
        service.Settings.For("openai").Model = "gpt-other";
        Assert.Equal("openai#2: c", (await Updates.CollectAsync(service.SendAsync("c"))).Text());
        await service.NewChatAsync();
        Assert.Equal("openai#3: d", (await Updates.CollectAsync(service.SendAsync("d"))).Text());

        Assert.Equal("gpt-other", openai.Sessions[1].Model);
        Assert.All(openai.Sessions, s => Assert.Null(s.Mcp));   // API keys use the tools in process
        Assert.Contains(AssistantTools.ReadDocTool, openai.Sessions[0].Instructions);
    }

    [Fact]
    public async Task A_failing_provider_ends_the_turn_with_its_message()
    {
        await using var bot = new Bot();
        var openai = new FakeProvider("openai", ready: true) { FailWith = new InvalidOperationException("OpenAI: 429 Too Many Requests") };
        await using var service = Service(bot, openai);

        var updates = await Updates.CollectAsync(service.SendAsync("hi"));

        Assert.IsType<AssistantText>(updates[0]);
        Assert.Equal("OpenAI: 429 Too Many Requests", Assert.IsType<AssistantError>(updates[1]).Message);
        Assert.Single(bot.Log.Recent(), e => e.Message.Contains("429"));
    }

    [Fact]
    public async Task Errors_the_AI_app_reports_are_logged_for_problem_reports()
    {
        await using var bot = new Bot();
        var codex = new FakeProvider("codex", ready: true, AssistantProviderKind.Cli) { ErrorText = "Codex: stream disconnected before completion" };
        await using var service = Service(bot, codex);

        var updates = await Updates.CollectAsync(service.SendAsync("hi"));

        Assert.Equal("Codex: stream disconnected before completion", Assert.IsType<AssistantError>(updates[^1]).Message);
        Assert.Contains(bot.Log.Recent(), e => e.Level == Cuebot.Core.Logging.LogLevel.Warning && e.Message == "CODEX: Codex: stream disconnected before completion");

        codex.Ready = false;
        await Updates.CollectAsync(service.SendAsync("hi"));
        Assert.Contains(bot.Log.Recent(), e => e.Level == Cuebot.Core.Logging.LogLevel.Warning && e.Message.StartsWith("No AI is set up yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AI_CLIs_get_the_MCP_endpoint_only_while_it_listens()
    {
        await using var bot = new Bot();
        var cli = new FakeProvider("claude", ready: true, AssistantProviderKind.Cli);
        await using var service = Service(bot, cli);

        await Updates.CollectAsync(service.SendAsync("a"));
        Assert.Null(cli.Sessions[0].Mcp);
        Assert.Contains("MCP server", cli.Sessions[0].Instructions);

        bot.Manager.Settings.Mcp.Enabled = true;
        bot.Manager.Settings.Api.Port = 0;
        await bot.Manager.StartAsync();
        await service.NewChatAsync();
        await Updates.CollectAsync(service.SendAsync("b"));

        var mcp = cli.Sessions[1].Mcp!;
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/mcp$", mcp.Url);
        Assert.NotEqual(bot.Manager.Settings.Api.Token, mcp.Token);   // the conversation's own token
        Assert.StartsWith(Cuebot.Server.Api.ApiToken.Prefix, mcp.Token);
    }

    private const string DeleteHello =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"delete_action","arguments":{"action":"Hello"}}}""";

    /// <summary>An AI app that deletes "Hello" through Cuebot's MCP server over HTTP, as Codex or Claude Code do.</summary>
    private sealed class HttpDeletingProvider : IAssistantProvider
    {
        public string Id => "claude";
        public string Name => "HTTP AI";
        public AssistantProviderKind Kind => AssistantProviderKind.Cli;
        public string? Answer { get; private set; }

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(AssistantProviderStatus.Available("ok"));

        public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

        private sealed class Session(HttpDeletingProvider owner, AssistantContext context) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new AssistantToolStarted("c1", "delete_action");
                using var http = new HttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, context.Mcp!.Url)
                {
                    Content = new StringContent(DeleteHello, System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Mcp.Token);
                using var response = await http.SendAsync(request, ct);
                owner.Answer = await response.Content.ReadAsStringAsync(ct);
                yield return new AssistantToolFinished("c1", "delete_action", false);
                yield return new AssistantText("Done.");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_AI_apps_destructive_call_waits_for_the_card_in_its_turn(bool approve)
    {
        await using var bot = new Bot();
        bot.Manager.Settings.Mcp.Enabled = true;
        bot.Manager.Settings.Api.Port = 0;
        await bot.Manager.StartAsync();
        var ai = new HttpDeletingProvider();
        await using var service = Service(bot, ai);

        var updates = await Updates.CollectAsync(service.SendAsync("delete Hello"), approve);

        var card = Assert.Single(updates.OfType<AssistantConfirmation>());
        Assert.Equal("Delete the action 'Hello'", card.Summary);
        Assert.Equal(approve, await card.Answer);
        Assert.Equal(approve, bot.Profile.Snapshot.FindAction("Hello") is null);
        if (!approve) Assert.Contains("declined", ai.Answer);
        Assert.Equal("Done.", updates.Text());
    }

    [Fact]
    public async Task A_conversation_token_works_on_MCP_only_and_until_the_conversation_ends()
    {
        await using var bot = new Bot();
        bot.Manager.Settings.Mcp.Enabled = true;
        bot.Manager.Settings.Api.Enabled = true;
        bot.Manager.Settings.Api.Port = 0;
        await bot.Manager.StartAsync();
        var asked = new List<string>();
        var (token, registration) = bot.Manager.Mcp.RegisterAssistant((_, summary, _) =>
        {
            asked.Add(summary);
            return Task.FromResult(true);
        });
        using var http = new HttpClient();
        async Task<System.Net.HttpStatusCode> Post(string path, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, bot.Manager.ApiBaseUrl + path) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, await Post("/api/v1/actions", "{}"));
        Assert.Equal(System.Net.HttpStatusCode.OK, await Post("/mcp", DeleteHello));
        Assert.Equal(["Delete the action 'Hello'"], asked);
        Assert.Null(bot.Profile.Snapshot.FindAction("Hello"));

        registration.Dispose();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, await Post("/mcp", DeleteHello));
    }

    // ---- Approvals ---------------------------------------------------------------------------------------------------

    private static async Task<(AssistantService Service, HttpDeletingProvider Ai)> McpServiceAsync(Bot bot)
    {
        bot.Manager.Settings.Mcp.Enabled = true;
        bot.Manager.Settings.Api.Port = 0;
        await bot.Manager.StartAsync();
        var ai = new HttpDeletingProvider();
        return (Service(bot, ai), ai);
    }

    [Theory]
    [InlineData(AssistantApprovals.ThisChat)]
    [InlineData(AssistantApprovals.Always)]
    public async Task An_allowed_mode_runs_an_AI_apps_destructive_call_without_a_card(AssistantApprovals mode)
    {
        await using var bot = new Bot();
        var (service, ai) = await McpServiceAsync(bot);
        await using var _ = service;
        service.Approvals = mode;

        var updates = await Updates.CollectAsync(service.SendAsync("delete Hello"), approve: false);   // a card would be denied

        Assert.Empty(updates.OfType<AssistantConfirmation>());
        var allowed = Assert.Single(updates.OfType<AssistantAllowed>());
        Assert.Equal(("delete_action", "Delete the action 'Hello'"), (allowed.Tool, allowed.Summary));
        Assert.Null(bot.Profile.Snapshot.FindAction("Hello"));
        Assert.DoesNotContain("declined", ai.Answer);
        Assert.Contains(bot.Log.Recent(), e => e.Source == "Assistant" && e.Message.StartsWith("Tool delete_action allowed without asking", StringComparison.Ordinal));
        Assert.Single(updates.OfType<AssistantChanges>());   // the undo card still follows the turn
    }

    [Fact]
    public async Task Ask_every_time_shows_the_card_until_the_tool_is_always_allowed()
    {
        await using var bot = new Bot();
        var (service, _) = await McpServiceAsync(bot);
        await using var _ = service;
        Assert.Equal(AssistantApprovals.Ask, service.Approvals);

        var first = await Updates.CollectAsync(service.SendAsync("delete Hello"), approve: false);
        Assert.Single(first.OfType<AssistantConfirmation>());
        Assert.NotNull(bot.Profile.Snapshot.FindAction("Hello"));

        Assert.True(service.AlwaysAllow("delete_action"));
        Assert.True(service.AlwaysAllow("delete_action"));   // remembered once
        Assert.Equal(["delete_action"], service.AlwaysAllowedTools);
        Assert.Equal(["delete_action"], service.Settings.AlwaysAllowedTools);
        Assert.True(service.AllowsWithoutAsking("delete_action"));
        Assert.False(service.AllowsWithoutAsking("delete_command"));

        var second = await Updates.CollectAsync(service.SendAsync("delete Hello"), approve: false);
        Assert.Empty(second.OfType<AssistantConfirmation>());
        Assert.Single(second.OfType<AssistantAllowed>());
        Assert.Null(bot.Profile.Snapshot.FindAction("Hello"));

        service.Forget("delete_action");
        Assert.Empty(service.AlwaysAllowedTools);
        Assert.False(service.AllowsWithoutAsking("delete_action"));
        service.AlwaysAllow("delete_action");
        service.AlwaysAllow("delete_timer");
        service.ForgetAll();
        Assert.Empty(service.AlwaysAllowedTools);
    }

    /// <summary>An API-key AI whose tool call asks through its own stream, as <c>ToolCalls.RunAsync</c> does.</summary>
    private sealed class AskingProvider(string tool, string summary) : IAssistantProvider
    {
        public string Id => "openai";
        public string Name => "Asking AI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;
        public List<bool> Answers { get; } = [];
        public string Tool => tool;
        public string Summary => summary;

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(AssistantProviderStatus.Available("ok"));

        public IAssistantSession StartSession(AssistantContext context) => new Session(this);

        private sealed class Session(AskingProvider owner) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                var card = new AssistantConfirmation(owner.Tool, owner.Summary);
                yield return card;
                owner.Answers.Add(await card.Answer);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_API_key_AIs_card_is_answered_by_the_approvals_setting()
    {
        await using var bot = new Bot();
        var ai = new AskingProvider("delete_timer", "Delete the timer 'Socials'");
        await using var service = Service(bot, ai);

        var asked = await Updates.CollectAsync(service.SendAsync("go"), approve: false);
        Assert.Single(asked.OfType<AssistantConfirmation>());

        service.Approvals = AssistantApprovals.ThisChat;
        var allowed = await Updates.CollectAsync(service.SendAsync("go"), approve: false);
        Assert.Empty(allowed.OfType<AssistantConfirmation>());
        Assert.Equal("Delete the timer 'Socials'", Assert.Single(allowed.OfType<AssistantAllowed>()).Summary);
        Assert.Equal([false, true], ai.Answers);
    }

    [Theory]
    [InlineData(AssistantApprovals.ThisChat)]
    [InlineData(AssistantApprovals.Always)]
    public async Task Publishing_tools_always_ask_whatever_the_setting(AssistantApprovals mode)
    {
        await using var bot = new Bot();
        var ai = new AskingProvider("request_feature", "File this feature request as a public GitHub issue");
        await using var service = Service(bot, ai);
        service.Approvals = mode;

        Assert.True(service.AlwaysAsks("request_feature"));
        Assert.False(service.AlwaysAsks("delete_action"));
        Assert.False(service.AlwaysAllow("request_feature"));
        Assert.Empty(service.AlwaysAllowedTools);
        service.Settings.AlwaysAllowedTools.Add("request_feature");   // even a hand-edited assistant.json
        Assert.False(service.AllowsWithoutAsking("request_feature"));

        var updates = await Updates.CollectAsync(service.SendAsync("file it"), approve: false);
        Assert.Single(updates.OfType<AssistantConfirmation>());
        Assert.Empty(updates.OfType<AssistantAllowed>());
        Assert.Equal([false], ai.Answers);
    }

    [Fact]
    public async Task Allow_everything_in_this_chat_ends_with_New_chat_and_a_restart()
    {
        await using var bot = new Bot();
        await using var service = Service(bot, new FakeProvider("openai", ready: true));

        service.Approvals = AssistantApprovals.ThisChat;
        await Updates.CollectAsync(service.SendAsync("hi"));
        Assert.Equal(AssistantApprovals.ThisChat, service.Approvals);   // the first message does not end it
        await service.NewChatAsync();
        Assert.Equal(AssistantApprovals.Ask, service.Approvals);

        service.Approvals = AssistantApprovals.Always;
        await service.NewChatAsync();
        Assert.Equal(AssistantApprovals.Always, service.Approvals);   // trusted stays

        // A restart: assistant.json as saved with "this chat" loads as Ask; remembered tools stay.
        var path = Path.Combine(Directory.CreateTempSubdirectory("cuebot-assistant-").FullName, "assistant.json");
        try
        {
            using (var file = new Cuebot.Core.Storage.JsonFile<AssistantSettings>(path, bot.Log))
            {
                file.Value.Approvals = AssistantApprovals.ThisChat;
                file.Value.AlwaysAllowedTools.Add("delete_action");
                file.SaveNow();
            }
            using var reloaded = new Cuebot.Core.Storage.JsonFile<AssistantSettings>(path, bot.Log);
            await using var restarted = new AssistantService(reloaded, [], bot.Manager, null, bot.Log);
            Assert.Equal(AssistantApprovals.Ask, restarted.Approvals);
            Assert.Equal(["delete_action"], restarted.AlwaysAllowedTools);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
