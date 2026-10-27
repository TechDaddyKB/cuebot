using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Donations.Tests;

public sealed class TriggerTests
{
    private static ITriggerType Trigger(ITriggerProvider provider, string id) => provider.GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    private static BotEvent Donation(double amount, string currency, bool isTest = false) =>
        new(StreamlabsEventTypes.Donation, new Dictionary<string, object?> { ["amount"] = amount, ["currency"] = currency, ["isTest"] = isTest });

    [Fact]
    public void Every_trigger_listens_to_its_own_event_type_in_the_service_category()
    {
        foreach (var (provider, category, prefix) in new (ITriggerProvider, string, string)[]
                 { (new StreamlabsTriggers(), "Streamlabs", "streamlabs."), (new StreamElementsTriggers(), "StreamElements", "streamelements.") })
        {
            var triggers = provider.GetTriggers().ToArray();
            Assert.All(triggers, t =>
            {
                Assert.Equal(category, t.Category);
                Assert.StartsWith(prefix, t.TypeId);
                Assert.Equal(t.TypeId, Assert.Single(t.EventTypes));
            });
            Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        }
        Assert.Equal(13, new StreamlabsTriggers().GetTriggers().Count());
        Assert.Equal(15, new StreamElementsTriggers().GetTriggers().Count());
    }

    [Fact]
    public void Donation_filters_on_minimum_maximum_currency_and_tests()
    {
        var trigger = Trigger(new StreamlabsTriggers(), StreamlabsEventTypes.Donation);
        Assert.True(Matches(trigger, Donation(5, "USD")));
        Assert.False(Matches(trigger, Donation(4.99, "USD"), new() { ["minAmount"] = "5" }));
        Assert.True(Matches(trigger, Donation(5, "USD"), new() { ["minAmount"] = "5" }));
        Assert.False(Matches(trigger, Donation(101, "USD"), new() { ["maxAmount"] = "100" }));
        Assert.True(Matches(trigger, Donation(5, "eur"), new() { ["currency"] = "USD, EUR" }));
        Assert.False(Matches(trigger, Donation(5, "GBP"), new() { ["currency"] = "USD, EUR" }));
        Assert.True(Matches(trigger, Donation(5, "USD", isTest: true)));
        Assert.False(Matches(trigger, Donation(5, "USD", isTest: true), new() { ["includeTests"] = "false" }));
    }

    [Fact]
    public void Relayed_and_redemption_filters()
    {
        var bits = Trigger(new StreamlabsTriggers(), StreamlabsEventTypes.Bits);
        var cheer = new BotEvent(StreamlabsEventTypes.Bits, new Dictionary<string, object?> { ["bits"] = 100 });
        Assert.True(Matches(bits, cheer, new() { ["minBits"] = "100" }));
        Assert.False(Matches(bits, cheer, new() { ["minBits"] = "101" }));

        var redemption = Trigger(new StreamElementsTriggers(), StreamElementsEventTypes.Redemption);
        var hydrate = new BotEvent(StreamElementsEventTypes.Redemption, new Dictionary<string, object?> { ["item"] = "Hydrate!" });
        Assert.True(Matches(redemption, hydrate));
        Assert.True(Matches(redemption, hydrate, new() { ["item"] = "hydrate!" }));
        Assert.False(Matches(redemption, hydrate, new() { ["item"] = "Pushups" }));

        var other = Trigger(new StreamElementsTriggers(), StreamElementsEventTypes.Activity);
        var hype = new BotEvent(StreamElementsEventTypes.Activity, new Dictionary<string, object?> { ["activityType"] = "hypetrainStart" });
        Assert.True(Matches(other, hype, new() { ["activityType"] = "hypetrainStart" }));
        Assert.False(Matches(other, hype, new() { ["activityType"] = "raid" }));
    }
}

public sealed class AdapterTests
{
    private static BotEvent Mapped(DonationEvent e) => new(e.Type, e.Args);

    [Fact]
    public void Tips_become_feed_tips_with_formatted_amount_and_message()
    {
        var adapter = new StreamlabsChatAdapter();
        var item = adapter.ToFeedItem(Mapped(StreamlabsMappingTests.One(StreamlabsMappingTests.Donation)));
        Assert.NotNull(item);
        Assert.Equal(("Streamlabs", FeedKind.Tip, "test", "test donated $13.37", "$13.37", "test donation"),
            (item.Platform, item.Kind, item.User, item.Title, item.Amount, item.Detail));

        var se = new StreamElementsChatAdapter();
        var tip = se.ToFeedItem(Mapped(StreamElementsEventMapper.Map(StreamlabsMappingTests.Parse(StreamElementsMappingTests.Tip))!));
        Assert.Equal(("StreamElements", FeedKind.Tip, "Styler tipped $4.20", "$4.20"), (tip!.Platform, tip.Kind, tip.Title, tip.Amount));

        Assert.Null(adapter.ToChatMessage(Mapped(StreamlabsMappingTests.One(StreamlabsMappingTests.Donation))));
    }

    [Fact]
    public void Each_adapter_handles_only_its_own_service_and_skips_connection_events()
    {
        var donation = Mapped(StreamlabsMappingTests.One(StreamlabsMappingTests.Donation));
        Assert.Null(new StreamElementsChatAdapter().ToFeedItem(donation));
        Assert.Null(new StreamlabsChatAdapter().ToFeedItem(new BotEvent(StreamlabsEventTypes.Connected)));
    }

