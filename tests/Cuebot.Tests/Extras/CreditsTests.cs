using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Credits;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class CreditsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-credits-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();

    public CreditsTests() => _bus = new EventBus(_log);

    private CreditsCollector Collector() => new(new AppPaths(_dir), _bus, _log, _clock);

    /// <summary>A chat.activity event as the ChatHub publishes it from a module's feed item.</summary>
    internal static BotEvent Activity(FeedKind kind, string user, string title, string platform = "twitch", string amount = "", string detail = "") =>
        new(ChatHub.ActivityEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["kind"] = kind.ToString(), ["user"] = user, ["title"] = title, ["detail"] = detail, ["amount"] = amount,
        });

    private void PublishStream()
    {
        _bus.Publish(Activity(FeedKind.Follow, "Alice", "Alice followed"));
        _bus.Publish(Activity(FeedKind.Follow, "Zed", "Zed followed", "youtube"));
        _bus.Publish(Activity(FeedKind.Subscription, "Bob", "Bob subscribed (tier 1000)"));
        _bus.Publish(Activity(FeedKind.Subscription, "Rumbler", "Rumbler subscribed", "rumble", "$5.00"));
        _bus.Publish(Activity(FeedKind.GiftSubscription, "Carol", "Carol gifted 5 tier 1000 subs"));
        _bus.Publish(Activity(FeedKind.GiftSubscription, "Dave", "Dave gifted a subscription", "rumble"));
        _bus.Publish(Activity(FeedKind.Tip, "Erin", "Erin cheered 100 bits", amount: "100 bits"));
        _bus.Publish(Activity(FeedKind.Tip, "Erin", "Erin cheered 500 bits", amount: "500 bits"));
        _bus.Publish(Activity(FeedKind.Tip, "Frank", "Frank cheered 1000 bits", amount: "1000 bits"));
        _bus.Publish(Activity(FeedKind.Tip, "Gina", "Gina sent a $4.50 rant", "rumble", "$4.50"));
        _bus.Publish(Activity(FeedKind.Raid, "Hank", "Hank raided with 42 viewers"));
        _bus.Publish(Activity(FeedKind.Redemption, "Ivy", "Ivy redeemed Hydrate!", detail: "now"));
        _bus.Publish(Chat.Message("hi", "Moddy", "10", "twitch", ChatRoles.Moderator | ChatRoles.Subscriber));
        _bus.Publish(Chat.Message("again", "Moddy", "10", "twitch", ChatRoles.Moderator | ChatRoles.Subscriber));
        _bus.Publish(Chat.Message("hello", "Vee", "11", "twitch", ChatRoles.Vip));
        _bus.Publish(Chat.Message("yo", "Lurker", "12", "kick"));
        _bus.Publish(Chat.Message("it's me", "Streamer", "13", "twitch", ChatRoles.Broadcaster));
        _bus.Publish(Activity(FeedKind.Other, "Nobody", "something else"));
    }

    private static string[] Names(CreditsSnapshot s, string section) => s.Section(section)?.Entries.Select(e => e.User).ToArray() ?? [];

    [Fact]
    public void Collects_every_kind_of_activity_from_neutral_events()
    {
        using var credits = Collector();
        var changes = 0;
        credits.Changed += () => changes++;
        PublishStream();
        var s = credits.Snapshot();

        Assert.Equal(["Alice", "Zed"], Names(s, CreditsSections.Follows));
        Assert.Equal(["Bob", "Rumbler"], Names(s, CreditsSections.Subscriptions));
        Assert.Equal(["Carol", "Dave"], Names(s, CreditsSections.GiftSubscriptions));
        Assert.Equal([5.0, 1.0], s.Section(CreditsSections.GiftSubscriptions)!.Entries.Select(e => e.Amount));
        Assert.Equal(["Frank", "Erin"], Names(s, CreditsSections.Cheers));   // ranked by total bits
        var erin = s.Section(CreditsSections.Cheers)!.Entries[1];
        Assert.Equal((2, 600.0, "500 bits"), (erin.Count, erin.Amount, erin.Detail));
        Assert.Equal(4.5, Assert.Single(s.Section(CreditsSections.Tips)!.Entries).Amount);
        Assert.Equal(42, Assert.Single(s.Section(CreditsSections.Raids)!.Entries).Amount);
        Assert.Equal("Hydrate!", Assert.Single(s.Section(CreditsSections.Redemptions)!.Entries).Detail);
        Assert.Equal(["Moddy"], Names(s, CreditsSections.Moderators));
        Assert.Equal(["Vee"], Names(s, CreditsSections.Vips));
        Assert.Equal(["Moddy"], Names(s, CreditsSections.Subscribers));
        Assert.Equal(["Moddy", "Vee", "Lurker"], Names(s, CreditsSections.Chatters));   // broadcaster left out
        Assert.Equal(CreditsSections.Order, s.Sections.Select(x => x.Id));
        Assert.Equal(_clock.Now, s.StartedAt);
        Assert.Equal(15, changes);   // repeated chat from the same user changes nothing
    }

    [Fact]
    public void Resets_on_stream_online_unless_turned_off_and_survives_restart()
    {
        using (var credits = Collector())
        {
            PublishStream();
            credits.Flush();
        }
        using (var credits = Collector())
        {
            Assert.Equal(["Alice", "Zed"], Names(credits.Snapshot(), CreditsSections.Follows));
            credits.UpdateSettings(s => s.ResetOnStreamOnline = false);
            _bus.Publish(Activity(FeedKind.StreamOnline, "Streamer", "Stream went live"));
            Assert.NotEmpty(credits.Snapshot().Sections);

            credits.UpdateSettings(s => s.ResetOnStreamOnline = true);
            _bus.Publish(Activity(FeedKind.StreamOnline, "Streamer", "Stream went live"));
            Assert.Empty(credits.Snapshot().Sections);
            Assert.Null(credits.Snapshot().StartedAt);
        }
    }

    [Fact]
    public void Auto_reset_after_idle_hours_and_settings_filters()
    {
        using var credits = Collector();
        credits.UpdateSettings(s =>
        {
            s.AutoResetHours = 2;
            s.DisabledSections.Add(CreditsSections.Chatters);
            s.IgnoredUsers.Add("StreamElements");
        });
        _bus.Publish(Chat.Message("hi", "Someone", "1"));
        _bus.Publish(Activity(FeedKind.Follow, "StreamElements", "followed"));
        Assert.Empty(credits.Snapshot().Sections);

        _bus.Publish(Activity(FeedKind.Follow, "Alice", "Alice followed"));
        _clock.Advance(3600);
        credits.Tick(_clock.Now);
        Assert.NotEmpty(credits.Snapshot().Sections);
        _clock.Advance(3600);
        credits.Tick(_clock.Now);
        Assert.Empty(credits.Snapshot().Sections);
    }

    [Fact]
    public void Custom_credits_test_data_and_overlay_json()
    {
        using var credits = Collector();
        credits.AddCustom("Special thanks", "@Mom", amount: 0, detail: "for the snacks");
        credits.AddCustom(CreditsSections.Tips, "Paypal Pete", "paypal", 20, "$20");
        Assert.Throws<ArgumentException>(() => credits.AddCustom("", "x"));
        var s = credits.Snapshot();
        Assert.Equal([CreditsSections.Tips, "Special thanks"], s.Sections.Select(x => x.Id));
        Assert.Equal("Mom", s.Section("special thanks")!.Entries[0].User);

        var test = credits.Test();
        Assert.True(test.IsTest);
        Assert.Equal(CreditsSections.Order, test.Sections.Select(x => x.Id));
        Assert.Equal(2, credits.Snapshot().Sections.Count);   // the real credits are untouched

        using var doc = JsonDocument.Parse(CreditsJson.Serialize(test));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.GetProperty("isTest").GetBoolean());
        var cheers = root.GetProperty("sections").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "cheers");
        Assert.Equal("Cheers", cheers.GetProperty("title").GetString());
        var top = cheers.GetProperty("entries")[0];
        Assert.Equal("BitBaron", top.GetProperty("user").GetString());
        Assert.Equal(1000, top.GetProperty("amount").GetDouble());
        Assert.Equal(["user", "platform", "count", "amount", "detail"], top.EnumerateObject().Select(p => p.Name));

        credits.Reset();
        Assert.Empty(credits.Snapshot().Sections);
    }

    [Fact]
    public async Task Sub_actions_reset_add_and_get()
    {
        using var credits = Collector();
        PublishStream();
        var got = await SubActionHarness.RunAsync(new GetCreditsSubAction(credits), []);
        Assert.Equal("Alice, Zed", got.Args["creditsFollows"]);
        Assert.Equal(2, got.Args["creditsFollowsCount"]);
        Assert.Equal("Frank, Erin", got.Args["creditsCheers"]);
        Assert.Equal(17, got.Args["creditsTotal"]);
        Assert.Contains("\"sections\"", (string)got.Args["credits"]!);

        var added = await SubActionHarness.RunAsync(new AddCustomCreditSubAction(credits), new() { ["section"] = "Special thanks", ["amount"] = "3" },
            new() { ["user"] = "Helper", ["platform"] = "twitch" });
        Assert.True(added.Success);
        var again = await SubActionHarness.RunAsync(new GetCreditsSubAction(credits), []);
        Assert.Equal("Helper", again.Args["creditsSpecialthanks"]);

        var noUser = await SubActionHarness.RunAsync(new AddCustomCreditSubAction(credits), []);
        Assert.False(noUser.Success);

        await SubActionHarness.RunAsync(new ResetCreditsSubAction(credits), []);
        var empty = await SubActionHarness.RunAsync(new GetCreditsSubAction(credits), []);
        Assert.Equal("", empty.Args["creditsFollows"]);
        Assert.Equal(0, empty.Args["creditsTotal"]);

        var sample = await SubActionHarness.RunAsync(new GetCreditsSubAction(credits), new() { ["test"] = "true" });
        Assert.Equal("BitBaron, SparkleSloth", sample.Args["creditsCheers"]);
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
