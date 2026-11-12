using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cuebot.Server.Mcp;

namespace Cuebot.Assistant.Tests;

/// <summary>Conversations for mods (ScopedAssistant): their own tools only, their own confirmations, limits on turns.</summary>
public sealed class ScopedTests
{
    /// <summary>An API-key provider whose turn is a script run against the conversation's context.</summary>
    private sealed class ScriptedProvider(Func<AssistantContext, string, CancellationToken, IAsyncEnumerable<AssistantUpdate>> turn) : IAssistantProvider
    {
        public string Id => "openai";
        public string Name => "OpenAI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;
        public List<AssistantContext> Contexts { get; } = [];

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(AssistantProviderStatus.Available("ok"));

        public IAssistantSession StartSession(AssistantContext context)
        {
            Contexts.Add(context);
            return new Session(this, context);
        }

        private sealed class Session(ScriptedProvider owner, AssistantContext context) : IAssistantSession
        {
            public IAsyncEnumerable<AssistantUpdate> SendAsync(string text, CancellationToken ct = default) => owner._turn(context, text, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private readonly Func<AssistantContext, string, CancellationToken, IAsyncEnumerable<AssistantUpdate>> _turn = turn;
    }

    private static McpScope Scope(List<string> zapped) => new(
    [
        new McpTool("echo", "Echo", "Says it back.", new JsonObject { ["type"] = "object" }, new McpToolHints(ReadOnly: true),
            (a, _) => Task.FromResult<JsonNode>(new JsonObject { ["said"] = a["text"]?.DeepClone() })),
        new McpTool("zap", "Zap", "Deletes something.", new JsonObject { ["type"] = "object" }, new McpToolHints(ReadOnly: false, Destructive: true),
            (a, _) =>
            {
                zapped.Add((string?)a["what"] ?? "");
                return Task.FromResult<JsonNode>(new JsonObject { ["zapped"] = true });
            })
        {
            Confirmation = a => $"Zap {(string?)a["what"]}",
        },
    ], "Scoped instructions");

