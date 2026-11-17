using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Spending;
using Cuebot.Core.Subathon;

namespace Cuebot.Tests.Extras;

/// <summary>Per-service money values, and the history of every timer and goal with its corrections.</summary>
public sealed class SubathonHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-subathon-history-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly Clock _clock = new();

    public SubathonHistoryTests() => _bus = new EventBus(_log);

    private SubathonService Service() => new(new AppPaths(_dir), null, _bus, _log, _clock);

    private void Money(double amount, string platform, string user = "Viewer", string label = "Donation", bool isTest = false) =>
        _bus.Publish(new BotEvent(SpendingEvents.Recorded, new Dictionary<string, object?>
        {
            ["user"] = user, ["platform"] = platform, ["label"] = label, ["unit"] = nameof(SpendUnit.Money), ["quantity"] = amount,
            ["tier"] = "", ["amount"] = amount, ["currency"] = "USD", ["isTest"] = isTest,
        }));

    private void Sub(string user = "Viewer") =>
        _bus.Publish(new BotEvent(SpendingEvents.Recorded, new Dictionary<string, object?>
        {
            ["user"] = user, ["platform"] = "Twitch", ["label"] = "Subscription", ["unit"] = nameof(SpendUnit.Subscription), ["quantity"] = 1,
            ["tier"] = "1", ["amount"] = 2.5, ["currency"] = "USD",
        }));

    /// <summary>A running timer that starts at one hour, with 60 seconds per 1 of money and 300 per sub.</summary>
    private SubathonTimer Running(SubathonService s, Action<EventValues>? values = null)
    {
        var t = s.AddTimer("Donothon");
        s.UpdateTimer(t.Id, x =>
        {
            x.InitialSeconds = 3600;
            values?.Invoke(x.Values);
        });
        s.Start(t.Id);
        return t;
    }

    private static double Left(SubathonService s) => s.Timer("")!.RemainingSeconds;

    [Fact]
    public void Each_service_can_have_its_own_money_value()
    {
        using var s = Service();
        Running(s, v =>
        {
            v.PerCurrencyUnit = 60;
            v.MoneyBySource["throne"] = 10;        // gifts bought for the streamer: worth less
            v.MoneyBySource["Fourthwall"] = 0;     // merch: does not count
        });

        Money(5, "Throne");
        Assert.Equal(3650, Left(s));
        Money(5, "Ko-fi");                          // not listed: the general value
        Assert.Equal(3950, Left(s));
        Money(40, "Fourthwall");
        Assert.Equal(3950, Left(s));

        Assert.Equal(10, s.Timer("")!.Values.MoneyValue("THRONE"));
        Assert.Equal(60, s.Timer("")!.Values.MoneyValue("YouTube"));
    }

    [Fact]
    public void Records_what_every_event_and_change_by_hand_added()
    {
        using var s = Service();
        var t = Running(s);
        s.AddGoal("Sub goal");
        Money(10, "Throne", "Ann", "Wishlist gift");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Sub("Bob");
        s.AddTime("", 120, user: "Mod", reason: "Cash at the door");
        s.SetTime("", 4000);

        var timer = s.History(new HistoryFilter(Owner: "Donothon"));
        Assert.Equal(["Set time left", "Cash at the door", "Subscription", "Wishlist gift"], timer.Select(e => e.Reason));
        var gift = timer[^1];
        Assert.Equal((HistoryKind.Event, "Ann", "Throne", 10.0, 600.0, t.Id, false), (gift.Kind, gift.User, gift.Source, gift.Amount, gift.Value, gift.OwnerId, gift.IsGoal));
        Assert.Equal(HistoryKind.Manual, timer[1].Kind);
        Assert.Equal(HistoryKind.Set, timer[0].Kind);
        Assert.Equal(4000 - (3600 + 600 - 60 + 300 + 120), timer[0].Value);

        var goal = s.History(new HistoryFilter(Owner: "Sub goal")).Single();   // only the sub is worth points
        Assert.Equal((true, "Bob", 1.0), (goal.IsGoal, goal.User, goal.Value));
        Assert.Equal("+0:10:00", SubathonService.Change(gift));
        Assert.Equal("+1 point", SubathonService.Change(goal));

        var sum = s.Summarize(new HistoryFilter(Owner: t.Id, Source: "Throne"));
        Assert.Equal((1, 600.0, 10.0), (sum.Count, sum.Value, sum.Amount));
        Assert.Equal("By hand", s.Summarize(new HistoryFilter(Owner: t.Id)).BySource.Single(x => x.Count == 2).Source);
    }

    [Fact]
    public void Removing_an_entry_takes_its_time_back_and_restoring_puts_it_back()
    {
        using var s = Service();
        Running(s);
        Money(10, "Streamlabs", "Ann");
        var entry = s.History(new HistoryFilter()).Single();
        Assert.Equal(4200, Left(s));

        var removed = s.RemoveEntry(entry.Id, "Charged back");
        Assert.Equal((true, 600.0, "Charged back"), (removed.Removed, removed.Reversed, removed.Note));
        Assert.Equal(3600, Left(s));
        Assert.Equal(0, s.Timer("")!.AddedSeconds);
        var sum = s.Summarize(new HistoryFilter());
        Assert.Equal((0, 1, 0.0), (sum.Count, sum.Removed, sum.Value));
        Assert.Empty(s.History(new HistoryFilter(IncludeRemoved: false)));

        s.RemoveEntry(entry.Id);   // twice does nothing
        Assert.Equal(3600, Left(s));

        var back = s.RestoreEntry(entry.Id);
        Assert.False(back.Removed);
        Assert.Equal(4200, Left(s));
        Assert.Equal(600, s.Summarize(new HistoryFilter()).Value);
    }

    [Fact]
    public void Changing_the_amount_rescales_the_value_and_moves_the_clock()
    {
        using var s = Service();
        Running(s, v => v.MoneyBySource["Throne"] = 30);
        Money(50, "Throne");   // really $5: a typo in a test
        var entry = s.History(new HistoryFilter()).Single();
        Assert.Equal(3600 + 1500, Left(s));

        var fixedEntry = s.ChangeEntry(entry.Id, amount: 5);
        Assert.Equal((5.0, 150.0, 50.0, 1500.0), (fixedEntry.Amount, fixedEntry.Value, fixedEntry.OriginalAmount, fixedEntry.OriginalValue));
        Assert.Equal(3750, Left(s));

        s.ChangeEntry(entry.Id, value: 300, note: "Agreed with chat");
        Assert.Equal(3900, Left(s));
        Assert.Equal("Agreed with chat", s.HistoryEntry(entry.Id)!.Note);
        Assert.Throws<ArgumentException>(() => s.ChangeEntry(s.History(new HistoryFilter()).Single().Id, amount: -1));
    }

    [Fact]
    public void Time_that_already_ran_cannot_be_taken_back()
    {
        using var s = Service();
        var t = Running(s);
        s.UpdateTimer(t.Id, x => x.Values.PerCurrencyUnit = 60);
        Money(10, "Ko-fi");                                   // +600: 4200 left
        _clock.Advance(TimeSpan.FromSeconds(4000));           // 200 left
        var removed = s.RemoveEntry(s.History(new HistoryFilter()).Single().Id);
        Assert.Equal(200, removed.Reversed);
        Assert.Contains("could be taken back", removed.Note);
        Assert.Equal(TimerState.Ended, s.Timer("")!.State);
        Assert.Equal(0, s.Summarize(new HistoryFilter()).Value);
    }

    [Fact]
    public void Entries_from_before_a_reset_are_only_marked()
    {
        using var s = Service();
        Running(s);
        Money(10, "Ko-fi");
        var old = s.History(new HistoryFilter()).Single();
        s.Reset("");
        s.Start("");
        Assert.False(s.IsCurrent(old));

        var removed = s.RemoveEntry(old.Id);
        Assert.Equal((true, 0.0), (removed.Removed, removed.Reversed));
        Assert.Equal(3600, Left(s));
        s.ChangeEntry(s.RestoreEntry(old.Id).Id, value: 60);
        Assert.Equal((3600.0, 60.0), (Left(s), s.HistoryEntry(old.Id)!.Value));
    }

    [Fact]
    public void Goal_entries_take_their_points_back()
    {
        using var s = Service();
        var g = s.AddGoal("Sub goal");
        Sub("Ann");
        Sub("Bob");
        Assert.Equal(2, s.Goal("")!.Points);
        s.RemoveEntry(s.History(new HistoryFilter(Owner: g.Id, User: "ann")).Single().Id);
        Assert.Equal(1, s.Goal("")!.Points);

        s.ResetGoal("");
        s.RemoveEntry(s.History(new HistoryFilter(User: "bob")).Single().Id);   // before the reset: points stay 0
        Assert.Equal(0, s.Goal("")!.Points);
    }

    [Fact]
    public void Test_entries_can_be_removed_together()
    {
        using var s = Service();
        Running(s);
        Money(5, "Throne", isTest: true);
        Money(5, "Throne", isTest: true);
        Money(5, "Throne", "Real person");
        Assert.Equal(3600 + 900, Left(s));

        Assert.Equal(2, s.RemoveEntries(new HistoryFilter(TestOnly: true)));
        Assert.Equal(3900, Left(s));
        Assert.Equal("Real person", s.History(new HistoryFilter(IncludeRemoved: false)).Single().User);
        Assert.Equal(0, s.RemoveEntries(new HistoryFilter(TestOnly: true)));
    }

    [Fact]
    public void Filters_by_date_and_exports_csv()
    {
        using var s = Service();
        var t = Running(s);
        s.UpdateTimer(t.Id, x => x.AddWhilePaused = true);
        Money(10, "Ko-fi", "Ann, the \"great\"");
        s.Pause("");
        _clock.Advance(TimeSpan.FromDays(3));
        Money(20, "Throne", "Bob");

        var since = _clock.GetUtcNow().AddDays(-1);
        Assert.Equal("Bob", s.History(new HistoryFilter(From: since)).Single().User);
        Assert.Equal("Ann, the \"great\"", s.History(new HistoryFilter(To: since)).Single().User);

        var csv = s.HistoryCsv(new HistoryFilter()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csv.Length);
        Assert.StartsWith("Time,Timer or goal,Kind,Name,Source", csv[0]);
        Assert.Contains("Donothon,Event,\"Ann, the \"\"great\"\"\",Ko-fi,Donation,Money,10,10,USD,600,seconds", csv[1]);
    }

    [Fact]
    public void History_is_kept_between_runs_and_dropped_with_its_timer()
    {
        string id;
        using (var s = Service())
        {
            id = Running(s).Id;
            Money(10, "Ko-fi");
            s.FlushFile();
        }
        using (var s = Service())
        {
            Assert.Single(s.History(new HistoryFilter(Owner: id)));
            Assert.Equal(60, s.Timer("")!.Values.MoneyValue("Ko-fi"));
            s.RemoveTimer(id);
            Assert.Empty(s.History(new HistoryFilter()));
        }
    }

    [Fact]
    public void Unknown_entries_are_refused()
    {
        using var s = Service();
        Assert.Throws<ArgumentException>(() => s.RemoveEntry("nope"));
        Assert.Throws<ArgumentException>(() => s.ChangeEntry("nope", value: 1));
    }

    public void Dispose()
    {
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
