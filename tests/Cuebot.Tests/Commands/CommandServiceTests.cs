using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;

namespace Cuebot.Tests.Commands;

public class CommandServiceTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private readonly CommandService _service;
    private readonly EventRecorder _events;

    public CommandServiceTests()
    {
        _bus = new EventBus(_log);
        _store = new CommandStore(null, _log);
        _groups = new GroupStore(null, _log, _bus);
        _service = new CommandService(_store, _groups, _bus, _log, _clock);
        _events = new EventRecorder(_bus, CommandService.TriggeredEvent, CommandService.CooldownEvent);
    }

    private CommandDefinition Add(Action<CommandDefinition>? configure = null, params string[] aliases)
    {
        var command = new CommandDefinition { Name = "Hello", Commands = aliases.Length > 0 ? [.. aliases] : ["!hello"] };
        configure?.Invoke(command);
        _store.Save(command);
        return command;
    }

    private IReadOnlyList<BotEvent> Send(string text, string user = "alice", string userId = "1", string platform = "Twitch",
        ChatRoles roles = ChatRoles.None, Dictionary<string, object?>? extra = null)
    {
        _events.Clear();
        _bus.Publish(Chat.Message(text, user, userId, platform, roles, extra));
        return _events.Events;
    }

    private IReadOnlyList<BotEvent> Triggered(string text, string user = "alice", string userId = "1", string platform = "Twitch",
        ChatRoles roles = ChatRoles.None) =>
        Send(text, user, userId, platform, roles).Where(e => e.Type == CommandService.TriggeredEvent).ToArray();

    [Fact]
    public void Publishes_command_triggered_with_inputs_and_counters()
    {
        var command = Add();
        var e = Assert.Single(Send("!hello big world"));
        Assert.Equal(CommandService.TriggeredEvent, e.Type);
        Assert.Equal("!hello", e.Args["command"]);
        Assert.Equal(command.Id.ToString(), e.Args["commandId"]);
        Assert.Equal("Hello", e.Args["commandName"]);
        Assert.Equal("Start", e.Args["commandLocation"]);
        Assert.Equal("big world", e.Args["input"]);
        Assert.Equal("big", e.Args["input0"]);
        Assert.Equal("world", e.Args["input1"]);
        Assert.Equal(2, e.Args["inputCount"]);
        Assert.Equal("!hello big world", e.Args["rawInput"]);
        Assert.Equal("alice", e.Args["user"]);
        Assert.Equal("1", e.Args["userId"]);
        Assert.Equal("Twitch", e.Args["platform"]);
        Assert.Equal(1L, e.Args["counter"]);
        Assert.Equal(1L, e.Args["userCounter"]);

        Send("!hello", user: "bob", userId: "2");
        var third = Assert.Single(Send("!hello again"));
        Assert.Equal(3L, third.Args["counter"]);
        Assert.Equal(2L, third.Args["userCounter"]);
    }

    [Fact]
    public void Non_matching_and_disabled_commands_publish_nothing()
    {
        var command = Add();
        Assert.Empty(Send("hello"));
        _store.SetEnabled(command.Id, false);
        Assert.Empty(Send("!hello"));
        _store.SetEnabled(command.Id, true);
        Assert.Single(Send("!hello"));
    }

    [Fact]
    public void Every_matching_command_fires()
    {
        Add(c => c.Name = "A");
        Add(c => { c.Name = "B"; c.Location = CommandLocation.Anywhere; }, "hey");
        Assert.Equal(["A"], Triggered("!hello").Select(e => (string)e.Args["commandName"]!));
        Assert.Equal(["B"], Triggered("well hey there").Select(e => (string)e.Args["commandName"]!));
    }

    [Fact]
    public void Aliases_share_one_cooldown_and_counter()
    {
        Add(c => c.GlobalCooldownSeconds = 30, "!hi", "!hey");
        Assert.Equal("!hi", Assert.Single(Triggered("!hi"))!.Args["command"]);

        var cooldown = Assert.Single(Send("!hey", user: "bob", userId: "2"));
        Assert.Equal(CommandService.CooldownEvent, cooldown.Type);
        Assert.Equal("Global", cooldown.Args["cooldownType"]);
        Assert.Equal(30, cooldown.Args["remaining"]);

        _clock.Advance(30);
        var again = Assert.Single(Triggered("!hey", user: "bob", userId: "2"));
        Assert.Equal(2L, again.Args["counter"]);
    }

    [Fact]
    public void User_cooldown_blocks_only_that_user_and_reports_remaining()
    {
        Add(c => c.UserCooldownSeconds = 60);
        Assert.Single(Triggered("!hello"));
        _clock.Advance(15);
        var e = Assert.Single(Send("!hello"));
        Assert.Equal(CommandService.CooldownEvent, e.Type);
        Assert.Equal("User", e.Args["cooldownType"]);
        Assert.Equal(45, e.Args["remaining"]);
        Assert.Equal("Hello", e.Args["commandName"]);
        Assert.Single(Triggered("!hello", user: "bob", userId: "2"));
        Assert.Equal(1L, _store.GetUserCounter(_store.Snapshot[0].Id, CommandStore.UserKey("Twitch", "1", "alice")));
    }

    [Fact]
    public void Broadcaster_is_exempt_from_cooldowns()
    {
        Add(c => c.GlobalCooldownSeconds = 60);
        Assert.Single(Triggered("!hello"));
        Assert.Single(Triggered("!hello", "streamer", "99", roles: ChatRoles.Broadcaster));
        Assert.Single(Triggered("!hello", "streamer", "99", roles: ChatRoles.Broadcaster));
        Assert.Equal(CommandService.CooldownEvent, Assert.Single(Send("!hello", "bob", "2")).Type);
    }

    [Fact]
    public void Broadcaster_does_not_start_the_cooldown()
    {
        Add(c => c.GlobalCooldownSeconds = 60);
        Assert.Single(Triggered("!hello", "streamer", "99", roles: ChatRoles.Broadcaster));
        Assert.Single(Triggered("!hello"));
    }

    [Theory]
    [InlineData(ChatPermission.Everyone, ChatRoles.None, true)]
    [InlineData(ChatPermission.Subscriber, ChatRoles.None, false)]
    [InlineData(ChatPermission.Subscriber, ChatRoles.Subscriber, true)]
    [InlineData(ChatPermission.Subscriber, ChatRoles.Vip, true)]
    [InlineData(ChatPermission.Moderator, ChatRoles.Vip | ChatRoles.Subscriber, false)]
    [InlineData(ChatPermission.Moderator, ChatRoles.Moderator, true)]
    [InlineData(ChatPermission.Broadcaster, ChatRoles.Moderator, false)]
    [InlineData(ChatPermission.Broadcaster, ChatRoles.Broadcaster, true)]
    public void Minimum_role_is_enforced(ChatPermission minimum, ChatRoles roles, bool allowed)
    {
        Add(c => c.MinimumRole = minimum);
        Assert.Equal(allowed, Triggered("!hello", roles: roles).Count == 1);
    }

    [Fact]
    public void Allowed_users_and_groups_bypass_the_minimum_role()
    {
        Add(c =>
        {
            c.MinimumRole = ChatPermission.Broadcaster;
            c.AllowedUsers = ["Bob", "Rumble:carol", "1234"];
            c.AllowedGroups = ["Trusted"];
        });
        _groups.AddUser("trusted", "Twitch", "7", "dave", createGroup: true);

        Assert.Empty(Triggered("!hello"));                                   // alice: not listed
        Assert.Single(Triggered("!hello", "bob", "2"));                      // by name
        Assert.Single(Triggered("!hello", "someone", "1234"));               // by id
        Assert.Empty(Triggered("!hello", "carol", "3", platform: "Twitch")); // platform-qualified entry
        Assert.Single(Triggered("!hello", "carol", "3", platform: "Rumble"));
        Assert.Single(Triggered("!hello", "dave", "7"));                     // via group (matched by id)
        Assert.Single(Triggered("!hello", "renamed", "7"));
        Assert.Empty(Triggered("!hello", "dave", "7", platform: "Rumble"));  // groups are per platform
    }

    [Fact]
    public void Denied_users_and_groups_win_over_roles_but_not_over_the_broadcaster()
    {
        Add(c =>
        {
            c.DeniedUsers = ["@mallory"];
            c.DeniedGroups = ["Banned"];
            c.AllowedUsers = ["eve"];
        });
        _groups.AddUser("Banned", "Twitch", "", "eve", createGroup: true);
        _groups.AddUser("Banned", "Twitch", "99", "streamer");

        Assert.Single(Triggered("!hello"));
        Assert.Empty(Triggered("!hello", "Mallory", "5", roles: ChatRoles.Moderator));
        Assert.Empty(Triggered("!hello", "eve", "6"));                       // deny beats allow
        Assert.Single(Triggered("!hello", "streamer", "99", roles: ChatRoles.Broadcaster));
    }

    [Fact]
    public void Sources_filter_platforms_and_message_kinds()
    {
        Add(c => c.Sources = ["Twitch:Whisper", "Rumble"]);
        Assert.Empty(Triggered("!hello"));                                   // Twitch message
        Assert.Single(Triggered("!hello", platform: "Rumble"));
        Assert.Single(Send("!hello", extra: new() { ["messageKind"] = "whisper" }));
        Assert.Empty(Triggered("!hello", platform: "YouTube"));
    }

    [Fact]
    public void Empty_sources_listen_everywhere()
    {
        Add();
        foreach (var platform in new[] { "Twitch", "Rumble", "YouTube", "Kick" }) Assert.Single(Triggered("!hello", platform: platform));
    }

    [Fact]
    public void Bot_and_internal_messages_are_ignored_when_asked()
    {
        var command = Add();
        Assert.Empty(Send("!hello", extra: new() { ["isBot"] = true }));
        Assert.Empty(Send("!hello", extra: new() { ["isInternal"] = "true" }));
        command.IgnoreBotAccount = false;
        command.IgnoreInternal = false;
        _store.Save(command);
        Assert.Single(Send("!hello", extra: new() { ["isBot"] = true }));
        Assert.Single(Send("!hello", extra: new() { ["isInternal"] = true }));
    }

    [Fact]
    public void Case_sensitive_commands()
    {
        Add(c => c.CaseSensitive = true);
        Assert.Empty(Send("!HELLO"));
        Assert.Single(Send("!hello"));
    }

    [Fact]
    public void Regex_commands_add_match_and_group_arguments()
    {
        Add(c =>
        {
            c.Mode = CommandMode.Regex;
            c.Location = CommandLocation.Anywhere;
        }, @"(?<count>\d+)d(\d+)");
        var e = Assert.Single(Send("I roll 2d20 now"));
        Assert.Equal("2d20", e.Args["match"]);
        Assert.Equal("2", e.Args["count"]);
        Assert.Equal("20", e.Args["group1"]);
        Assert.Equal("now", e.Args["input"]);
        Assert.Equal("Regex", e.Args["commandMode"]);
    }

    [Fact]
    public void Handle_returns_the_published_events()
    {
        Add();
        var results = _service.Handle(Chat.Message("!hello"));
        Assert.Equal(CommandService.TriggeredEvent, Assert.Single(results).Type);
    }

    [Fact]
    public void Store_change_events_group_toggles_and_lookup()
    {
        var changes = 0;
        _store.Changed += () => changes++;
        var a = Add(c => { c.Name = "A"; c.Group = "Fun"; });
        var b = Add(c => { c.Name = "B"; c.Group = "fun"; }, "!b");
        Add(c => { c.Name = "C"; c.Group = "Mod"; }, "!c");
        Assert.Equal(3, changes);
        Assert.Equal(["Fun", "Mod"], _store.Groups);

        Assert.Equal(2, _store.SetGroupEnabled("FUN", false));
        Assert.False(_store.Get(a.Id)!.Enabled);
        Assert.False(_store.Get(b.Id)!.Enabled);
        Assert.Equal(4, changes);
        Assert.Equal(2, _store.SetGroupEnabled("fun", false)); // no change, no event
        Assert.Equal(4, changes);

        Assert.Equal(a.Id, _store.Find("a")!.Id);
        Assert.Equal(b.Id, _store.Find(b.Id.ToString())!.Id);
        Assert.Null(_store.Find("missing"));

        var copy = _store.Get(a.Id)!;
        copy.Name = "edited but not saved";
        Assert.Equal("A", _store.Get(a.Id)!.Name);

        Assert.Equal(2, _store.RenameGroup("fun", "Games"));
        Assert.Equal(["Games", "Mod"], _store.Groups);
        Assert.True(_store.Remove(a.Id));
        Assert.False(_store.Remove(a.Id));
        Assert.Equal(2, _store.All().Count);
    }

    [Fact]
    public void Renaming_a_user_group_updates_commands_that_allow_or_deny_it()
    {
        _groups.Add("Regulars");
        _groups.AddUser("Regulars", "twitch", "1", "alice");
        var allow = Add(c => { c.MinimumRole = ChatPermission.Moderator; c.AllowedGroups = ["regulars"]; }, "!vip");
        var deny = Add(c => c.DeniedGroups = ["Regulars", "Other"], "!pleb");
        var unrelated = Add(c => c.AllowedGroups = ["Other"], "!other");

        Assert.True(_groups.Rename("REGULARS", "Friends"));

        Assert.Equal(["Friends"], _store.Get(allow.Id)!.AllowedGroups);
        Assert.Equal(["Friends", "Other"], _store.Get(deny.Id)!.DeniedGroups);
        Assert.Equal(["Other"], _store.Get(unrelated.Id)!.AllowedGroups);
        Assert.Single(Triggered("!vip", roles: ChatRoles.None)); // alice is still let in through the renamed group
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        _groups.Dispose();
        _log.Dispose();
    }
}

