using Cuebot.Core.Events;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Compat;
using static Cuebot.YouTube.Tests.Support.ChatJson;
using Y = Cuebot.YouTube.YouTubeEventTypes;

namespace Cuebot.YouTube.Tests;

/// <summary>
/// Streamer.bot's documented trigger variable names (docs: 3.api/2.triggers/youtube/**) are present on Cuebot's
/// YouTube events, next to Cuebot's own names.
/// </summary>
public sealed class StreamerBotVariableTests
{
    private static readonly YouTubeAccount Broadcaster = new() { ChannelId = Owner, ChannelTitle = "Owner", ProfileImageUrl = "https://yt3.test/owner.jpg" };

    private static readonly YouTubeBroadcast Live = new("b1", "Dev testing", "desc", "chat1", "live", "public", Owner, "", null, null, null);

    private readonly ChatContext _ctx = new("b1", "chat1", new ChatterDirectory());

    private BotEvent Map(string json) =>
        StreamerBotVariables.Apply(ChatItemMapper.Map(Parse(json), _ctx).Events[0], Broadcaster, Live);

    [Fact]
    public void Chat_message_has_YouTubeUser_broadcaster_broadcast_and_input_names()
    {
        var e = Map(Text("m1", "hello big world", Author(Alice, "Alice", member: true)));

        Assert.Equal("youtube", e.Args["userType"]);
        Assert.Equal(true, e.Args["userIsSponsor"]);
        Assert.Equal("Owner", e.Args["broadcastUsername"]);
        Assert.Equal(Owner, e.Args["broadcastUserId"]);
        Assert.Equal("https://yt3.test/owner.jpg", e.Args["broadcastUserProfileImage"]);
        Assert.Equal("b1", e.Args["broadcast.id"]);
        Assert.Equal("Dev testing", e.Args["broadcast.title"]);
        Assert.Equal("public", e.Args["broadcast.privacy"]);
        Assert.Equal("live", e.Args["broadcast.status"]);
        Assert.Equal("big", e.Args["input1"]);
        Assert.Equal("big", e.Args["input1Escaped"]);
        Assert.Equal("hello%20big%20world", e.Args["rawInputUrlEncoded"]);
        Assert.Equal("hello big world", e.Args["rawInputEscaped"]);
    }

    [Fact]
    public void Super_chat_has_microAmount_and_currencyCode()
    {
        var e = Map(Item("s1", "superChatEvent",
            """ "superChatDetails":{"amountMicros":"5000000","currency":"USD","amountDisplayString":"$5.00","userComment":"great","tier":2} """));

        Assert.Equal(5_000_000L, e.Args["microAmount"]);
        Assert.Equal("USD", e.Args["currencyCode"]);
        Assert.Equal(2, e.Args["tier"]);
    }

    [Fact]
    public void Poll_has_nested_poll_names()
    {
        var e = Map(Item("p1", "pollEvent",
            """
            "pollDetails":{"id":"poll1","metadata":{"questionText":"Best fruit?","status":"closed",
                "options":[{"optionText":"Apple","tally":"3"},{"optionText":"Pear","tally":"5"}]}}
            """, Author(Owner, "Owner", owner: true)));

        Assert.Equal(Y.PollClosed, e.Type);
        Assert.Equal("Best fruit?", e.Args["poll.question"]);
        Assert.Equal(8L, e.Args["poll.totalVotes"]);
        Assert.Equal(2, e.Args["poll.options.count"]);
        Assert.Equal("Apple", e.Args["poll.option0.text"]);
        Assert.Equal(5L, e.Args["poll.option1.votes"]);
        Assert.Equal("Pear", e.Args["poll.winningOption.text"]);
        Assert.Equal(5L, e.Args["poll.winningOption.votes"]);
    }

    [Fact]
    public void Ban_and_timeout_have_banDuration()
    {
        var e = Map(Item("x1", "userBannedEvent",
            $$""" "userBannedDetails":{"bannedUserDetails":{"channelId":"{{Alice}}","displayName":"Alice"},"banType":"temporary","banDurationSeconds":"300"} """));

        Assert.Equal(Y.UserTimedOut, e.Type);
        Assert.Equal(300L, e.Args["banDuration"]);
        Assert.Equal("temporary", e.Args["banType"]);
    }

