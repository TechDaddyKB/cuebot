using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Credits;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Webhooks.Tests;

public sealed class TriggerTests
{
    private static readonly ITriggerProvider[] Providers =
        [new KofiTriggers(), new PatreonTriggers(), new ShopifyTriggers(), new FourthwallTriggers(), new ThroneTriggers(), new CustomWebhookTriggers()];

    private static ITriggerType Trigger(string id) => Providers.SelectMany(p => p.GetTriggers()).Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    [Fact]
    public void Trigger_lists_match_streamerbots_documented_triggers()
    {
        string[] Names(ITriggerProvider p) => p.GetTriggers().Select(t => t.DisplayName).Order().ToArray();
        Assert.Equal(["Commission", "Donation", "Resubscription", "Shop Order", "Subscription"], Names(new KofiTriggers()));
        Assert.Equal(["Follow Created", "Follow Deleted", "Member Updated", "Pledge Created", "Pledge Deleted", "Pledge Updated", "Post Published"],
            Names(new PatreonTriggers()));
        Assert.Equal(["Order Created", "Order Paid"], Names(new ShopifyTriggers()));
        Assert.Equal(["Donation", "Gift Draw Ended", "Gift Draw Started", "Gift Purchase", "Newsletter Subscribed", "Order Placed", "Order Updated",
            "Product Created", "Product Updated", "Subscription Changed", "Subscription Expired", "Subscription Purchased", "Thank You Sent"],
            Names(new FourthwallTriggers()));
        Assert.Equal(["Contribution Purchased", "Gift Crowdfunded", "Gift Purchased"], Names(new ThroneTriggers()));
        Assert.Equal(["Custom Webhook"], Names(new CustomWebhookTriggers()));

        foreach (var trigger in Providers.SelectMany(p => p.GetTriggers()))
            Assert.Equal(trigger.TypeId, Assert.Single(trigger.EventTypes));
        Assert.Equal(31, Providers.SelectMany(p => p.GetTriggers()).Select(t => t.TypeId).Distinct().Count());
    }

    private static BotEvent Money(string type, double amount, string currency, bool isTest = false, string tier = "") =>
        new(type, new Dictionary<string, object?> { ["amount"] = amount, ["currency"] = currency, ["isTest"] = isTest, ["tier"] = tier });

    [Fact]
    public void Amount_currency_tier_and_test_filters()
    {
        var donation = Trigger(KofiEventTypes.Donation);
        Assert.True(Matches(donation, Money(KofiEventTypes.Donation, 5, "USD")));
        Assert.False(Matches(donation, Money(KofiEventTypes.Donation, 4.99, "USD"), new() { ["minAmount"] = "5" }));
        Assert.False(Matches(donation, Money(KofiEventTypes.Donation, 101, "USD"), new() { ["maxAmount"] = "100" }));
        Assert.False(Matches(donation, Money(KofiEventTypes.Donation, 5, "GBP"), new() { ["currency"] = "USD, EUR" }));
        Assert.False(Matches(donation, Money(KofiEventTypes.Donation, 5, "USD", isTest: true), new() { ["includeTests"] = "false" }));

        var sub = Trigger(KofiEventTypes.Subscription);
        Assert.True(Matches(sub, Money(KofiEventTypes.Subscription, 5, "USD", tier: "Gold"), new() { ["tier"] = "gold" }));
        Assert.False(Matches(sub, Money(KofiEventTypes.Subscription, 5, "USD", tier: "Bronze"), new() { ["tier"] = "gold" }));

        var pledge = Trigger(PatreonEventTypes.PledgeCreated);
        Assert.True(Matches(pledge, Money(PatreonEventTypes.PledgeCreated, 5, "", tier: "Supporter, Fan"), new() { ["tier"] = "fan" }));
        Assert.False(Matches(pledge, Money(PatreonEventTypes.PledgeCreated, 5, "", tier: "Supporter"), new() { ["tier"] = "fan" }));

        var custom = Trigger(CustomWebhookEventTypes.Received);
        var door = new BotEvent(CustomWebhookEventTypes.Received, new Dictionary<string, object?> { ["hookName"] = "door" });
        Assert.True(Matches(custom, door));
        Assert.True(Matches(custom, door, new() { ["hookName"] = "DOOR" }));
        Assert.False(Matches(custom, door, new() { ["hookName"] = "bell" }));
    }
}

public sealed class AdapterTests
{
    private static BotEvent E(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("service", "S");
        return new BotEvent(type, args);
    }

