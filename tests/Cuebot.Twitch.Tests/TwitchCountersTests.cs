using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>Sub counter, reward counters and the latest subscriber, as fed by the event router.</summary>
public sealed class TwitchCountersTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly TwitchSettings _settings = new();
    private readonly TwitchCounters _counters;
    private readonly TwitchEventRouter _router;
    private int _saves;

    public TwitchCountersTests()
    {
        _counters = new TwitchCounters(() => _settings, () => _saves++);
        _router = new TwitchEventRouter(_time, () => "1337", () => "", new SentMessageTracker(), counters: _counters);
    }

    private IReadOnlyList<BotEvent> Route(string type, string json) => _router.Route(type, JsonSerializer.Deserialize<JsonElement>(json));

    private const string B = """ "broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer" """;

    private static string Sub(string userId, bool gift = false) =>
        "{" + B + $",\"user_id\":\"{userId}\",\"user_login\":\"u{userId}\",\"user_name\":\"U{userId}\",\"tier\":\"1000\",\"is_gift\":{(gift ? "true" : "false")}" + "}";

    private static string Resub(string userId) =>
        "{" + B + $",\"user_id\":\"{userId}\",\"user_login\":\"u{userId}\",\"user_name\":\"U{userId}\",\"tier\":\"2000\",\"message\":{{\"text\":\"hi\"}},\"cumulative_months\":3" + "}";

    private static string Redemption(string rewardId, string userId) =>
        "{" + B + $",\"id\":\"red-{userId}\",\"user_id\":\"{userId}\",\"user_login\":\"u\",\"user_name\":\"U\",\"user_input\":\"\",\"status\":\"unfulfilled\"," +
        $"\"reward\":{{\"id\":\"{rewardId}\",\"title\":\"Hydrate\",\"cost\":100,\"prompt\":\"\"}}" + "}";

    [Fact]
    public void Subs_count_up_and_roll_over_with_an_event()
    {
        _settings.SubCounter.Rollover = 3;

        Assert.Equal(1, Assert.Single(Route("channel.subscribe", Sub("1"))).Args["subCounter"]);
        Route("channel.subscription.message", Resub("2"));
        var third = Route("channel.subscribe", Sub("3", gift: true));

        Assert.Equal([TwitchEventTypes.Sub, TwitchEventTypes.SubCounterRollover], third.Select(e => e.Type));
        Assert.Equal(0, third[0].Args["subCounter"]);
        var rollover = third[1];
        Assert.Equal(3, rollover.Args["rollover"]);
        Assert.Equal(1, rollover.Args["rolloverCount"]);
        Assert.Equal(0, rollover.Args["subCounter"]);
        Assert.Equal(new SubCounterState(0, 3, 1), _counters.SubCounter);
        Assert.True(_saves > 0);
    }

    [Fact]
    public void Resubs_and_gifts_can_be_left_out_of_the_counter()
    {
        _settings.SubCounter.CountResubs = false;
        _settings.SubCounter.CountGiftedSubs = false;

        var resub = Assert.Single(Route("channel.subscription.message", Resub("2")));
        Assert.False(resub.Args.ContainsKey("subCounter"));
        Route("channel.subscribe", Sub("3", gift: true));
        Route("channel.subscribe", Sub("4"));

        Assert.Equal(1, _counters.SubCounter.Count);
    }

    [Fact]
    public void The_latest_subscriber_is_remembered()
    {
        Route("channel.subscribe", Sub("1"));
        Route("channel.subscription.message", Resub("2"));

        var latest = _counters.LatestSubscriber;
        Assert.Equal("2", latest.UserId);
        Assert.Equal("U2", latest.UserName);
        Assert.Equal("u2", latest.UserLogin);
        Assert.Equal("2", latest.Tier);
        Assert.Equal(_time.GetUtcNow(), latest.At);
    }

    [Fact]
    public void Redemptions_carry_reward_and_user_counters_which_reset_when_going_live_unless_persisted()
    {
        Route("channel.channel_points_custom_reward_redemption.add", Redemption("r1", "5"));
        Route("channel.channel_points_custom_reward_redemption.add", Redemption("r1", "6"));
        var third = Assert.Single(Route("channel.channel_points_custom_reward_redemption.add", Redemption("r1", "5")));
        Assert.Equal(3, third.Args["counter"]);
        Assert.Equal(2, third.Args["userCounter"]);
        Assert.Equal("Hydrate", third.Args["rewardTitle"]);

        Route("channel.channel_points_custom_reward_redemption.add", Redemption("r2", "5"));
        _counters.SetPersistence("r2", counter: true, userCounter: false);

        Assert.Equal(TwitchEventTypes.StreamOnline, Assert.Single(Route("stream.online", "{" + B + ",\"type\":\"live\"}")).Type);
        Assert.Equal((0, 0), _counters.GetRewardCounter("r1", "5"));
        Assert.Equal((1, 0), _counters.GetRewardCounter("r2", "5"));
    }

    [Fact]
    public void Reward_groups()
    {
        _counters.SetGroup("r1", " Drinks ");
        _counters.SetGroup("r2", "drinks");
        _counters.SetGroup("r3", "Games");

        Assert.Equal("Drinks", _counters.GetGroup("r1"));
        Assert.Equal(["r1", "r2"], _counters.RewardsInGroup("DRINKS").Order());
        Assert.Equal(2, _counters.Groups.Count);
        Assert.Throws<ArgumentException>(() => _counters.SetGroup(" ", "x"));
    }

    [Fact]
    public void Counters_survive_a_settings_round_trip()
    {
        _counters.SetSubCount(7, 2);
        _counters.SetGroup("r1", "Drinks");
        _counters.CountRedemption("r1", "5");

        var json = JsonSerializer.Serialize(_settings, Cuebot.Core.Storage.Json.Options);
        var copy = JsonSerializer.Deserialize<TwitchSettings>(json, Cuebot.Core.Storage.Json.Options)!;
        var restored = new TwitchCounters(() => copy, () => { });

        Assert.Equal(new SubCounterState(7, 0, 2), restored.SubCounter);
        Assert.Equal("Drinks", restored.GetGroup("r1"));
        Assert.Equal((1, 1), restored.GetRewardCounter("r1", "5"));
    }
}

