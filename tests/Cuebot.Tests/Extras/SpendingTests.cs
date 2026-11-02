using System.Net;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Spending;
using Cuebot.Tests.ChatExtras;

namespace Cuebot.Tests.Extras;

public sealed class SpendingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-spending-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly UtcClock _clock = new();
    private readonly FakeRates _rates = new();

    public SpendingTests()
    {
        _bus = new EventBus(_log);
        // 1 USD = 0.9 EUR = 0.8 GBP; 1 EUR = 1.1 USD.
        _rates.Tables["USD"] = new ExchangeRateTable("USD", "2026-11-01", "Test", new Dictionary<string, double> { ["USD"] = 1, ["EUR"] = 0.9, ["GBP"] = 0.8 });
        _rates.Tables["EUR"] = new ExchangeRateTable("EUR", "2026-11-01", "Test", new Dictionary<string, double> { ["EUR"] = 1, ["USD"] = 1.1, ["GBP"] = 0.88 });
    }

    private SpendingLedger Ledger() => new(new AppPaths(_dir), null, _bus, _log, _rates, _clock);

    private FeedItem Item(string platform, string user, FeedSpend? spend, FeedKind kind = FeedKind.Tip) =>
        new(platform, kind, user, "", _clock.GetUtcNow()) { Spend = spend };

    private static FeedSpend Money(double amount, string currency, string label = "Tip") => FeedSpend.Money(amount, currency, label)!;

    private void Live(SpendingLedger ledger, string platform = "Twitch") => ledger.Record(Item(platform, "", null, FeedKind.StreamOnline));

    private void Offline(SpendingLedger ledger, string platform = "Twitch") => ledger.Record(Item(platform, "", null, FeedKind.StreamOffline));

    private static async Task Settled(SpendingLedger ledger) => await ledger.RatesTask.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Values_other_currencies_in_the_chosen_currency_with_the_rates_locked_at_the_start_of_the_stream()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("YouTube", "Euro", Money(9, "EUR", "Super Chat")));
        ledger.Record(Item("Rumble", "Dollar", Money(5, "USD", "Rant")));
        ledger.Record(Item("Streamlabs", "Pound", Money(8, "gbp", "Donation")));

        var list = ledger.Top(SpendingPeriod.Stream);
        Assert.Equal("USD", list.Currency);
        Assert.Equal(["Euro", "Pound", "Dollar"], list.Spenders.Select(s => s.User));
        Assert.Equal([10.0, 10.0, 5.0], list.Spenders.Select(s => s.Total));   // 9 EUR / 0.9, 8 GBP / 0.8; ties keep arrival order
        Assert.Equal(25, list.Total);

        // The next stream's rates are different; the first stream keeps its values.
        _rates.Tables["USD"] = new ExchangeRateTable("USD", "2026-11-02", "Test", new Dictionary<string, double> { ["USD"] = 1, ["EUR"] = 0.5 });
        Offline(ledger);
        _clock.Advance(TimeSpan.FromHours(1));
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("YouTube", "Euro", Money(9, "EUR", "Super Chat")));
        Assert.Equal(18, ledger.Top(SpendingPeriod.Stream).Spenders.Single().Total);
        Assert.Equal(28, ledger.Top(SpendingPeriod.AllTime, "YouTube").Spenders.Single().Total);
        Assert.Equal("2026-11-02", ledger.CurrentSession!.RatesDate);
    }

    [Fact]
    public async Task Prices_bits_kicks_jewels_and_subscriptions_from_the_settings_and_converts_them()
    {
        using var ledger = Ledger();
        ledger.UpdateSettings(s => s.Currency = "eur");
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Cheery", FeedSpend.Units(SpendUnit.Bits, 1000, "Cheer")));
        ledger.Record(Item("Twitch", "Gifter", FeedSpend.Units(SpendUnit.GiftSubscription, 5, "Gift subs", "1")));
        ledger.Record(Item("Twitch", "Tier3", FeedSpend.Units(SpendUnit.Subscription, 1, "Subscription", "3")));
        ledger.Record(Item("Kick", "Kicker", FeedSpend.Units(SpendUnit.Kicks, 500, "Kicks")));
        ledger.Record(Item("YouTube", "Member", FeedSpend.Units(SpendUnit.Subscription, 1, "Membership", "Gold")));

        var totals = ledger.Top(SpendingPeriod.Stream).Spenders.ToDictionary(s => s.User, s => s.Total);
        Assert.Equal("EUR", ledger.Settings.Currency);
        Assert.Equal(Math.Round(24.95 / 1.1, 2), totals["Gifter"]);
        Assert.Equal(Math.Round(24.99 / 1.1, 2), totals["Tier3"]);
        Assert.Equal(Math.Round(10 / 1.1, 2), totals["Cheery"]);
        Assert.Equal(Math.Round(5 / 1.1, 2), totals["Kicker"]);
        Assert.Equal(Math.Round(4.99 / 1.1, 2), totals["Member"]);
    }

    [Fact]
    public async Task Lists_per_platform_and_across_platforms_with_a_breakdown()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Sam", FeedSpend.Units(SpendUnit.Bits, 500, "Cheer")));
        ledger.Record(Item("Twitch", "sam", FeedSpend.Units(SpendUnit.Subscription, 1, "Subscription", "1")));
        ledger.Record(Item("YouTube", "Sam", Money(20, "USD", "Super Chat")));
        ledger.Record(Item("Twitch", "Alex", FeedSpend.Units(SpendUnit.Bits, 800, "Cheer")));

        var twitch = ledger.Top(SpendingPeriod.Stream, "twitch");
        Assert.Equal(["sam", "Alex"], twitch.Spenders.Select(s => s.User));
        Assert.Equal(9.99, twitch.Spenders[0].Total);
        Assert.Equal(2, twitch.Spenders[0].Count);
        Assert.Equal(5.0, twitch.Spenders[0].Breakdown["Cheer"]);
        Assert.Equal(4.99, twitch.Spenders[0].Breakdown["Subscription"]);

        var all = ledger.Top(SpendingPeriod.Stream, "", 2);
        Assert.Equal([("Sam", "YouTube"), ("sam", "Twitch")], all.Spenders.Select(s => (s.User, s.Platform)));
        Assert.Equal(37.99, all.Total);
        Assert.Equal(["Twitch", "YouTube"], ledger.Platforms());
    }

    [Fact]
    public async Task Calendar_periods_follow_local_days_weeks_months_and_years()
    {
        _clock.Now = new DateTimeOffset(2026, 12, 30, 20, 0, 0, TimeSpan.Zero);   // a Wednesday
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Old", Money(1, "USD")));
        _clock.Now = new DateTimeOffset(2027, 1, 1, 10, 0, 0, TimeSpan.Zero);   // Friday, a new year and month
        ledger.Record(Item("Twitch", "New", Money(2, "USD")));

        Assert.Equal(["New"], ledger.Top(SpendingPeriod.Day).Spenders.Select(s => s.User));
        Assert.Equal(["New", "Old"], ledger.Top(SpendingPeriod.Week).Spenders.Select(s => s.User));   // weeks start on Monday
        Assert.Equal(["New"], ledger.Top(SpendingPeriod.Month).Spenders.Select(s => s.User));
        Assert.Equal(["New"], ledger.Top(SpendingPeriod.Year).Spenders.Select(s => s.User));
        Assert.Equal(["New", "Old"], ledger.Top(SpendingPeriod.AllTime).Spenders.Select(s => s.User));

        ledger.UpdateSettings(s => s.WeekStart = DayOfWeek.Friday);
        Assert.Equal(["New"], ledger.Top(SpendingPeriod.Week).Spenders.Select(s => s.User));
    }

    [Fact]
    public async Task Leaves_out_test_events_charity_and_ignored_names()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Tester", Money(100, "USD")) with { IsTest = true });
        ledger.Record(Item("Twitch", "Kind", FeedSpend.Money(50, "USD", "Charity", charity: true)));
        ledger.Record(Item("Twitch", "Anonymous", FeedSpend.Units(SpendUnit.GiftSubscription, 10, "Gift subs", "1")));
        Assert.Empty(ledger.Top(SpendingPeriod.Stream).Spenders);

        ledger.UpdateSettings(s => s.IncludeCharity = true);
        ledger.Record(Item("Twitch", "Kind", FeedSpend.Money(50, "USD", "Charity", charity: true)));
        Assert.Equal(["Kind"], ledger.Top(SpendingPeriod.Stream).Spenders.Select(s => s.User));
    }

    [Fact]
    public async Task Purchases_wait_for_the_rates_and_trigger_once_they_are_valued()
    {
        var gate = new TaskCompletionSource();
        _rates.Gate = gate.Task;
        var recorded = new List<BotEvent>();
        using var sub = _bus.Subscribe(e => { if (e.Type == SpendingEvents.Recorded) lock (recorded) recorded.Add(e); });
        using var ledger = Ledger();
        Live(ledger);
        ledger.Record(Item("YouTube", "Early", Money(9, "EUR")));
        ledger.Record(Item("Rumble", "Same", Money(3, "USD")));   // already in the chosen currency

        var waiting = ledger.Top(SpendingPeriod.Stream);
        Assert.Equal(1, waiting.Pending);
        Assert.Equal(["Same"], waiting.Spenders.Select(s => s.User));

        gate.SetResult();
        await Settled(ledger);
        Assert.Equal(["Early", "Same"], ledger.Top(SpendingPeriod.Stream).Spenders.Select(s => s.User));
        Assert.Equal(0, ledger.Top(SpendingPeriod.Stream).Pending);
        lock (recorded) Assert.Equal(["Same", "Early"], recorded.Select(e => e.Args.GetValueOrDefault("user")));
    }

    [Fact]
    public async Task Reuses_the_last_streams_rates_when_no_service_answers()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        Offline(ledger);
        _rates.Tables.Clear();
        _clock.Advance(TimeSpan.FromHours(2));
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("YouTube", "Euro", Money(9, "EUR")));
        Assert.Equal(10, ledger.Top(SpendingPeriod.Stream).Spenders.Single().Total);
        Assert.Contains("reused", ledger.CurrentSession!.RatesSource);
    }

    [Fact]
    public async Task Streams_continue_across_a_short_drop_and_start_again_after_going_offline()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        var first = ledger.CurrentSession!.Id;
        ledger.Record(Item("Twitch", "A", Money(1, "USD")));
        Offline(ledger);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Live(ledger);
        Assert.Equal(first, ledger.CurrentSession!.Id);

        Offline(ledger);
        _clock.Advance(TimeSpan.FromMinutes(30));
        ledger.Record(Item("Twitch", "B", Money(1, "USD")));   // a purchase after the stream ended starts the next one
        await Settled(ledger);
        Assert.NotEqual(first, ledger.CurrentSession!.Id);
        Assert.Equal(["B"], ledger.Top(SpendingPeriod.Stream).Spenders.Select(s => s.User));

        var manual = ledger.StartSession();
        await Settled(ledger);
        Assert.Equal(manual.Id, ledger.CurrentSession!.Id);
        Assert.Empty(ledger.Top(SpendingPeriod.Stream).Spenders);
        Assert.Equal(2, ledger.Top(SpendingPeriod.AllTime).Spenders.Count);
    }

    [Fact]
    public async Task Announces_a_new_top_spender_overall_and_per_platform()
    {
        var changes = new List<BotEvent>();
        using var sub = _bus.Subscribe(e => { if (e.Type == SpendingEvents.TopSpenderChanged) lock (changes) changes.Add(e); });
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "First", Money(5, "USD")));
        ledger.Record(Item("YouTube", "Second", Money(3, "USD")));
        ledger.Record(Item("YouTube", "Third", Money(10, "USD")));

        lock (changes)
        {
            Assert.Equal(
                [("All platforms", "First", ""), ("Twitch", "First", ""), ("YouTube", "Second", ""), ("All platforms", "Third", "First"), ("YouTube", "Third", "Second")],
                changes.Select(e => ((string)e.Args["scope"]!, (string)e.Args["user"]!, (string)e.Args["previousUser"]!)));
        }
    }

    [Fact]
    public async Task Remembers_everything_across_restarts_and_removes_a_persons_purchases()
    {
        using (var ledger = Ledger())
        {
            Live(ledger);
            await Settled(ledger);
            ledger.Record(Item("Twitch", "Keep", Money(5, "USD")));
            ledger.AddManual("", "Cash", 7, "EUR", "Cash gift");
            ledger.Flush();
        }
        using var again = Ledger();
        Assert.Equal([("Cash", "Manual", 7.78), ("Keep", "Twitch", 5.0)], again.Top(SpendingPeriod.Stream).Spenders.Select(s => (s.User, s.Platform, s.Total)));
        Assert.Equal(1, again.RemoveUser("", "cash"));
        Assert.Equal(["Keep"], again.Top(SpendingPeriod.AllTime).Spenders.Select(s => s.User));
        Assert.Throws<ArgumentException>(() => again.AddManual("Twitch", "Nobody", 0));
    }

    [Fact]
    public async Task Shows_older_streams_in_a_newly_chosen_currency_with_their_own_rates()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Dollar", Money(10, "USD")));
        ledger.UpdateSettings(s => s.Currency = "EUR");
        Assert.Equal(9, ledger.Top(SpendingPeriod.Stream).Spenders.Single().Total);   // 10 USD × the stream's 0.9
        Assert.Equal("EUR", ledger.Top(SpendingPeriod.Stream).Currency);
    }

    [Theory]
    [InlineData("This stream", SpendingPeriod.Stream)]
    [InlineData("today", SpendingPeriod.Day)]
    [InlineData("This week", SpendingPeriod.Week)]
    [InlineData("monthly", SpendingPeriod.Month)]
    [InlineData("year", SpendingPeriod.Year)]
    [InlineData("All time", SpendingPeriod.AllTime)]
    [InlineData("nonsense", SpendingPeriod.Stream)]
    public void Reads_period_names(string text, SpendingPeriod period) => Assert.Equal(period, SpendingPeriods.Parse(text));

    [Fact]
    public async Task Fetches_rates_from_frankfurter_and_fills_the_rest_from_currency_api()
    {
        var http = new FakeHttp();
        http.Json("https://api.frankfurter.dev/v1/latest?base=USD", """{"amount":1.0,"base":"USD","date":"2026-10-30","rates":{"EUR":0.92,"GBP":0.79}}""");
        http.Json("https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.json",
            """{"date":"2026-11-01","usd":{"eur":0.95,"php":58.1,"btc":0.00001,"doge":5.0}}""");
        var table = await new WebExchangeRateSource(http.Client()).FetchAsync("usd", CancellationToken.None);
        Assert.NotNull(table);
        Assert.Equal("USD", table.Base);
        Assert.Equal("2026-10-30", table.Date);
        Assert.Equal(0.92, table.Rates["EUR"]);   // the central bank's rate wins
        Assert.Equal(58.1, table.Rates["PHP"]);
        Assert.False(table.Rates.ContainsKey("DOGE"));
        Assert.Contains("Frankfurter", table.Source);

        // Frankfurter down, jsDelivr down: the Cloudflare mirror answers.
        var mirror = new FakeHttp();
        mirror.Json("https://latest.currency-api.pages.dev/v1/currencies/php.json", """{"date":"2026-11-01","php":{"usd":0.0172}}""");
        var only = await new WebExchangeRateSource(mirror.Client()).FetchAsync("PHP", CancellationToken.None);
        Assert.Equal(0.0172, only!.Rates["USD"]);
        Assert.Equal(WebExchangeRateSource.CurrencyApiName, only.Source);

        Assert.Null(await new WebExchangeRateSource(new FakeHttp().Client()).FetchAsync("USD", CancellationToken.None));
    }

    [Fact]
    public async Task Puts_the_lists_in_the_overlay_session_data()
    {
        using var ledger = Ledger();
        Live(ledger);
        await Settled(ledger);
        ledger.Record(Item("Twitch", "Big", Money(12.5, "USD")));
        var data = new System.Text.Json.Nodes.JsonObject();
        new SpendingSessionData(ledger).AddTo(data);
        var top = data["cuebot-top-spenders"]!;
        Assert.Equal("USD", (string)top["currency"]!);
        var spender = top["periods"]!["week"]!["twitch"]!["spenders"]![0]!;
        Assert.Equal("Big", (string)spender["name"]!);
        Assert.Equal("$12.50", (string)spender["formatted"]!);
        Assert.Equal("$12.50", (string)top["periods"]!["stream"]!["all"]!["formattedTotal"]!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class UtcClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeRates : IExchangeRateSource
    {
        public Dictionary<string, ExchangeRateTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task Gate { get; set; } = Task.CompletedTask;

        public async Task<ExchangeRateTable?> FetchAsync(string baseCurrency, CancellationToken ct)
        {
            await Gate;
            return Tables.GetValueOrDefault(baseCurrency);
        }
    }
}
