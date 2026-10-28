using System.Net;
using System.Text;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

public sealed class ParsingTests : IDisposable
{
    private const string KofiToken = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b";
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private BotEvent Kofi(string file, string? id = null)
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        kofi.Settings.Secret = KofiToken;
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(Harness.Data(file)),
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }, id: id)));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Kofi_donation_has_streamerbots_arguments()
    {
        var e = Kofi("kofi-donation.json");
        Assert.Equal(KofiEventTypes.Donation, e.Type);
        Assert.Equal(3.0, e.Args["amount"]);
        Assert.Equal("USD", e.Args["currency"]);
        Assert.Equal("Ko-fi Team", e.Args["from"]);
        Assert.Equal("Ko-fi Team", e.Args["user"]);
        Assert.Equal(true, e.Args["isPublic"]);
        Assert.Equal("Good luck with the integration!", e.Args["message"]);
        Assert.Equal("3a1fac0c-f960-4506-a60e-824979a74e74", e.Args["messageId"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 13, 4, 30, TimeSpan.Zero), e.Args["timestamp"]);
        Assert.Equal("$3.00", e.Args["formattedAmount"]);
        Assert.Equal("Ko-fi", e.Args["service"]);
        Assert.Equal(false, e.Args["isTest"]);
        Assert.DoesNotContain(e.Args.Keys, k => k.Contains("verification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kofi_subscription_payments_after_the_first_are_resubscriptions_and_shop_orders_list_items()
    {
        var resub = Kofi("kofi-subscription.json");
        Assert.Equal(KofiEventTypes.Resubscription, resub.Type);
        Assert.Equal("Gold", resub.Args["tier"]);
        Assert.Equal(false, resub.Args["isPublic"]);

        var first = Harness.Data("kofi-subscription.json")
            .Replace("\"is_first_subscription_payment\": false", "\"is_first_subscription_payment\": true", StringComparison.Ordinal)
            .Replace("9b2fac0c", "9b2fac0d", StringComparison.Ordinal).Replace("11111111-", "11111112-", StringComparison.Ordinal);
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(first))));
        Assert.Single(_h.Events(KofiEventTypes.Subscription));

        var order = Kofi("kofi-shop-order.json");
        Assert.Equal(KofiEventTypes.ShopOrder, order.Type);
        Assert.Equal(2, order.Args["itemCount"]);
        Assert.Equal("Sticker pack", order.Args["item0"]);
        Assert.Equal("Poster", order.Args["item1"]);
        Assert.Equal(2.0, order.Args["item0.quantity"]);
        Assert.Equal("", order.Args["message"]);
    }

    [Fact]
    public void Kofi_commissions_map_and_unknown_types_are_ignored()
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = KofiToken } };
        var commission = Harness.Data("kofi-donation.json").Replace("\"Donation\"", "\"Commission\"", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(commission))));
        Assert.Single(_h.Events(KofiEventTypes.Commission));
        var other = Harness.Data("kofi-donation.json").Replace("\"Donation\"", "\"Mystery\"", StringComparison.Ordinal).Replace("3a1fac0c", "3a1fac0d", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Ignored, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(other))));
    }

    private BotEvent Patreon(string topic, string body)
    {
        var patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "s" } };
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, patreon.Handle(Harness.Delivery("patreon", body, new Dictionary<string, string>
        {
            ["X-Patreon-Event"] = topic,
            ["X-Patreon-Signature"] = WebhookVerification.HmacMd5Hex("s", Encoding.UTF8.GetBytes(body)),
        })));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Patreon_pledge_flattens_member_user_and_tier_attributes_like_streamerbot()
    {
        var e = Patreon("members:pledge:create", Harness.Data("patreon-pledge-create.json"));
        Assert.Equal(PatreonEventTypes.PledgeCreated, e.Type);
        Assert.Equal("03ca69c3-ebea-4b9a-8fac-e4a837873254", e.Args["id"]);
        Assert.Equal("active_patron", e.Args["attributes.patron_status"]);
        Assert.Equal("Paid", e.Args["attributes.last_charge_status"]);
        Assert.Equal("jane@example.com", e.Args["attributes.email"]);
        Assert.Equal(500L, e.Args["attributes.currently_entitled_amount_cents"]);
        Assert.Equal("98765432", e.Args["user.id"]);
        Assert.Equal("user", e.Args["user.type"]);
        Assert.Equal("Jane", e.Args["user.Attributes.first_name"]);
        Assert.Equal("https://twitch.tv/janepatron", e.Args["user.Attributes.social_connections.twitch.url"]);
        Assert.Equal("7654321", e.Args["entitledTiers[0].id"]);
        Assert.Equal("Supporter", e.Args["entitledTiers[0].Attributes.title"]);
        Assert.Equal(500L, e.Args["entitledTiers[0].Attributes.amount_cents"]);
        Assert.Equal("Jane Patron", e.Args["user"]);
        Assert.Equal(5.0, e.Args["amount"]);
        Assert.Equal("Supporter", e.Args["tier"]);
        Assert.Equal("members:pledge:create", e.Args["eventType"]);
    }

    [Fact]
    public void Patreon_topics_map_to_streamerbots_triggers()
    {
        var body = Harness.Data("patreon-pledge-create.json");
        var free = body.Replace("\"active_patron\"", "null", StringComparison.Ordinal);
        Assert.Equal(PatreonEventTypes.FollowCreated, Patreon("members:create", free).Type);
        Assert.Equal(PatreonEventTypes.FollowDeleted, Patreon("members:delete", free).Type);
        var updated = Patreon("members:pledge:update", body);
        Assert.Equal(PatreonEventTypes.PledgeUpdated, updated.Type);
        Assert.Equal("Supporter", updated.Args["entitledTiers[0].updatedToTier"]);
        Assert.False(Patreon("members:pledge:create", body).Args.ContainsKey("entitledTiers[0].updatedToTier"));
        Assert.Equal(PatreonEventTypes.PledgeDeleted, Patreon("members:pledge:delete", body).Type);
        Assert.Equal(PatreonEventTypes.MemberUpdated, Patreon("members:update", body).Type);

        // members:create for a paying patron is covered by members:pledge:create; posts:update is not a trigger.
        var patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        foreach (var topic in new[] { "members:create", "posts:update" })
            Assert.Equal(WebhookOutcome.Ignored, patreon.Handle(Harness.Delivery("patreon", body, new Dictionary<string, string>
            {
                ["X-Patreon-Event"] = topic, ["X-Patreon-Signature"] = WebhookVerification.HmacMd5Hex("s", Encoding.UTF8.GetBytes(body)),
            })));
    }

    private BotEvent Shopify(string topic, string body, string eventId)
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = topic, ["X-Shopify-Event-Id"] = eventId,
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
        })));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Shopify_orders_are_flattened_to_shopify_arguments()
    {
        var body = Harness.Data("shopify-order.json");
        var e = Shopify("orders/create", body, "e1");
        Assert.Equal(ShopifyEventTypes.OrderCreated, e.Type);
        Assert.Equal("orders/create", e.Args["shopify.event"]);
        Assert.Equal(820982911946154508L, e.Args["shopify.id"]);
        Assert.Equal("USD", e.Args["shopify.currency"]);
        Assert.Equal("254.98", e.Args["shopify.total_price"]);
        Assert.Equal("Aviator sunglasses", e.Args["shopify.line_items[0].name"]);
        Assert.Equal("89.99", e.Args["shopify.line_items[0].price_set.shop_money.amount"]);
        Assert.Equal(2L, e.Args["shopify.line_items[1].quantity"]);
        Assert.Equal(body, e.Args["_json"]);
        Assert.Equal("John Smith", e.Args["user"]);
        Assert.Equal(254.98, e.Args["amount"]);
        Assert.Equal("#9999", e.Args["orderName"]);
        Assert.Equal(3.0, e.Args["itemCount"]);

        Assert.Equal(ShopifyEventTypes.OrderPaid, Shopify("orders/paid", body, "e2").Type);
    }

    private BotEvent Fourthwall(string file)
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "f" } };
        var body = Harness.Data(file);
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(Harness.Delivery("fourthwall", body, new Dictionary<string, string>
        {
            ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(body)),
        })));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Fourthwall_order_placed_has_the_fw_arguments()
    {
        var e = Fourthwall("fourthwall-order-placed.json");
        Assert.Equal(FourthwallEventTypes.OrderPlaced, e.Type);
        Assert.Equal("7b83a86f-9221-4416-b739-ef88870f999b", e.Args["fw.orderId"]);
        Assert.Equal("F2LGPYCV", e.Args["fw.friendly"]);
        Assert.Equal("ch_4bRo4nB-RpWwXYx6iG4HuQ", e.Args["fw.checkoutId"]);
        Assert.Equal(17.0, e.Args["fw.total"]);
        Assert.Equal(10.0, e.Args["fw.subtotal"]);
        Assert.Equal(3.0, e.Args["fw.donation"]);
        Assert.Equal(2.0, e.Args["fw.discount"]);
        Assert.Equal("USD", e.Args["fw.currency"]);
        Assert.Equal("ORDER", e.Args["fw.source"]);
        Assert.Equal("message from supporter", e.Args["fw.statmessageus"]);
        Assert.Equal("CONFIRMED", e.Args["fw.status"]);
        Assert.Equal("sh_7ad0c438-beda-4779-a885-0dc325a755c1", e.Args["fw.shopId"]);
        Assert.Equal("New T-shirt", e.Args["fw.variants[0].name"]);
        Assert.Equal("White", e.Args["fw.variants[0].attributes.color"]);
        Assert.Equal("#FFFFFF", e.Args["fw.variants[0].attributes.colorSwatch"]);
        Assert.Equal("S", e.Args["fw.variants[0].attributes.size"]);
        Assert.Equal(2.0, e.Args["fw.variants[0].quantity"]);
        Assert.Equal(5.0, e.Args["fw.variants[0].unitPrice"]);
        Assert.Equal("Z3YD-8CTV00S", e.Args["fw.variants[0].sku"]);
        Assert.Equal("MerchFan", e.Args["user"]);
        Assert.Equal(17.0, e.Args["amount"]);
        Assert.Equal(false, e.Args["isTest"]);
    }

    [Fact]
    public void Fourthwall_donation_subscription_and_gift_purchase()
    {
        var donation = Fourthwall("fourthwall-donation.json");
        Assert.Equal(FourthwallEventTypes.Donation, donation.Type);
        Assert.Equal(5.5, donation.Args["fw.amount"]);
        Assert.Equal("EUR", donation.Args["fw.currency"]);
        Assert.Equal("don_xyz", donation.Args["fw.donationId"]);
        Assert.Equal("KindSupporter", donation.Args["fw.username"]);
        Assert.Equal(true, donation.Args["isTest"]);   // testMode from Fourthwall's "send test"

        var sub = Fourthwall("fourthwall-subscription-purchased.json");
        Assert.Equal(FourthwallEventTypes.SubscriptionPurchased, sub.Type);
        Assert.Equal("MONTHLY", sub.Args["fw.interval"]);
        Assert.Equal("ACTIVE", sub.Args["fw.type"]);
        Assert.Equal("NewMember", sub.Args["fw.nickname"]);
        Assert.Equal(5.0, sub.Args["fw.amount"]);

        var gift = Fourthwall("fourthwall-gift-purchase.json");
        Assert.Equal(FourthwallEventTypes.GiftPurchase, gift.Type);
        Assert.Equal("giv_voWcDCtfQUygxpuYx1HtNg", gift.Args["fw.Id"]);
        Assert.Equal("New T-shirt", gift.Args["fw.offer.name"]);
        Assert.Equal("https://cdn.fourthwall.com/tee.jpeg", gift.Args["fw.offer.imageUrl"]);
        Assert.Equal("supporter-username", gift.Args["fw.gifts[0].winner"]);
        Assert.Equal("F2LGPYCV", gift.Args["fw.gifts[0].orderFriendlyId"]);
        Assert.Equal("", gift.Args["fw.gifts[2].winner"]);
        Assert.Equal("prm_EdJvIXu3SEiXe_QkPavHSA", gift.Args["fw.gifts[3].promotionId"]);
        Assert.Equal(4.0, gift.Args["fw.quantity"]);
        Assert.Equal(5.0, gift.Args["fw.shipping"]);
        Assert.Equal(10.0, gift.Args["fw.subtotal"]);
        Assert.Equal(1.0, gift.Args["fw.tax"]);
        Assert.Equal(17.0, gift.Args["fw.total"]);
        Assert.Equal(0.0, gift.Args["fw.donation"]);   // not in a gift purchase; present like Streamer.bot's
        Assert.Equal(0.0, gift.Args["fw.discount"]);
        Assert.Equal(false, gift.Args["fw.isTest"]);
    }

    [Fact]
    public void Fourthwall_streamer_bot_names_for_orders_products_and_tests()
    {
        var order = Fourthwall("fourthwall-order-placed.json");
        Assert.Equal(false, order.Args["fw.isTest"]);
        Assert.Equal("https://cdn.fourthwall.com/customization/sh_7ad0c438/tee.jpeg", order.Args["fw.variants[0].image"]);
        Assert.Equal(2.0, order.Args["fw.variants[0].quantity"]);
        Assert.Equal("White, S", order.Args["fw.variants[0].attributes.description"]);
        Assert.True(order.Args.ContainsKey("fw.variants[0].stock"));
        Assert.Equal(true, Fourthwall("fourthwall-donation.json").Args["fw.isTest"]);

        const string product = """
            {"testMode":false,"id":"weve_p1","shopId":"sh_1","type":"PRODUCT_CREATED","createdAt":"2026-10-27T15:05:11Z","data":{
             "id":"prod_1","name":"Hoodie","slug":"hoodie","description":"Warm","state":{"type":"AVAILABLE"},
             "images":[{"id":"i1","url":"https://cdn/a.jpg"},{"id":"i2","url":"https://cdn/b.jpg"}],
             "variants":[
               {"id":"v1","name":"Black, M","sku":"H-BM","unitPrice":{"value":40,"currency":"USD"},
                "attributes":{"description":"Black, M","color":{"name":"Black","swatch":"#000000"},"size":{"name":"M"}},
                "stock":{"type":"LIMITED","inStock":12},"weight":{"value":0.5,"unit":"kg"},"images":[{"url":"https://cdn/v1.jpg"}]},
               {"id":"v2","name":"Black, L","sku":"H-BL","unitPrice":{"value":40,"currency":"USD"},"attributes":{},"stock":{"type":"UNLIMITED"}}],
             "createdAt":"2026-10-27T15:05:10Z","updatedAt":"2026-10-27T15:05:10Z"}}
            """;
        var e = FourthwallWebhookHandler.Map(JsonReaders.ParseBody(product), isTest: false)!.Args;
        Assert.Equal("prod_1", e["fw.productId"]);
        Assert.Equal("prod_1", e["fw.id"]);
        Assert.Equal("https://cdn/a.jpg", e["fw.images[0]"]);
        Assert.Equal("https://cdn/b.jpg", e["fw.images[1]"]);
        Assert.Equal("https://cdn/v1.jpg", e["fw.variants[0].image"]);
        Assert.Equal("Black", e["fw.variants[0].attributes.color"]);
        Assert.Equal("#000000", e["fw.variants[0].attributes.colorSwatch"]);
        Assert.Equal("M", e["fw.variants[0].attributes.size"]);
        Assert.Equal("0.5 kg", e["fw.variants[0].attributes.weight"]);
        Assert.Equal(12.0, e["fw.variants[0].stock"]);
        Assert.Equal("LIMITED", e["fw.variants[0].stockType"]);
        Assert.Equal("UNLIMITED", e["fw.variants[1].stock"]);
        Assert.Equal("AVAILABLE", e["fw.state"]);
        Assert.Equal(false, e["fw.isTest"]);
    }

    [Fact]
    public void Every_fourthwall_sample_maps_and_unknown_types_are_ignored()
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        foreach (var kind in fw.TestEventKinds) Assert.True(fw.SendTestEvent(kind), kind);
        Assert.Equal(fw.TestEventKinds.Count, _h.Published.Count);
        Assert.All(_h.Published, e => Assert.Equal(true, e.Args["isTest"]));

        fw.Settings.Secret = "f";
        const string body = """{"id":"weve_x","type":"CART_ABANDONED_1H","data":{}}""";
        Assert.Equal(WebhookOutcome.Ignored, fw.Handle(Harness.Delivery("fourthwall", body, new Dictionary<string, string>
        {
            ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(body)),
        })));
    }

    private readonly ThroneSigner _throneKey = new();

    private BotEvent Throne(string file, string? timestamp = null) => ThroneBody(Harness.Data(file), timestamp);

    private BotEvent ThroneBody(string body, string? timestamp = null)
    {
        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log, _throneKey.PublicKey);
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(_throneKey.Delivery(body, timestamp)));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Throne_reads_the_real_envelope()
    {
        var ts = ThroneSigner.Now();
        var gift = Throne("throne-gift-purchased.json", ts);
        Assert.Equal(ThroneEventTypes.GiftPurchased, gift.Type);
        Assert.Equal("4b6f2d1e-8c3a-4f7b-9e21-6d5c4b3a2f10", gift.Args["throne.eventId"]);
        Assert.Equal("gift_purchased", gift.Args["throne.eventType"]);
        Assert.Equal("1", gift.Args["throne.contractVersion"]);
        Assert.Equal("8f3c1a2b4d5e6f708192a3b4", gift.Args["throne.creatorId"]);
        Assert.Equal("twitchuser123", gift.Args["throne.creatorLogin"]);
        Assert.Equal("Lyfesaver74", gift.Args["throne.gifterLogin"]);
        Assert.Equal("Lyfesaver74", gift.Args["user"]);
        Assert.Equal(false, gift.Args["isAnonymous"]);
        Assert.Equal("AirPods Max", gift.Args["throne.itemName"]);
        Assert.Equal("AirPods Max", gift.Args["item"]);
        Assert.Equal(54900.0, gift.Args["throne.price"]);
        Assert.Equal(false, gift.Args["throne.isSurpriseGift"]);
        Assert.Equal("Happy Birthday!", gift.Args["throne.message"]);
        Assert.Equal(549.0, gift.Args["amount"]);
        Assert.Equal("$549.00", gift.Args["formattedAmount"]);
        // createdAt is the signed timestamp (Unix seconds).
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(long.Parse(ts, System.Globalization.CultureInfo.InvariantCulture)), gift.Args["throne.createdAt"]);

        var contribution = Throne("throne-contribution-purchased.json");
        Assert.Equal(ThroneEventTypes.ContributionPurchased, contribution.Type);
        Assert.Equal(1099.0, contribution.Args["throne.amount"]);
        Assert.False(contribution.Args.ContainsKey("throne.price"));
        Assert.Equal(10.99, contribution.Args["amount"]);
        Assert.Equal("", contribution.Args["message"]);   // omitted when there is none

        var crowdfunded = Throne("throne-gift-crowdfunded.json");
        Assert.Equal(ThroneEventTypes.GiftCrowdfunded, crowdfunded.Type);
        Assert.Equal(149900.0, crowdfunded.Args["throne.price"]);
        Assert.Equal(true, crowdfunded.Args["throne.isSurpriseGift"]);
        Assert.Equal("EUR", crowdfunded.Args["currency"]);
        Assert.Equal("Streaming PC was fully crowdfunded on Throne", WebhookChatAdapter.Describe(crowdfunded)!.Title);
    }

    [Fact]
    public void Throne_anonymous_gifters_are_flagged_and_shown_as_anonymous()
    {
        var contribution = Throne("throne-contribution-purchased.json");
        Assert.Equal(true, contribution.Args["isAnonymous"]);
        Assert.Equal("Anonymous", contribution.Args["user"]);
        var item = WebhookChatAdapter.Describe(contribution)!;
        Assert.Equal(("Anonymous", "Anonymous contributed $10.99 to Streaming PC"), (item.User, item.Title));

        var gift = Harness.Data("throne-gift-purchased.json").Replace("Lyfesaver74", "Anonymous", StringComparison.Ordinal);
        Assert.Equal(true, ThroneBody(gift).Args["isAnonymous"]);
    }

    [Fact]
    public void Throne_warns_about_an_unknown_contract_version_and_ignores_unknown_event_types()
    {
        var v2 = Harness.Data("throne-gift-purchased.json").Replace("\"contract_version\": \"1\"", "\"contract_version\": \"2\"", StringComparison.Ordinal);
        Assert.Equal(ThroneEventTypes.GiftPurchased, ThroneBody(v2).Type);
        Assert.Contains(_h.Log.Recent(), e => e.Message.Contains("contract_version '2'", StringComparison.Ordinal));

        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log, _throneKey.PublicKey);
        var other = Harness.Data("throne-gift-purchased.json").Replace("\"gift_purchased\"", "\"wishlist_updated\"", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Ignored, throne.Handle(_throneKey.Delivery(other)));
    }

    [Fact]
    public void Throne_built_in_samples_are_real_envelopes()
    {
        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        foreach (var kind in throne.TestEventKinds)
        {
            var json = ThroneWebhookHandler.SampleJson(kind)!;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("1", doc.RootElement.GetProperty("contract_version").GetString());
            Assert.True(Guid.TryParse(doc.RootElement.GetProperty("event_id").GetString(), out _));
            Assert.True(throne.SendTestEvent(kind), kind);
        }
        Assert.Single(_h.Events(ThroneEventTypes.GiftCrowdfunded));
        Assert.All(_h.Published, e => Assert.Equal(true, e.Args["isTest"]));
        Assert.Null(ThroneWebhookHandler.SampleJson("nope"));
    }

    [Fact]
    public void Shopify_real_test_delivery_is_a_test_with_presentment_money()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var body = Harness.Data("shopify-test-order.json");
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Test"] = "true", ["X-Shopify-Webhook-Id"] = "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043",
            ["X-Shopify-Event-Id"] = "9d1b4f9e-3c4e-4d4c-9c64-6f6b6c2f0d11", ["Content-Type"] = "application/json",
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
        })));
        var e = _h.Published.Single();
        Assert.Equal(true, e.Args["isTest"]);
        Assert.Equal("voided", e.Args["shopify.financial_status"]);
        Assert.Equal(4.0, e.Args["itemCount"]);
        Assert.Equal("IPod Nano - 8GB, IPod Nano - 8GB, Gift card, Sticker", e.Args["items"]);
        Assert.Equal(403.0, e.Args["amount"]);
        Assert.Equal(403.0, e.Args["presentmentAmount"]);
        Assert.Equal("USD", e.Args["presentmentCurrency"]);
        Assert.StartsWith("[Test] John Smith placed order #9999", WebhookChatAdapter.Describe(e)!.Title, StringComparison.Ordinal);

        // The header alone marks a test, even when the body says test:false.
        Assert.Equal(true, ShopifyMapped(Harness.Data("shopify-order.json"), test: true).Args["isTest"]);
        Assert.Equal(false, ShopifyMapped(Harness.Data("shopify-order.json"), test: false).Args["isTest"]);
    }

    private BotEvent ShopifyMapped(string body, bool test)
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var h = new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = "orders/paid", ["X-Shopify-Webhook-Id"] = Guid.NewGuid().ToString(),
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
        };
        if (test) h["X-Shopify-Test"] = "true";
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, h)));
        return _h.Published.Skip(before).Single();
    }

    [Fact]
    public void Shopify_presentment_money_differs_from_shop_money()
    {
        var body = Harness.Data("shopify-test-order.json").Replace(
            "\"presentment_money\": { \"amount\": \"403.00\", \"currency_code\": \"USD\" }",
            "\"presentment_money\": { \"amount\": \"372.10\", \"currency_code\": \"EUR\" }", StringComparison.Ordinal);
        Assert.Contains("372.10", body, StringComparison.Ordinal);
        var e = ShopifyMapped(body, test: false);
        Assert.Equal(403.0, e.Args["amount"]);
        Assert.Equal("USD", e.Args["currency"]);
        Assert.Equal(372.10, e.Args["presentmentAmount"]);
        Assert.Equal("EUR", e.Args["presentmentCurrency"]);
    }

    [Fact]
    public void Fourthwall_gift_draws_count_gifts_and_display_text_is_html_decoded()
    {
        var draw = Fourthwall("fourthwall-gift-draw-ended.json");
        Assert.Equal(FourthwallEventTypes.GiftDrawEnded, draw.Type);
        Assert.Equal(3.0, draw.Args["fw.quantity"]);
        Assert.Equal(3, draw.Args["fw.giftCount"]);
        Assert.Equal("winner-2", draw.Args["fw.gifts[1].winner"]);
        Assert.Equal(60.0, draw.Args["fw.durationSeconds"]);
        // Raw text in the fw.* arguments, decoded for display.
        Assert.Equal("Good luck, y&#39;all!", draw.Args["fw.message"]);
        Assert.Equal("Tom &amp; Jerry", draw.Args["fw.username"]);
        Assert.Equal("Good luck, y'all!", draw.Args["message"]);
        Assert.Equal("Tom & Jerry", draw.Args["user"]);
    }

    [Fact]
    public void Kofi_exposes_discord_fields_and_defaults_item_quantity_to_one()
    {
        var sub = Harness.Data("kofi-subscription.json").Replace("\"tier_name\": \"Gold\",",
            "\"tier_name\": \"Gold\", \"discord_username\": \"quiet#0001\", \"discord_userid\": \"012345678901234567\",", StringComparison.Ordinal);
        Assert.Contains("discord_userid", sub, StringComparison.Ordinal);
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = KofiToken } };
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(sub))));
        var e = _h.Published.Single();
        Assert.Equal("quiet#0001", e.Args["discordUsername"]);
        Assert.Equal("012345678901234567", e.Args["discordUserId"]);
        Assert.False(Kofi("kofi-donation.json").Args.ContainsKey("discordUsername"));

        var order = Harness.Data("kofi-shop-order.json").Replace(", \"quantity\": 1 }", " }", StringComparison.Ordinal);
        Assert.DoesNotContain("\"quantity\": 1", order, StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(order))));
        Assert.Equal(1.0, _h.Events(KofiEventTypes.ShopOrder).Single().Args["item1.quantity"]);
    }

    [Fact]
    public void Patreon_note_is_private_and_free_tiers_are_dropped_next_to_paid_ones()
    {
        var e = Patreon("members:pledge:create", Harness.Data("patreon-paid-and-free-tiers.json"));
        Assert.Equal("", e.Args["message"]);
        Assert.Equal("Met at the meetup, wants a shout-out", e.Args["note"]);
        Assert.Equal("Met at the meetup, wants a shout-out", e.Args["attributes.note"]);
        Assert.Equal("Gold", e.Args["tier"]);
        Assert.Equal("Free", e.Args["entitledTiers[0].Attributes.title"]);   // still in the arguments
        Assert.Equal("2222222", e.Args["entitledTiers[1].id"]);
        Assert.Equal(10.0, e.Args["amount"]);
        Assert.Equal(false, e.Args["isGifted"]);
        Assert.Equal(false, e.Args["isFreeTrial"]);
        Assert.Null(WebhookChatAdapter.Describe(e)!.Detail);

        // Only a free tier: it is named.
        var free = Harness.Data("patreon-paid-and-free-tiers.json")
            .Replace("""{ "id": "1111111", "type": "tier" }, { "id": "2222222", "type": "tier" }""", """{ "id": "1111111", "type": "tier" }""", StringComparison.Ordinal);
        Assert.Equal("Free", Patreon("members:update", free).Args["tier"]);
    }

    [Fact]
    public void Patreon_gifted_members_with_a_tier_missing_from_included_still_publish()
    {
        var e = Patreon("members:create", Harness.Data("patreon-missing-tier.json"));
        Assert.Equal(PatreonEventTypes.FollowCreated, e.Type);
        Assert.Equal(true, e.Args["isGifted"]);
        Assert.Equal(false, e.Args["isFreeTrial"]);
        Assert.Equal("", e.Args["tier"]);
        Assert.Equal("3333333", e.Args["entitledTiers[0].id"]);
        Assert.Equal("tier", e.Args["entitledTiers[0].type"]);
        Assert.Equal("Sam Gifted", e.Args["user"]);
        var item = WebhookChatAdapter.Describe(e)!;
        Assert.Equal((FeedKind.Subscription, "Sam Gifted received a gifted Patreon membership"), (item.Kind, item.Title));

        var trial = Harness.Data("patreon-missing-tier.json")
            .Replace("\"is_free_trial\": false", "\"is_free_trial\": true", StringComparison.Ordinal)
            .Replace("\"is_gifted\": true", "\"is_gifted\": false", StringComparison.Ordinal);
        var t = Patreon("members:create", trial);
        Assert.Equal(true, t.Args["isFreeTrial"]);
        Assert.Equal("Sam Gifted started a Patreon free trial", WebhookChatAdapter.Describe(t)!.Title);
    }

    [Fact]
    public void Patreon_post_urls_are_made_absolute()
    {
        var patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        Assert.True(patreon.SendTestEvent("postPublished"));
        Assert.Equal("https://www.patreon.com/posts/new-video-is-up-123", _h.Events(PatreonEventTypes.PostPublished).Single().Args["url"]);
        Assert.Equal("https://example.com/x", PatreonWebhookHandler.AbsoluteUrl("https://example.com/x"));
    }

    [Fact]
    public void Custom_hook_publishes_request_details_and_top_level_json_keys()
    {
        var custom = new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        custom.Hooks.Add(new CustomWebhookSettings { Name = "door", Token = "door-token" });
        var body = Harness.Data("custom-ifttt.json");
        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", body,
            new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Cuebot-Token"] = "door-token", ["User-Agent"] = "IFTTT" },
            path: "door", query: "a=1&b=two%20words", method: "PUT")));
        var e = Assert.Single(_h.Events(CustomWebhookEventTypes.Received));
        Assert.Equal("door", e.Args["hookName"]);
        Assert.Equal("PUT", e.Args["method"]);
        Assert.Equal(body, e.Args["body"]);
        Assert.Equal("Front door opened", e.Args["value1"]);
        Assert.Equal(3L, e.Args["value2"]);
        Assert.Equal(true, e.Args["value3"]);
        Assert.Equal("""{ "room": "hall" }""", e.Args["details"]);
        Assert.Equal("""["a", "b"]""", e.Args["tags"]);
        Assert.Equal("1", e.Args["query.a"]);
        Assert.Equal("two words", e.Args["query.b"]);
        Assert.Contains("IFTTT", (string)e.Args["headers"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("door-token", (string)e.Args["headers"]!, StringComparison.Ordinal);
        Assert.Equal("application/json", e.Args["contentType"]);
    }

    [Fact]
    public void Custom_hook_does_not_leak_the_query_token_and_reads_form_bodies()
    {
        var custom = new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        custom.Hooks.Add(new CustomWebhookSettings { Name = "form", Token = "tok" });
        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", "name=Alice&hookName=evil&amount=5",
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }, path: "form", query: "token=tok&x=y")));
        var e = Assert.Single(_h.Events(CustomWebhookEventTypes.Received));
        Assert.Equal("form", e.Args["hookName"]);   // body keys never overwrite Cuebot's own arguments
        Assert.Equal("Alice", e.Args["name"]);
        Assert.Equal("5", e.Args["amount"]);
        Assert.Equal("x=y", e.Args["query"]);
        Assert.False(e.Args.ContainsKey("query.token"));
    }

    [Fact]
    public void Custom_hook_names_are_sanitised_and_unique()
    {
        var custom = new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        var hook = custom.AddHook(" My Hook! ");
        Assert.Equal("My-Hook", hook.Name);
        Assert.Equal(32, hook.Token.Length);
        Assert.Throws<ArgumentException>(() => custom.AddHook("my-hook"));
        Assert.Throws<ArgumentException>(() => custom.AddHook("  "));
        Assert.Equal(["My-Hook"], custom.TestEventKinds);
        Assert.True(custom.SendTestEvent("My-Hook"));
        Assert.Equal("Hello from a custom webhook", _h.Events(CustomWebhookEventTypes.Received).Single().Args["value1"]);
        custom.RemoveHook(hook);
        Assert.Empty(custom.Hooks);
    }
}
