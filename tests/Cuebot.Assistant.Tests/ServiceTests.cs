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
        Assert.Contains(bot.Log.Recent(), e => e.Message.Contains("429"));
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
        Assert.Equal(bot.Manager.Settings.Api.Token, mcp.Token);
    }
}
