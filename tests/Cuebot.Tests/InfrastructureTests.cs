using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cuebot-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class JsonFileTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "";
        public LogLevel Level { get; set; }
    }

    [Fact]
    public void Round_trips_and_writes_enums_as_strings()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var path = System.IO.Path.Combine(dir.Path, "s.json");

        using (var file = new JsonFile<Sample>(path, log))
        {
            file.Value.Name = "hello";
            file.Value.Level = LogLevel.Warning;
            file.SaveNow();
        }

        Assert.Contains("\"Warning\"", File.ReadAllText(path));
        using var reloaded = new JsonFile<Sample>(path, log);
        Assert.Equal("hello", reloaded.Value.Name);
        Assert.Equal(LogLevel.Warning, reloaded.Value.Level);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_replaced_with_defaults()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var path = System.IO.Path.Combine(dir.Path, "s.json");
        File.WriteAllText(path, "{ not json");

        using var file = new JsonFile<Sample>(path, log);
        Assert.Equal("", file.Value.Name);
        Assert.Single(Directory.GetFiles(dir.Path, "s.json.corrupt-*"));
        Assert.Contains(log.Recent(), e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Profile_with_branches_round_trips()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        using (var profile = new ProfileService(paths, log))
        {
            var step = new SubActionDefinition { Type = "core.if", Layout = new NodeLayout(10, 20) };
            step.Branch("True").Add(new SubActionDefinition { Type = "core.log" });
            profile.Profile.Actions.Add(new ActionDefinition { Name = "A", SubActions = [step] });
            profile.NotifyChanged();
        }

        using var reloaded = new ProfileService(paths, log);
        var loaded = reloaded.Profile.Actions.Single().SubActions.Single();
        Assert.Equal(new NodeLayout(10, 20), loaded.Layout);
        Assert.Equal("core.log", loaded.Branches["True"].Single().Type);
    }
}

public class GlobalVariablesTests
{
    [Fact]
    public void Only_persisted_values_survive_restart()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        using (var globals = new GlobalVariables(paths, log))
        {
            globals.Set("kept", "1", persist: true);
            globals.Set("temp", "2", persist: false);
            Assert.Equal("2", globals.Get("TEMP"));
        }

        using var reloaded = new GlobalVariables(paths, log);
        Assert.Equal("1", reloaded.Get("kept"));
        Assert.Null(reloaded.Get("temp"));
    }
}

public class TimerServiceTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Fires_each_enabled_timer_at_its_interval()
    {
        using var log = new AppLog();
        var profile = new ProfileService(null, log);
        profile.Profile.Timers.Add(new TimerDefinition { Name = "Fast", IntervalSeconds = 10 });
        profile.Profile.Timers.Add(new TimerDefinition { Name = "Off", IntervalSeconds = 1, Enabled = false });
        profile.NotifyChanged();
        var bus = new EventBus(log);
        var fired = new List<string>();
        bus.Subscribe(e => fired.Add((string)e.Args["timerName"]!));
        var time = new ManualTime();
        using var timers = new TimerService(profile, bus, time);

        timers.Tick();               // schedules
        time.Now += TimeSpan.FromSeconds(9);
        timers.Tick();
        Assert.Empty(fired);
        time.Now += TimeSpan.FromSeconds(1);
        timers.Tick();
        time.Now += TimeSpan.FromSeconds(10);
        timers.Tick();
        Assert.Equal(["Fast", "Fast"], fired);
    }
}

public class EventBusTests
{
    [Fact]
    public void Failing_handler_does_not_block_others_and_unsubscribe_works()
    {
        using var log = new AppLog();
        var bus = new EventBus(log);
        var received = 0;
        bus.Subscribe(_ => throw new Exception("bad"));
        var sub = bus.Subscribe(_ => received++);

        bus.Publish(new BotEvent("x"));
        sub.Dispose();
        bus.Publish(new BotEvent("x"));

        Assert.Equal(1, received);
        Assert.Equal(2, log.Recent().Count(e => e.Level == LogLevel.Error));
    }
}