    private static async IAsyncEnumerable<AssistantUpdate> CallTools(AssistantContext context, string text, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AssistantText("tools: " + string.Join(",", context.Tools.List().Select(t => t.Name)) + "\n");
        foreach (var (name, args) in new[] { ("zap", new JsonObject { ["what"] = text }), ("list_actions", new JsonObject()) })
        {
            var result = new StrongBox<AssistantToolResult?>();
            await foreach (var update in ToolCalls.RunAsync(context.Tools, name, name, args, result, ct)) yield return update;
            yield return new AssistantText($"{name}: {result.Value!.Content}\n");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_mods_conversation_has_only_its_tools_and_asks_the_mod_before_a_destructive_one()
    {
        // Confirmation is off for MCP clients here: a scoped conversation asks anyway.
        await using var bot = new Bot(requireConfirmation: false);
        var provider = new ScriptedProvider(CallTools);
        await using var service = new AssistantService(null, [provider], bot.Manager, null, bot.Log);
        await using var scoped = new ScopedAssistant(service);
        var zapped = new List<string>();
        var conversation = scoped.For("mod-1", () => Scope(zapped), () => "Mod instructions");

        Assert.Null(conversation.Send("the thing"));
        Assert.NotNull(conversation.Send("again"));   // one turn at a time
        await Until(() => conversation.Since(0).Any(l => l.Kind == "confirm"));
        var question = conversation.Since(0).Single(l => l.Kind == "confirm");
        Assert.Equal(("Zap the thing", "waiting"), (question.Text, question.State));
        Assert.Empty(zapped);

        Assert.True(conversation.Answer(question.Id, approve: true));
        Assert.False(conversation.Answer(question.Id, approve: true));
        await Until(() => !conversation.Busy);

        Assert.Equal(["the thing"], zapped);
        var context = Assert.Single(provider.Contexts);
        Assert.Equal("Mod instructions", context.Instructions);
        Assert.True(context.Tools.Scoped);
        var answer = string.Concat(conversation.Since(0).Where(l => l.Kind == "assistant").Select(l => l.Text));
        Assert.Contains($"tools: echo,zap,{AssistantTools.ReadDocTool}", answer);
        Assert.Contains("zap: {\"zapped\":true}", answer);
        Assert.Contains("list_actions: ", answer);
        Assert.Contains("Unknown tool: list_actions", answer);   // Cuebot's own tools do not exist here
        Assert.Equal("approved", conversation.Since(0).Single(l => l.Kind == "confirm").State);
        Assert.Equal(["done", "failed"], conversation.Since(0).Where(l => l.Kind == "tool").Select(l => l.State));
        // The streamer's own conversation was not touched.
        Assert.Empty(service.History);
    }

    [Fact]
    public async Task A_denied_or_stopped_turn_does_nothing_and_turns_share_two_slots()
    {
        await using var bot = new Bot();
        var provider = new ScriptedProvider(CallTools);
        await using var service = new AssistantService(null, [provider], bot.Manager, null, bot.Log);
        await using var scoped = new ScopedAssistant(service);
        var zapped = new List<string>();

        var a = scoped.For("a", () => Scope(zapped), () => "");
        Assert.Null(a.Send("one"));
        await Until(() => a.Since(0).Any(l => l.Kind == "confirm"));
        Assert.True(a.Answer(a.Since(0).Single(l => l.Kind == "confirm").Id, approve: false));
        await Until(() => !a.Busy);
        Assert.Empty(zapped);
        Assert.Contains("declined", string.Concat(a.Since(0).Where(l => l.Kind == "assistant").Select(l => l.Text)));

        // Two turns wait for their mods; a third conversation has to wait for a slot.
        var b = scoped.For("b", () => Scope(zapped), () => "");
        var c = scoped.For("c", () => Scope(zapped), () => "");
        var d = scoped.For("d", () => Scope(zapped), () => "");
        Assert.Null(b.Send("two"));
        Assert.Null(c.Send("three"));
        await Until(() => b.Since(0).Any(l => l.Kind == "confirm") && c.Since(0).Any(l => l.Kind == "confirm"));
        Assert.Contains("busy", d.Send("four"));

        // Stop ends the turn and answers its question with no.
        b.Stop();
        await Until(() => !b.Busy);
        Assert.Equal("denied", b.Since(0).Single(l => l.Kind == "confirm").State);
        Assert.Contains(b.Since(0), l => l.Kind == "notice" && l.Text == "Stopped.");
        Assert.Null(d.Send("four"));

        // Reading after a seq gives only what changed since.
        var seq = c.Seq;
        Assert.Empty(c.Since(seq));
        await c.ResetAsync();
        Assert.Equal("New conversation.", Assert.Single(c.Since(seq)).Text);
        Assert.Empty(zapped);
    }

    [Fact]
    public async Task The_mcp_server_offers_a_scoped_token_only_its_tools_and_the_two_guides()
    {
        await using var bot = new Bot(requireConfirmation: false);
        var zapped = new List<string>();
        var scope = Scope(zapped);
        JsonNode? Ask(string method, JsonObject? p = null) => bot.Manager.Mcp.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = p ?? new JsonObject(),
        }, scope: scope).GetAwaiter().GetResult();

        Assert.Equal("Scoped instructions", (string?)Ask("initialize")!["result"]!["instructions"]);
        Assert.Equal(["echo", "zap"], Ask("tools/list")!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.Equal("Unknown tool: list_actions", (string?)Ask("tools/call", new JsonObject { ["name"] = "list_actions" })!["error"]!["message"]);
        Assert.Equal([McpCatalog.VariablesUri, McpCatalog.ActionsDocUri],
            Ask("resources/list")!["result"]!["resources"]!.AsArray().Select(r => (string)r!["uri"]!));
        Assert.NotNull(Ask("resources/read", new JsonObject { ["uri"] = McpCatalog.ActionsDocUri })!["result"]);
        Assert.NotNull(Ask("resources/read", new JsonObject { ["uri"] = "cuebot://docs/capabilities" })!["error"]);
        Assert.Empty(Ask("prompts/list")!["result"]!["prompts"]!.AsArray());
        Assert.NotNull(Ask("prompts/get", new JsonObject { ["name"] = "build_action" })!["error"]);

        // Without an Approve callback a destructive scoped tool answers with a confirmation token, even with confirmation off.
        var call = Ask("tools/call", new JsonObject { ["name"] = "zap", ["arguments"] = new JsonObject { ["what"] = "x" } })!["result"]!;
        Assert.True((bool)call["structuredContent"]!["confirmationRequired"]!);
        Assert.Empty(zapped);
    }
}
