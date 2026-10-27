using System.Text.Json;
using Cuebot.Kick.Events;
using Cuebot.Kick.Pusher;
using Cuebot.Kick.Tests.Support;

namespace Cuebot.Kick.Tests;

/// <summary>Decoding of the kick.com website's Pusher events, with payloads shaped like the real ones.</summary>
public sealed class PusherDecoderTests
{
    private static PusherFrame Frame(string ev, string dataJson, string channel = "chatrooms.2345678.v2") =>
        new("App\\Events\\" + ev, channel, dataJson);

    private static PusherFrame Frame(string ev, object data, string channel = "chatrooms.2345678.v2") =>
        Frame(ev, JsonSerializer.Serialize(data), channel);

    private static KickEvent One(PusherFrame frame) => Assert.Single(KickPusherDecoder.Decode(frame, 100));

    [Fact]
    public void Chat_message_maps_sender_badges_and_emotes()
    {
        var e = One(Frame("ChatMessageEvent", TestData.Read("pusher-chat.json")));
        Assert.Equal(KickEventTypes.ChatMessage, e.Type);
        Assert.Equal("chat:4c1f0f0a-6c69-4d8c-9d3e-2b8f3a1d7e55", e.DedupeKey);
        var a = e.Args;
        Assert.Equal("Kick", a["platform"]);
        Assert.Equal("realtime", e.Source);
        Assert.Equal("ModAlice", a["user"]);
        Assert.Equal("333", a["userId"]);
        Assert.Equal("first! KEKW", a["message"]);
        Assert.Equal("first! [emote:37226:KEKW]", a["rawMessage"]);
        Assert.Equal(1, a["emoteCount"]);
        Assert.Equal(true, a["isModerator"]);
        Assert.Equal(true, a["isOg"]);
        Assert.Equal(true, a["isSubscriber"]);
        Assert.Equal(3, a["subscribedMonths"]);
        Assert.Equal(false, a["isBroadcaster"]);
        Assert.Equal("#FF5733", a["color"]);
        Assert.Equal(false, a["isReply"]);
    }

    [Fact]
    public void Reply_carries_the_parent_message()
    {
        var a = One(Frame("ChatMessageEvent", TestData.Read("pusher-reply.json"))).Args;
        Assert.Equal(true, a["isReply"]);
        Assert.Equal("0f1e2d3c-4b5a-4968-8776-655443322110", a["replyToMessageId"]);
        Assert.Equal("this is great wow", a["replyToMessage"]);
        Assert.Equal("OtherViewer", a["replyToUser"]);
        Assert.Equal("222", a["replyToUserId"]);
    }

    [Fact]
    public void Broadcaster_is_recognized_by_user_id_without_a_badge()
    {
        var a = One(Frame("ChatMessageEvent", new { id = "x", content = "hi", type = "message", sender = new { id = 100, username = "CuebotDev", identity = new { color = "", badges = Array.Empty<object>() } } })).Args;
        Assert.Equal(true, a["isBroadcaster"]);
    }

    [Fact]
    public void Message_deleted()
    {
        var e = One(Frame("MessageDeletedEvent", new { id = "del-1", message = new { id = "4c1f0f0a" }, aiModerated = false }));
        Assert.Equal(KickEventTypes.MessageDeleted, e.Type);
        Assert.Equal("4c1f0f0a", e.Args["messageId"]);
    }

    [Fact]
    public void Timeout_and_permanent_ban()
    {
        var timeout = One(Frame("UserBannedEvent", new
        {
            id = "b1", user = new { id = 666, username = "Spammer", slug = "spammer" },
            banned_by = new { id = 0, username = "ModAlice", slug = "modalice" }, permanent = false, duration = 10,
            expires_at = "2025-01-14T16:18:06+00:00",
        }));
        Assert.Equal(KickEventTypes.UserTimedOut, timeout.Type);
        Assert.Equal("Spammer", timeout.Args["user"]);
        Assert.Equal("666", timeout.Args["userId"]);
        Assert.Equal(600L, timeout.Args["duration"]);
        Assert.Equal("ModAlice", timeout.Args["moderator"]);

        var ban = One(Frame("UserBannedEvent", new { id = "b2", user = new { id = 666, username = "Spammer" }, banned_by = new { username = "ModAlice" }, permanent = true }));
        Assert.Equal(KickEventTypes.UserBanned, ban.Type);
        Assert.Equal(true, ban.Args["isPermanent"]);
    }

    [Fact]
    public void Unban()
    {
        var e = One(Frame("UserUnbannedEvent", new { id = "u1", user = new { id = 666, username = "Spammer" }, unbanned_by = new { username = "ModAlice" }, permanent = false }));
        Assert.Equal(KickEventTypes.UserUnbanned, e.Type);
        Assert.Equal("Spammer", e.Args["user"]);
    }

    [Theory]
    [InlineData(1, KickEventTypes.Subscription)]
    [InlineData(6, KickEventTypes.Resubscription)]
    public void Subscriptions_by_months(int months, string type)
    {
        var e = One(Frame("SubscriptionEvent", new { chatroom_id = 2345678, username = "SubGuy", months }));
        Assert.Equal(type, e.Type);
        Assert.Equal(months, e.Args["months"]);
        Assert.Equal("SubGuy", e.Args["user"]);
    }

