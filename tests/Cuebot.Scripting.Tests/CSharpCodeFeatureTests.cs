using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cuebot.Scripting.Tests;

/// <summary>Streamer.bot's C# sub-action options: Execute C# Method, Save Result to Variable, precompile and custom references.</summary>
public sealed class CSharpCodeFeatureTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TestBot _bot;
    private CSharpScriptEngine _engine = null!;

    public CSharpCodeFeatureTests()
    {
        _bot = new TestBot((b, api) =>
        {
            _engine = new CSharpScriptEngine(api, new CphServices { Profile = b.Profile, Bus = b.Bus, Globals = b.Globals, Log = b.Log });
            return [new CSharpCodeSubAction(_engine), new CSharpMethodSubAction(_engine)];
        });
    }

    public void Dispose()
    {
        _engine.Dispose();
        _bot.Dispose();
        _temp.Dispose();
    }

    private const string Shared =
        """
        using System;

        public class CPHInline
        {
            private int _calls;

            public bool Execute() => true;

            public bool Count()
            {
                _calls++;
                CPH.SetArgument("calls", _calls);
                return _calls < 2;
            }

            public void Init() => CPH.SetGlobalVar("inited", (CPH.GetGlobalVar<int>("inited", false) + 1), false);
        }
        """;

    private ActionDefinition Add(string name, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name };
        action.SubActions.AddRange(steps);
        action.SubActions.Add(new SubActionDefinition { Type = CaptureSubAction.Id });
        _bot.Profile.Profile.Actions.Add(action);
        _bot.Profile.NotifyChanged();
        return action;
    }

    private static SubActionDefinition Code(string code, params (string Key, string Value)[] extra)
    {
        var step = new SubActionDefinition { Type = CSharpCodeSubAction.Id, Parameters = { ["code"] = code, ["timeoutSeconds"] = "30" } };
        foreach (var (key, value) in extra) step.Parameters[key] = value;
        return step;
    }

    private static SubActionDefinition Method(string codeName, string method, params (string Key, string Value)[] extra)
    {
        var step = new SubActionDefinition { Type = CSharpMethodSubAction.Id, Parameters = { ["codeName"] = codeName, ["method"] = method } };
        foreach (var (key, value) in extra) step.Parameters[key] = value;
        return step;
    }

    [Fact]
    public async Task Execute_csharp_method_calls_into_named_code_and_false_stops_the_action()
    {
        var holder = Add("Holder", Code(Shared, ("name", "Counter")));
        holder.Enabled = false;   // the holding action need not run or be enabled
        var caller = Add("Caller", Method("counter", "Count"));

        var first = await _bot.RunAsync(caller);
        Assert.True(first.Success, first.Error);
        Assert.Equal(1, _bot.Capture.Args!["calls"]);

        // The second call returns false: the action stops before the capture step.
        await _bot.RunAsync(caller);
        Assert.Null(_bot.Capture.Args);
    }

    [Fact]
    public async Task Save_result_to_variable_keeps_the_action_going()
    {
        var action = Add("Save",
            Code("return false;", ("saveResult", "true"), ("resultVariable", "bodyResult")),
            Code(Shared, ("name", "Saver")),
            Method("Saver", "Count", ("saveResult", "true"), ("resultVariable", "first")),
            Method("Saver", "Count", ("saveResult", "true"), ("resultVariable", "%second%")));

        var run = await _bot.RunAsync(action);

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.Args!;
        Assert.Equal(false, args["bodyResult"]);
        Assert.Equal(true, args["first"]);
        Assert.Equal(false, args["second"]);
    }

    [Fact]
    public async Task Execute_csharp_method_fails_clearly_for_unknown_code_or_method()
    {
        Add("Holder", Code(Shared, ("name", "Known")));

        var noCode = await _bot.RunAsync(Add("A", Method("Missing", "Count")));
        var noMethod = await _bot.RunAsync(Add("B", Method("Known", "Nope")));

        Assert.False(noCode.Success);
        Assert.Contains("no C# code", noCode.Error, StringComparison.Ordinal);
        Assert.False(noMethod.Success);
        Assert.Contains("no public method Nope()", noMethod.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imported_execute_method_scripts_work_through_CPH()
    {
        // What the Streamer.bot importer emits for an "Execute C# Method" sub-action.
        Add("Holder", Code(Shared, ("name", "Imported code")));
        var caller = Add("Caller", Code("""
            using System;

            public class CPHInline
            {
                public bool Execute()
                {
                    // Imported from a Streamer.bot "Execute C# Method" sub-action.
                    return CPH.ExecuteMethod("Imported code", "Count");
                }
            }
            """));

        var run = await _bot.RunAsync(caller);

        Assert.True(run.Success, run.Error);
        Assert.Equal(1, _bot.Capture.Args!["calls"]);
    }

    [Fact]
    public async Task Precompile_runs_init_at_startup_only_for_enabled_actions()
    {
        Add("Now", Code(Shared.Replace("\"inited\"", "\"now\""), ("precompile", "true")));
        Add("Later", Code(Shared.Replace("\"inited\"", "\"later\""), ("precompile", "true"), ("delayedStart", "true")));
        Add("NotAsked", Code(Shared.Replace("\"inited\"", "\"never\"")));
        var disabled = Add("Disabled", Code(Shared.Replace("\"inited\"", "\"disabled\""), ("precompile", "true")));
        disabled.Enabled = false;
        _bot.Profile.NotifyChanged();
        var precompiler = new CSharpPrecompiler(_engine, _bot.Profile, _bot.Bus, _bot.Log);

        await precompiler.StartAsync();
        Assert.Equal(1L, _bot.Globals.GetValue("now"));
        Assert.Null(_bot.Globals.GetValue("later"));

        _bot.Bus.Publish(new BotEvent(CoreEvents.AppStarted));
        for (var i = 0; i < 100 && _bot.Globals.GetValue("later") is null; i++) await Task.Delay(50);
        Assert.Equal(1L, _bot.Globals.GetValue("later"));
        Assert.Null(_bot.Globals.GetValue("never"));
        Assert.Null(_bot.Globals.GetValue("disabled"));

        // Running the precompiled code does not run Init() again.
        var run = await _bot.RunAsync(_bot.Profile.Snapshot.FindAction("Now")!);
        Assert.True(run.Success, run.Error);
        Assert.Equal(1L, _bot.Globals.GetValue("now"));
    }

    [Fact]
    public async Task Custom_references_load_only_the_listed_dll()
    {
        var dll = BuildLibrary("CuebotRefTest" + Guid.NewGuid().ToString("N"), "namespace RefTest { public static class Greeter { public static string Hi(string n) => \"Hi \" + n; } }");
        var inline = """
            using System;
            using RefTest;

            public class CPHInline
            {
                public bool Execute()
                {
                    CPH.SetArgument("greeting", Greeter.Hi("Ann"));
                    return true;
                }
            }
            """;

        var withRef = await _bot.RunAsync(Add("WithRef", Code(inline, ("references", dll))));
        Assert.True(withRef.Success, withRef.Error);
        Assert.Equal("Hi Ann", _bot.Capture.Args!["greeting"]);

        var body = await _bot.RunAsync(Add("Body", Code("Args[\"b\"] = RefTest.Greeter.Hi(\"Bob\");", ("references", "# helper\n" + dll))));
        Assert.True(body.Success, body.Error);
        Assert.Equal("Hi Bob", _bot.Capture.Args!["b"]);

        var without = await _bot.RunAsync(Add("WithoutRef", Code(inline.Replace("Ann", "Cy"))));
        Assert.False(without.Success);
        Assert.Contains("RefTest", without.Error, StringComparison.Ordinal);

        var missing = await _bot.RunAsync(Add("Missing", Code(inline, ("references", Path.Combine(_temp.Path, "nope.dll")))));
        Assert.False(missing.Success);
        Assert.Contains("was not found", missing.Error, StringComparison.Ordinal);

        var relative = await _bot.RunAsync(Add("Relative", Code(inline, ("references", "nope.dll"))));
        Assert.False(relative.Success);
        Assert.Contains("full path", relative.Error, StringComparison.Ordinal);
    }

    private string BuildLibrary(string name, string source)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        // Not in the disposable temp folder: the loaded assembly keeps its file locked until the process exits.
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cuebot-scripting-refs")).FullName;
        var path = Path.Combine(folder, name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }
}
