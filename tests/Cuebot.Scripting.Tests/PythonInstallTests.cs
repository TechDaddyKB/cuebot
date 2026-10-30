using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Scripting.Tests;

/// <summary>Runs only with CUEBOT_TEST_PYTHON_INSTALL=1: downloads the pinned Python build (20–35 MB).</summary>
public sealed class PythonInstallFactAttribute : FactAttribute
{
    public PythonInstallFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CUEBOT_TEST_PYTHON_INSTALL") != "1")
            Skip = "Set CUEBOT_TEST_PYTHON_INSTALL=1 to download and install the pinned Python build";
    }
}

public sealed class PythonInstallTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly AppLog _log = new();

    [PythonInstallFact]
    public async Task Installs_the_pinned_python_on_first_use_and_runs_the_step_afterwards()
    {
        var settings = new JsonFile<ScriptingSettings>(Path.Combine(_data.Path, "scripting.json"), _log);
        using var http = new HttpClient();
        await using var runtime = new PythonRuntime(new AppPaths(_data.Path), settings, http, _log);
        PythonScriptHost? host = null;
        using var bot = new TestBot((api, log) => [new PythonCodeSubAction(host = new PythonScriptHost(runtime, api, log))]);
        // Stop the Python worker before the data folder is deleted: Windows keeps a running interpreter's files locked.
        await using var stopWorker = new AsyncDisposer(() => host?.DisposeAsync() ?? ValueTask.CompletedTask);
        var action = bot.AddAction("Test", "code.python", "import sys\ncb.set_arg(\"v\", sys.version.split()[0])\nreturn True", timeoutSeconds: 60);

        var first = await bot.RunAsync(action);
        await runtime.InstallInBackground();
        var second = await bot.RunAsync(action);

        Assert.False(first.Success);
        Assert.Equal(PythonRuntimeState.Ready, runtime.State);
        Assert.Equal(PinnedPython.Version, runtime.Version);
        Assert.True(second.Success, second.Error);
        Assert.Equal(PinnedPython.Version, second.Arguments?["v"]?.ToString());
    }

    public void Dispose() => _data.Dispose();

    private sealed class AsyncDisposer(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
