using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Scripting.Tests;

/// <summary>Runs scripts through the real worker process, using a local interpreter via CustomPythonPath.</summary>
public sealed class PythonScriptTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private PythonRuntime _runtime = null!;
    private PythonScriptHost _host = null!;
    private TestBot _bot = null!;

    public async Task InitializeAsync()
    {
        _bot = new TestBot((api, log) =>
        {
            var settings = new JsonFile<ScriptingSettings>(Path.Combine(_data.Path, "scripting.json"), log);
            settings.Value.CustomPythonPath = TestPython.Executable ?? "";
            _runtime = new PythonRuntime(new AppPaths(_data.Path), settings, new HttpClient(), log);
            _host = new PythonScriptHost(_runtime, api, log);
            return [new PythonCodeSubAction(_host)];
        });
        if (TestPython.Executable is not null) await _runtime.RefreshAsync();
    }

    private Task<ActionRunRecord> RunAsync(string code, Dictionary<string, object?>? args = null, double timeout = 30) =>
        _bot.RunAsync(_bot.AddAction("Test", "code.python", code, timeout), args);

    [PythonFact]
    public async Task Reads_and_writes_args()
    {
        var run = await RunAsync("""
            cb.set_arg("out", cb.get_arg("user") + "!")
            cb.set_arg("snapshot", cb.args["user"])
            cb.set_arg("missing", cb.get_arg("nothing", "fallback"))
            """, new() { ["user"] = "Zoë 🎉" });

        Assert.True(run.Success, run.Error);
        Assert.Equal("Zoë 🎉!", _bot.Capture.Args!["out"]);
        Assert.Equal("Zoë 🎉", _bot.Capture.Args["snapshot"]);
        Assert.Equal("fallback", _bot.Capture.Args["missing"]);
    }

    [PythonFact]
    public async Task Cb_call_round_trips_through_script_functions()
    {
        var run = await RunAsync("""
            cb.set_arg("sum", cb.call("test.add", a=2, b=3))
            echoed = cb.call("test.echo", items=[1, 2], name="x")
            cb.set_arg("echo", echoed["name"] + str(len(echoed["items"])))
            cb.set_arg("user", cb.call("test.user")["userName"])
            cb.set_global("color", "blue", persisted=False)
            cb.set_arg("color", cb.get_global("color"))
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal(5L, Convert.ToInt64(_bot.Capture.Args!["sum"]));
        Assert.Equal("x2", _bot.Capture.Args["echo"]);
        Assert.Equal("ann", _bot.Capture.Args["user"]);
        Assert.Equal("blue", _bot.Capture.Args["color"]);
    }

    [PythonFact]
    public async Task Named_methods_call_the_script_functions()
    {
        var run = await RunAsync("""
            cb.set_arg("sum", cb.test_add(a=2, b=3))
            cb.set_arg("keys", cb.test_keys(shown=1, hidden=None))
            cb.set_arg("user", cb.test_user()["userName"])
            cb.set_arg("doc", cb.test_add.__doc__.splitlines()[0])
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal(5L, Convert.ToInt64(_bot.Capture.Args!["sum"]));
        Assert.Equal("shown", _bot.Capture.Args["keys"]);
        Assert.Equal("ann", _bot.Capture.Args["user"]);
        Assert.Equal("Adds two numbers", _bot.Capture.Args["doc"]);
        Assert.True(File.Exists(Path.Combine(_data.Path, "python", PythonFunctionLibrary.ModuleFile)));
        Assert.Contains("    def test_add(self, *, a: Any = None, b: Any = None) -> Any:\n",
            File.ReadAllText(Path.Combine(_data.Path, "python", PythonFunctionLibrary.StubFile)));
    }

    /// <summary>The editor-only import from guides/developers/code-editors.md can be pasted into Cuebot as it is.</summary>
    [PythonFact]
    public async Task Code_with_the_code_editor_import_lines_runs()
    {
        var run = await RunAsync("""
            from typing import TYPE_CHECKING
            if TYPE_CHECKING:
                from cuebot import cb, CuebotError

            cb.set_arg("sum", cb.test_add(a=2, b=3))
            try:
                cb.call("test.missing")
            except CuebotError:
                cb.set_arg("caught", True)
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal(5L, Convert.ToInt64(_bot.Capture.Args!["sum"]));
        Assert.Equal(true, _bot.Capture.Args["caught"]);
    }

    [PythonFact]
    public async Task Type_checking_blocks_keep_their_else_and_line_numbers()
    {
        var withElse = await RunAsync("""
            import typing
            if typing.TYPE_CHECKING:
                from cuebot import cb
            else:
                cb.set_arg("else", "ran")
            """);
        Assert.True(withElse.Success, withElse.Error);
        Assert.Equal("ran", _bot.Capture.Args!["else"]);

        var failing = await RunAsync("""
            from typing import TYPE_CHECKING
            if TYPE_CHECKING:
                from cuebot import cb
            undefined_name
            """);
        Assert.False(failing.Success);
        Assert.Contains("NameError at line 4", failing.Error);
    }

    [PythonFact]
    public async Task Print_output_goes_to_the_log()
    {
        var run = await RunAsync("print('hello', 'from print')\nprint('partial', end='')");

        Assert.True(run.Success, run.Error);
        var lines = _bot.Log.Recent().Where(e => e.Source == "Test").Select(e => e.Message).ToList();
        Assert.Contains("hello from print", lines);
        Assert.Contains("partial", lines);
    }

    [PythonFact]
    public async Task Returning_false_stops_the_action()
    {
        var run = await RunAsync("if True:\n    return False\nprint('unreachable')");

        Assert.True(run.Success, run.Error);
        Assert.Null(_bot.Capture.Args);
    }

    [PythonFact]
    public async Task Exceptions_fail_with_line_and_traceback()
    {
        var run = await RunAsync("x = 1\nraise ValueError('bad value')");

        Assert.False(run.Success);
        Assert.Contains("ValueError at line 2: bad value", run.Error);
        var traceback = _bot.Log.Recent().Single(e => e.Level == LogLevel.Error && e.Message.StartsWith("Traceback")).Message;
        Assert.Contains("line 2", traceback);
        Assert.Contains("raise ValueError('bad value')", traceback);
    }

    [PythonFact]
    public async Task Syntax_errors_report_the_line()
    {
        var run = await RunAsync("x = 1\ndef broken(:\n    pass");

        Assert.False(run.Success);
        Assert.Contains("SyntaxError at line 2", run.Error);
    }

    [PythonFact]
    public async Task Failed_host_calls_raise_in_the_script()
    {
        var run = await RunAsync("""
            try:
                cb.call("nope")
            except CuebotError as error:
                cb.set_arg("caught", str(error))
            """);

        Assert.True(run.Success, run.Error);
        Assert.Contains("Unknown function 'nope'", (string)_bot.Capture.Args!["caught"]!);
    }

    [PythonFact]
    public async Task Timeout_restarts_the_worker()
    {
        var run = await RunAsync("import time\ntime.sleep(60)", timeout: 1);

        Assert.False(run.Success);
        Assert.Contains("did not finish within 1 seconds", run.Error);

        var next = await RunAsync("cb.set_arg('alive', True)");
        Assert.True(next.Success, next.Error);
        Assert.Equal(true, _bot.Capture.Args!["alive"]);
    }

    [PythonFact]
    public async Task Worker_crash_fails_the_run_and_restarts()
    {
        var crash = await RunAsync("import os\nos._exit(3)");

        Assert.False(crash.Success);
        Assert.Contains("stopped unexpectedly", crash.Error);

        var next = await RunAsync("cb.set_arg('alive', 1)");
        Assert.True(next.Success, next.Error);
    }

    [PythonFact]
    public async Task Scripts_can_run_actions_containing_python()
    {
        _bot.AddAction("Inner", "code.python", "cb.log('inner ran')");
        var run = await RunAsync("cb.set_arg('innerResult', cb.run_action('Inner'))");

        Assert.True(run.Success, run.Error);
        Assert.Equal(true, _bot.Capture.Args!["innerResult"]);
        Assert.Contains(_bot.Log.Recent(), e => e.Source == "Inner" && e.Message == "inner ran");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _runtime.DisposeAsync();
        _bot.Dispose();
        _data.Dispose();
    }
}