public class CommandPersistenceTests
{
    [Fact]
    public void Commands_and_only_persisted_counters_survive_restart()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        var kept = new CommandDefinition
        {
            Name = "Kept", Commands = ["!kept"], PersistCounter = true, PersistUserCounter = true,
            Mode = CommandMode.Regex, Location = CommandLocation.Exact, MinimumRole = ChatPermission.Vip,
            AllowedGroups = ["Trusted"], Sources = ["Twitch"],
        };
        var lost = new CommandDefinition { Name = "Lost", Commands = ["!lost"] };
        var userKey = CommandStore.UserKey("Twitch", "1", "alice");

        using (var store = new CommandStore(paths, log))
        {
            store.Save(kept);
            store.Save(lost);
            store.Increment(kept.Id, userKey);
            store.Increment(kept.Id, userKey);
            store.Increment(lost.Id, userKey);
        }

        Assert.Contains("\"Regex\"", File.ReadAllText(paths.File("commands.json")));
        using var reloaded = new CommandStore(paths, log);
        var loaded = reloaded.Find("kept")!;
        Assert.Equal(CommandLocation.Exact, loaded.Location);
        Assert.Equal(ChatPermission.Vip, loaded.MinimumRole);
        Assert.Equal(["Trusted"], loaded.AllowedGroups);
        Assert.Equal(2, reloaded.GetCounter(kept.Id));
        Assert.Equal(2, reloaded.GetUserCounter(kept.Id, userKey));
        Assert.Equal(0, reloaded.GetCounter(lost.Id));
        Assert.Equal(0, reloaded.GetUserCounter(lost.Id, userKey));
    }

    [Fact]
    public void Resetting_counters()
    {
        using var log = new AppLog();
        using var store = new CommandStore(null, log);
        var command = new CommandDefinition { Commands = ["!x"] };
        store.Save(command);
        var changed = new List<Guid>();
        store.CountersChanged += changed.Add;
        store.Increment(command.Id, "twitch:1");
        store.Increment(command.Id, "twitch:2");

        store.ResetUserCounters(command.Id, "twitch:1");
        Assert.Equal(0, store.GetUserCounter(command.Id, "twitch:1"));
        Assert.Equal(1, store.GetUserCounter(command.Id, "twitch:2"));
        store.ResetUserCounters(command.Id);
        Assert.Equal(0, store.GetUserCounter(command.Id, "twitch:2"));
        Assert.Equal(2, store.GetCounter(command.Id));
        store.ResetCounter(command.Id);
        Assert.Equal(0, store.GetCounter(command.Id));
        Assert.Equal(5, changed.Count);
    }

    [Fact]
    public void Missing_optional_fields_load_with_defaults()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.File("commands.json"), """{ "commands": [ { "name": "Old", "commands": ["!old"] } ] }""");
        using var store = new CommandStore(paths, log);
        var command = store.Find("Old")!;
        Assert.True(command.Enabled);
        Assert.Equal(CommandMode.Basic, command.Mode);
        Assert.Equal(CommandLocation.Start, command.Location);
        Assert.Empty(command.Sources);
    }
}
