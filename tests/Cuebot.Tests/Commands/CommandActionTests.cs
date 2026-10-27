using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Commands;

public class CommandActionTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly CommandStore _store;
    private readonly CommandDefinition _hello;

    public CommandActionTests()
    {
        _store = new CommandStore(null, _log);
        _hello = new CommandDefinition { Name = "Hello", Group = "Fun", Commands = ["!hello", "!hi"] };
        _store.Save(_hello);
        _store.Save(new CommandDefinition { Name = "Dice", Group = "Fun", Commands = ["!roll"] });
        _store.Save(new CommandDefinition { Name = "Ban", Group = "Mod", Commands = ["!ban"], Include = false });
        _store.Save(new CommandDefinition { Name = "Off", Group = "Mod", Commands = ["!off"], Enabled = false });
    }

    [Fact]
    public void Triggers_filter_by_command_name_or_id()
    {
        var triggers = new CommandTriggers(_store).GetTriggers().ToArray();
        Assert.Equal(["command.triggered", "command.cooldown"], triggers.Select(t => t.TypeId));
        var trigger = triggers[0];
        Assert.Contains("Hello", trigger.Parameters[0].Suggestions!());

        var e = new BotEvent(CommandService.TriggeredEvent, new Dictionary<string, object?>
        {
            ["commandName"] = "Hello", ["commandId"] = _hello.Id.ToString(),
        });
        bool Matches(string setting) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["command"] = setting }), new Dictionary<string, object?>());
        Assert.True(Matches(""));
        Assert.True(Matches("hello"));
        Assert.True(Matches(_hello.Id.ToString()));
        Assert.False(Matches("Dice"));
    }

    [Fact]
    public async Task Get_and_set_command_state()
    {
        var result = await SubActionHarness.RunAsync(new SetCommandStateSubAction(_store), new() { ["command"] = "hello", ["state"] = "Disable" });
        Assert.True(result.Success);
        Assert.False(_store.Find("Hello")!.Enabled);

        await SubActionHarness.RunAsync(new SetCommandStateSubAction(_store), new() { ["command"] = "Hello", ["state"] = "Toggle" });
        Assert.True(_store.Find("Hello")!.Enabled);

        _store.Increment(_hello.Id, "twitch:1");
        var state = await SubActionHarness.RunAsync(new GetCommandStateSubAction(_store), new() { ["command"] = "%commandName%" },
            new() { ["commandName"] = "Hello" });
        Assert.Equal(true, state.Args["commandEnabled"]);
        Assert.Equal(1L, state.Args["commandCounter"]);
        Assert.Equal("Fun", state.Args["commandStateGroup"]);
    }

    [Fact]
    public async Task Unknown_command_fails_the_action()
    {
        var result = await SubActionHarness.RunAsync(new GetCommandStateSubAction(_store), new() { ["command"] = "nope" });
        Assert.False(result.Success);
        Assert.Contains(result.Log, l => l.Level == LogLevel.Error && l.Message.Contains("nope"));
    }

    [Fact]
    public async Task Get_and_set_command_group_state()
    {
        var mod = await SubActionHarness.RunAsync(new GetCommandGroupStateSubAction(_store), new() { ["group"] = "mod" });
        Assert.Equal(false, mod.Args["commandGroupEnabled"]);
        Assert.Equal(1, mod.Args["commandGroupEnabledCount"]);
        Assert.Equal(2, mod.Args["commandGroupCount"]);

        await SubActionHarness.RunAsync(new SetCommandGroupStateSubAction(_store), new() { ["group"] = "Mod", ["state"] = "Toggle" });
        Assert.All(_store.Snapshot.Where(c => c.Group == "Mod"), c => Assert.True(c.Enabled));
        await SubActionHarness.RunAsync(new SetCommandGroupStateSubAction(_store), new() { ["group"] = "Mod", ["state"] = "Toggle" });
        Assert.All(_store.Snapshot.Where(c => c.Group == "Mod"), c => Assert.False(c.Enabled));
        Assert.All(_store.Snapshot.Where(c => c.Group == "Fun"), c => Assert.True(c.Enabled));
    }

    [Fact]
    public async Task Get_commands_lists_included_enabled_commands()
    {
        var all = await SubActionHarness.RunAsync(new GetCommandsSubAction(_store), []);
        Assert.Equal("!hello, !roll", all.Args["commands"]);
        Assert.Equal(2, all.Args["commandsCount"]);

        var aliases = await SubActionHarness.RunAsync(new GetCommandsSubAction(_store),
            new() { ["argument"] = "list", ["show"] = "All aliases", ["separator"] = " | ", ["group"] = "fun" });
        Assert.Equal("!hello | !hi | !roll", aliases.Args["list"]);

        var names = await SubActionHarness.RunAsync(new GetCommandsSubAction(_store),
            new() { ["show"] = "Names", ["includeDisabled"] = "true" });
        Assert.Equal("Hello, Dice, Off", names.Args["commands"]); // Ban is not included
    }

    [Fact]
    public async Task Reset_counters_sub_action()
    {
        _store.Increment(_hello.Id, CommandStore.UserKey("Twitch", "1", "a"));
        _store.Increment(_hello.Id, CommandStore.UserKey("Twitch", "2", "b"));

        await SubActionHarness.RunAsync(new ResetCommandCountersSubAction(_store),
            new() { ["command"] = "Hello", ["reset"] = "User counters", ["user"] = "%userId%" },
            new() { ["platform"] = "Twitch", ["userId"] = "1" });
        Assert.Equal(0, _store.GetUserCounter(_hello.Id, CommandStore.UserKey("Twitch", "1", "a")));
        Assert.Equal(1, _store.GetUserCounter(_hello.Id, CommandStore.UserKey("Twitch", "2", "b")));
        Assert.Equal(2, _store.GetCounter(_hello.Id));

        await SubActionHarness.RunAsync(new ResetCommandCountersSubAction(_store), new() { ["command"] = "Hello", ["reset"] = "Both" });
        Assert.Equal(0, _store.GetCounter(_hello.Id));
        Assert.Equal(0, _store.GetUserCounter(_hello.Id, CommandStore.UserKey("Twitch", "2", "b")));
    }

    public void Dispose()
    {
        _store.Dispose();
        _log.Dispose();
    }
}
