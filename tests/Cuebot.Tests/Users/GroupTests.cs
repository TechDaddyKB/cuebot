using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Users;

public class GroupStoreTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly GroupStore _groups;
    private readonly EventRecorder _events;

    public GroupStoreTests()
    {
        _bus = new EventBus(_log);
        _groups = new GroupStore(null, _log, _bus);
        _events = new EventRecorder(_bus);
    }

    [Fact]
    public void Crud_publishes_events()
    {
        var changes = 0;
        _groups.Changed += () => changes++;

        Assert.True(_groups.Add("VIPs"));
        Assert.False(_groups.Add("vips"));
        Assert.False(_groups.Add("  "));
        Assert.True(_groups.Exists("VIPS"));

        Assert.True(_groups.AddUser("vips", "Twitch", "1", "alice"));
        Assert.False(_groups.AddUser("vips", "Twitch", "1", "Alice2")); // already there (by id)
        Assert.True(_groups.AddUser("vips", "Rumble", "", "bob"));
        Assert.False(_groups.AddUser("missing", "Twitch", "1", "alice"));
        Assert.Equal(2, _groups.UsersIn("VIPs").Count);
        Assert.Equal("Alice2", _groups.UsersIn("VIPs").First().UserName); // name refreshed

        Assert.True(_groups.RemoveUser("VIPs", "Rumble", "", "BOB"));
        Assert.False(_groups.RemoveUser("VIPs", "Rumble", "", "bob"));
        Assert.Equal(1, _groups.Clear("vips"));
        Assert.Equal(-1, _groups.Clear("missing"));
        Assert.True(_groups.Remove("VIPs"));
        Assert.False(_groups.Remove("VIPs"));

        Assert.Equal(
            [GroupEvents.Added, GroupEvents.UsersAdded, GroupEvents.UsersAdded, GroupEvents.UsersRemoved, GroupEvents.Cleared, GroupEvents.Removed],
            _events.Events.Select(e => e.Type));
        Assert.All(_events.Events, e => Assert.Equal("VIPs", e.Args["groupName"]));
        var added = _events.Events[1];
        Assert.Equal("Twitch", added.Args["platform"]);
        Assert.Equal("alice", added.Args["user"]);
        Assert.Equal("1", added.Args["userId"]);
        Assert.Equal(1, _events.Events[4].Args["count"]);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Add_user_can_create_the_group()
    {
        Assert.True(_groups.AddUser("New", "Twitch", "1", "alice", createGroup: true));
        Assert.Equal([GroupEvents.Added, GroupEvents.UsersAdded], _events.Events.Select(e => e.Type));
    }

    [Fact]
    public void Membership_is_per_platform_by_id_then_name()
    {
        _groups.AddUser("G", "Twitch", "1", "alice", createGroup: true);
        _groups.AddUser("G", "Twitch", "", "bob");
        _groups.AddUser("H", "Twitch", "1", "alice", createGroup: true);

        Assert.True(_groups.Contains("g", "twitch", "1", "whatever"));
        Assert.False(_groups.Contains("G", "Twitch", "2", "alice"));  // ids differ
        Assert.True(_groups.Contains("G", "Twitch", "", "ALICE"));    // no id given: by name
        Assert.True(_groups.Contains("G", "Twitch", "5", "bob"));     // member has no id: by name
        Assert.False(_groups.Contains("G", "Rumble", "1", "alice"));
        Assert.False(_groups.Contains("missing", "Twitch", "1", "alice"));
        Assert.Equal(["G", "H"], _groups.GroupsOf("Twitch", "1", ""));
    }

    [Fact]
    public void Rename_keeps_members_and_rejects_clashes()
    {
        _groups.AddUser("A", "Twitch", "1", "alice", createGroup: true);
        _groups.Add("B");
        Assert.False(_groups.Rename("A", "b"));
        Assert.True(_groups.Rename("A", "Alpha"));
        Assert.True(_groups.Contains("alpha", "Twitch", "1", ""));
        Assert.Equal(["Alpha", "B"], _groups.Names);
    }

    [Fact]
    public void Groups_survive_restart()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        using (var groups = new GroupStore(paths, _log))
        {
            groups.AddUser("Mods", "Twitch", "1", "alice", createGroup: true);
        }
        using var reloaded = new GroupStore(paths, _log);
        Assert.True(reloaded.Contains("mods", "Twitch", "1", ""));
        Assert.Contains("\"groups\"", File.ReadAllText(paths.File("groups.json")));
    }

    [Fact]
    public void Triggers_filter_by_group_name()
    {
        var triggers = new GroupTriggers(_groups).GetTriggers().ToArray();
        Assert.Equal(5, triggers.Length);
        var usersAdded = triggers.Single(t => t.EventTypes.Contains(GroupEvents.UsersAdded));
        var e = new BotEvent(GroupEvents.UsersAdded, new Dictionary<string, object?> { ["groupName"] = "Mods" });
        bool Matches(string group) => usersAdded.Matches(e, new TriggerDefinition(),
            ParameterValues.From(usersAdded.Parameters, new Dictionary<string, string> { ["group"] = group }), new Dictionary<string, object?>());
        Assert.True(Matches(""));
        Assert.True(Matches("mods"));
        Assert.False(Matches("VIPs"));
    }

    public void Dispose()
    {
        _groups.Dispose();
        _log.Dispose();
    }
}