    [Fact]
    public void Gifted_subscriptions_produce_a_mass_gift_and_one_event_per_recipient()
    {
        var events = KickPusherDecoder.Decode(Frame("GiftedSubscriptionsEvent",
            new { chatroom_id = 2345678, gifted_usernames = new[] { "LuckyOne", "LuckyTwo" }, gifter_username = "Generous", gifter_total = 25 }), 100);
        Assert.Equal([KickEventTypes.MassGiftSubscription, KickEventTypes.GiftSubscription, KickEventTypes.GiftSubscription], events.Select(e => e.Type));
        Assert.Equal(2, events[0].Args["gifts"]);
        Assert.Equal(25L, events[0].Args["totalGifted"]);
        Assert.Equal("LuckyOne, LuckyTwo", events[0].Args["recipients"]);
        Assert.Equal("Generous", events[1].Args["user"]);
        Assert.Equal("LuckyTwo", events[2].Args["recipient"]);
        Assert.Equal(true, events[2].Args["fromMassGift"]);
    }

    [Fact]
    public void Single_anonymous_gift()
    {
        var e = One(Frame("GiftedSubscriptionsEvent", new { gifted_usernames = new[] { "LuckyOne" }, gifter_username = "" }));
        Assert.Equal(KickEventTypes.GiftSubscription, e.Type);
        Assert.Equal("Anonymous", e.Args["user"]);
        Assert.Equal(true, e.Args["isAnonymous"]);
        Assert.Equal(false, e.Args["fromMassGift"]);
    }

    [Fact]
    public void Host()
    {
        var e = One(Frame("StreamHostEvent", new { chatroom_id = 2345678, optional_message = "go follow!", number_viewers = 57, host_username = "BigStreamer" }));
        Assert.Equal(KickEventTypes.Host, e.Type);
        Assert.Equal("BigStreamer", e.Args["user"]);
        Assert.Equal(57L, e.Args["viewers"]);
        Assert.Equal("go follow!", e.Args["message"]);
    }

    [Fact]
    public void Stream_start_and_stop_on_the_channel_channel()
    {
        var live = One(Frame("StreamerIsLive", new { livestream = new { id = 99001, channel_id = 1234, session_title = "Testing", source = (string?)null, created_at = "2025-01-14T16:00:00.000000Z" } }, "channel.1234"));
        Assert.Equal(KickEventTypes.StreamOnline, live.Type);
        Assert.Equal("Testing", live.Args["title"]);
        Assert.Equal("99001", live.Args["livestreamId"]);
        var stop = One(Frame("StopStreamBroadcast", new { livestream = new { id = 99001, channel = new { id = 1234, is_banned = false } } }, "channel.1234"));
        Assert.Equal(KickEventTypes.StreamOffline, stop.Type);
    }

    [Fact]
    public void Followers_updated_becomes_a_follow_only_with_a_follower_name()
    {
        var follow = One(Frame("FollowersUpdated", new { followersCount = 101, channel_id = 1234, username = "NewFan", created_at = 1736870946, followed = true }, "channel.1234"));
        Assert.Equal(KickEventTypes.Follow, follow.Type);
        Assert.Equal("NewFan", follow.Args["user"]);
        Assert.Equal(101L, follow.Args["followerCount"]);

        Assert.Empty(KickPusherDecoder.Decode(Frame("FollowersUpdated", new { followersCount = 100, channel_id = 1234, username = (string?)null }), 100));
        Assert.Empty(KickPusherDecoder.Decode(Frame("FollowersUpdated", new { followersCount = 100, username = "Leaver", followed = false }), 100));
    }

    [Fact]
    public void Kicks_gifted()
    {
        var e = One(Frame("KicksGifted", new
        {
            message = "w stream", sender = new { id = 777, username = "KickFan", username_color = "#AABBCC" },
            gift = new { gift_id = "full_send", name = "Full Send", amount = 100, type = "BASIC", tier = "BASIC", character_limit = 0, pinned_time = 0 },
        }));
        Assert.Equal(KickEventTypes.KicksGifted, e.Type);
        Assert.Equal(100L, e.Args["kicks"]);
        Assert.Equal("Full Send", e.Args["giftName"]);
        Assert.Equal("w stream", e.Args["message"]);
        Assert.Equal("777", e.Args["userId"]);
    }

    [Fact]
    public void Reward_redeemed()
    {
        var e = One(Frame("RewardRedeemedEvent", new { reward_title = "Hydrate", user_id = 888, channel_id = 1234, username = "Requester", user_input = "now!", reward_background_color = "#000" }));
        Assert.Equal(KickEventTypes.RewardRedemption, e.Type);
        Assert.Equal("Hydrate", e.Args["rewardTitle"]);
        Assert.Equal("now!", e.Args["userInput"]);
        Assert.Equal("pending", e.Args["status"]);
    }

    [Fact]
    public void Unknown_and_malformed_events_are_ignored()
    {
        Assert.Empty(KickPusherDecoder.Decode(Frame("PollUpdateEvent", new { poll = new { title = "?" } }), 100));
        Assert.Empty(KickPusherDecoder.Decode(Frame("ChatMessageEvent", "{not json"), 100));
        Assert.Empty(KickPusherDecoder.Decode(Frame("ChatMessageEvent", new { content = "no id" }), 100));
    }

    [Fact]
    public void Channel_names_for_chatroom_and_channel()
    {
        Assert.Equal(["chatrooms.5678.v2", "chatroom_5678", "channel.1234", "channel_1234"], KickPusherDecoder.ChannelsFor(1234, 5678));
        Assert.Equal(["chatrooms.5678.v2", "chatroom_5678"], KickPusherDecoder.ChannelsFor(0, 5678));
    }
}