public sealed class UpcomingAdWarningsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TwitchAdSchedule Schedule(DateTimeOffset? next) => new(next, null, 90, 0, 2, null);

    [Fact]
    public void Warns_once_per_minute_from_five_minutes_before_the_ad()
    {
        var warnings = new UpcomingAdWarnings();
        var next = Now.AddMinutes(5).AddSeconds(30);

        Assert.Null(warnings.Check(Schedule(next), Now));
        var five = warnings.Check(Schedule(next), Now.AddSeconds(45));
        Assert.NotNull(five);
        Assert.Equal(TwitchEventTypes.UpcomingAd, five.Type);
        Assert.Equal(5, five.Args["minutes"]);
        Assert.Equal(90, five.Args["adLength"]);
        Assert.Equal(2, five.Args["snoozesLeft"]);
        Assert.Null(warnings.Check(Schedule(next), Now.AddSeconds(75)));
        Assert.Equal(4, warnings.Check(Schedule(next), Now.AddSeconds(105))!.Args["minutes"]);
        Assert.Equal(1, warnings.Check(Schedule(next), next.AddSeconds(-10))!.Args["minutes"]);
        Assert.Null(warnings.Check(Schedule(next), next.AddSeconds(1)));
        Assert.Null(warnings.Check(Schedule(null), Now));
    }

    [Fact]
    public void A_snoozed_ad_is_announced_again()
    {
        var warnings = new UpcomingAdWarnings();
        Assert.Equal(2, warnings.Check(Schedule(Now.AddMinutes(2)), Now)!.Args["minutes"]);
        Assert.Equal(5, warnings.Check(Schedule(Now.AddMinutes(5)), Now)!.Args["minutes"]);
    }
}

public sealed class EventSubScopeTests
{
    [Fact]
    public void Shared_chat_moderation_needs_every_moderate_scope()
    {
        var moderate = EventSubSubscriptions.All.Single(s => s.Type == "channel.moderate");
        Assert.Equal("2", moderate.Version);
        var all = TwitchScopes.All.ToHashSet(StringComparer.Ordinal);
        Assert.True(moderate.IsAuthorized(all));

        all.Remove(TwitchScopes.ReadVips);
        Assert.Equal(TwitchScopes.ReadVips, moderate.MissingScope(all));
        all.Add(TwitchScopes.ReadVips);
        all.Remove(TwitchScopes.ManageWarnings);
        Assert.Equal(TwitchScopes.ManageWarnings, moderate.MissingScope(all));
        all.Add("moderator:read:warnings");
        Assert.Null(moderate.MissingScope(all));
    }

    [Fact]
    public void Guest_star_subscriptions_use_the_beta_version()
    {
        var guestStar = EventSubSubscriptions.All.Where(s => s.Type.StartsWith("channel.guest_star", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, guestStar.Length);
        Assert.All(guestStar, s => Assert.Equal("beta", s.Version));
        Assert.All(guestStar, s => Assert.True(s.IsAuthorized(new HashSet<string> { TwitchScopes.ManageGuestStar })));
        Assert.Equal("moderator_user_id", guestStar[0].BuildCondition("1337").Keys.Last());
    }

    [Fact]
    public void Every_subscription_is_authorized_by_the_full_profile() =>
        Assert.All(EventSubSubscriptions.All, s => Assert.True(s.IsAuthorized(TwitchScopes.All.ToHashSet(StringComparer.Ordinal)), s.Type));
}
