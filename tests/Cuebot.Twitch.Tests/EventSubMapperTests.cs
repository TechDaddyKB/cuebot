using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class EventSubMapperTests
{
    private static BotEvent Map(string type, string json) =>
        EventSubMapper.Map(type, JsonSerializer.Deserialize<JsonElement>(json)) ?? throw new Xunit.Sdk.XunitException("not mapped");

    private const string ChatMessage =
        """
        {"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer",
         "chatter_user_id":"4145994","chatter_user_login":"viewer32","chatter_user_name":"Viewer32",
         "message_id":"cc106a89-1814-919d-454c-f4f2f970aae7",
         "message":{"text":"Hi chat Cheer100","fragments":[{"type":"text","text":"Hi chat ","cheermote":null,"emote":null,"mention":null}]},
         "color":"#00FF7F",
         "badges":[{"set_id":"moderator","id":"1","info":""},{"set_id":"founder","id":"0","info":"3"}],
         "message_type":"text","cheer":{"bits":100},"reply":null,"channel_points_custom_reward_id":null}
        """;

    [Fact]
    public void ChatMessage_maps_user_text_and_badges()
    {
        var e = Map("channel.chat.message", ChatMessage);

        Assert.Equal(TwitchEventTypes.ChatMessage, e.Type);
        Assert.Equal("Viewer32", e.Args["user"]);
        Assert.Equal("viewer32", e.Args["userName"]);
        Assert.Equal("4145994", e.Args["userId"]);
        Assert.Equal("Hi chat Cheer100", e.Args["message"]);
        Assert.Equal("Hi chat Cheer100", e.Args["rawInput"]);
        Assert.Equal("cc106a89-1814-919d-454c-f4f2f970aae7", e.Args["messageId"]);
        Assert.Equal(false, e.Args["isBroadcaster"]);
        Assert.Equal(true, e.Args["isModerator"]);
        Assert.Equal(false, e.Args["isVip"]);
        Assert.Equal(true, e.Args["isSubscriber"]);
        Assert.Equal(100, e.Args["bits"]);
        Assert.Equal("#00FF7F", e.Args["color"]);
    }

    [Fact]
    public void ChatMessage_from_broadcaster_without_cheer()
    {
        var e = Map("channel.chat.message",
            """
            {"broadcaster_user_id":"1337","chatter_user_id":"1337","chatter_user_login":"streamer","chatter_user_name":"Streamer",
             "message_id":"m1","message":{"text":"hello"},"color":"","badges":[{"set_id":"vip","id":"1","info":""}],"cheer":null}
            """);

        Assert.Equal(true, e.Args["isBroadcaster"]);
        Assert.Equal(true, e.Args["isVip"]);
        Assert.Equal(false, e.Args["isSubscriber"]);
        Assert.Equal(0, e.Args["bits"]);
    }

    [Fact]
    public void Follow()
    {
        var e = Map("channel.follow", EventSubMessages.FollowEvent);

        Assert.Equal(TwitchEventTypes.Follow, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal("cool_user", e.Args["userName"]);
        Assert.Equal("1234", e.Args["userId"]);
    }

    [Fact]
    public void Sub_converts_tier()
    {
        var e = Map("channel.subscribe",
            """
            {"user_id":"1234","user_login":"cool_user","user_name":"Cool_User","broadcaster_user_id":"1337",
             "broadcaster_user_login":"cooler_user","broadcaster_user_name":"Cooler_User","tier":"2000","is_gift":true}
            """);

        Assert.Equal(TwitchEventTypes.Sub, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal("2", e.Args["tier"]);
        Assert.Equal(true, e.Args["isGift"]);
    }

    [Fact]
    public void Resub()
    {
        var e = Map("channel.subscription.message",
            """
            {"user_id":"1234","user_login":"cool_user","user_name":"Cool_User","broadcaster_user_id":"1337",
             "broadcaster_user_login":"cooler_user","broadcaster_user_name":"Cooler_User","tier":"1000",
             "message":{"text":"Love the stream! FevziGG","emotes":[{"begin":23,"end":30,"id":"302976485"}]},
             "cumulative_months":15,"streak_months":1,"duration_months":6}
            """);

        Assert.Equal(TwitchEventTypes.Resub, e.Type);
        Assert.Equal("1", e.Args["tier"]);
        Assert.Equal("Love the stream! FevziGG", e.Args["message"]);
        Assert.Equal(15, e.Args["cumulativeMonths"]);
        Assert.Equal(1, e.Args["streakMonths"]);
        Assert.Equal(6, e.Args["durationMonths"]);
    }

    [Fact]
    public void Resub_with_hidden_streak_reports_zero()
    {
        var e = Map("channel.subscription.message",
            """{"user_id":"1","user_login":"a","user_name":"A","tier":"3000","message":{"text":""},"cumulative_months":2,"streak_months":null,"duration_months":1}""");

        Assert.Equal("3", e.Args["tier"]);
        Assert.Equal(0, e.Args["streakMonths"]);
    }

    [Fact]
    public void GiftSub()
    {
        var e = Map("channel.subscription.gift",
            """
            {"user_id":"1234","user_login":"cool_user","user_name":"Cool_User","broadcaster_user_id":"1337",
             "broadcaster_user_login":"cooler_user","broadcaster_user_name":"Cooler_User",
             "total":2,"tier":"1000","cumulative_total":284,"is_anonymous":false}
            """);

        Assert.Equal(TwitchEventTypes.GiftSub, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal(false, e.Args["isAnonymous"]);
        Assert.Equal(2, e.Args["total"]);
        Assert.Equal("1", e.Args["tier"]);
        Assert.Equal(284, e.Args["cumulativeTotal"]);
    }

    [Fact]
    public void GiftSub_anonymous()
    {
        var e = Map("channel.subscription.gift",
            """
            {"user_id":null,"user_login":null,"user_name":null,"broadcaster_user_id":"1337",
             "total":5,"tier":"1000","cumulative_total":null,"is_anonymous":true}
            """);

        Assert.Equal("Anonymous", e.Args["user"]);
        Assert.Equal("", e.Args["userName"]);
        Assert.Equal("", e.Args["userId"]);
        Assert.Equal(true, e.Args["isAnonymous"]);
        Assert.Equal(5, e.Args["total"]);
        Assert.Equal(0, e.Args["cumulativeTotal"]);
    }

    [Fact]
    public void Cheer()
    {
        var e = Map("channel.cheer",
            """
            {"is_anonymous":false,"user_id":"1234","user_login":"cool_user","user_name":"Cool_User",
             "broadcaster_user_id":"1337","broadcaster_user_login":"cooler_user","broadcaster_user_name":"Cooler_User",
             "message":"pogchamp","bits":1000}
            """);

        Assert.Equal(TwitchEventTypes.Cheer, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal(1000, e.Args["bits"]);
        Assert.Equal("pogchamp", e.Args["message"]);
        Assert.Equal(false, e.Args["isAnonymous"]);
    }

    [Fact]
    public void Cheer_anonymous()
    {
        var e = Map("channel.cheer",
            """{"is_anonymous":true,"user_id":null,"user_login":null,"user_name":null,"message":"hi","bits":50}""");

        Assert.Equal("Anonymous", e.Args["user"]);
        Assert.Equal(true, e.Args["isAnonymous"]);
        Assert.Equal(50, e.Args["bits"]);
    }

    [Fact]
    public void Raid_uses_the_raiding_broadcaster()
    {
        var e = Map("channel.raid",
            """
            {"from_broadcaster_user_id":"1234","from_broadcaster_user_login":"cool_user","from_broadcaster_user_name":"Cool_User",
             "to_broadcaster_user_id":"1337","to_broadcaster_user_login":"cooler_user","to_broadcaster_user_name":"Cooler_User",
             "viewers":9001}
            """);

        Assert.Equal(TwitchEventTypes.Raid, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal("cool_user", e.Args["userName"]);
        Assert.Equal("1234", e.Args["userId"]);
        Assert.Equal(9001, e.Args["viewers"]);
    }

    [Fact]
    public void RewardRedemption()
    {
        var e = Map("channel.channel_points_custom_reward_redemption.add",
            """
            {"id":"17fa2df1-ad76-4804-bfa5-a40ef63efe63","broadcaster_user_id":"1337","broadcaster_user_login":"cool_user",
             "broadcaster_user_name":"Cool_User","user_id":"9001","user_login":"cooler_user","user_name":"Cooler_User",
             "user_input":"pogchamp","status":"unfulfilled",
             "reward":{"id":"92af127c-7326-4483-a52b-b0da0be61c01","title":"title","cost":100,"prompt":"reward prompt"},
             "redeemed_at":"2026-01-01T12:00:00Z"}
            """);

        Assert.Equal(TwitchEventTypes.RewardRedemption, e.Type);
        Assert.Equal("Cooler_User", e.Args["user"]);
        Assert.Equal("9001", e.Args["userId"]);
        Assert.Equal("92af127c-7326-4483-a52b-b0da0be61c01", e.Args["rewardId"]);
        Assert.Equal("title", e.Args["rewardTitle"]);
        Assert.Equal(100, e.Args["rewardCost"]);
        Assert.Equal("pogchamp", e.Args["userInput"]);
        Assert.Equal("17fa2df1-ad76-4804-bfa5-a40ef63efe63", e.Args["redemptionId"]);
    }

    [Fact]
    public void StreamOnline_and_offline()
    {
        var online = Map("stream.online",
            """
            {"id":"9001","broadcaster_user_id":"1337","broadcaster_user_login":"cool_user","broadcaster_user_name":"Cool_User",
             "type":"live","started_at":"2026-01-01T12:00:00Z"}
            """);
        var offline = Map("stream.offline",
            """{"broadcaster_user_id":"1337","broadcaster_user_login":"cool_user","broadcaster_user_name":"Cool_User"}""");

        Assert.Equal(TwitchEventTypes.StreamOnline, online.Type);
        Assert.Equal("live", online.Args["streamType"]);
        Assert.Equal(TwitchEventTypes.StreamOffline, offline.Type);
        Assert.Equal("Cool_User", offline.Args["user"]);
    }

    [Fact]
    public void Unknown_type_is_not_mapped() =>
        Assert.Null(EventSubMapper.Map("channel.not_a_type", JsonSerializer.Deserialize<JsonElement>("{}")));

    [Fact]
    public void Args_are_case_insensitive() =>
        Assert.Equal("Cool_User", Map("channel.follow", EventSubMessages.FollowEvent).Args["USER"]);
}
