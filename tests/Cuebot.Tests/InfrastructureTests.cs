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

    private sealed class UtcTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 5, 12, 0, 30, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    private sealed class Harness : IDisposable
    {
        public readonly AppLog Log = new();
        public readonly ProfileService Profile;
        public readonly EventBus Bus;
        public readonly UtcTime Time = new();
        public readonly TimerService Timers;
        public readonly List<BotEvent> Fired = [];

        public Harness(IEnumerable<IPeriodicService>? periodic = null, Random? random = null, params TimerDefinition[] timers)
        {
            Profile = new ProfileService(null, Log);
            Profile.Profile.Timers.AddRange(timers);
            Profile.NotifyChanged();
            Bus = new EventBus(Log);
            Bus.Subscribe(e => { if (e.Type == TimerService.EventType) Fired.Add(e); });
            Timers = new TimerService(Profile, Bus, Time, periodic, Log, random);
        }

        public int Run(double seconds, double step = 1)
        {
            var before = Fired.Count;
            for (double t = 0; t < seconds; t += step)
            {
                Time.Advance(step);
                Timers.Tick();
            }
            return Fired.Count - before;
        }

        public void Chat(int lines)
        {
            for (var i = 0; i < lines; i++)
                Bus.Publish(new BotEvent(Cuebot.Core.Chat.ChatHub.MessageEvent, new Dictionary<string, object?> { ["message"] = "hi" }));
        }

        public void Dispose()
        {
            Timers.Dispose();
            Log.Dispose();
        }
    }

    [Fact]
    public void Old_timer_json_loads_with_defaults()
    {
        var timer = System.Text.Json.JsonSerializer.Deserialize<TimerDefinition>(
            """{ "name": "Old", "intervalSeconds": 60, "enabled": true }""", Json.Options)!;
        Assert.True(timer.Repeat);
        Assert.False(timer.RandomInterval);
        Assert.Equal(0, timer.MinChatLines);
        Assert.Null(timer.Cron);
        Assert.Equal(60, timer.IntervalSeconds);
    }

    [Fact]
    public void Random_interval_stays_within_bounds()
    {
        using var h = new Harness(random: new Random(42),
            timers: new TimerDefinition { Name = "R", RandomInterval = true, MinIntervalSeconds = 10, MaxIntervalSeconds = 20 });
        h.Timers.Tick(); // arm
        var fireTimes = new List<DateTimeOffset>();
        var start = h.Time.Now;
        for (var i = 0; i < 2000; i++)
        {
            h.Time.Advance(1);
            var before = h.Fired.Count;
            h.Timers.Tick();
            if (h.Fired.Count > before) fireTimes.Add(h.Time.Now);
        }
        var gaps = fireTimes.Prepend(start).Zip(fireTimes, (a, b) => (b - a).TotalSeconds).ToArray();
        Assert.True(gaps.Length > 50);
        Assert.All(gaps, g => Assert.InRange(g, 10, 20));
        Assert.True(gaps.Distinct().Count() > 3, "intervals should vary");
    }

    [Fact]
    public void Min_chat_lines_skips_quiet_cycles()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Lines", IntervalSeconds = 10, MinChatLines = 3 });
        h.Timers.Tick(); // arm
        h.Chat(2);
        Assert.Equal(0, h.Run(10));   // due, but only 2 lines: skipped
        h.Chat(1);                    // lines keep counting across the skipped cycle
        Assert.Equal(1, h.Run(10));
        Assert.Equal(3, h.Fired[0].Args["chatLines"]);
        Assert.Equal(0, h.Run(10));   // counter reset after firing
        h.Chat(5);
        Assert.Equal(1, h.Run(10));
    }

    [Fact]
    public void Non_repeating_timer_fires_once_until_re_enabled()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Once", IntervalSeconds = 5, Repeat = false });
        h.Timers.Tick();
        Assert.Equal(1, h.Run(30));
        Assert.Null(h.Timers.GetState("once")!.NextFire);

        h.Timers.SetState("Once", enabled: false);
        h.Timers.SetState("Once", enabled: true);
        Assert.Equal(1, h.Run(30));
    }

    [Fact]
    public void Cron_timer_fires_on_schedule()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Cron", Cron = "*/15 * * * *", IntervalSeconds = 1 });
        h.Timers.Tick(); // 12:00:30 → next 12:15
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 12, 15, 0, TimeSpan.Zero), h.Timers.GetState("Cron")!.NextFire);
        Assert.Equal(0, h.Run(14 * 60 + 29));
        Assert.Equal(1, h.Run(1));
        Assert.Equal(3, h.Run(45 * 60, step: 30));
        Assert.Equal("*/15 * * * *", h.Fired[0].Args["timerCron"]);
    }

    [Fact]
    public void Invalid_cron_never_fires_and_logs_once()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Bad", Cron = "not a cron", IntervalSeconds = 1 });
        Assert.Equal(0, h.Run(120));
        Assert.Single(h.Log.Recent(), e => e.Level == LogLevel.Warning && e.Message.Contains("Bad"));
    }

    [Fact]
    public void Set_state_overrides_enable_and_interval_until_the_profile_changes()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "T", IntervalSeconds = 10 });
        var changed = new List<Guid>();
        h.Timers.StateChanged += changed.Add;
        h.Timers.Tick();
        Assert.Equal(1, h.Run(10));

        Assert.True(h.Timers.SetState("t", enabled: false));
        Assert.Equal(0, h.Run(60));
        Assert.False(h.Timers.GetState("T")!.Enabled);

        Assert.True(h.Timers.SetState("T", enabled: true, intervalSeconds: 30));
        var state = h.Timers.GetState("T")!;
        Assert.True(state.Enabled);
        Assert.Equal(30, state.IntervalSeconds);
        Assert.Equal(h.Time.Now.AddSeconds(30), state.NextFire);
        Assert.Equal(2, h.Run(60));
        Assert.Equal(30, h.Fired[^1].Args["interval"]);

        // Editing the timer in the profile drops runtime overrides.
        h.Profile.Profile.Timers[0].IntervalSeconds = 20;
        h.Profile.NotifyChanged();
        h.Timers.Tick();
        Assert.Equal(20, h.Timers.GetState("T")!.IntervalSeconds);

        Assert.False(h.Timers.SetState("missing", enabled: true));
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void Set_state_enables_a_timer_disabled_in_the_profile()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Off", IntervalSeconds = 5, Enabled = false });
        h.Timers.Tick();
        Assert.Equal(0, h.Run(20));
        h.Timers.SetState("Off", enabled: true);
        Assert.Equal(4, h.Run(20));
    }

    [Fact]
    public async Task Timer_sub_actions_get_and_set_state()
    {
        using var h = new Harness(timers: new TimerDefinition { Name = "Promo", IntervalSeconds = 100 });
        h.Timers.Tick();
        var set = new Cuebot.Core.Commands.SetTimerStateSubAction(h.Timers, h.Profile);
        var get = new Cuebot.Core.Commands.GetTimerStateSubAction(h.Timers, h.Profile, h.Time);

        await Commands.SubActionHarness.RunAsync(set, new() { ["timer"] = "Promo", ["state"] = "Toggle" });
        Assert.False(h.Timers.GetState("Promo")!.Enabled);
        await Commands.SubActionHarness.RunAsync(set, new() { ["timer"] = "Promo", ["state"] = "Enable", ["interval"] = "40" });

        var result = await Commands.SubActionHarness.RunAsync(get, new() { ["timer"] = "%timerName%" }, new() { ["timerName"] = "promo" });
        Assert.Equal(true, result.Args["timerEnabled"]);
        Assert.Equal(40, result.Args["timerInterval"]);
        Assert.Equal(40, result.Args["timerSecondsRemaining"]);

        await Commands.SubActionHarness.RunAsync(set, new() { ["timer"] = "Promo", ["state"] = "No change", ["interval"] = "7" });
        Assert.Equal(7, h.Timers.GetState("Promo")!.IntervalSeconds);
        Assert.True(h.Timers.GetState("Promo")!.Enabled);

        Assert.False((await Commands.SubActionHarness.RunAsync(set, new() { ["timer"] = "Promo", ["interval"] = "soon" })).Success);
        Assert.False((await Commands.SubActionHarness.RunAsync(get, new() { ["timer"] = "Nope" })).Success);
    }

    [Fact]
    public void Periodic_services_are_ticked_and_failures_are_logged()
    {
        var ticks = new List<DateTimeOffset>();
        using var h = new Harness(periodic: [new Periodic(ticks.Add), new Periodic(_ => throw new InvalidOperationException("boom"))]);
        h.Run(3);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(3, h.Log.Recent().Count(e => e.Level == LogLevel.Error));
    }

    private sealed class Periodic(Action<DateTimeOffset> tick) : IPeriodicService
    {
        public void Tick(DateTimeOffset now) => tick(now);
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
