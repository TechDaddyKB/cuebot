using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Variables;

namespace Cuebot.Tests.Variables;

public sealed class VariableSubActionTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly GlobalVariables _globals;
    private readonly UserVariables _users;
    private readonly CaptureSubAction _capture = new();
    private readonly ActionEngine _engine;
    private readonly EventBus _bus;
    private readonly ScriptApi _scripts;

    public VariableSubActionTests()
    {
        _profile = new ProfileService(null, _log);
        _globals = new GlobalVariables(null, _log, TestEnv.Create(_log));
        _users = new UserVariables(null, _log);
        _bus = new EventBus(_log);
        var registry = new Registry(
        [
            _capture, new SetArgumentSubAction(), new GetGlobalSubAction(), new SetGlobalSubAction(), new DeleteGlobalSubAction(),
            new ClearTemporaryGlobalsSubAction(), new RandomNumberSubAction(), new GetUserVariableSubAction(_users),
            new SetUserVariableSubAction(_users), new DeleteUserVariableSubAction(_users), new ClearTemporaryUserVariablesSubAction(_users),
        ], []);
        _engine = new ActionEngine(_bus, _profile, registry, _globals, _log);
        _scripts = new ScriptApi([new CoreScriptFunctions(), new VariableScriptFunctions(_users)]);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _users.Dispose();
        _globals.Dispose();
        _log.Dispose();
    }

    private static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        return step;
    }

    private async Task<Dictionary<string, object?>> RunAsync(IReadOnlyDictionary<string, object?>? args, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = "Test", SubActions = [.. steps, Step(CaptureSubAction.Id)] };
        var ok = await _engine.RunAsync(action, args);
        Assert.True(ok, string.Join(" | ", _log.Recent().Select(e => e.Message)));
        return _capture.Args!;
    }

    private static Dictionary<string, object?> ChatArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["platform"] = "twitch",
        ["userId"] = "42",
        ["userName"] = "Alice",
        ["eventType"] = "twitch.chatMessage",
    };

    [Fact]
    public async Task Set_global_value_stays_compatible_and_auto_type_keeps_numbers()
    {
        await RunAsync(null,
            Step("core.setGlobal", ("name", "plain"), ("value", "5")),
            Step("core.setGlobal", ("name", "typed"), ("value", "$math(2*3)$"), ("autoType", "true"), ("persisted", "false")));

        Assert.Equal("5", _globals.GetValue("plain"));
        Assert.True(_globals.IsPersisted("plain"));
        Assert.Equal(6L, _globals.GetValue("typed"));
        Assert.False(_globals.IsPersisted("typed"));
    }

    [Fact]
    public async Task Increment_and_decrement_globals()
    {
        var args = await RunAsync(null,
            Step("core.setGlobal", ("name", "count"), ("mode", "Increment")),
            Step("core.setGlobal", ("name", "count"), ("mode", "Increment"), ("value", "10")),
            Step("core.setGlobal", ("name", "count"), ("mode", "Decrement"), ("value", "4"), ("storeIn", "newCount")),
            Step("core.setGlobal", ("name", "money"), ("mode", "Increment"), ("value", "2.50")));

        Assert.Equal(7L, _globals.GetValue("count"));
        Assert.Equal(7L, args["newCount"]);
        Assert.Equal("2.50", _globals.Get("money"));
    }

    [Fact]
    public async Task Incrementing_text_fails_the_action()
    {
        _globals.Set("word", "abc", persist: false);
        var action = new ActionDefinition { Name = "Bad", SubActions = [Step("core.setGlobal", ("name", "word"), ("mode", "Increment"))] };
        Assert.False(await _engine.RunAsync(action));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("is not a number"));
    }

    [Fact]
    public async Task Get_global_into_argument_with_default_and_types()
    {
        _globals.Set("score", "41", persist: false);
        var args = await RunAsync(null,
            Step("core.getGlobal", ("name", "score")),
            Step("core.getGlobal", ("name", "score"), ("destination", "raw"), ("autoType", "false")),
            Step("core.getGlobal", ("name", "missing"), ("destination", "fallback"), ("defaultValue", "0")),
            Step("core.getGlobal", ("name", "missing"), ("destination", "unset")),
            Step("core.setArgument", ("name", "next"), ("value", "$math(%score% + 1)$"), ("autoType", "true")));

        Assert.Equal(41L, args["score"]);
        Assert.Equal("41", args["raw"]);
        Assert.Equal(0L, args["fallback"]);
        Assert.False(args.ContainsKey("unset"));
        Assert.Equal(42L, args["next"]);
    }

    [Fact]
    public async Task Delete_and_clear_temporary_globals()
    {
        _globals.Set("a", "1", persist: false);
        _globals.Set("b", "1", persist: false);
        _globals.Set("kept", "1", persist: true);
        await RunAsync(null, Step("core.deleteGlobal", ("name", "a")), Step("core.clearTemporaryGlobals"));
        Assert.Equal(["kept"], _globals.All().Keys);
    }

    [Fact]
    public async Task User_variables_default_to_the_triggering_user()
    {
        var args = await RunAsync(ChatArgs(),
            Step("core.setUserVariable", ("name", "points"), ("mode", "Increment"), ("value", "5")),
            Step("core.setUserVariable", ("name", "points"), ("mode", "Increment"), ("storeIn", "total")),
            Step("core.setUserVariable", ("name", "title"), ("value", "007"), ("persisted", "false")),
            Step("core.getUserVariable", ("name", "points")),
            Step("core.getUserVariable", ("name", "lives"), ("defaultValue", "3")));

        Assert.Equal(6L, _users.Get("twitch", "42", "points"));
        Assert.Equal("007", _users.Get("twitch", "42", "title"));
        Assert.Equal(6L, args["total"]);
        Assert.Equal(6L, args["points"]);
        Assert.Equal(3L, args["lives"]);
        Assert.Equal("Alice", Assert.Single(_users.Snapshot(persisted: true)).UserName);
    }

    [Fact]
    public async Task User_variables_for_another_user_by_name()
    {
        _users.Set("twitch", "7", "Bob", "points", 10L, persist: true);
        var args = await RunAsync(ChatArgs(),
            Step("core.getUserVariable", ("name", "points"), ("userName", "bob"), ("destination", "bobPoints")),
            Step("core.setUserVariable", ("name", "points"), ("userName", "BOB"), ("mode", "Decrement"), ("value", "3")),
            Step("core.setUserVariable", ("name", "points"), ("platform", "kick"), ("userId", "k1"), ("value", "1")));

        Assert.Equal(10L, args["bobPoints"]);
        Assert.Equal(7L, _users.Get("twitch", "7", "points"));
        Assert.Equal(1L, _users.Get("kick", "k1", "points"));
        Assert.Null(_users.Get("twitch", "42", "points"));
    }

    [Fact]
    public async Task Setting_a_user_variable_without_a_user_fails()
    {
        var action = new ActionDefinition { Name = "NoUser", SubActions = [Step("core.setUserVariable", ("name", "x"), ("value", "1"))] };
        Assert.False(await _engine.RunAsync(action));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("No user"));

        var args = await RunAsync(null, Step("core.getUserVariable", ("name", "x"), ("defaultValue", "none")));
        Assert.Equal("none", args["x"]);
    }

    [Fact]
    public async Task Delete_user_variables_for_one_or_all_users_and_clear_temporary()
    {
        _users.Set("twitch", "42", "Alice", "points", 1L, persist: true);
        _users.Set("twitch", "7", "Bob", "points", 1L, persist: true);
        _users.Set("twitch", "7", "Bob", "temp", 1L, persist: false);
        _users.Set("kick", "1", "Kim", "points", 1L, persist: true);

        await RunAsync(ChatArgs(), Step("core.deleteUserVariable", ("name", "points")));
        Assert.Null(_users.Get("twitch", "42", "points"));
        Assert.Equal(1L, _users.Get("twitch", "7", "points"));

        await RunAsync(ChatArgs(),
            Step("core.deleteUserVariable", ("name", "points"), ("allUsers", "true"), ("platform", "twitch")),
            Step("core.clearTemporaryUserVariables"));
        Assert.Equal(["kick"], _users.Snapshot().Select(r => r.Platform));
    }

    [Fact]
    public async Task Invalid_modes_and_names_fail_clearly()
    {
        var badMode = new ActionDefinition { Name = "Mode", SubActions = [Step("core.setGlobal", ("name", "x"), ("mode", "Multiply"))] };
        Assert.False(await _engine.RunAsync(badMode));
        var badName = new ActionDefinition { Name = "Name", SubActions = [Step("core.setGlobal", ("name", "two words"), ("value", "1"))] };
        Assert.False(await _engine.RunAsync(badName));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("Unknown mode 'Multiply'"));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("contains spaces"));
    }

    [Fact]
    public async Task Random_number_uses_the_environment_random()
    {
        var args = await RunAsync(null, Step("core.randomNumber", ("min", "5"), ("max", "5")));
        Assert.Equal(5, args["random"]);
    }

    [Fact]
    public async Task Parameters_resolve_built_ins_formats_and_functions()
    {
        var args = await RunAsync(new Dictionary<string, object?> { ["amount"] = 3.5 },
            Step("core.setArgument", ("name", "text"), ("value", "%date:yyyy% %amount:c2% $upper(ok)$")));
        Assert.Equal("2026 $3.50 OK", args["text"]);
    }

    [Fact]
    public async Task Script_functions_reach_user_variables_and_globals()
    {
        ActionRunContext? context = null;
        _capture.OnExecute = c => context = c;
        await _engine.RunAsync(new ActionDefinition { Name = "Script", SubActions = [Step(CaptureSubAction.Id)] }, ChatArgs());
        Assert.NotNull(context);

        Task<object?> Call(string name, object args) => _scripts.CallAsync(context!, name, args, CancellationToken.None);

        await Call("userVars.set", new { name = "points", value = 5 });
        Assert.Equal(5L, await Call("userVars.get", new { name = "points" }));
        Assert.Equal(7L, await Call("userVars.increment", new { name = "points", amount = 2 }));
        Assert.Equal("none", await Call("userVars.get", new { name = "missing", @default = "none" }));
        Assert.Null(await Call("userVars.get", new { name = "points", platform = "kick", userId = "k" }));
        var all = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(await Call("userVars.all", new { }));
        Assert.Equal(7L, all["points"]);
        Assert.Equal(true, await Call("userVars.delete", new { name = "points" }));
        Assert.Null(_users.Get("twitch", "42", "points"));

        Assert.Equal(3L, await Call("globals.increment", new { name = "g", amount = 3, persisted = false }));
        Assert.Equal(3L, await Call("globals.getValue", new { name = "g" }));
        Assert.Equal(1, await Call("globals.clearTemporary", new { }));
        Assert.Equal(false, await Call("globals.delete", new { name = "g" }));
        Assert.Equal("Hi Alice 4", await Call("text.resolve", new { text = "Hi %userName% $math(2+2)$" }));
        await Assert.ThrowsAsync<ScriptException>(() => Call("userVars.set", new { name = "bad name", value = 1 }));
    }

    [Fact]
    public void Variable_changes_are_published_as_trigger_events()
    {
        using var triggers = new VariableTriggers(_globals, _users, _bus);
        var events = new List<BotEvent>();
        using var subscription = _bus.Subscribe(events.Add);

        _globals.Set("a", "1", persist: false);
        _globals.Set("a", "2", persist: false);
        _globals.Remove("a");
        _users.Set("twitch", "1", "U", "p", 1L, persist: false);

        Assert.Equal(
            [VariableEvents.GlobalCreated, VariableEvents.GlobalUpdated, VariableEvents.GlobalDeleted, VariableEvents.UserUpdated],
            events.Select(e => e.Type));
        Assert.Equal("1", events[1].Args["oldValue"]);
        Assert.Equal("U", events[3].Args["userName"]);

        var trigger = triggers.GetTriggers().Single(t => t.TypeId == "variables.globalUpdated");
        var settings = new ParameterValues(new Dictionary<string, string> { ["variable"] = "A" });
        Assert.True(trigger.Matches(events[1], new TriggerDefinition(), settings, new Dictionary<string, object?>()));
        var other = new ParameterValues(new Dictionary<string, string> { ["variable"] = "b" });
        Assert.False(trigger.Matches(events[1], new TriggerDefinition(), other, new Dictionary<string, object?>()));
    }

    private sealed class CaptureSubAction : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?>? Args { get; private set; }
        public Action<ActionRunContext>? OnExecute { get; set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            OnExecute?.Invoke(context);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
