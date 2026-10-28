using System.Text.Json;
using Cuebot.Donations.Streamlabs;

namespace Cuebot.Donations.Tests;

/// <summary>Payloads modelled on the samples in Streamlabs' public Socket API documentation.</summary>
public sealed class StreamlabsMappingTests
{
    internal static DonationEvent One(string json) => Assert.Single(StreamlabsEventMapper.Map(Parse(json)));

    internal static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal const string Donation = """
        {"type":"donation","message":[{"id":96164121,"name":"test","amount":"13.37","formatted_amount":"$13.37","formattedAmount":"$13.37",
        "message":"test donation","currency":"USD","emotes":null,"iconClassName":"user","to":{"name":"Streamer"},"from":"test",
        "from_user_id":null,"_id":"0820c9d5bafd768c9843f5e35c885e71"}],"event_id":"evt_17e5f4dc6888767ed9799f78dfa2cabc"}
        """;

    [Fact]
    public void Donation_maps_amount_currency_message_and_streamerbot_names()
    {
        var e = One(Donation);
        Assert.Equal(StreamlabsEventTypes.Donation, e.Type);
        Assert.False(e.Relayed);
        var a = e.Args;
        Assert.Equal("test", a["user"]);
        Assert.Equal(13.37, a["amount"]);
        Assert.Equal("USD", a["currency"]);
        Assert.Equal("$13.37", a["formattedAmount"]);
        Assert.Equal("test donation", a["message"]);
        Assert.Equal("96164121", a["donationId"]);
        Assert.Equal("evt_17e5f4dc6888767ed9799f78dfa2cabc", a["eventId"]);
        Assert.Equal(13.37, a["donationAmount"]);
        Assert.Equal("test", a["donationFrom"]);
        Assert.Equal("Streamlabs", a["platform"]);
        Assert.Equal(false, a["isTest"]);
        Assert.Contains("evt:evt_17e5f4dc6888767ed9799f78dfa2cabc:0", e.Keys);
        Assert.Contains("donation:_id:0820c9d5bafd768c9843f5e35c885e71", e.Keys);
    }

    [Fact]
    public void Donation_without_formatted_amount_is_formatted_from_number_and_currency()
    {
        var a = One("""{"type":"donation","message":[{"name":"Eve","amount":5,"currency":"eur","message":null}]}""").Args;
        Assert.Equal("€5.00", a["formattedAmount"]);
        Assert.Equal("EUR", a["currency"]);
        Assert.Equal("", a["message"]);
        Assert.Equal("Anonymous", One("""{"type":"donation","message":[{"amount":"1"}]}""").Args["user"]);
    }

    [Fact]
    public void Several_entries_in_one_event_become_several_events()
    {
        var events = StreamlabsEventMapper.Map(Parse("""
            {"type":"donation","event_id":"evt_1","message":[{"name":"A","amount":"1","currency":"USD","_id":"a"},{"name":"B","amount":"2","currency":"USD","_id":"b"}]}
            """));
        Assert.Equal(["A", "B"], events.Select(e => e.Args["user"]));
        Assert.Equal(["evt:evt_1:0", "evt:evt_1:1"], events.Select(e => e.Keys[0]));
    }

    [Fact]
    public void Charity_donation_maps_campaign_fields()
    {
        var e = One("""
            {"type":"streamlabscharitydonation","for":"streamlabs","message":[{"charityDonationId":"cd1","campaignId":"camp1","name":"Kind Person",
            "from":"Kind Person","amount":"25.00","formattedAmount":"$25.00","currency":"USD","message":"Good luck!","createdAt":"2026-01-02T03:04:05Z",
            "iconClassName":"fas fa-heart","_id":"x1"}],"event_id":"evt_c"}
            """);
        Assert.Equal(StreamlabsEventTypes.CharityDonation, e.Type);
        Assert.Equal(25.0, e.Args["charityDonationAmount"]);
        Assert.Equal("$25.00", e.Args["charityDonationFormattedAmount"]);
        Assert.Equal("Kind Person", e.Args["charityDonationFrom"]);
        Assert.Equal("camp1", e.Args["campaignId"]);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z").LocalDateTime, e.Args["charityDonationCreatedAt"]);
        Assert.Equal("fas fa-heart", e.Args["charityDonationIconClassName"]);
    }

    [Fact]
    public void Merch_and_loyalty_store_redemptions_map_buyer_product_and_image()
    {
        var merch = One("""
            {"type":"merch","message":[{"id":1,"from":"Kevin","product":"Logo Hoodie","message":"Rocking it","image_href":"https://cdn.test/a b.png","_id":"m1"}],"event_id":"evt_m"}
            """);
        Assert.Equal(StreamlabsEventTypes.Merch, merch.Type);
        Assert.Equal("Kevin", merch.Args["merchandiseFrom"]);
        Assert.Equal("Logo Hoodie", merch.Args["merchandiseProduct"]);
        Assert.Equal("Rocking it", merch.Args["merchandiseMessage"]);
        Assert.Equal("https://cdn.test/a b.png", merch.Args["merchandiseImageUrl"]);
        Assert.Equal("https%3A%2F%2Fcdn.test%2Fa%20b.png", merch.Args["merchandiseImageEscaped"]);

        var loyalty = One("""
            {"type":"loyalty_store_redemption","message":[{"from":"Viewer","product":"Hydrate","message":"drink!","imageHref":"https://cdn.test/w.png","_id":"l1"}]}
            """);
        Assert.Equal(StreamlabsEventTypes.LoyaltyStoreRedemption, loyalty.Type);
        Assert.Equal("Hydrate", loyalty.Args["product"]);
        Assert.Equal("Viewer", loyalty.Args["redemptionFrom"]);
    }

