using Cuebot.Core.Events;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Tests.Support;
using static Cuebot.YouTube.Tests.Support.ChatJson;

namespace Cuebot.YouTube.Tests;

public sealed class ChatItemMapperTests
{
    private readonly ChatContext _ctx = new("broadcast1", "chat1", new ChatterDirectory()) { BotChannelId = Bob };

    private ChatItemResult Map(string json) => ChatItemMapper.Map(Parse(json), _ctx);

    private BotEvent Single(string json, string type)
    {
        var result = Map(json);
        var e = Assert.Single(result.Events);
        Assert.Equal(type, e.Type);
        Assert.Equal("YouTube", e.Args["platform"]);
        return e;
    }

    [Fact]
    public void Text_message_maps_author_roles_and_ids()
    {
        var e = Single(Text("m1", "hello world", Author(Alice, "Alice", moderator: true, member: true, verified: true)), YouTubeEventTypes.ChatMessage);
        Assert.Equal("Alice", e.Args["user"]);
        Assert.Equal("Alice", e.Args["userName"]);
        Assert.Equal(Alice, e.Args["userId"]);
        Assert.Equal("hello world", e.Args["message"]);
        Assert.Equal("hello world", e.Args["rawInput"]);
        Assert.Equal("m1", e.Args["messageId"]);
        Assert.Equal("broadcast1", e.Args["broadcastId"]);
        Assert.Equal("chat1", e.Args["liveChatId"]);
        Assert.Equal(true, e.Args["isModerator"]);
        Assert.Equal(true, e.Args["isSubscriber"]);
        Assert.Equal(true, e.Args["isMember"]);
        Assert.Equal(true, e.Args["isVerified"]);
        Assert.Equal(false, e.Args["isBroadcaster"]);
        Assert.Equal(false, e.Args["isBotAccount"]);
        Assert.Equal("https://yt3.test/" + Alice + ".jpg", e.Args["userProfileUrl"]);
    }

    [Fact]
    public void Owner_is_broadcaster_and_bot_account_is_flagged()
    {
        var owner = Single(Text("m1", "hi", Author(Owner, "Owner", owner: true)), YouTubeEventTypes.ChatMessage);
        Assert.Equal(true, owner.Args["isBroadcaster"]);
        var bot = Single(Text("m2", "beep", Author(Bob, "Bob")), YouTubeEventTypes.ChatMessage);
        Assert.Equal(true, bot.Args["isBotAccount"]);
    }

    [Fact]
    public void Super_chat_carries_amount_currency_and_tier()
    {
        var e = Single(Item("sc1", "superChatEvent", """
            "superChatDetails":{"amountMicros":"5000000","currency":"USD","amountDisplayString":"$5.00","userComment":"great stream","tier":2}
            """), YouTubeEventTypes.SuperChat);
        Assert.Equal(5.0, e.Args["amount"]);
        Assert.Equal(5_000_000L, e.Args["amountMicros"]);
        Assert.Equal("USD", e.Args["currency"]);
        Assert.Equal("$5.00", e.Args["amountText"]);
        Assert.Equal(2, e.Args["tier"]);
        Assert.Equal("great stream", e.Args["message"]);
    }

    [Fact]
    public void Super_sticker_carries_sticker_and_amount()
    {
        var e = Single(Item("ss1", "superStickerEvent", """
            "superStickerDetails":{"superStickerMetadata":{"stickerId":"st_1","altText":"Dancing cat","language":"en"},
                                   "amountMicros":2000000,"currency":"EUR","amountDisplayString":"€2.00","tier":1}
            """), YouTubeEventTypes.SuperSticker);
        Assert.Equal(2.0, e.Args["amount"]);
        Assert.Equal("EUR", e.Args["currency"]);
        Assert.Equal("st_1", e.Args["stickerId"]);
        Assert.Equal("Dancing cat", e.Args["stickerAltText"]);
        Assert.Equal("Dancing cat", e.Args["message"]);
    }

    [Fact]
    public void New_sponsor_maps_level_and_upgrade()
    {
        var e = Single(Item("ns1", "newSponsorEvent", """ "newSponsorDetails":{"memberLevelName":"Gold","isUpgrade":true} """),
            YouTubeEventTypes.NewSponsor);
        Assert.Equal("Gold", e.Args["levelName"]);
        Assert.Equal(true, e.Args["isUpgrade"]);
    }

