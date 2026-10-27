using Cuebot.Donations.StreamElements;

namespace Cuebot.Donations.Tests;

/// <summary>Activity payloads modelled on StreamElements' public websocket/activity documentation.</summary>
public sealed class StreamElementsMappingTests
{
    private static DonationEvent Map(string json) =>
        StreamElementsEventMapper.Map(StreamlabsMappingTests.Parse(json)) ?? throw new Xunit.Sdk.XunitException("no event");

    internal const string Tip = """
        {"type":"tip","provider":"twitch","channel":"5ad23dcc18fff500d78c5348","data":{"tipId":"67b5f39d07ecd4c594e60f73","username":"styler",
        "displayName":"Styler","amount":4.2,"currency":"USD","message":"Great stream","avatar":"https://cdn.test/a.png"},
        "_id":"65ce2539b1585c5f909a91e1","createdAt":"2025-02-19T15:07:09.302Z","updatedAt":"2025-02-19T15:07:17.099Z",
        "activityId":"65ce2539b1585c5f909a91e1","sessionEventsCount":6}
        """;

    [Fact]
    public void Tip_maps_amount_message_and_streamerbot_names()
    {
        var e = Map(Tip);
        Assert.Equal(StreamElementsEventTypes.Tip, e.Type);
        Assert.False(e.Relayed);
        var a = e.Args;
        Assert.Equal(("Styler", "styler"), (a["user"], a["userName"]));
        Assert.Equal((4.2, "USD", "$4.20", "Great stream"), (a["amount"], a["currency"], a["formattedAmount"], a["message"]));
        Assert.Equal((4.2, "USD", "Great stream", "Styler", "https://cdn.test/a.png"),
            (a["tipAmount"], a["tipCurrency"], a["tipMessage"], a["tipUsername"], a["tipAvatar"]));
        Assert.Equal("StreamElements", a["platform"]);
        Assert.Equal(["activity:65ce2539b1585c5f909a91e1", "tip:67b5f39d07ecd4c594e60f73"], e.Keys);
    }

    [Fact]
    public void Merch_maps_items_quantity_and_amount()
    {
        var e = Map("""
            {"type":"merch","provider":"twitch","channel":"c1","data":{"username":"buyer","displayName":"Buyer","amount":45,"currency":"EUR",
            "message":"My merch message","items":[{"name":"Hoodie","price":30,"quantity":1},{"name":"Mug","price":7.5,"quantity":2}]},
            "_id":"m1","activityId":"m1"}
            """);
        Assert.Equal(StreamElementsEventTypes.Merch, e.Type);
        var a = e.Args;
        Assert.Equal((45.0, "EUR", "€45.00"), (a["merchAmount"], a["merchCurrency"], a["formattedAmount"]));
        Assert.Equal((2, 3), (a["merchItems"], a["merchQuantity"]));
        Assert.Equal(("Hoodie", 30.0, 1), (a["merchItem0.name"], a["merchItem0.price"], a["merchItem0.quantity"]));
        Assert.Equal(("Mug", 7.5, 2), (a["merchItem1.name"], a["merchItem1.price"], a["merchItem1.quantity"]));
        Assert.Equal("Hoodie, 2x Mug", a["product"]);
        Assert.Equal("My merch message", a["merchMessage"]);
    }

    [Fact]
    public void Store_redemption_maps_item_and_cost()
    {
        var e = Map("""
            {"type":"redemption","provider":"twitch","channel":"c1","data":{"username":"viewer","displayName":"Viewer","itemId":"i1",
            "itemName":"Hydrate!","amount":500,"message":"Drink up"},"_id":"r1","activityId":"r1"}
            """);
        Assert.Equal(StreamElementsEventTypes.Redemption, e.Type);
        Assert.Equal(("Hydrate!", "i1", 500.0, "Drink up"), (e.Args["item"], e.Args["itemId"], e.Args["cost"], e.Args["message"]));
    }

    [Theory]
    [InlineData("follow", StreamElementsEventTypes.Follow)]
    [InlineData("follower", StreamElementsEventTypes.Follow)]
    [InlineData("subscriber", StreamElementsEventTypes.Subscriber)]
    [InlineData("cheer", StreamElementsEventTypes.Cheer)]
    [InlineData("raid", StreamElementsEventTypes.Raid)]
    [InlineData("host", StreamElementsEventTypes.Host)]
    [InlineData("superchat", StreamElementsEventTypes.Superchat)]
    [InlineData("sponsor", StreamElementsEventTypes.Sponsor)]
    [InlineData("communityGiftPurchase", StreamElementsEventTypes.CommunityGift)]
    [InlineData("charityCampaignDonation", StreamElementsEventTypes.CharityDonation)]
    [InlineData("hypetrainStart", StreamElementsEventTypes.Activity)]
    public void Platform_activities_are_relayed(string type, string expected)
    {
        var e = Map($$"""
            {"type":"{{type}}","provider":"youtube","channel":"c1","data":{"username":"jtv","displayName":"jtv","providerId":"14027",
            "amount":12,"currency":"USD","tier":"1000","gifted":true,"sender":"Santa","message":"hi"},"_id":"x","activityId":"x"}
            """);
        Assert.Equal(expected, e.Type);
        Assert.True(e.Relayed);
        Assert.Equal(("jtv", "14027", "YouTube", type), (e.Args["user"], e.Args["userId"], e.Args["platform"], e.Args["activityType"]));
    }

    [Fact]
    public void Relayed_amounts_mean_months_bits_viewers_or_gifts()
    {
        string Json(string type) => $$"""{"type":"{{type}}","provider":"twitch","data":{"username":"u","amount":7,"tier":"2000","gifted":true,"sender":"Santa"},"_id":"{{type}}"}""";
        var sub = Map(Json("subscriber"));
        Assert.Equal((7, "2000", true, "Santa"), (sub.Args["months"], sub.Args["tier"], sub.Args["isGift"], sub.Args["gifter"]));
        Assert.Equal((7, "7 bits"), (Map(Json("cheer")).Args["bits"], Map(Json("cheer")).Args["formattedAmount"]));
        Assert.Equal(7, Map(Json("raid")).Args["viewers"]);
        Assert.Equal(7, Map(Json("communityGiftPurchase")).Args["gift"]);
    }

    [Theory]
    [InlineData("""{"provider":"twitch","data":{}}""")]
    [InlineData("""[]""")]
    [InlineData("""{"type":""}""")]
    public void Malformed_activities_map_to_nothing(string json) =>
        Assert.Null(StreamElementsEventMapper.Map(StreamlabsMappingTests.Parse(json)));

    [Fact]
    public void Activity_without_data_still_maps_safely()
    {
        var e = Map("""{"type":"tip","_id":"t"}""");
        Assert.Equal(("Anonymous", 0.0), (e.Args["user"], e.Args["amount"]));
    }

    [Fact]
    public void Every_test_sample_maps_and_is_marked_as_a_test()
    {
        foreach (var (kind, json) in StreamElementsEventMapper.Samples)
        {
            var e = StreamElementsEventMapper.Map(StreamlabsMappingTests.Parse(json), isTest: true);
            Assert.NotNull(e);
            Assert.True(e.Args["isTest"] is true, kind);
            Assert.False(e.Relayed, kind);
        }
    }
}
