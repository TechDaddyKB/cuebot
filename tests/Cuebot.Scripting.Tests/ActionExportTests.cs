using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Scripting.Export;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// "Export to code" round trips: every sample action runs as steps, then as exported C#, then as exported Python
/// (when a Python is available), and all three must do the same things in the same order and leave the same
/// arguments behind.
/// </summary>
public sealed class ActionExportTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private TestBot _bot = null!;
    private PythonRuntime _runtime = null!;
    private readonly RecordStep _record = new();

    public async Task InitializeAsync()
    {
        _bot = new TestBot((bot, api) =>
        {
            var settings = new JsonFile<ScriptingSettings>(Path.Combine(_data.Path, "scripting.json"), bot.Log);
            settings.Value.CustomPythonPath = TestPython.Executable ?? "";
            _runtime = new PythonRuntime(new AppPaths(_data.Path), settings, new HttpClient(), bot.Log);
            return
            [
                _record, new BranchStep(), new StopperStep(), new DelaySubAction(), new LogSubAction(), new CommentSubAction(),
                new GroupSubAction(), new IfSubAction(), new SwitchSubAction(), new ParallelSubAction(), new WhileSubAction(),
                new LoopBreakSubAction(), new LoopContinueSubAction(), new StopSubAction(), new SetArgumentSubAction(),
                new CSharpCodeSubAction(new CSharpScriptEngine(api)), new PythonCodeSubAction(new PythonScriptHost(_runtime, api, bot.Log)),
            ];
        });
        if (TestPython.Executable is not null) await _runtime.RefreshAsync();
    }

    public Task DisposeAsync()
    {
        _bot.Dispose();
        _data.Dispose();
        return Task.CompletedTask;
    }

    // ---- Samples ----

    public static TheoryData<string> Samples => [.. SampleActions.Keys];

    private static readonly Dictionary<string, Func<ActionDefinition>> SampleActions = new()
    {
        ["variables and text"] = () => Action(
            Set("greeting", "Hi \"%user%\"\n\tC:\\path ~color~ 🎉"),
            Record("%greeting%"),
            Step("core.comment", ("text", "A note\nover two lines")),
            Record("done")),
        ["if and else"] = () => Action(
            If("%user%", "equals", "ANN", [Record("is ann"), If("%count%", "greater than", "9", [Record("big")], [Record("small")])],
                [Record("not ann")]),
            If("%message%", "matches regex", "^!(?<cmd>\\w+)", [Record("command %cmd% %match.count%")], []),
            If("missing", "does not exist", "", [Record("no missing")], []),
            If("%user%", "contains", "zzz", [], [Record("no zzz")])),
        ["switch"] = () => Action(
            Switch("%color%", "equals", "red | crimson\ngreen\n%favourite%",
                ("red | crimson", [Record("warm %switchCase%")]), ("green", [Record("leafy")]),
                ("%favourite%", [Record("favourite")]), ("Default", [Record("other %switchCase%")])),
            Switch("%user%", "starts with", "b\nc", ("Default", [Record("default %switchCase%")])),
            Switch("%user%", "equals", "")),
        ["while with break and continue"] = () => Action(
            Set("loopIteration", "outer"),
            While("%loopIteration%", "less than", "6",
                If("%loopIteration%", "equals", "1", [Step("core.loopContinue")], []),
                Group("Body", Record("round %loopIteration%"),
                    If("%loopIteration%", "equals", "4", [Step("core.loopBreak")], []))),
            Record("after %loopIteration%"),
            While("1", "equals", "1", Record("limited %loopIteration%")).With("maxIterations", "3")),
        ["stop in a branch"] = () => Action(
            Record("before"),
            If("%user%", "equals", "Ann", [Record("stopping"), Step("core.stop")], []),
            Record("never")),
        ["loop break outside a loop stops"] = () => Action(Record("one"), Step("core.loopBreak"), Record("never")),
        ["run at the same time"] = () => Action(
            Record("before"),
            Parallel(
                [Delay(150), Record("slow"), Set("shared", "first"), Set("fromOne", "1")],
                [Record("fast"), Set("shared", "second"), Set("fromTwo", "2")],
                []),
            Record("after %shared% %fromOne% %fromTwo%")),
        ["stop in a lane"] = () => Action(
            Parallel([Delay(2000), Record("never")], [Delay(50), Step("core.stop")]),
            Record("never after")),
        ["break inside a lane in a loop"] = () => Action(
            While("%loopIteration%", "less than", "5",
                Record("round %loopIteration%"),
                Parallel([Record("lane")], [If("%loopIteration%", "equals", "2", [Step("core.loopBreak")], [])])),
            Record("after")),
        ["random group"] = () => Action(
            Group(random: true, "Pick", WithWeight(Record("never"), 0), WithWeight(Record("always"), 2), Disabled(Record("off")))),
        ["branching step"] = () => Action(
            Step(BranchStep.Id, ("pick", "Right"))
                .WithBranch("Left", Record("left"))
                .WithBranch("Right", Record("right %user%")),
            Step(BranchStep.Id, ("pick", "Left")).WithBranch("Right", Record("never"))),
        ["a step that stops"] = () => Action(Record("one"), Step(StopperStep.Id), Record("never")),
        ["disabled steps"] = () => Action(Disabled(Record("off")), Record("on"), Disabled(Group("Off", Record("never")))),
        ["code steps"] = () => Action(
            Step("code.csharp", ("code", "Args[\"fromCs\"] = \"cs \" + Args[\"user\"]; return true;")),
            Step("code.python", ("code", "cb.set_arg(\"fromPy\", \"py \" + cb.get_arg(\"user\"))")),
            Record("%fromCs% %fromPy%")),
    };

    private static ActionDefinition Concurrent()
    {
        var action = Action(
            Group("A", Delay(100), Record("a"), Set("x", "from a")),
            Group("B", Record("b"), Set("x", "from b")));
        action.ConcurrentSubActions = true;
        return action;
    }

    private static ActionDefinition RandomAction()
    {
        var action = Action(WithWeight(Record("never"), 0), WithWeight(Record("picked"), 1));
        action.RandomAction = true;
        return action;
    }

    private static readonly Dictionary<string, object?> RunArgs = new()
    {
        ["user"] = "Ann", ["count"] = 12, ["message"] = "!lurk now", ["color"] = "crimson", ["favourite"] = "teal",
    };

    // ---- Tests ----

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Exported_CSharp_does_what_the_steps_do(string sample) =>
        await AssertSame(SampleActions[sample](), CodeLanguage.CSharp);

    [PythonTheory]
    [MemberData(nameof(Samples))]
    public async Task Exported_Python_does_what_the_steps_do(string sample) =>
        await AssertSame(SampleActions[sample](), CodeLanguage.Python);

    [Fact]
    public async Task Concurrent_and_random_actions_export_to_CSharp()
    {
        await AssertSame(Concurrent(), CodeLanguage.CSharp);
        await AssertSame(RandomAction(), CodeLanguage.CSharp);
    }

    [PythonFact]
    public async Task Concurrent_and_random_actions_export_to_Python()
    {
        await AssertSame(Concurrent(), CodeLanguage.Python);
        await AssertSame(RandomAction(), CodeLanguage.Python);
    }

    [Fact]
    public void Python_lanes_and_CSharp_loop_breaks_in_lanes_stay_as_steps_with_a_note()
    {
        var python = Export(SampleActions["run at the same time"](), CodeLanguage.Python);
        Assert.Contains("cb.run_steps(", python.Code);
        Assert.Contains(python.Warnings, w => w.Contains("Python runs one thing at a time", StringComparison.Ordinal));
        Assert.StartsWith("# Notes from the export:", python.Code, StringComparison.Ordinal);

        var csharp = Export(SampleActions["run at the same time"](), CodeLanguage.CSharp);
        Assert.Contains("Cb.Together(", csharp.Code);
        Assert.Empty(csharp.Warnings);

        var breaking = Export(SampleActions["break inside a lane in a loop"](), CodeLanguage.CSharp);
        Assert.Contains("Cb.RunSteps(", breaking.Code);
        Assert.Contains(breaking.Warnings, w => w.Contains("cannot leave a C# lane", StringComparison.Ordinal));
    }

    [Fact]
    public void Exports_warn_about_steps_elsewhere_that_change_single_steps_and_about_left_out_ones()
    {
        var action = SampleActions["disabled steps"]();
        action.Name = "Target";
        var other = Action(Step("core.setSubActionState", ("action", "target"), ("subAction", Guid.NewGuid().ToString())));
        other.Name = "Changer";
        var unrelated = Action(Step("core.setSubActionWeight", ("action", "Someone else")));

        var result = ActionCodeExporter.Export(action, _bot.Registry, CodeLanguage.Python, [action, other, unrelated]);

        Assert.Contains(result.Warnings, w => w.Contains("\"Changer\" changes single steps", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Someone", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("off in the editor", StringComparison.Ordinal));
        Assert.Contains("# Off in the editor, so not exported: Record", result.Code);
    }

    [Fact]
    public void Unknown_steps_and_other_language_code_steps_get_a_note()
    {
        var action = Action(Step("nobody.hasThis", ("x", "1")), Step("code.python", ("code", "pass")));
        var result = Export(action, CodeLanguage.CSharp);
        Assert.Contains(result.Warnings, w => w.Contains("\"nobody.hasThis\" is not installed", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Python code steps stay as steps", StringComparison.Ordinal));
        Assert.Contains("await Cb.Step(\"nobody.hasThis\", new() { [\"x\"] = \"1\" });", result.Code);

        var python = Export(Action(Step("code.csharp", ("code", "return true;"))), CodeLanguage.Python);
        Assert.Contains(python.Warnings, w => w.Contains("C# code steps stay as steps", StringComparison.Ordinal));
    }

    [Fact]
    public void Literals_escape_quotes_backslashes_and_control_characters()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\u0001\\u2028é🎉\"", ActionCodeExporter.Literal("a\"b\\c\n\r\t\u0001\u2028é🎉"));
        Assert.Equal("\"\"", ActionCodeExporter.Literal(null));
    }

    [Fact]
    public async Task Empty_action_exports_to_code_that_does_nothing()
    {
        await AssertSame(Action(), CodeLanguage.CSharp);
        Assert.Empty(Export(Action(), CodeLanguage.Python).Warnings);
    }

    // ---- Helpers ----

    private CodeExport Export(ActionDefinition action, CodeLanguage language) =>
        ActionCodeExporter.Export(action, _bot.Registry, language);

    private async Task AssertSame(ActionDefinition original, CodeLanguage language)
    {
        var export = Export(original, language);
        var expected = await Run(original);
        var exported = Action(Step(language == CodeLanguage.CSharp ? "code.csharp" : "code.python",
            ("code", export.Code), ("timeoutSeconds", "0")));
        var actual = await Run(exported);

        Assert.True(expected.Success == actual.Success, $"{expected.Error} / {actual.Error}\n{export.Code}");
        Assert.True(expected.Calls.SequenceEqual(actual.Calls),
            $"steps: {string.Join(" | ", expected.Calls)}\ncode:  {string.Join(" | ", actual.Calls)}\n{actual.Error}\n{export.Code}");
        foreach (var (key, value) in expected.Args)
        {
            Assert.True(actual.Args.TryGetValue(key, out var other), $"{key} missing after the code ran\n{export.Code}");
            Assert.True(Text(value) == Text(other), $"{key}: steps {Text(value)}, code {Text(other)}\n{export.Code}");
        }
    }

    private async Task<(bool Success, string? Error, IReadOnlyList<string> Calls, Dictionary<string, object?> Args)> Run(ActionDefinition action)
    {
        action.Name = "Sample " + Guid.NewGuid();
        _bot.Profile.Profile.Actions.Add(action);
        _record.Clear();
        var run = await _bot.RunAsync(action, new Dictionary<string, object?>(RunArgs));
        var args = new Dictionary<string, object?>(_bot.Engine.History.Entries.Last().ArgsAfterRun!, StringComparer.OrdinalIgnoreCase);
        // Run bookkeeping differs between any two runs.
        foreach (var key in (string[])["actionName", "actionId", "runId", "runningActionId", "actionQueuedAt", "delayMs", "__source"]) args.Remove(key);
        return (run.Success, run.Error, _record.Snapshot(), args);
    }

    private static string Text(object? value) => Convert.ToString(value is System.Text.Json.JsonElement e ? e.ToString() : value,
        CultureInfo.InvariantCulture) ?? "";

    private static ActionDefinition Action(params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition();
        action.SubActions.AddRange(steps);
        return action;
    }

    private static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        return step;
    }

    private static SubActionDefinition Record(string text) => Step(RecordStep.Id, ("text", text));

    private static SubActionDefinition Set(string name, string value) => Step("core.setArgument", ("name", name), ("value", value));

    private static SubActionDefinition Delay(int ms) => Step("core.delay", ("milliseconds", ms.ToString(CultureInfo.InvariantCulture)));

    private static SubActionDefinition Disabled(SubActionDefinition step)
    {
        step.Enabled = false;
        return step;
    }

    private static SubActionDefinition WithWeight(SubActionDefinition step, double weight)
    {
        step.Weight = weight;
        return step;
    }

    private static SubActionDefinition If(string left, string op, string right, SubActionDefinition[] whenTrue, SubActionDefinition[] whenFalse)
    {
        var step = Step("core.if", ("left", left), ("operator", op), ("right", right));
        step.Branch(IfSubAction.True).AddRange(whenTrue);
        step.Branch(IfSubAction.False).AddRange(whenFalse);
        return step;
    }

    private static SubActionDefinition Switch(string input, string op, string cases, params (string Case, SubActionDefinition[] Steps)[] branches)
    {
        var step = Step(SwitchSubAction.Id, ("input", input), ("operator", op), ("cases", cases));
        foreach (var (name, steps) in branches) step.Branch(name).AddRange(steps);
        return step;
    }

    private static SubActionDefinition While(string left, string op, string right, params SubActionDefinition[] body)
    {
        var step = Step(WhileSubAction.Id, ("left", left), ("operator", op), ("right", right));
        step.Branch(WhileSubAction.Body).AddRange(body);
        return step;
    }

    private static SubActionDefinition Group(string name, params SubActionDefinition[] children) => Group(false, name, children);

    private static SubActionDefinition Group(bool random, string name, params SubActionDefinition[] children)
    {
        var step = Step(GroupSubAction.Id, ("name", name), ("random", random ? "true" : "false"));
        step.Branch(GroupSubAction.Steps).AddRange(children);
        return step;
    }

    private static SubActionDefinition Parallel(params SubActionDefinition[][] lanes)
    {
        var step = Step(ParallelSubAction.Id, ("lanes", lanes.Length.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < lanes.Length; i++) step.Branch(ParallelSubAction.Lane(i + 1)).AddRange(lanes[i]);
        return step;
    }

    /// <summary>Records its resolved text, in the order runs reach it.</summary>
    private sealed class RecordStep : ISubActionHandler
    {
        public const string Id = "test.record";
        private readonly List<string> _calls = [];
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Record";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Text")];

        public void Clear()
        {
            lock (_calls) _calls.Clear();
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_calls) return [.. _calls];
        }

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            lock (_calls) _calls.Add(p["text"]);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    /// <summary>A plain step with branches that picks the one named in its "pick" setting.</summary>
    private sealed class BranchStep : ISubActionHandler
    {
        public const string Id = "test.branch";
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Branch";
        public IReadOnlyList<string> Branches { get; } = ["Left", "Right"];
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("pick", "Pick")];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
            Task.FromResult(SubActionResult.TakeBranch(p["pick"]));
    }

    /// <summary>A plain step that stops the action (like a cooldown check that fails).</summary>
    private sealed class StopperStep : ISubActionHandler
    {
        public const string Id = "test.stopper";
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Stopper";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
            Task.FromResult(SubActionResult.Stop);
    }
}

internal static class StepExtensions
{
    public static SubActionDefinition With(this SubActionDefinition step, string key, string value)
    {
        step.Parameters[key] = value;
        return step;
    }

    public static SubActionDefinition WithBranch(this SubActionDefinition step, string branch, params SubActionDefinition[] steps)
    {
        step.Branch(branch).AddRange(steps);
        return step;
    }
}
