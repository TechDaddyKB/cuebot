using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;

namespace Cuebot.Assistant.Tests;

public sealed class UndoTests
{
    [Fact]
    public async Task Undo_puts_back_what_a_turn_changed_and_keeps_later_edits()
    {
        var log = new AppLog();
        using var profile = new ProfileService(null, log);
        var commands = new CommandStore(null, log);
        var keep = new ActionDefinition { Name = "Keep me" };
        var edit = new ActionDefinition { Name = "Edit me" };
        var gone = new ActionDefinition { Name = "Delete me" };
        await profile.EditAsync(p => p.Actions.AddRange([keep, edit, gone]));
        var before = AssistantUndo.Capture(profile, commands);

        // The turn: creates an action, a command and a timer, edits one action, deletes another.
        var made = new ActionDefinition { Name = "Lurk" };
        var later = new ActionDefinition { Name = "Edited later" };
        await profile.EditAsync(p =>
        {
            p.Actions.Add(made);
            p.Actions.Add(later);
            p.Actions.Single(a => a.Name == "Edit me").Enabled = false;
            p.Actions.RemoveAll(a => a.Name == "Delete me");
            p.Timers.Add(new TimerDefinition { Name = "Socials" });
        });
        commands.Save(new CommandDefinition { Name = "Lurk", Commands = ["!lurk"] });
        var undo = AssistantUndo.Since(before, profile, commands)!;
        Assert.Contains("created 2 actions ('Lurk', 'Edited later')", undo.Summary);
        Assert.Contains("changed the action 'Edit me'", undo.Summary);
        Assert.Contains("deleted the action 'Delete me'", undo.Summary);
        Assert.Contains("created the command 'Lurk'", undo.Summary);
        Assert.Contains("created the timer 'Socials'", undo.Summary);

        // The streamer changes one of the new actions afterwards: undo leaves that one alone.
        await profile.EditAsync(p => p.Actions.Single(a => a.Name == "Edited later").Group = "Mine");

        var result = await undo.UndoAsync();

        var names = profile.Snapshot.Actions.Select(a => a.Name).ToList();
        Assert.Equal(["Keep me", "Edit me", "Edited later", "Delete me"], names);
        Assert.True(profile.Snapshot.FindAction("Edit me")!.Enabled);
        Assert.Equal(gone.Id, profile.Snapshot.FindAction("Delete me")!.Id);   // the same action, so references to it still work
        Assert.Empty(profile.Snapshot.Timers);
        Assert.Null(commands.Find("Lurk"));
        Assert.Contains("removed the action 'Lurk'", result);
        Assert.Contains("brought back the action 'Delete me'", result);
        Assert.Contains("Kept the action 'Edited later'", result);
    }

    [Fact]
    public void A_turn_that_changes_nothing_offers_no_undo()
    {
        var log = new AppLog();
        using var profile = new ProfileService(null, log);
        var before = AssistantUndo.Capture(profile, null);
        Assert.Null(AssistantUndo.Since(before, profile, null));
    }

    /// <summary>Creates and deletes an action through Cuebot's tools, like an AI would.</summary>
    private sealed class ToolUsingProvider : IAssistantProvider
    {
        public string Id => "tools";
        public string Name => "Tool-using AI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(AssistantProviderStatus.Available("ready"));

        public IAssistantSession StartSession(AssistantContext context) => new Session(context);

        private sealed class Session(AssistantContext context) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                await context.Tools.CallAsync("create_action", new JsonObject { ["action"] = new JsonObject { ["name"] = "Made by AI" } },
                    (_, _) => Task.FromResult(true), ct);
                await context.Tools.CallAsync("delete_action", new JsonObject { ["action"] = "Hello" }, (_, _) => Task.FromResult(true), ct);
                yield return new AssistantText("Done.");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_turn_that_changed_something_ends_with_an_undo_card()
    {
        await using var bot = new Bot();
        await using var service = new AssistantService(null, [new ToolUsingProvider()], bot.Manager, null, bot.Log);

        var updates = await Updates.CollectAsync(service.SendAsync("Replace Hello with a new action"));

        var changes = Assert.IsType<AssistantChanges>(updates[^1]);
        Assert.Equal("The assistant created the action 'Made by AI', deleted the action 'Hello'.", changes.Undo.Summary);
        await changes.Undo.UndoAsync();
        Assert.NotNull(bot.Profile.Snapshot.FindAction("Hello"));
        Assert.Null(bot.Profile.Snapshot.FindAction("Made by AI"));
    }
}