    [Fact]
    public void Money_events_become_feed_items_of_the_right_kind()
    {
        var a = new Dictionary<string, object?> { ["user"] = "Kevin", ["amount"] = 5.0, ["formattedAmount"] = "$5.00", ["message"] = "hi", ["tier"] = "Gold" };
        FeedItem D(string type) => WebhookChatAdapter.Describe(E(type, new(a)))!;
        Assert.Equal((FeedKind.Tip, "Kevin donated $5.00 on Ko-fi", "$5.00", "hi"), (D(KofiEventTypes.Donation).Kind, D(KofiEventTypes.Donation).Title,
            D(KofiEventTypes.Donation).Amount, D(KofiEventTypes.Donation).Detail));
        Assert.Equal((FeedKind.Subscription, "Kevin subscribed on Ko-fi (Gold)"), (D(KofiEventTypes.Subscription).Kind, D(KofiEventTypes.Subscription).Title));
        Assert.Equal(FeedKind.Subscription, D(PatreonEventTypes.PledgeCreated).Kind);
        Assert.Equal(FeedKind.Follow, D(PatreonEventTypes.FollowCreated).Kind);
        Assert.Equal(FeedKind.Tip, D(FourthwallEventTypes.Donation).Kind);
        Assert.Equal(FeedKind.Subscription, D(FourthwallEventTypes.SubscriptionPurchased).Kind);
        Assert.Equal(FeedKind.Other, D(FourthwallEventTypes.OrderPlaced).Kind);
        Assert.Equal(FeedKind.Other, D(ShopifyEventTypes.OrderPaid).Kind);
        Assert.Equal(FeedKind.Tip, D(ThroneEventTypes.ContributionPurchased).Kind);
        Assert.Null(WebhookChatAdapter.Describe(E(PatreonEventTypes.PledgeDeleted, new(a))));
        Assert.Null(WebhookChatAdapter.Describe(E(FourthwallEventTypes.ProductUpdated, new(a))));
        Assert.Null(WebhookChatAdapter.Describe(E(CustomWebhookEventTypes.Received, new(a))));
    }

    [Fact]
    public void Private_kofi_supports_hide_the_name_and_message_and_repeat_shopify_orders_are_skipped()
    {
        var item = WebhookChatAdapter.Describe(E(KofiEventTypes.Donation, new()
        {
            ["user"] = "Secret Sam", ["isPublic"] = false, ["message"] = "private", ["formattedAmount"] = "$1.00", ["amount"] = 1.0,
        }))!;
        Assert.Equal(("Someone", "Someone donated $1.00 on Ko-fi", null), (item.User, item.Title, item.Detail));

        Assert.Null(WebhookChatAdapter.Describe(E(ShopifyEventTypes.OrderPaid, new() { ["isRepeatOrder"] = true })));
    }

    [Fact]
    public void Each_adapter_only_handles_its_own_service()
    {
        var adapters = WebhookChatAdapter.All().ToArray();
        var e = E(KofiEventTypes.Donation, new() { ["user"] = "K", ["amount"] = 1.0, ["formattedAmount"] = "$1.00" });
        Assert.Single(adapters, x => x.ToFeedItem(e) is not null);
        Assert.All(adapters, x => Assert.Null(x.ToChatMessage(e)));
    }
}

public sealed class RegistrationTests
{
    [Fact]
    public async Task AddWebhooks_registers_handlers_triggers_feed_credits_and_script_functions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-webhooks-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddWebhooks().BuildServiceProvider())
            {
                var handlers = provider.GetServices<IWebhookHandler>().ToArray();
                Assert.Equal(["kofi", "patreon", "fourthwall", "shopify", "throne", "custom"], handlers.Select(h => h.Service));
                Assert.Same(provider.GetRequiredService<KofiWebhookHandler>(), handlers[0]);
                Assert.Same(provider.GetRequiredService<WebhookServices>().Kofi, handlers[0]);

                var registry = provider.GetRequiredService<Registry>();
                foreach (var id in new[] { KofiEventTypes.Donation, PatreonEventTypes.PledgeCreated, ShopifyEventTypes.OrderPaid,
                             FourthwallEventTypes.OrderPlaced, ThroneEventTypes.GiftPurchased, CustomWebhookEventTypes.Received })
                    Assert.NotNull(registry.GetTrigger(id));

                var hub = provider.GetRequiredService<ChatHub>();
                var credits = provider.GetRequiredService<CreditsCollector>();
                var api = provider.GetRequiredService<ScriptApi>();
                Assert.Equal(true, await api.CallAsync(null!, "webhooks.sendTestEvent", JsonDocument.Parse("""{"service":"kofi","kind":"donation"}""").RootElement, default));
                Assert.Equal(true, await api.CallAsync(null!, "webhooks.sendTestEvent", JsonDocument.Parse("""{"service":"fourthwall","kind":"subscriptionPurchased"}""").RootElement, default));
                Assert.Equal(false, await api.CallAsync(null!, "webhooks.sendTestEvent", JsonDocument.Parse("""{"service":"nope"}""").RootElement, default));
                Assert.NotNull(await api.CallAsync(null!, "webhooks.status", JsonDocument.Parse("{}").RootElement, default));

                // The test donation reached the activity feed (event speech reads "chat.activity") and the credits.
                var feed = hub.RecentFeed();
                var tip = Assert.Single(feed, f => f.Kind == FeedKind.Tip);
                Assert.Equal(("Ko-fi", "$3.00"), (tip.Platform, tip.Amount));
                Assert.StartsWith("[Test]", tip.Title);
                Assert.Single(feed, f => f.Kind == FeedKind.Subscription && f.Platform == "Fourthwall");
                var snapshot = credits.Snapshot();
                Assert.Equal("Ko-fi Team", Assert.Single(snapshot.Section(CreditsSections.Tips)!.Entries).User);
                Assert.Equal("NewMember", Assert.Single(snapshot.Section(CreditsSections.Subscriptions)!.Entries).User);
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
