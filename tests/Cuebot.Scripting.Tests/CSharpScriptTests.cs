using System.Diagnostics;

namespace Cuebot.Scripting.Tests;

public sealed class CSharpScriptTests : IDisposable
{
    private readonly TestBot _bot = new((api, _) => [new CSharpCodeSubAction(new CSharpScriptEngine(api))]);

    private Task<Core.Actions.ActionRunRecord> RunAsync(string code, Dictionary<string, object?>? args = null, double timeout = 30) =>
        _bot.RunAsync(_bot.AddAction("Test", "code.csharp", code, timeout), args);

    [Fact]
    public async Task Reads_and_writes_args()
    {
        var run = await RunAsync("""Args["out"] = Args["user"] + "!";""", new() { ["user"] = "Ann" });

        Assert.True(run.Success, run.Error);
        Assert.Equal("Ann!", _bot.Capture.Args!["out"]);
    }

    [Fact]
    public async Task Cb_call_reaches_script_functions()
    {
        var run = await RunAsync("""
            var sum = await Cb.Call("test.add", new { a = 2, b = 3 });
            Args["sum"] = sum.GetInt32();
            await Cb.SetGlobal("color", "blue", persisted: false);
            Args["color"] = await Cb.GetGlobal("color");
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal(5, _bot.Capture.Args!["sum"]);
        Assert.Equal("blue", _bot.Capture.Args["color"]);
        Assert.Equal("blue", _bot.Globals.Get("color"));
    }

    [Fact]
    public async Task Returning_false_stops_the_action()
    {
        var run = await RunAsync("return false;");

        Assert.True(run.Success);
        Assert.Null(_bot.Capture.Args);
    }

    [Fact]
    public async Task Returning_anything_else_continues()
    {
        var run = await RunAsync("return 42;");

        Assert.True(run.Success);
        Assert.NotNull(_bot.Capture.Args);
    }

    [Fact]
    public async Task Compile_errors_report_line_and_column()
    {
        var run = await RunAsync("var x = 1;\nUndefinedThing();");

        Assert.False(run.Success);
        Assert.Contains("compile error at line 2, column 1", run.Error);
        Assert.Contains("CS0103", run.Error);
    }

    [Fact]
    public async Task Runtime_errors_report_the_script_line()
    {
        var run = await RunAsync("var x = 1;\nthrow new InvalidOperationException(\"boom\");");

        Assert.False(run.Success);
        Assert.Contains("line 2", run.Error);
        Assert.Contains("InvalidOperationException: boom", run.Error);
    }

    [Fact]
    public async Task Unknown_functions_fail_the_action()
    {
        var run = await RunAsync("await Cb.Call(\"nope\");");

        Assert.False(run.Success);
        Assert.Contains("Unknown function 'nope'", run.Error);
    }

    [Fact]
    public async Task Timeout_cancels_the_script()
    {
        var watch = Stopwatch.StartNew();
        var run = await RunAsync("await Task.Delay(30000, CancellationToken);", timeout: 0.5);

        Assert.False(run.Success);
        Assert.Contains("did not finish within 0.5 seconds", run.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Compiled_scripts_are_cached_by_code()
    {
        var compiler = new CSharpScriptCompiler(capacity: 2);

        var first = compiler.GetRunner("return 1;");
        Assert.Same(first, compiler.GetRunner("return 1;"));
        compiler.GetRunner("return 2;");
        compiler.GetRunner("return 3;");

        Assert.Equal(2, compiler.CachedCount);
        Assert.NotSame(first, compiler.GetRunner("return 1;"));
    }

    public void Dispose() => _bot.Dispose();
}
