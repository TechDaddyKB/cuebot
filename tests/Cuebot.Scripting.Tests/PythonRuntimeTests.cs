using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Scripting.Tests;

public sealed class PythonRuntimeTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly AppLog _log = new();

    private readonly RecordingHandler _http = new();

    private PythonRuntime CreateRuntime(string customPath = "", bool installAutomatically = false)
    {
        var settings = new JsonFile<ScriptingSettings>(Path.Combine(_data.Path, "scripting.json"), _log);
        settings.Value.CustomPythonPath = customPath;
        settings.Value.InstallPythonAutomatically = installAutomatically;
        return new PythonRuntime(new AppPaths(_data.Path), settings, new HttpClient(_http), _log);
    }

    /// <summary>Answers every request with 404 and remembers the URLs, so no test downloads anything.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Reports_not_installed_without_a_runtime()
    {
        await using var runtime = CreateRuntime();
        await runtime.RefreshAsync();

        Assert.Equal(PythonRuntimeState.NotInstalled, runtime.State);
        Assert.Null(runtime.PythonExecutable);
    }

    [Fact]
    public async Task Reports_an_error_for_a_missing_custom_interpreter()
    {
        await using var runtime = CreateRuntime(Path.Combine(_data.Path, "missing", "python"));
        await runtime.RefreshAsync();

        Assert.Equal(PythonRuntimeState.Error, runtime.State);
        Assert.Contains("not found", runtime.StatusMessage);
    }

    [Fact]
    public async Task Python_sub_action_explains_how_to_install()
    {
        await using var runtime = CreateRuntime();
        using var bot = new TestBot((api, log) => [new PythonCodeSubAction(new PythonScriptHost(runtime, api, log))]);

        var run = await bot.RunAsync(bot.AddAction("Test", "code.python", "pass"));

        Assert.False(run.Success);
        Assert.Contains("Install it on Automation > Scripting", run.Error);
        Assert.Empty(_http.Urls);
    }

    [Fact]
    public async Task First_python_step_starts_installing_the_pinned_build()
    {
        if (PinnedPython.ForThisComputer() is null) return;
        await using var runtime = CreateRuntime(installAutomatically: true);
        using var bot = new TestBot((api, log) => [new PythonCodeSubAction(new PythonScriptHost(runtime, api, log))]);

        var run = await bot.RunAsync(bot.AddAction("Test", "code.python", "pass"));
        await runtime.InstallInBackground();

        Assert.False(run.Success);
        Assert.Contains("Python is being installed for the first Python step", run.Error);
        var url = Assert.Single(_http.Urls);
        Assert.Equal(PinnedPython.ForThisComputer()!.DownloadUrl, url);
        Assert.Equal(PythonRuntimeState.Error, runtime.State);   // the fake server answered 404
    }

    [Fact]
    public async Task Install_notice_offers_the_install_until_python_is_ready()
    {
        if (PinnedPython.ForThisComputer() is null) return;
        await using var runtime = CreateRuntime(installAutomatically: true);
        var notice = new PythonInstallNotice(runtime);

        var current = Assert.IsType<ParameterNotice>(notice.Current);
        Assert.Equal("Install Python", current.ButtonText);
        Assert.Contains($"Python {PinnedPython.Version}", current.Text);
        Assert.Contains("installed the first time this step runs", current.Text);

        await using var custom = CreateRuntime(Path.Combine(_data.Path, "missing", "python"));
        await custom.RefreshAsync();
        Assert.Null(new PythonInstallNotice(custom).Current!.ButtonText);
    }

    [Fact]
    public async Task Package_operations_require_a_runtime()
    {
        await using var runtime = CreateRuntime();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ListPackagesAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.InstallPackageAsync("--pre requests"));
    }

    [Fact]
    public void Parses_pip_list_output()
    {
        var packages = PythonRuntime.ParsePackageList("""[{"name":"requests","version":"2.32.3"},{"name":"certifi","version":"2024.8.30"}]""");

        Assert.Equal([new PythonPackage("certifi", "2024.8.30"), new PythonPackage("requests", "2.32.3")], packages);
    }

    [Fact]
    public void Reads_the_environment_version_from_pyvenv_cfg()
    {
        var layout = new PythonLayout(_data.Path);
        Directory.CreateDirectory(layout.EnvironmentDirectory);
        File.WriteAllText(Path.Combine(layout.EnvironmentDirectory, "pyvenv.cfg"), "home = /x\nversion_info = 3.13.1.final.0\n");

        Assert.Equal("3.13.1", layout.ReadEnvironmentVersion());
    }

    [PythonFact]
    public async Task Uses_a_custom_interpreter_directly()
    {
        await using var runtime = CreateRuntime(TestPython.Executable!);
        await runtime.RefreshAsync();

        Assert.Equal(PythonRuntimeState.Ready, runtime.State);
        Assert.Equal(TestPython.Executable, runtime.PythonExecutable);
        Assert.StartsWith("3.", runtime.Version);
    }

    public void Dispose()
    {
        _log.Dispose();
        _data.Dispose();
    }
}
