using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Tests.Storage;

/// <summary>A delayed save runs on a timer thread, where an escaping exception ends the process.</summary>
public class JsonFileShutdownTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "";
    }

    private static string Blocked(TempDir dir) => Path.Combine(dir.Path, "not-yet", "sample.json");

    private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public void A_failed_save_after_the_log_closed_does_not_throw()
    {
        using var dir = new TempDir();
        var log = new AppLog(new AppPaths(dir.Path));
        var file = new JsonFile<Sample>(Blocked(dir), log);
        file.Value.Name = "late";
        file.ScheduleSave();
        log.Dispose();

        var thrown = Record.Exception(file.Flush);
        var disposing = Record.Exception(file.Dispose);

        Assert.Null(thrown);
        Assert.Null(disposing);
    }

    [Fact]
    public void A_closed_log_keeps_entries_in_memory()
    {
        using var dir = new TempDir();
        var log = new AppLog(new AppPaths(dir.Path));
        log.Dispose();

        log.Error("Storage", "after close");

        Assert.Contains(log.Recent(), e => e.Message == "after close");
    }

    [Fact]
    public async Task A_failed_save_is_retried_until_it_can_be_written()
    {
        using var dir = new TempDir();
        var path = Blocked(dir);
        using var log = new AppLog();
        using var file = new JsonFile<Sample>(path, log);
        file.Value.Name = "kept";
        file.ScheduleSave();
        file.Flush();
        Assert.False(File.Exists(path));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.True(await WaitFor(() => File.Exists(path)), "the save was dropped after its first failure");
        Assert.Contains("kept", File.ReadAllText(path));
    }

    [Fact]
    public void A_repeated_failure_is_logged_once()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        using var file = new JsonFile<Sample>(Blocked(dir), log);
        file.Value.Name = "x";
        file.ScheduleSave();

        file.Flush();
        file.Flush();
        file.Flush();

        Assert.Single(log.Recent(), e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void A_newer_edit_replaces_a_failed_save()
    {
        using var dir = new TempDir();
        var path = Blocked(dir);
        using var log = new AppLog();
        using var file = new JsonFile<Sample>(path, log);
        file.Value.Name = "old";
        file.ScheduleSave();
        file.Flush();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file.Value.Name = "new";
        file.ScheduleSave();
        file.Flush();

        Assert.Contains("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_save_requested_after_dispose_is_still_written()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sample.json");
        using var log = new AppLog();
        var file = new JsonFile<Sample>(path, log);
        file.Dispose();
        file.Value.Name = "after dispose";

        var thrown = Record.Exception(file.ScheduleSave);

        Assert.Null(thrown);
        Assert.Contains("after dispose", File.ReadAllText(path));
    }

    [Fact]
    public void Dispose_writes_a_pending_save_and_can_run_twice()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sample.json");
        using var log = new AppLog();
        var file = new JsonFile<Sample>(path, log);
        file.Value.Name = "pending";
        file.ScheduleSave();

        file.Dispose();
        file.Dispose();

        Assert.Contains("pending", File.ReadAllText(path));
    }
}