    [Fact]
    public void Memberships_have_count_tier_id_and_gifter_names()
    {
        var gift = Map(Item("mg1", "membershipGiftingEvent",
            """ "membershipGiftingDetails":{"giftMembershipsCount":5,"giftMembershipsLevelName":"Gold"} """));
        Assert.Equal(5, gift.Args["count"]);
        Assert.Equal("Gold", gift.Args["tier"]);
        Assert.Equal("mg1", gift.Args["id"]);

        Map(Text("c1", "hello", Author(Owner, "Owner", member: true)));
        var received = Map(Item("r1", "giftMembershipReceivedEvent",
            $$""" "giftMembershipReceivedDetails":{"memberLevelName":"Gold","gifterChannelId":"{{Owner}}","associatedMembershipGiftingMessageId":"mg1"} """));
        Assert.Equal("mg1", received.Args["id"]);
        Assert.Equal("Gold", received.Args["tier"]);
        Assert.Equal("Owner", received.Args["gifterUser"]);
        Assert.Equal("Owner", received.Args["gifterUserName"]);
        Assert.Equal(Owner, received.Args["gifterUserId"]);
        Assert.Equal("youtube", received.Args["gifterUserType"]);
        Assert.Equal("https://yt3.test/" + Owner + ".jpg", received.Args["gifterProfileUrl"]);
        Assert.Equal(true, received.Args["gifterIsSponsor"]);
    }

    [Fact]
    public void Jewels_gift_has_gift_names()
    {
        var e = Map(Item("j1", "giftEvent",
            """ "giftDetails":{"jewelsAmount":50,"giftName":"Rose","comboCount":3,"altText":"A rose","altTextLanguage":"en","hasVisualEffect":true} """));

        Assert.Equal(50L, e.Args["gift.jewelsAmount"]);
        Assert.Equal("Rose", e.Args["gift.name"]);
        Assert.Equal(3, e.Args["gift.comboCount"]);
        Assert.Equal(true, e.Args["gift.isCombo"]);
        Assert.Equal("A rose", e.Args["gift.altText"]);
        Assert.Equal("en", e.Args["gift.altTextLanguage"]);
        Assert.Equal(true, e.Args["gift.hasVisualEffect"]);
    }

    [Fact]
    public void Statistics_have_every_documented_count()
    {
        var e = StreamerBotVariables.Apply(YouTubeEvents.Statistics(Live, new BroadcastStatistics(12, 100, 5, 3), Broadcaster), Broadcaster, Live);

        foreach (var name in (string[])["likeCount", "dislikeCount", "viewCount", "favoriteCount", "commentCount", "concurrentViewers"])
            Assert.True(e.Args.ContainsKey(name), name);
        Assert.False(e.Args.ContainsKey("userType"));
    }

    [Fact]
    public void Cuebot_names_are_kept_and_non_youtube_events_are_untouched()
    {
        var e = Map(Text("m1", "hi"));
        Assert.Equal("hi", e.Args["message"]);
        Assert.Equal("Alice", e.Args["user"]);

        var neutral = new BotEvent(Y.NeutralUserBanned, new Dictionary<string, object?> { ["userId"] = Alice });
        Assert.Same(neutral, StreamerBotVariables.Apply(neutral, Broadcaster, Live));
    }

    [Fact]
    public void Variables_the_data_api_cannot_supply_are_present_with_neutral_values()
    {
        var e = Map(Text("m1", "hello"));
        Assert.Equal("", e.Args["parts"]);
        Assert.Equal("", e.Args["emotes"]);
        Assert.Equal(false, e.Args["isSubscribed"]);
        Assert.Equal(default(DateTime), e.Args["userPreviousActive"]);
        Assert.Equal(default(DateTime), e.Args["broadcast.publishedAt"]);
        Assert.Equal("", e.Args["broadcast.defaultLanguage"]);
        Assert.Equal("", e.Args["broadcast.defaultAudioLanguage"]);
        Assert.Equal("", e.Args["broadcast.tags"]);

        var sticker = Map(Item("s2", "superStickerEvent",
            """ "superStickerDetails":{"amountMicros":"2000000","currency":"USD","amountDisplayString":"$2.00","tier":1,"superStickerMetadata":{"stickerId":"st1","altText":"Wave","language":"en"}} """));
        Assert.Equal("", sticker.Args["stickerImageUrl"]);

        var stats = StreamerBotVariables.Apply(YouTubeEvents.Statistics(Live, new BroadcastStatistics(12, 100, 5, 3), Broadcaster), Broadcaster, Live);
        Assert.False(stats.Args.ContainsKey("isSubscribed"));   // no user on broadcast events
    }
}