public class GroupActionTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly GroupStore _groups;
    private readonly Dictionary<string, object?> _chatArgs = new() { ["platform"] = "Twitch", ["user"] = "alice", ["userId"] = "1" };

    public GroupActionTests() => _groups = new GroupStore(null, _log);

    [Fact]
    public async Task Add_check_remove_and_clear_with_trigger_arguments()
    {
        var add = await SubActionHarness.RunAsync(new AddUserToGroupSubAction(_groups), new() { ["group"] = "Regulars" }, _chatArgs);
        Assert.True(add.Success);
        Assert.Equal(true, add.Args["groupUserAdded"]);
        Assert.True(_groups.Contains("Regulars", "Twitch", "1", "alice"));

        var check = await SubActionHarness.RunAsync(new UserInGroupSubAction(_groups), new() { ["group"] = "regulars" }, _chatArgs);
        Assert.Equal(UserInGroupSubAction.True, check.Branch);
        Assert.Equal(true, check.Args["inGroup"]);

        var other = await SubActionHarness.RunAsync(new UserInGroupSubAction(_groups),
            new() { ["group"] = "regulars", ["user"] = "bob", ["userId"] = "2", ["argument"] = "isRegular" }, _chatArgs);
        Assert.Equal(UserInGroupSubAction.False, other.Branch);
        Assert.Equal(false, other.Args["isRegular"]);

        var remove = await SubActionHarness.RunAsync(new RemoveUserFromGroupSubAction(_groups), new() { ["group"] = "Regulars" }, _chatArgs);
        Assert.Equal(true, remove.Args["groupUserRemoved"]);

        _groups.AddUser("Regulars", "Twitch", "3", "carol");
        var clear = await SubActionHarness.RunAsync(new ClearGroupSubAction(_groups), new() { ["group"] = "Regulars" });
        Assert.Equal(1, clear.Args["groupUsersRemoved"]);
        Assert.Empty(_groups.UsersIn("Regulars"));
    }

    [Fact]
    public async Task Unresolved_user_id_falls_back_to_the_name()
    {
        // No %userId% argument (e.g. a timer-triggered action): the literal "%userId%" must not become the id.
        await SubActionHarness.RunAsync(new AddUserToGroupSubAction(_groups),
            new() { ["group"] = "G", ["platform"] = "Rumble", ["user"] = "@dave" });
        var member = Assert.Single(_groups.UsersIn("G"));
        Assert.Equal("", member.UserId);
        Assert.Equal("dave", member.UserName);
    }

    [Fact]
    public async Task Add_without_create_fails_for_missing_group_and_clear_of_missing_group_fails()
    {
        Assert.False((await SubActionHarness.RunAsync(new AddUserToGroupSubAction(_groups),
            new() { ["group"] = "Nope", ["create"] = "false" }, _chatArgs)).Success);
        Assert.False((await SubActionHarness.RunAsync(new ClearGroupSubAction(_groups), new() { ["group"] = "Nope" })).Success);
        Assert.False(_groups.Exists("Nope"));
    }

    [Fact]
    public async Task Script_functions()
    {
        using var log = new AppLog();
        var api = new ScriptApi([new GroupScriptFunctions(_groups)]);
        var globals = new GlobalVariables(null, log);
        using var engine = new ActionEngine(new EventBus(log), new ProfileService(null, log), new Registry([], []), globals, log);
        var context = new ActionRunContext(new ActionDefinition(), new Dictionary<string, object?>(_chatArgs, StringComparer.OrdinalIgnoreCase),
            engine, globals, log, 0);

        Task<object?> Call(string name, object? args = null) => api.CallAsync(context, name, args, CancellationToken.None);

        Assert.Equal(true, await Call("groups.add", new { group = "Fans" }));               // defaults from %platform%/%user%/%userId%
        Assert.Equal(true, await Call("groups.add", new { group = "Fans", platform = "Rumble", user = "bob" }));
        Assert.Equal(true, await Call("groups.contains", new { group = "fans" }));
        Assert.Equal(false, await Call("groups.contains", new { group = "fans", userId = "2", user = "x" }));
        Assert.Equal(true, await Call("groups.exists", new { group = "Fans" }));
        Assert.Equal(new[] { "Fans" }, await Call("groups.list"));
        var users = JsonSerializer.Serialize(await Call("groups.users", new { group = "Fans" }));
        Assert.Contains("\"userName\":\"bob\"", users);
        Assert.Equal(true, await Call("groups.remove", new { group = "Fans" }));
        Assert.Equal(1, await Call("groups.clear", new { group = "Fans" }));
        Assert.Equal(true, await Call("groups.create", new { group = "Empty" }));
        Assert.Equal(true, await Call("groups.delete", new { group = "Empty" }));
        await Assert.ThrowsAsync<ScriptException>(() => Call("groups.add", new { }));
    }

    public void Dispose()
    {
        _groups.Dispose();
        _log.Dispose();
    }
}
