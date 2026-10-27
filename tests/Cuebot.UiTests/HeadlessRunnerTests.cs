using Cuebot.App.Shell;
using Cuebot.Core;

namespace Cuebot.UiTests;

public sealed class HeadlessRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-headless-").FullName;

    [Fact]
    public async Task Runs_every_service_without_a_window_and_flushes_stores_on_stop()
    {
        var paths = new AppPaths(_root);
        using var instance = SingleInstance.TryAcquire(_root);
        var ctx = new ShellContext
        {
            Options = StartupOptions.Parse(["--headless"]),
            Paths = paths,
            Settings = new AppSettingsStore(paths),
            Instance = instance,
        };
        ctx.Note(Cuebot.Core.Logging.LogLevel.Info, "note from before the log existed");

        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => HeadlessRunner.Run(ctx, stop.Token));

        // Wait until it is up, then a second launch is answered (and ignored) while headless.
        for (var i = 0; i < 100 && !ReadLog(paths).Contains("Running headless"); i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(await SingleInstance.ActivateExistingAsync(_root, TimeSpan.FromSeconds(10)));

        stop.CancelAfter(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // The first-run sample profile was written by the shutdown flush.
        Assert.True(File.Exists(Path.Combine(_root, "actions.json")));
        var log = ReadLog(paths);
        Assert.Contains("note from before the log existed", log);
        Assert.Contains("Running headless", log);
        Assert.Contains("running headless, so it was ignored", log);
        Assert.Contains("Shutting down", log);
        Assert.True(ctx.Host!.ShutDown(TimeSpan.FromSeconds(1)));   // already disposed: returns at once
    }

    private static string ReadLog(AppPaths paths) => string.Join("\n", Directory.GetFiles(paths.Logs, "*.log").Select(f =>
    {
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(s).ReadToEnd();
    }));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