    [Fact]
    public void Member_milestone_maps_months_and_comment()
    {
        var e = Single(Item("mm1", "memberMilestoneChatEvent",
            """ "memberMilestoneChatDetails":{"userComment":"a year!","memberMonth":12,"memberLevelName":"Silver"} """),
            YouTubeEventTypes.MemberMilestone);
        Assert.Equal(12, e.Args["months"]);
        Assert.Equal("Silver", e.Args["levelName"]);
        Assert.Equal("a year!", e.Args["message"]);
    }

    [Fact]
    public void Membership_gifting_maps_count_and_level()
    {
        var e = Single(Item("mg1", "membershipGiftingEvent",
            """ "membershipGiftingDetails":{"giftMembershipsCount":5,"giftMembershipsLevelName":"Gold"} """),
            YouTubeEventTypes.MembershipGift);
        Assert.Equal(5, e.Args["total"]);
        Assert.Equal("Gold", e.Args["levelName"]);
    }

    [Fact]
    public void Gift_membership_received_names_the_gifter_when_seen()
    {
        Map(Text("m0", "hi", Author(Owner, "Generous")));
        var e = Single(Item("gr1", "giftMembershipReceivedEvent",
            $$""" "giftMembershipReceivedDetails":{"memberLevelName":"Gold","gifterChannelId":"{{Owner}}","associatedMembershipGiftingMessageId":"mg1"} """),
            YouTubeEventTypes.GiftMembershipReceived);
        Assert.Equal(Owner, e.Args["gifterId"]);
        Assert.Equal("Generous", e.Args["gifterName"]);
        Assert.Equal("mg1", e.Args["giftId"]);
        Assert.Equal("Alice", e.Args["user"]);
    }

    [Fact]
    public void Jewels_gift_reads_the_amount()
    {
        var e = Single(Item("j1", "giftEvent", """ "giftDetails":{"giftName":"Rose","jewelsAmount":50,"comboCount":3} """),
            YouTubeEventTypes.JewelsGifted);
        Assert.Equal(50L, e.Args["jewels"]);
        Assert.Equal("Rose", e.Args["giftName"]);
        Assert.Equal(3, e.Args["comboCount"]);
    }

    [Fact]
    public void Message_deleted_names_the_original_author_and_publishes_the_neutral_event()
    {
        Map(Text("m1", "bad words", Author(Alice, "Alice")));
        var result = Map(Item("d1", "messageDeletedEvent", """ "messageDeletedDetails":{"deletedMessageId":"m1"} """,
            Author(Owner, "Owner", owner: true)));
        Assert.Collection(result.Events,
            e =>
            {
                Assert.Equal(YouTubeEventTypes.MessageDeleted, e.Type);
                Assert.Equal("m1", e.Args["messageId"]);
                Assert.Equal("m1", e.Args["targetMessageId"]);
                Assert.Equal("Alice", e.Args["targetUser"]);
                Assert.Equal(Alice, e.Args["targetUserId"]);
                Assert.Equal("bad words", e.Args["targetMessage"]);
                Assert.Equal("Owner", e.Args["moderator"]);
            },
            e =>
            {
                Assert.Equal("chat.messageDeleted", e.Type);
                Assert.Equal("YouTube", e.Args["platform"]);
                Assert.Equal("m1", e.Args["messageId"]);
                Assert.Equal(Alice, e.Args["userId"]);
            });
    }

    [Fact]
    public void Permanent_ban_is_user_banned()
    {
        var result = Map(Item("b1", "userBannedEvent", $$"""
            "userBannedDetails":{"bannedUserDetails":{"channelId":"{{Alice}}","displayName":"Alice","channelUrl":"u","profileImageUrl":"p"},
                                 "banType":"permanent"}
            """, Author(Owner, "Owner", owner: true)));
        Assert.Collection(result.Events,
            e =>
            {
                Assert.Equal(YouTubeEventTypes.UserBanned, e.Type);
                Assert.Equal("Alice", e.Args["user"]);
                Assert.Equal(Alice, e.Args["userId"]);
                Assert.Equal("Owner", e.Args["moderator"]);
                Assert.Equal(Owner, e.Args["moderatorId"]);
                Assert.Equal("permanent", e.Args["banType"]);
                Assert.False(e.Args.ContainsKey("isBroadcaster"));
            },
            e =>
            {
                Assert.Equal("chat.userBanned", e.Type);
                Assert.Equal(Alice, e.Args["userId"]);
                Assert.Equal("Alice", e.Args["user"]);
            });
    }