    [Theory]
    [InlineData(StreamlabsEventTypes.Merch, FeedKind.Other, "Kevin bought Hoodie")]
    [InlineData(StreamlabsEventTypes.LoyaltyStoreRedemption, FeedKind.Redemption, "Kevin redeemed Hoodie")]
    [InlineData(StreamlabsEventTypes.CharityDonation, FeedKind.Tip, "Kevin donated $5.00 to charity")]
    [InlineData(StreamlabsEventTypes.Follow, FeedKind.Follow, "Kevin followed")]
    [InlineData(StreamlabsEventTypes.Raid, FeedKind.Raid, "Kevin raided with 30 viewers")]
    [InlineData(StreamlabsEventTypes.Bits, FeedKind.Tip, "Kevin cheered 100 bits")]
    [InlineData(StreamElementsEventTypes.Subscriber, FeedKind.Subscription, "Kevin subscribed")]
    [InlineData(StreamElementsEventTypes.CommunityGift, FeedKind.GiftSubscription, "Kevin gifted 5 subs")]
    public void Other_events_have_feed_titles(string type, FeedKind kind, string title)
    {
        var e = new BotEvent(type, new Dictionary<string, object?>
        {
            ["service"] = "S", ["user"] = "Kevin", ["product"] = "Hoodie", ["formattedAmount"] = "$5.00", ["viewers"] = 30, ["bits"] = 100, ["gift"] = 5,
        });
        var item = DonationChatAdapter.Describe(e);
        Assert.Equal((kind, title), (item!.Kind, item.Title));
    }
}

public sealed class HelperTests
{
    [Theory]
    [InlineData(5, "USD", "$5.00")]
    [InlineData(1234.5, "eur", "€1,234.50")]
    [InlineData(500, "JPY", "¥500")]
    [InlineData(3, "XYZ", "3.00 XYZ")]
    [InlineData(3, "", "3.00")]
    public void Formats_money(double amount, string currency, string expected) => Assert.Equal(expected, Money.Format(amount, currency));

    [Fact]
    public void Masks_and_redacts_tokens()
    {
        Assert.Equal("", Secrets.Mask(""));
        Assert.Equal("••••••••", Secrets.Mask("short"));
        Assert.Equal("••••••••wxyz", Secrets.Mask("abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal("url ?token=*** end", Secrets.Redact("url ?token=tok/en+1 end".Replace("tok/en+1", Uri.EscapeDataString("tok/en+1")), "tok/en+1"));
        Assert.Equal("a *** b", Secrets.Redact("a tok/en+1 b", "tok/en+1"));
    }

    [Fact]
    public void Recent_ids_dedupe_by_any_key_and_forget_old_ones()
    {
        var ids = new RecentIds(capacity: 3);
        Assert.True(ids.AddAll(["a", "b"]));
        Assert.False(ids.AddAll(["x", "b"]));
        Assert.True(ids.AddAll(["x"]));
        Assert.True(ids.AddAll([]));
        Assert.True(ids.AddAll([""]));
        Assert.True(ids.Add("y"));
        Assert.True(ids.Add("a"));   // evicted
    }
}

public sealed class RegistrationTests
{
    [Fact]
    public async Task AddDonations_registers_services_adapters_triggers_sub_actions_and_script_functions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-donations-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddDonations().BuildServiceProvider())
            {
                var streamlabs = provider.GetRequiredService<StreamlabsService>();
                var streamElements = provider.GetRequiredService<StreamElementsService>();
                var statuses = provider.GetServices<IConnectionStatus>().ToArray();
                Assert.Contains(statuses, s => ReferenceEquals(s, streamlabs));
                Assert.Contains(statuses, s => ReferenceEquals(s, streamElements));

                var registry = provider.GetRequiredService<Registry>();
                foreach (var id in new[] { StreamlabsEventTypes.Donation, StreamlabsEventTypes.Merch, StreamlabsEventTypes.CharityDonation,
                             StreamElementsEventTypes.Tip, StreamElementsEventTypes.Merch, StreamElementsEventTypes.Redemption })
                    Assert.NotNull(registry.GetTrigger(id));
                foreach (var id in new[] { "streamelements.getUserPoints", "streamelements.addUserPoints", "streamelements.getPointsLeaderboard",
                             "streamelements.getTips", "streamelements.getTipLeaderboard" })
                    Assert.Equal("StreamElements", registry.GetSubAction(id)!.Category);

                var hub = provider.GetRequiredService<ChatHub>();
                var api = provider.GetRequiredService<ScriptApi>();
                Assert.Contains(api.Functions, f => f.Name == "streamelements.addUserPoints");
                Assert.Equal(true, await api.CallAsync(null!, "streamlabs.sendTestEvent", JsonDocument.Parse("""{"kind":"donation"}""").RootElement, default));
                Assert.Equal(false, await api.CallAsync(null!, "streamlabs.isConnected", JsonDocument.Parse("{}").RootElement, default));

                // The test donation reached the activity feed and is ready for event speech ("chat.activity").
                var item = Assert.Single(hub.RecentFeed());
                Assert.Equal(("Streamlabs", FeedKind.Tip, "$13.37"), (item.Platform, item.Kind, item.Amount));
                Assert.StartsWith("[Test]", item.Title);
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
