using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Spending;
using Cuebot.Core.Subathon;

namespace Cuebot.Tests.Extras;

public sealed class SubathonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-subathon-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly Clock _clock = new();
    private readonly List<BotEvent> _events = [];
    private readonly IDisposable _sub;

    public SubathonTests()
    {
        _bus = new EventBus(_log);
        _sub = _bus.Subscribe(e => { if (e.Type.StartsWith("subathon.") || e.Type.StartsWith("goals.")) lock (_events) _events.Add(e); });
    }

    private SubathonService Service() => new(new AppPaths(_dir), null, _bus, _log, _clock);

    private BotEvent[] Events(string type)
    {
        lock (_events) return _events.Where(e => e.Type == type).ToArray();
    }

    /// <summary>A purchase as the spending ledger announces it.</summary>
    private void Spend(SpendUnit unit, double quantity, string platform = "Twitch", string tier = "", double amount = 0, string user = "Viewer") =>
        _bus.Publish(new BotEvent(SpendingEvents.Recorded, new Dictionary<string, object?>
        {
            ["user"] = user, ["platform"] = platform, ["label"] = unit.ToString(), ["unit"] = unit.ToString(), ["quantity"] = quantity,
            ["tier"] = tier, ["amount"] = amount, ["currency"] = "USD",
        }));

    private FeedItem Item(FeedKind kind, string title = "", string platform = "Twitch", string user = "Viewer") =>
        new(platform, kind, user, title, _clock.GetUtcNow());

    private static double Left(SubathonService s, string name = "") => s.Timer(name)!.RemainingSeconds;

    [Fact]
    public void Counts_down_pauses_resumes_and_ends()
    {
        using var s = Service();
        var t = s.AddTimer("Subathon");
        s.UpdateTimer(t.Id, x => x.InitialSeconds = 120);
        Assert.Equal((TimerState.Stopped, 120.0), (s.Timer("")!.State, Left(s)));

        s.Start("subathon");
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(90, Left(s));
        s.Pause("");
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal((TimerState.Paused, 90.0), (s.Timer("")!.State, Left(s)));
        s.Toggle("");
        _clock.Advance(TimeSpan.FromSeconds(89));
        s.Tick(_clock.GetUtcNow());
        Assert.Equal(TimerState.Running, s.Timer("")!.State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        s.Tick(_clock.GetUtcNow());
        var ended = s.Timer("")!;
        Assert.Equal((TimerState.Ended, 0.0, 120.0), (ended.State, ended.RemainingSeconds, ended.ElapsedSeconds));
        Assert.Equal(["subathon.started", "subathon.paused", "subathon.resumed", "subathon.ended"],
            _events.Select(e => e.Type).Where(x => x != SubathonEvents.TimeAdded));
        Assert.Equal("0:00:00", Events(SubathonEvents.Ended).Single().Args["formattedRemaining"]);

        s.Start("");   // an ended timer starts over
        Assert.Equal((TimerState.Running, 120.0), (s.Timer("")!.State, Left(s)));
        s.Reset("");
        Assert.Equal((TimerState.Stopped, 120.0, 0.0), (s.Timer("")!.State, Left(s), s.Timer("")!.ElapsedSeconds));
    }

    [Fact]
    public void Events_add_time_by_their_values_only_while_running_unless_paused_counting_is_on()
    {
        using var s = Service();
        var t = s.AddTimer();
        s.UpdateTimer(t.Id, x => x.InitialSeconds = 600);
        Spend(SpendUnit.Bits, 500);
        Assert.Equal(600, Left(s));   // stopped: nothing counts

        s.Start("");
        Spend(SpendUnit.Bits, 500);                                   // 5 × 60
        Spend(SpendUnit.Subscription, 1, tier: "2");                  // 600
        Spend(SpendUnit.GiftSubscription, 5, tier: "1");              // 5 × 300
        Spend(SpendUnit.Money, 9, "YouTube", amount: 10.5);           // $10.50 × 60
        Spend(SpendUnit.Kicks, 100, "Kick");                          // 60
        Spend(SpendUnit.Subscription, 1, "YouTube", "Gold");          // 300
        Assert.Equal(600 + 300 + 600 + 1500 + 630 + 60 + 300, Left(s));

        s.Pause("");
        Spend(SpendUnit.Bits, 100);
        Assert.Equal(3990, Left(s));
        s.UpdateTimer(t.Id, x => x.AddWhilePaused = true);
        Spend(SpendUnit.Bits, 100);
        Assert.Equal(4050, Left(s));

        var added = Events(SubathonEvents.TimeAdded);
        Assert.Equal(7, added.Length);
        Assert.Equal(("+0:05:00", "Viewer", "Twitch"), ((string)added[0].Args["formattedSeconds"]!, (string)added[0].Args["user"]!, (string)added[0].Args["platform"]!));
    }

    [Fact]
    public void Follows_raids_and_redemptions_count_by_their_values()
    {
        using var s = Service();
        var t = s.AddTimer();
        s.UpdateTimer(t.Id, x =>
        {
            x.Values.Follow = 10;
            x.Values.RaidEach = 60;
            x.Values.RaidPerViewer = 2;
            x.Values.Redemptions["Add a minute"] = 60;
        });
        s.Start("");
        var start = Left(s);
        s.Record(Item(FeedKind.Follow, "Viewer followed"));
        s.Record(Item(FeedKind.Raid, "Viewer raided with 25 viewers"));
        s.Record(Item(FeedKind.Redemption, "Viewer redeemed add A Minute"));
        s.Record(Item(FeedKind.Redemption, "Viewer redeemed Hydrate"));
        s.Record(Item(FeedKind.Follow, "Test followed") with { IsTest = true });   // a platform's test button: ignored
        Assert.Equal(start + 10 + 110 + 60, Left(s));
        s.Record(Item(FeedKind.Follow, "Emulated followed") with { IsTest = true, IsEmulated = true });   // Cuebot's emulator: counts
        Assert.Equal(start + 10 + 110 + 60 + 10, Left(s));
    }

    [Fact]
    public void Multiplier_and_value_changes_only_affect_what_comes_next_and_the_cap_holds()
    {
        using var s = Service();
        var t = s.AddTimer();
        s.UpdateTimer(t.Id, x =>
        {
            x.InitialSeconds = 1000;
            x.MaxSeconds = 2000;
        });
        s.Start("");
        Spend(SpendUnit.Subscription, 1, tier: "1");
        Assert.Equal(1300, Left(s));

        s.SetTimerMultiplier("", 2);   // happy hour
        Spend(SpendUnit.Subscription, 1, tier: "1");
        Assert.Equal(1900, Left(s));

        s.UpdateTimer(t.Id, x =>
        {
            x.Multiplier = 0.5;
            x.Values.TwitchTier1 = 60;   // too many subs: bring it down mid-run
        });
        Spend(SpendUnit.Subscription, 1, tier: "1");
        Assert.Equal(1930, Left(s));
        Assert.Equal(TimerState.Running, s.Timer("")!.State);   // settings never touch the running state

        _clock.Advance(TimeSpan.FromSeconds(100));
        Spend(SpendUnit.Bits, 100000);
        Assert.Equal(1900, Left(s));   // 100 run + 1900 left = the 2000 limit
        Assert.Equal(0, s.AddTime("", 500));
        Assert.Equal(-1900, s.AddTime("", -5000));
        Assert.Equal(TimerState.Ended, s.Timer("")!.State);
        Assert.Equal(60, s.AddTime("", 60));   // a manual add brings an ended timer back, paused
        Assert.Equal(TimerState.Paused, s.Timer("")!.State);
    }

    [Fact]
    public void A_running_timer_is_frozen_at_its_last_save_after_a_crash_and_resumes_on_go_live()
    {
        var file = Path.Combine(_dir, "subathon.json");
        var crashCopy = file + ".crash";
        using (var s = Service())
        {
            var t = s.AddTimer();
            s.UpdateTimer(t.Id, x =>
            {
                x.InitialSeconds = 3600;
                x.StartOnStreamOnline = true;
            });
            s.Start("");
            _clock.Advance(TimeSpan.FromSeconds(100));
            s.Tick(_clock.GetUtcNow());   // saves 3500 left
            s.FlushFile();
            File.Copy(file, crashCopy);   // what is on disk if Cuebot crashes now
            _clock.Advance(TimeSpan.FromSeconds(5));
        }
        File.Copy(crashCopy, file, overwrite: true);   // a crash never gets to the clean-shutdown save
        _clock.Advance(TimeSpan.FromHours(3));   // Cuebot was closed
        using var again = Service();
        var timer = again.Timer("")!;
        Assert.Equal((TimerState.Paused, 3500.0), (timer.State, timer.RemainingSeconds));

        again.Record(Item(FeedKind.StreamOnline));
        Assert.Equal(TimerState.Running, again.Timer("")!.State);
    }

    [Fact]
    public void Starts_and_pauses_with_the_stream_when_asked()
    {
        using var s = Service();
        var t = s.AddTimer();
        s.UpdateTimer(t.Id, x =>
        {
            x.StartOnStreamOnline = true;
            x.PauseOnStreamOffline = true;
        });
        var manual = s.AddTimer("Manual");
        s.Record(Item(FeedKind.StreamOnline, platform: "Twitch"));
        s.Record(Item(FeedKind.StreamOnline, platform: "YouTube"));
        Assert.Equal(TimerState.Running, s.Timer("Subathon")!.State);
        Assert.Equal(TimerState.Stopped, s.Timer("Manual")!.State);

        s.Record(Item(FeedKind.StreamOffline, platform: "Twitch"));
        Assert.Equal(TimerState.Running, s.Timer("Subathon")!.State);   // still live on YouTube
        s.Record(Item(FeedKind.StreamOffline, platform: "YouTube"));
        Assert.Equal(TimerState.Paused, s.Timer("Subathon")!.State);
        s.Record(Item(FeedKind.StreamOnline, platform: "Twitch"));
        Assert.Equal(TimerState.Running, s.Timer("Subathon")!.State);
        Assert.True(s.RemoveTimer(manual.Id));
        Assert.Throws<ArgumentException>(() => s.Start("Manual"));
    }

    [Fact]
    public void Goal_steps_are_sorted_and_each_is_reached_once()
    {
        using var s = Service();
        var g = s.AddGoal("Emote ladder");
        s.UpdateGoal(g.Id, x => x.Steps =
        [
            new() { Name = "Third emote", Points = 30 },
            new() { Name = "First emote", Points = 10 },
            new() { Name = "", Points = 5 },
            new() { Name = "Second emote", Points = 20 },
        ]);
        Assert.Equal(["First emote", "Second emote", "Third emote"], s.Goal("emote ladder")!.Steps.Select(x => x.Name));

        for (var i = 0; i < 9; i++) Spend(SpendUnit.Subscription, 1, tier: "1");
        Assert.Empty(Events(SubathonEvents.StepReached));
        Spend(SpendUnit.Subscription, 1, tier: "1");
        Assert.Equal(["First emote"], Events(SubathonEvents.StepReached).Select(e => (string)e.Args["step"]!));

        s.AddPoints("", -5);
        s.AddPoints("", 5);
        Assert.Single(Events(SubathonEvents.StepReached));   // 10 again: not announced twice

        Spend(SpendUnit.GiftSubscription, 4, tier: "3");   // 4 × 5 = 20 points at once: two steps
        Assert.Equal(["First emote", "Second emote", "Third emote"], Events(SubathonEvents.StepReached).Select(e => (string)e.Args["step"]!));
        Assert.Single(Events(SubathonEvents.GoalCompleted));
        var last = Events(SubathonEvents.StepReached)[^1].Args;
        Assert.Equal((3, 3, 30.0), ((int)last["stepNumber"]!, (int)last["stepCount"]!, (double)last["points"]!));

        s.ResetGoal("");
        Assert.Equal(0, s.Goal("")!.Points);
        s.SetPoints("", 15);
        Assert.Equal(4, Events(SubathonEvents.StepReached).Length);
        var goal = SubathonService.GoalArgs(s.Goal("")!);
        Assert.Equal(("Second emote", 20.0, 5.0), ((string)goal["nextStep"]!, (double)goal["nextStepPoints"]!, (double)goal["pointsToNext"]!));
    }

    [Fact]
    public void Goals_reset_for_a_new_stream_but_not_after_a_short_drop_or_a_restart()
    {
        using (var s = Service())
        {
            var g = s.AddGoal();
            s.UpdateGoal(g.Id, x => x.ResetOnNewStream = true);
            s.Record(Item(FeedKind.StreamOnline));
            s.AddPoints("", 7);
            s.Record(Item(FeedKind.StreamOffline));
            _clock.Advance(TimeSpan.FromMinutes(5));
            s.Record(Item(FeedKind.StreamOnline));
            Assert.Equal(7, s.Goal("")!.Points);   // back within 15 minutes: the same stream
            s.FlushFile();
        }
        using (var restarted = Service())
        {
            restarted.Record(Item(FeedKind.StreamOnline));
            Assert.Equal(7, restarted.Goal("")!.Points);   // Cuebot restarted while live
            restarted.Record(Item(FeedKind.StreamOffline));
            _clock.Advance(TimeSpan.FromMinutes(20));
            restarted.Record(Item(FeedKind.StreamOnline));
            Assert.Equal(0, restarted.Goal("")!.Points);
        }
    }

    [Fact]
    public void Puts_timers_and_goals_in_the_overlay_session_data()
    {
        using var s = Service();
        s.AddTimer();
        s.AddGoal();
        s.Start("");
        s.AddPoints("", 12);
        var data = new System.Text.Json.Nodes.JsonObject();
        new SubathonSessionData(s, _clock).AddTo(data);
        var timer = data["cuebot-subathon"]!["timers"]![0]!;
        Assert.Equal("running", (string)timer["state"]!);
        Assert.Equal(_clock.GetUtcNow().AddHours(4).ToUnixTimeMilliseconds(), (long)timer["endsAt"]!);
        var goal = data["cuebot-subathon"]!["goals"]![0]!;
        Assert.Equal((12.0, "Second goal", 10.0), ((double)goal["points"]!, (string)goal["next"]!["name"]!, (double)goal["previous"]!));
        Assert.True((bool)goal["steps"]![0]!["reached"]!);
    }

    [Fact]
    public void A_clean_shutdown_freezes_a_running_timer_at_that_second()
    {
        using (var s = Service())
        {
            s.AddTimer();
            s.Start("");
            s.Tick(_clock.GetUtcNow());   // the periodic save: 4:00:00 left
            _clock.Advance(TimeSpan.FromSeconds(7));
        }
        using var again = Service();
        var timer = again.Timer("")!;
        Assert.Equal((TimerState.Paused, 4 * 3600 - 7.0, 7.0), (timer.State, timer.RemainingSeconds, timer.ElapsedSeconds));
    }

    [Fact]
    public void Time_left_rounds_up_and_time_run_rounds_down_so_they_add_up()
    {
        Assert.Equal("3:59:58", SubathonService.Format(14397.4));
        Assert.Equal("0:00:02", SubathonService.Format(2.6, roundUp: false));
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(59.4, "0:01:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(93790, "26:03:10")]
    public void Formats_time_left(double seconds, string text) => Assert.Equal(text, SubathonService.Format(seconds));

    public void Dispose()
    {
        _sub.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now += by;
    }
}