    [Fact]
    public void Prime_gift_maps_gifter_and_recipient()
    {
        var e = One("""{"type":"prime_sub_gift","message":[{"name":"Lucky","from":"Generous","giftType":"Twitch Prime gift","_id":"p1"}]}""");
        Assert.Equal(StreamlabsEventTypes.PrimeSubGift, e.Type);
        Assert.Equal("Generous", e.Args["user"]);
        Assert.Equal("Lucky", e.Args["recipient"]);
        Assert.Equal("Twitch Prime gift", e.Args["gift"]);
    }

    [Fact]
    public void Relayed_twitch_and_youtube_events_are_marked_relayed()
    {
        var follow = One("""{"type":"follow","message":[{"created_at":"2017-08-22 00:14:35","id":"170884026","name":"h4r5h48002","_id":"74a0b9"}],"for":"twitch_account"}""");
        Assert.Equal((StreamlabsEventTypes.Follow, true, "Twitch", "170884026"), (follow.Type, follow.Relayed, follow.Args["platform"], follow.Args["userId"]));

        var sub = One("""{"type":"subscription","message":[{"name":"kevin","months":7,"message":"sub message","emotes":null,"sub_plan":"1000","sub_plan_name":"Plan","sub_type":"resub","_id":"a2"}],"for":"twitch_account"}""");
        Assert.Equal((StreamlabsEventTypes.Subscription, 7, "1000", "resub", "sub message"),
            (sub.Type, sub.Args["months"], sub.Args["tier"], sub.Args["subType"], sub.Args["message"]));

        var bits = One("""{"type":"bits","message":[{"id":"fc54","name":"h4r5h48002","amount":"100","emotes":null,"message":"cheer100","_id":"b1"}],"for":"twitch_account"}""");
        Assert.Equal((StreamlabsEventTypes.Bits, 100, "100 bits"), (bits.Type, bits.Args["bits"], bits.Args["formattedAmount"]));

        var raid = One("""{"type":"raid","message":[{"name":"sunnyding602","raiders":138,"_id":"r1"}],"for":"twitch_account","event_id":"evt_r"}""");
        Assert.Equal((StreamlabsEventTypes.Raid, 138), (raid.Type, raid.Args["viewers"]));

        var host = One("""{"type":"host","message":[{"name":"h4r5h48002","viewers":"1","type":"manual","_id":"h1"}],"for":"twitch_account"}""");
        Assert.Equal((StreamlabsEventTypes.Host, 1, "manual"), (host.Type, host.Args["viewers"], host.Args["hostType"]));

        var superchat = One("""
            {"type":"superchat","message":[{"id":"LCC.1","channelId":"UCec","name":"Kappa Lord","comment":"love the stream","amount":"2000000",
            "currency":"USD","displayString":"$2.00","messageType":2,"createdAt":"2017-08-22 00:51:57","_id":"s1"}],"for":"youtube_account"}
            """);
        Assert.Equal((StreamlabsEventTypes.Superchat, 2.0, "$2.00", "love the stream", "YouTube"),
            (superchat.Type, superchat.Args["amount"], superchat.Args["formattedAmount"], superchat.Args["message"], superchat.Args["platform"]));

        var ytSub = One("""{"type":"follow","message":[{"publishedAt":"2017-08-22 00:25:57","id":"UCec","name":"Kappa Lord","_id":"6af0"}],"for":"youtube_account"}""");
        Assert.Equal(("YouTube", true), (ytSub.Args["platform"], ytSub.Relayed));
    }

    [Theory]
    [InlineData("""{"type":"alertPlaying","message":{"type":"donation"}}""")]
    [InlineData("""{"type":"streamlabels","message":{"data":{}}}""")]
    [InlineData("""{"type":"donation"}""")]
    [InlineData("""{"type":"donation","message":"oops"}""")]
    [InlineData("""[]""")]
    [InlineData("""{"type":"mysteryType","message":[{"name":"x"}],"for":"twitch_account"}""")]
    public void Unknown_or_malformed_payloads_map_to_nothing(string json) =>
        Assert.Empty(StreamlabsEventMapper.Map(Parse(json)));

    [Fact]
    public void Every_test_sample_maps_and_is_marked_as_a_test()
    {
        foreach (var (kind, json) in StreamlabsEventMapper.Samples)
        {
            var e = Assert.Single(StreamlabsEventMapper.Map(Parse(json), isTest: true));
            Assert.True(e.Args["isTest"] is true, kind);
        }
    }
}