    [Fact]
    public void Temporary_ban_is_user_timed_out_with_duration()
    {
        var result = Map(Item("b2", "userBannedEvent", $$"""
            "userBannedDetails":{"bannedUserDetails":{"channelId":"{{Alice}}","displayName":"Alice"},"banType":"temporary","banDurationSeconds":"300"}
            """, Author(Owner, "Owner", owner: true)));
        Assert.Equal(YouTubeEventTypes.UserTimedOut, result.Events[0].Type);
        Assert.Equal(300L, result.Events[0].Args["duration"]);
        Assert.Equal("chat.userTimedOut", result.Events[1].Type);
        Assert.Equal(300L, result.Events[1].Args["duration"]);
    }

    [Fact]
    public void Members_only_mode_events()
    {
        Single(Item("s1", "sponsorOnlyModeStartedEvent", ""), YouTubeEventTypes.SponsorOnlyStarted);
        Single(Item("s2", "sponsorOnlyModeEndedEvent", ""), YouTubeEventTypes.SponsorOnlyEnded);
    }

    [Fact]
    public void Polls_start_update_and_close_with_choices()
    {
        static string Poll(string status, int a, int b) => Item("p1", "pollEvent", $$$"""
            "pollDetails":{"id":"poll1","metadata":{"questionText":"Best fruit?","status":"{{{status}}}",
                "options":[{"optionText":"Apple","tally":"{{{a}}}"},{"optionText":"Pear","tally":"{{{b}}}"}]}}
            """, Author(Owner, "Owner", owner: true));

        var started = Single(Poll("active", 0, 0), YouTubeEventTypes.PollStarted);
        Assert.Equal("poll1", started.Args["pollId"]);
        Assert.Equal("p1", started.Args["pollMessageId"]);
        Assert.Equal("Best fruit?", started.Args["pollTitle"]);
        Assert.Equal(2, started.Args["pollChoiceCount"]);
        Assert.Equal("Apple", started.Args["pollChoice0Title"]);

        var updated = Single(Poll("active", 3, 1), YouTubeEventTypes.PollUpdated);
        Assert.Equal(3L, updated.Args["pollChoice0Votes"]);
        Assert.Equal(4L, updated.Args["pollTotalVotes"]);

        var closed = Single(Poll("closed", 3, 5), YouTubeEventTypes.PollClosed);
        Assert.Equal("Pear", closed.Args["pollWinningChoice"]);
        Assert.Equal(5L, closed.Args["pollWinningVotes"]);

        // As overlay poll states: open while running, the winner once closed.
        var adapter = new YouTubePollAdapter();
        var live = adapter.ToPoll(updated)!;
        Assert.Equal(("youtube", "poll1", "Best fruit?", "active", -1), (live.Source, live.Id, live.Title, live.Status, live.Winner));
        Assert.Equal([3L, 1], live.Choices.Select(c => c.Votes));
        var result = adapter.ToPoll(closed)!;
        Assert.Equal(("ended", 1), (result.Status, result.Winner));
        Assert.Null(adapter.ToPoll(new Cuebot.Core.Events.BotEvent("youtube.chatMessage", new Dictionary<string, object?>())));
    }

    [Fact]
    public void Poll_dedupe_key_changes_with_votes_but_text_key_is_the_id()
    {
        var a = ChatItemMapper.DedupeKey(Parse(Item("p1", "pollEvent", """ "pollDetails":{"metadata":{"options":[{"tally":"1"}]}} """)));
        var b = ChatItemMapper.DedupeKey(Parse(Item("p1", "pollEvent", """ "pollDetails":{"metadata":{"options":[{"tally":"2"}]}} """)));
        Assert.NotEqual(a, b);
        Assert.Equal("m1", ChatItemMapper.DedupeKey(Parse(Text("m1", "x"))));
    }

    [Fact]
    public void Chat_ended_and_unknown_types()
    {
        Assert.True(Map(Item("e1", "chatEndedEvent", "")).ChatEnded);
        Assert.Empty(Map(Item("t1", "tombstone", "")).Events);
        Assert.Empty(Map(Item("x1", "somethingNewEvent", "")).Events);
    }
}
