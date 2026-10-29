using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Tests;

public sealed class ToolTests
{
    [Fact]
    public async Task Lists_the_MCP_tools_plus_the_guide_reader()
    {
        await using var bot = new Bot();
        var tools = bot.Tools.List();

        Assert.Contains(tools, t => t.Name == "list_actions");
        Assert.Contains(tools, t => t.Name == "delete_action");
        var doc = Assert.Single(tools, t => t.Name == AssistantTools.ReadDocTool);
        Assert.Equal("object", doc.InputSchema["type"]!.GetValue<string>());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task Calls_a_tool_in_process()
    {
        await using var bot = new Bot();
        var result = await bot.Tools.CallAsync("list_actions", [], (_, _) => throw new InvalidOperationException("no confirmation expected"));

        Assert.False(result.IsError);
        Assert.Contains("Hello", result.Content);
        Assert.Contains(bot.Log.Recent(), e => e.Message == "Tool call: list_actions");
    }

    [Fact]
    public async Task Reads_a_guide_and_refuses_other_uris()
    {
        await using var bot = new Bot();
        var guide = await bot.Tools.CallAsync(AssistantTools.ReadDocTool, new JsonObject { ["uri"] = "cuebot://docs/actions" }, (_, _) => Task.FromResult(true));
        var other = await bot.Tools.CallAsync(AssistantTools.ReadDocTool, new JsonObject { ["uri"] = "file:///etc/passwd" }, (_, _) => Task.FromResult(true));

        Assert.False(guide.IsError);
        Assert.Contains("trigger", guide.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(other.IsError);
    }

    [Fact]
    public async Task A_destructive_call_runs_only_after_the_streamer_approves()
    {
        await using var bot = new Bot();
        string? asked = null;

        var declined = await bot.Tools.CallAsync("delete_action", new JsonObject { ["action"] = "Hello" }, (_, summary) =>
        {
            asked = summary;
            return Task.FromResult(false);
        });
        Assert.Equal("Delete the action 'Hello'", asked);
        Assert.Contains("declined", declined.Content);
        Assert.NotNull(bot.Profile.Snapshot.FindAction("Hello"));

        var approved = await bot.Tools.CallAsync("delete_action", new JsonObject { ["action"] = "Hello" }, (_, _) => Task.FromResult(true));
        Assert.False(approved.IsError);
        Assert.Null(bot.Profile.Snapshot.FindAction("Hello"));
    }

    [Fact]
    public async Task Without_required_confirmation_nothing_is_asked()
    {
        await using var bot = new Bot(requireConfirmation: false);
        var result = await bot.Tools.CallAsync("delete_action", new JsonObject { ["action"] = "Hello" }, (_, _) => throw new InvalidOperationException("asked"));

        Assert.False(result.IsError);
        Assert.Null(bot.Profile.Snapshot.FindAction("Hello"));
    }

    [Fact]
    public async Task The_update_stream_carries_the_confirmation_and_a_stopped_turn_denies_it()
    {
        await using var bot = new Bot();
        using var cts = new CancellationTokenSource();
        var result = new StrongBox<AssistantToolResult?>();
        var seen = new List<AssistantUpdate>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in ToolCalls.RunAsync(bot.Tools, "c1", "delete_action", new JsonObject { ["action"] = "Hello" }, result, cts.Token))
            {
                seen.Add(update);
                if (update is AssistantConfirmation) await cts.CancelAsync();
            }
        });

        Assert.IsType<AssistantToolStarted>(seen[0]);
        var confirmation = Assert.IsType<AssistantConfirmation>(seen[1]);
        Assert.False(await confirmation.Answer);
        Assert.NotNull(bot.Profile.Snapshot.FindAction("Hello"));
    }
}
