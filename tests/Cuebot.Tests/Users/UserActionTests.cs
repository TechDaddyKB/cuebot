using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Tests.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Tests.Users;

public class UserActionTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ChatHub _hub;
    private readonly UserRegistry _users;

    public UserActionTests()
    {
        _bus = new EventBus(_log);
        _hub = new ChatHub(_bus, _log, [], []);
        _users = new UserRegistry(null, _bus, _log, new TestClock());
    }

    [Fact]
    public async Task Reset_first_words_sub_action()
    {
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("hi", "bob", "2", "Rumble"));
        var result = await SubActionHarness.RunAsync(new ResetFirstWordsSubAction(_users, _hub), new() { ["platform"] = "Rumble" });
        Assert.Equal(1, result.Args["firstWordsReset"]);
        Assert.True(_users.HasSpoken("Twitch", "1"));
        Assert.False(_users.HasSpoken("Rumble", "2"));
    }

    [Fact]
    public async Task Add_random_present_user_to_arguments()
    {
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("hi", "bob", "2"));
        var caller = new Dictionary<string, object?> { ["platform"] = "Twitch", ["user"] = "alice", ["userId"] = "1" };

        var result = await SubActionHarness.RunAsync(new AddRandomPresentUserSubAction(_users, _hub), [], caller);
        Assert.Equal("bob", result.Args["randomUser"]);
        Assert.Equal("2", result.Args["randomUserId"]);
        Assert.Equal("Twitch", result.Args["randomUserPlatform"]);
        Assert.Equal(true, result.Args["randomUserFound"]);

        var none = await SubActionHarness.RunAsync(new AddRandomPresentUserSubAction(_users, _hub),
            new() { ["platform"] = "Rumble", ["argument"] = "pick" }, caller);
        Assert.Equal(false, none.Args["pickFound"]);
        Assert.Equal("", none.Args["pick"]);
    }

    [Fact]
    public void Triggers_filter_by_platform()
    {
        var trigger = new UserTriggers(_hub).GetTriggers().Single(t => t.TypeId == "users.firstWords");
        var e = new BotEvent(UserEvents.FirstWords, new Dictionary<string, object?> { ["platform"] = "Twitch" });
        bool Matches(string platform) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["platform"] = platform }), new Dictionary<string, object?>());
        Assert.True(Matches("All"));
        Assert.True(Matches("twitch"));
        Assert.False(Matches("Rumble"));
    }

    public void Dispose()
    {
        _users.Dispose();
        _hub.Dispose();
        _log.Dispose();
    }
}

public class CommandAndUserWiringTests
{
    [Fact]
    public async Task Core_registration_wires_commands_users_groups_and_timers_end_to_end()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        await using (var services = new ServiceCollection().AddCuebotCore(paths).BuildServiceProvider())
        {
            services.GetRequiredService<ChatHub>();
            var engine = services.GetRequiredService<ActionEngine>();
            engine.Start();
            services.GetRequiredService<TimerService>(); // brings CommandService and UserRegistry to life

            var registry = services.GetRequiredService<Registry>();
            foreach (var id in new[]
                     {
                         "command.triggered", "command.cooldown", "users.firstWords", "users.presentViewers", "groups.added",
                         "groups.removed", "groups.cleared", "groups.usersAdded", "groups.usersRemoved",
                     })
                Assert.NotNull(registry.GetTrigger(id));
            foreach (var id in new[]
                     {
                         "commands.getState", "commands.setState", "commands.getGroupState", "commands.setGroupState",
                         "commands.getCommands", "commands.resetCounters", "users.resetFirstWords", "users.addRandomPresentUser",
                         "groups.addUser", "groups.removeUser", "groups.userInGroup", "groups.clear", "timers.setState", "timers.getState",
                     })
                Assert.NotNull(registry.GetSubAction(id));
            Assert.Contains(services.GetRequiredService<ScriptApi>().Functions, f => f.Name == "groups.contains");

            var store = services.GetRequiredService<CommandStore>();
            store.Save(new CommandDefinition { Name = "Hello", Commands = ["!hello"], PersistCounter = true });
            var profile = services.GetRequiredService<ProfileService>();
            var action = new ActionDefinition
            {
                Name = "Greeter",
                Triggers = [new TriggerDefinition { Type = "command.triggered", Settings = { ["command"] = "Hello" } }],
                SubActions = [new SubActionDefinition { Type = "groups.addUser", Parameters = { ["group"] = "Greeted" } }],
            };
            profile.Profile.Actions.Add(action);
            profile.NotifyChanged();
            var runs = new List<ActionRunRecord>();
            engine.RunCompleted += r => { lock (runs) runs.Add(r); };

            services.GetRequiredService<EventBus>().Publish(Chat.Message("!hello there", "alice", "1"));

            for (var i = 0; i < 100 && runs.Count == 0; i++) await Task.Delay(20);
            Assert.True(Assert.Single(runs).Success);
            Assert.True(services.GetRequiredService<GroupStore>().Contains("Greeted", "Twitch", "1", "alice"));
            Assert.Equal(1, services.GetRequiredService<UserRegistry>().Count);
            Assert.Equal(1, store.GetCounter(store.Snapshot[0].Id));
        }

        Assert.True(File.Exists(paths.File("commands.json")));
        Assert.True(File.Exists(paths.File("command-counters.json")));
        Assert.True(File.Exists(paths.File("groups.json")));
        Assert.True(File.Exists(paths.File("users.json")));
    }
}
