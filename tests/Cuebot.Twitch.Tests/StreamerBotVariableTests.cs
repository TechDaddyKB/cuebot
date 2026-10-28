using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;
using T = Cuebot.Twitch.TwitchEventTypes;

namespace Cuebot.Twitch.Tests;

/// <summary>
/// Streamer.bot's documented trigger variable names (docs: 3.api/2.triggers/twitch/**) are present on Cuebot's
/// Twitch events, next to Cuebot's own names.
/// </summary>
public sealed class StreamerBotVariableTests
{
    private const string B = """ "broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer" """;
    private const string U = """ "user_id":"1234","user_login":"cool_user","user_name":"Cool_User" """;
    private const string M = """ "moderator_user_id":"999","moderator_user_login":"mod_login","moderator_user_name":"Mod" """;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
    private readonly TwitchEventRouter _router;

    public StreamerBotVariableTests() =>
        _router = new TwitchEventRouter(_time, () => "1337", () => "", new SentMessageTracker(), broadcasterLogin: () => "streamer");

    private IReadOnlyList<BotEvent> Route(string type, string json) => _router.Route(type, JsonSerializer.Deserialize<JsonElement>(json));

    private BotEvent First(string type, string json) => Route(type, json)[0];

    private static string Obj(params string[] parts) => "{" + string.Join(",", parts) + "}";

    // ---------------------------------------------------------------- representative payloads per trigger family

    [Fact]
    public void Reward_redemption_has_rewardName_rawInput_and_the_reward_settings()
    {
        _router.SeedRewards([new TwitchReward("r1", "Hydrate", "Drink", 200, "#FFFF00", true, false, true, true, true, 5, 2, 30, true)]);

        var e = First("channel.channel_points_custom_reward_redemption.add", Obj(B, U,
            """
    "id":"red-1","user_input":"https://streamer.bot/a b $","status":"unfulfilled","redeemed_at":"2026-01-01T12:00:00Z",
    "reward":{"id":"r1","title":"Hydrate","cost":200,"prompt":"Drink"}
"""));

        Assert.Equal("Hydrate", e.Args["rewardName"]);
        Assert.Equal("Hydrate", e.Args["rewardTitle"]);
        Assert.Equal("https://streamer.bot/a b $", e.Args["rawInput"]);
        Assert.Equal("https://streamer.bot/a b $", e.Args["userInput"]);
        Assert.Equal(@"https://streamer\.bot/a b \$", e.Args["rawInputEscaped"]);
        Assert.Equal("https%3A%2F%2Fstreamer.bot%2Fa%20b%20%24", e.Args["rawInputUrlEncoded"]);
        Assert.Equal(true, e.Args["skipsQueue"]);
        Assert.Equal(true, e.Args["requiresUserInput"]);
        Assert.Equal(5, e.Args["maxPerStream"]);
        Assert.Equal(2, e.Args["maxPerUserPerStream"]);
        Assert.Equal(30, e.Args["globalCooldown"]);
        Assert.Equal("#FFFF00", e.Args["backgroundColor"]);
        Assert.Equal("streamer", e.Args["broadcastUserName"]);
        Assert.Equal("1337", e.Args["broadcastUserId"]);
        Assert.Equal("twitch", e.Args["userType"]);
    }

    [Fact]
    public void Reward_updates_keep_the_settings_for_later_redemptions()
    {
        Route("channel.channel_points_custom_reward.add", Obj(B,
            """
    "id":"r2","title":"Song","cost":10,"prompt":"","is_enabled":true,"is_paused":false,"is_in_stock":true,"background_color":"#00FF00",
    "is_user_input_required":true,"should_redemptions_skip_request_queue":false,
    "max_per_stream":{"is_enabled":true,"value":3},"max_per_user_per_stream":{"is_enabled":false,"value":9},
    "global_cooldown":{"is_enabled":true,"seconds":60}
"""));

        var e = First("channel.channel_points_custom_reward_redemption.update", Obj(B, U,
            """ "id":"red-2","user_input":"","status":"fulfilled","reward":{"id":"r2","title":"Song","cost":10,"prompt":""} """));

        Assert.Equal("fulfilled", e.Args["rewardStatus"]);
        Assert.Equal(3, e.Args["maxPerStream"]);
        Assert.Equal(0, e.Args["maxPerUserPerStream"]);
        Assert.Equal(60, e.Args["globalCooldown"]);
        Assert.Equal(true, e.Args["requiresUserInput"]);
    }

    [Fact]
    public void Resub_has_cumulative_monthStreak_and_multi_month()
    {
        var e = First("channel.subscription.message", Obj(B, U,
            """ "tier":"1000","message":{"text":"Love it","emotes":[]},"cumulative_months":15,"streak_months":null,"duration_months":6 """));

        Assert.Equal(15, e.Args["cumulative"]);
        Assert.Equal(15, e.Args["cumulativeMonths"]);
        Assert.Equal(0, e.Args["monthStreak"]);
        Assert.Equal(false, e.Args["streakShared"]);
        Assert.Equal(true, e.Args["isMultiMonth"]);
        Assert.Equal(6, e.Args["multiMonthDuration"]);
        Assert.Equal("Love it", e.Args["rawInput"]);
    }

    private static string Notice(string type, string detail) => Obj(B,
        """
        "chatter_user_id":"49912639","chatter_user_login":"viewer23","chatter_user_name":"viewer23","chatter_is_anonymous":false,
        "color":"#FF0080","badges":[{"set_id":"vip","id":"1","info":""}],"system_message":"system text","message_id":"n-1",
        "message":{"text":"","fragments":[]}
        """,
        $"\"notice_type\":\"{type}\"", $"\"{type}\":{detail}");

    [Fact]
    public void Gift_bomb_and_its_gifts_have_gifts_totalGifts_recipientId_and_subBombCount()
    {
        var bomb = First("channel.chat.notification", Notice("community_sub_gift",
            """{"id":"g1","total":5,"sub_tier":"1000","cumulative_total":50}"""));
        var gift = First("channel.chat.notification", Notice("sub_gift",
            """{"duration_months":3,"cumulative_total":51,"recipient_user_id":"77","recipient_user_name":"Lucky","recipient_user_login":"lucky","sub_tier":"1000","community_gift_id":"g1"}"""));

        Assert.Equal(T.GiftBomb, bomb.Type);
        Assert.Equal(5, bomb.Args["gifts"]);
        Assert.Equal(50, bomb.Args["totalGifts"]);
        Assert.Equal(true, bomb.Args["totalGiftsShared"]);
        Assert.Equal(false, bomb.Args["anonymous"]);

        Assert.Equal(T.SubGift, gift.Type);
        Assert.Equal("77", gift.Args["recipientId"]);
        Assert.Equal("Lucky", gift.Args["recipientUser"]);
        Assert.Equal("lucky", gift.Args["recipientUserName"]);
        Assert.Equal(3, gift.Args["monthsGifted"]);
        Assert.Equal(51, gift.Args["totalSubsGifted"]);
        Assert.Equal(true, gift.Args["fromGiftBomb"]);
        Assert.Equal(5, gift.Args["subBombCount"]);
        Assert.Equal(2, gift.Args["role"]);
        Assert.Equal(255, gift.Args["colorR"]);
        Assert.Equal(128, gift.Args["colorB"]);
    }

    [Fact]
    public void Poll_has_nested_poll_names_and_the_winner()
    {
        var e = First("channel.poll.end", Obj(B,
            """
            "id":"p1","title":"Best?","status":"completed","started_at":"2026-01-01T11:59:00Z","ends_at":"2026-01-01T12:00:00Z",
            "ended_at":"2026-01-01T12:00:00Z",
            "choices":[{"id":"c1","title":"A","votes":3,"channel_points_votes":1},{"id":"c2","title":"B","votes":7,"channel_points_votes":2}]
            """));

        Assert.Equal(T.PollCompleted, e.Type);
        Assert.Equal("Best?", e.Args["poll.Title"]);
        Assert.Equal("p1", e.Args["poll.Id"]);
        Assert.Equal(60, e.Args["poll.Duration"]);
        Assert.Equal(2, e.Args["poll.choices.count"]);
        Assert.Equal("A", e.Args["poll.choice0.title"]);
        Assert.Equal(2, e.Args["poll.choice0.votes"]);
        Assert.Equal(1, e.Args["poll.choice0.rewardVotes"]);
        Assert.Equal(3, e.Args["poll.choice0.totalVotes"]);
        Assert.Equal(10, e.Args["poll.totalVotes"]);
        Assert.Equal(1, e.Args["poll.winningIndex"]);
        Assert.Equal("B", e.Args["poll.winningChoice.title"]);
        Assert.IsType<DateTime>(e.Args["poll.StartedAt"]);
        Assert.Contains("\"choices\"", (string)e.Args["poll._json"]!);
        Assert.Equal("p1", e.Args["pollId"]);
    }

    [Fact]
    public void Prediction_has_nested_outcomes_and_winning_outcome()
    {
        var e = First("channel.prediction.end", Obj(B,
            """
            "id":"pr1","title":"Win?","status":"resolved","winning_outcome_id":"o2","started_at":"2026-01-01T11:58:00Z",
            "locks_at":"2026-01-01T11:59:00Z","ended_at":"2026-01-01T12:00:00Z",
            "outcomes":[{"id":"o1","title":"Yes","color":"blue","users":2,"channel_points":200},
                        {"id":"o2","title":"No","color":"pink","users":1,"channel_points":50}]
            """));

        Assert.Equal("pr1", e.Args["prediction.Id"]);
        Assert.Equal("Win?", e.Args["prediction.Title"]);
        Assert.Equal(60, e.Args["prediction.PredictionWindow"]);
        Assert.Equal("Yes", e.Args["prediction.outcome0.title"]);
        Assert.Equal(200, e.Args["prediction.outcome0.points"]);
        Assert.Equal("No", e.Args["prediction.winningOutcome.title"]);
        Assert.Equal("pink", e.Args["prediction.winningOutcome.color"]);
        Assert.IsType<DateTime>(e.Args["prediction.EndedAt"]);
    }

    [Fact]
    public void Charity_has_charity_names_with_value_valueMinor_and_currency()
    {
        var e = First("channel.charity_campaign.donate", Obj(B, U,
            """
            "id":"d1","campaign_id":"c1","charity_name":"Example","charity_description":"Good","charity_logo":"logo.png",
            "charity_website":"https://example.org","amount":{"value":1050,"decimal_places":2,"currency":"USD"}
            """));

        Assert.Equal("c1", e.Args["charity.campaignId"]);
        Assert.Equal("Example", e.Args["charity.name"]);
        Assert.Equal("logo.png", e.Args["charity.logo"]);
        Assert.Equal(10.50m, e.Args["charity.amount.value"]);
        Assert.Equal(1050L, e.Args["charity.amount.valueMinor"]);
        Assert.Equal("USD", e.Args["charity.amount.currency"]);
    }

    [Fact]
    public void Goal_has_goal_names()
    {
        var e = First("channel.goal.end", Obj(B,
            """
    "id":"g1","type":"follower","description":"Road","current_amount":90,"target_amount":100,"is_achieved":false,
    "started_at":"2026-01-01T10:00:00Z","ended_at":"2026-01-01T12:00:00Z"
"""));

        Assert.Equal("g1", e.Args["goal.id"]);
        Assert.Equal("follower", e.Args["goal.type"]);
        Assert.Equal(90, e.Args["goal.currentAmount"]);
        Assert.Equal(100, e.Args["goal.targetAmount"]);
        Assert.IsType<DateTime>(e.Args["goal.endedAt"]);
    }

    [Fact]
    public void Hype_train_has_top_contributors_and_flags()
    {
        var e = First("channel.hype_train.progress", Obj(B,
            """
            "id":"h1","level":2,"total":700,"progress":200,"goal":800,"type":"treasure","all_time_high_level":4,"all_time_high_total":9000,
            "top_contributions":[{"user_id":"5","user_login":"bitsy","user_name":"Bitsy","type":"bits","total":500},
                                 {"user_id":"6","user_login":"subby","user_name":"Subby","type":"subscription","total":200}],
            "started_at":"2026-01-01T11:50:00Z","expires_at":"2026-01-01T12:05:00Z"
            """));

        Assert.Equal("Bitsy", e.Args["top.bits.user"]);
        Assert.Equal("bitsy", e.Args["top.bits.userName"]);
        Assert.Equal("5", e.Args["top.bits.userId"]);
        Assert.Equal(500, e.Args["top.bits.total"]);
        Assert.Equal("subby", e.Args["top.subscription.userName"]);
        Assert.Equal(0.25, e.Args["percentDecimal"]);
        Assert.Equal(true, e.Args["isTreasureTrain"]);
        Assert.Equal(false, e.Args["isGoldenKappaTrain"]);
        Assert.Equal(9000, e.Args["allTimeHighTotal"]);
        Assert.Equal("h1", e.Args["id"]);
    }

    [Fact]
    public void Hype_train_knows_the_shared_chat_session()
    {
        _router.Route(new BotEvent(T.SharedChatBegin, new Dictionary<string, object?> { ["isHost"] = true, ["userId"] = "1337" }));
        var shared = _router.Route(new BotEvent(T.HypeTrainStart, new Dictionary<string, object?> { ["level"] = 1 }))[0];
        _router.Route(new BotEvent(T.SharedChatEnd, new Dictionary<string, object?> { ["isHost"] = true }));
        var alone = _router.Route(new BotEvent(T.HypeTrainEnd, new Dictionary<string, object?> { ["level"] = 1 }))[0];

        Assert.Equal(true, shared.Args["isInSharedChat"]);
        Assert.Equal(true, shared.Args["isSharedChatHost"]);
        Assert.Equal(false, alone.Args["isInSharedChat"]);
        Assert.Equal(false, alone.Args["isSharedChatHost"]);
    }

    [Fact]
    public void Hype_train_level_up_has_prevLevel()
    {
        const string start = """ "id":"h1","level":1,"total":10,"progress":10,"goal":100 """;
        Route("channel.hype_train.begin", Obj(B, start));
        var events = Route("channel.hype_train.progress", Obj(B, start.Replace("\"level\":1", "\"level\":3")));

        var levelUp = Assert.Single(events, x => x.Type == T.HypeTrainLevelUp);
        Assert.Equal(1, levelUp.Args["prevLevel"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ban_and_timeout_have_createdBy(bool permanent)
    {
        var e = First("channel.ban", Obj(B, U, M,
            $""" "reason":"spam","banned_at":"2026-01-01T12:00:00Z","ends_at":{(permanent ? "null" : "\"2026-01-01T12:10:00Z\"")},"is_permanent":{(permanent ? "true" : "false")} """));

        Assert.Equal(permanent ? T.UserBanned : T.UserTimedOut, e.Type);
        Assert.Equal("999", e.Args["createdById"]);
        Assert.Equal("mod_login", e.Args["createdByUsername"]);
        Assert.Equal("Mod", e.Args["createdByDisplayName"]);
        Assert.IsType<DateTime>(e.Args["createdAt"]);
    }

    [Fact]
    public void Raid_start_send_and_cancel_have_raidUser()
    {
        var start = _router.Route(new BotEvent(T.RaidStart, new Dictionary<string, object?>
        {
            ["user"] = "Target", ["userName"] = "target", ["userId"] = "4242", ["profileImageUrl"] = "https://img.test/p.png",
        }))[0];
        var cancel = _router.Route(new BotEvent(T.RaidCancelled, new Dictionary<string, object?> { ["user"] = "Streamer", ["userId"] = "1337" }))[0];

        Assert.Equal("Target", start.Args["raidUser"]);
        Assert.Equal("target", start.Args["raidUserName"]);
        Assert.Equal("4242", start.Args["raidUserId"]);
        Assert.Equal("https://img.test/p.png", start.Args["raidUserProfileImageURL"]);
        Assert.Equal(@"https://img\.test/p\.png", start.Args["raidUserProfileImageEscaped"]);
        Assert.Equal("target", cancel.Args["raidUserName"]);
        Assert.Equal("https://img.test/p.png", cancel.Args["raidUserProfileImageURL"]);

        _router.Route(new BotEvent(T.RaidStart, new Dictionary<string, object?>
        {
            ["user"] = "Target", ["userName"] = "target", ["userId"] = "4242", ["profileImageUrl"] = "p.png",
        }));
        var send = First("channel.raid", Obj(
            """
    "from_broadcaster_user_id":"1337","from_broadcaster_user_login":"streamer","from_broadcaster_user_name":"Streamer",
    "to_broadcaster_user_id":"4242","to_broadcaster_user_login":"target","to_broadcaster_user_name":"Target","viewers":9
"""));
        Assert.Equal(T.RaidSend, send.Type);
        Assert.Equal("Target", send.Args["raidUser"]);
        Assert.Equal("p.png", send.Args["raidUserProfileImageURL"]);
    }

    [Fact]
    public void Chat_message_has_TwitchChat_names()
    {
        var e = First("channel.chat.message", Obj(B,
            """
            "chatter_user_id":"4145994","chatter_user_login":"viewer32","chatter_user_name":"Viewer32","message_id":"m-1",
            "message":{"text":"Kappa hello world","fragments":[
              {"type":"emote","text":"Kappa","emote":{"id":"25"}},{"type":"text","text":" hello world"}]},
            "color":"#00FF7F","badges":[{"set_id":"moderator","id":"1","info":""},{"set_id":"subscriber","id":"12","info":"14"}],
            "message_type":"text","cheer":null,
            "reply":{"parent_message_id":"pm","parent_user_id":"5","parent_user_login":"parent","parent_user_name":"Parent",
                     "parent_message_body":"hi","thread_message_id":"tm","thread_user_login":"threader"}
            """));

        Assert.Equal("m-1", e.Args["msgId"]);
        Assert.Equal(3, e.Args["role"]);
        Assert.Equal(0, e.Args["colorR"]);
        Assert.Equal(255, e.Args["colorG"]);
        Assert.Equal(127, e.Args["colorB"]);
        Assert.Equal("hello world", e.Args["messageStripped"]);
        Assert.Equal("Kappa", e.Args["input0"]);
        Assert.Equal("world", e.Args["input2"]);
        Assert.Equal("Kappa%20hello%20world", e.Args["rawInputUrlEncoded"]);
        Assert.Equal(true, e.Args["isSubscribed"]);
        Assert.Equal(2, e.Args["badgeCount"]);
        Assert.Equal("pm", e.Args["reply.msgId"]);
        Assert.Equal("parent", e.Args["reply.userLogin"]);
        Assert.Equal("Parent", e.Args["reply.userName"]);
        Assert.Equal("threader", e.Args["reply.threadUserLogin"]);
    }

    [Fact]
    public void Deleted_message_has_targetMessageId_and_the_remembered_text()
    {
        Route("channel.chat.message", Obj(B, """ "chatter_user_id":"5","chatter_user_login":"a","chatter_user_name":"A","message_id":"m-9","message":{"text":"oops"},"badges":[] """));

        var e = First("channel.chat.message_delete", Obj(B, """ "target_user_id":"5","target_user_login":"a","target_user_name":"A","message_id":"m-9" """));

        Assert.Equal("m-9", e.Args["targetMessageId"]);
        Assert.Equal("oops", e.Args["message"]);
    }

    [Fact]
    public void Stream_update_reports_what_changed()
    {
        _router.SeedChannel(new TwitchChannelInfo("1337", "streamer", "Streamer", "en", "1", "Art", "Old title", [], [], false));

        var e = First("channel.update", Obj(B, """ "title":"New title","language":"en","category_id":"1","category_name":"Art","content_classification_labels":[] """));

        Assert.Equal("New title", e.Args["status"]);
        Assert.Equal(true, e.Args["statusUpdate"]);
        Assert.Equal(false, e.Args["gameUpdate"]);
        Assert.Equal("Old title", e.Args["oldStatus"]);
        Assert.Equal("Art", e.Args["oldGameName"]);
    }

    [Fact]
    public void Broadcaster_seed_adds_display_name_and_type()
    {
        _router.SeedBroadcaster(new TwitchUser("1337", "streamer", "StreamerGuy", "", "partner", "", "", "", null));

        var e = First("channel.follow", Obj(B, U, """ "followed_at":"2026-01-01T12:00:00Z" """));

        Assert.Equal("StreamerGuy", e.Args["broadcastUser"]);
        Assert.Equal(true, e.Args["broadcastIsPartner"]);
        Assert.Equal(false, e.Args["broadcastIsAffiliate"]);
    }

    [Fact]
    public void Blocked_terms_have_moderator_names_and_numbered_terms()
    {
        var e = First("automod.terms.update", Obj(B,
            """ "moderator_user_id":"999","moderator_user_login":"mod_login","moderator_user_name":"Mod","action":"add_blocked","from_automod":false,"terms":["bad","worse"] """));

        Assert.Equal("Mod", e.Args["moderatorDisplayName"]);
        Assert.Equal("mod_login", e.Args["moderatorUsername"]);
        Assert.Equal("999", e.Args["moderatorId"]);
        Assert.Equal("bad", e.Args["term.0"]);
        Assert.Equal("worse", e.Args["term.1"]);
    }

    // ---------------------------------------------------------------- every trigger family: the names are present

    /// <summary>
    /// One row per Twitch trigger with documented variables: Cuebot's event type, the arguments Cuebot's mapper sets
    /// (enough to derive the aliases), and Streamer.bot's names that must then be present.
    /// </summary>
    public static TheoryData<string, string, string> Documented => new()
    {
        { T.RewardRedemption, "rewardTitle=X;userInput=hi;userId=1", "rewardName,rawInput,rawInputEscaped,rawInputUrlEncoded" },
        { T.RewardRedemptionUpdated, "rewardTitle=X;userInput=hi;status=fulfilled;userId=1", "rewardName,rewardStatus,rawInputEscaped" },
        { T.AutomaticRewardRedemption, "rewardType=gigantify_an_emote;emoteId=25;emoteName=Kappa;message=Kappa;userId=1", "gigantifiedEmoteId,gigantifiedEmoteName,gigantifiedEmoteUrl" },
        { T.AutomaticRewardRedemption, "rewardType=random_sub_emote_unlock;emoteId=25;emoteName=Kappa;message=;userId=1", "unlockedEmoteId,unlockedEmoteName" },
        { T.Resub, "cumulativeMonths=3;streakMonths=2;durationMonths=1;message=hi;userId=1", "cumulative,monthStreak,isMultiMonth,multiMonthDuration" },
        { T.SharedChatResub, "cumulativeMonths=3;streakMonths=2;durationMonths=1;message=;messageId=m;userId=1;sourceBroadcasterId=9", "cumulative,monthStreak,source.userId" },
        { T.SubGift, "recipientUserId=7;durationMonths=1;cumulativeTotal=4;isAnonymous=false;communityGiftId=;userId=1", "recipientId,monthsGifted,totalSubsGifted,anonymous,fromGiftBomb,random" },
        { T.SharedChatSubGift, "recipientUserId=7;durationMonths=1;cumulativeTotal=4;isAnonymous=false;communityGiftId=g;userId=1", "recipientId,monthsGifted,fromGiftBomb" },
        { T.GiftBomb, "total=5;cumulativeTotal=9;isAnonymous=false;userId=1", "gifts,totalGifts,anonymous" },
        { T.SharedChatGiftBomb, "total=5;cumulativeTotal=9;isAnonymous=false;userId=1", "gifts,totalGifts,anonymous" },
        { T.GiftSub, "total=5;cumulativeTotal=9;isAnonymous=false;userId=1", "gifts,totalGifts,anonymous" },
        { T.PrimePaidUpgrade, "tier=1;userId=1", "upgradeTier,upgradeTierString" },
        { T.PollCreated, "pollId=p;title=Q;startedAt=2026-01-01T11:59:00Z;endsAt=2026-01-01T12:01:00Z;choiceCount=1;choice0Id=c;choice0Title=A;choice0Votes=1;choice0ChannelPointsVotes=0", "poll.Title,poll.Id,poll.StartedAt,poll.Duration,poll.DurationRemaining,poll.choices.count,poll.choice0.title,poll.choice0.id,poll.choice0.votes,poll.choice0.rewardVotes,poll.choice0.totalVotes,poll.votes,poll.rewardVotes,poll.totalVotes" },
        { T.PollUpdated, "pollId=p;title=Q;choiceCount=1;choice0Title=A;choice0Votes=1", "poll.Title,poll.choice0.title,poll.totalVotes" },
        { T.PollTerminated, "pollId=p;title=Q;choiceCount=1;choice0Id=c;choice0Title=A;choice0Votes=1", "poll.endedAt,poll.winningIndex,poll.winningChoice.id,poll.winningChoice.title,poll.winningChoice.votes,poll.winningChoice.rewardVotes,poll.winningChoice.totalVotes" },
        { T.PollArchived, "pollId=p;title=Q;choiceCount=1;choice0Title=A;choice0Votes=1", "poll.endedAt,poll.winningIndex" },
        { T.PredictionCreated, "predictionId=p;title=Q;startedAt=2026-01-01T11:59:00Z;locksAt=2026-01-01T12:01:00Z;outcomeCount=1;outcome0Id=o;outcome0Title=A;outcome0Users=1;outcome0ChannelPoints=5;outcome0Color=blue", "prediction.Id,prediction.Title,prediction.CreatedAt,prediction.PredictionWindow,prediction.outcome0.id,prediction.outcome0.title,prediction.outcome0.users,prediction.outcome0.points,prediction.outcome0.color" },
        { T.PredictionLocked, "predictionId=p;title=Q;lockedAt=2026-01-01T12:00:00Z;outcomeCount=0", "prediction.Id,prediction.LockedAt" },
        { T.PredictionCanceled, "predictionId=p;title=Q;endedAt=2026-01-01T12:00:00Z;outcomeCount=0", "prediction.EndedAt" },
        { T.PredictionCompleted, "predictionId=p;title=Q;winningOutcomeId=o;outcomeCount=1;outcome0Id=o;outcome0Title=A", "prediction.winningOutcome.id,prediction.winningOutcome.title" },
        { T.HypeTrainStart, "hypeTrainId=h;percent=50;isGoldenKappa=false;trainType=regular", "id,percentDecimal,isGoldenKappaTrain,isTreasureTrain,isInSharedChat,isSharedChatHost" },
        { T.WatchStreak, "messageId=n;message=hi;watchStreak=3;systemMessage=s;channelPointsAwarded=5;userId=1", "watchStreakId,systemMessage,watchStreak,channelPointsAwarded" },
        { T.HypeTrainEnd, "hypeTrainId=h;percent=50;isGoldenKappa=true;trainType=golden_kappa", "id,percentDecimal,isGoldenKappaTrain" },
        { T.GoalBegin, "goalId=g;goalType=follower;description=D;currentAmount=1;targetAmount=2;startedAt=2026-01-01T12:00:00Z", "goal.id,goal.type,goal.description,goal.currentAmount,goal.targetAmount,goal.startedAt" },
        { T.GoalProgress, "goalId=g;goalType=follower;description=D;currentAmount=1;targetAmount=2", "goal.id,goal.currentAmount" },
        { T.CharityStarted, "campaignId=c;charityName=N;charityDescription=D;charityWebsite=W;charityLogo=L", "charity.id,charity.name,charity.description,charity.website,charity.logo" },
        { T.CharityProgress, "campaignId=c;charityName=N", "charity.id,charity.name" },
        { T.CharityCompleted, "campaignId=c;charityName=N", "charity.id,charity.name" },
        { T.UserBanned, "moderatorUserId=9;moderatorUserName=m;moderator=M;bannedAt=2026-01-01T12:00:00Z;userId=1", "createdById,createdByUsername,createdByDisplayName,createdAt" },
        { T.UserTimedOut, "moderatorUserId=9;moderatorUserName=m;moderator=M;bannedAt=2026-01-01T12:00:00Z;userId=1", "createdById,createdByUsername,createdByDisplayName,createdAt" },
        { T.UserUnbanned, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1", "createdById,createdByUsername,createdByDisplayName,createdAt" },
        { T.UserUntimedOut, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1", "createdById,createdByUsername,createdByDisplayName,createdAt" },
        { T.SharedChatUserBanned, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1;sourceBroadcasterId=5", "createdById,createdByUsername,createdByDisplayName,createdAt,source.userId" },
        { T.SharedChatUserTimedOut, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1", "createdById,createdAt" },
        { T.SharedChatUserUnbanned, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1", "createdById,createdAt" },
        { T.SharedChatUserUntimedOut, "moderatorUserId=9;moderatorUserName=m;moderator=M;userId=1", "createdById,createdAt" },
        { T.ChatMessageDeleted, "messageId=m;userId=1", "targetMessageId" },
        { T.SharedChatMessageDeleted, "messageId=m;message=x;userId=1", "targetMessageId,message" },
        { T.UserWarned, "moderator=M;moderatorUserName=m;moderatorUserId=9;userId=1", "moderator.userName,moderator.login,moderator.id" },
        { T.SuspiciousUserUpdate, "moderator=M;moderatorUserName=m;moderatorUserId=9;userId=1", "moderator.userName,moderator.login,moderator.id" },
        { T.BlockedTermsAdded, "user=M;userName=m;userId=9", "moderatorDisplayName,moderatorUsername,moderatorId,createdAt" },
        { T.BlockedTermsDeleted, "user=M;userName=m;userId=9", "moderatorDisplayName,moderatorId" },
        { T.PermittedTermsAdded, "user=M;userName=m;userId=9", "moderatorDisplayName,moderatorId" },
        { T.PermittedTermsDeleted, "user=M;userName=m;userId=9", "moderatorDisplayName,moderatorId" },
        { T.ShoutoutCreated, "user=T;userName=t;userId=4;viewers=5", "targetUserId,targetUserLogin,targetUserDisplayName,viewerCount" },
        { T.ShoutoutReceived, "user=T;userName=t;userId=4;viewers=5", "viewerCount" },
        { T.UnbanRequestCreated, "message=please;userId=1", "text" },
        { T.AutomodMessageHeld, "message=bad words;messageId=m;userId=1", "rawInput,input0,input1" },
        { T.AutomodMessageUpdated, "message=bad words;messageId=m;userId=1", "rawInput,input0" },
        { T.RaidStart, "user=T;userName=t;userId=4;profileImageUrl=u", "raidUser,raidUserName,raidUserId,raidUserProfileImageURL,raidUserProfileImageEscaped" },
        { T.Pyramid, "width=3;emote=Kappa;totalPyramids=1;userId=1", "pyramidWidth,pyramidEmote,totalPyramids" },
        { T.PyramidBroken, "width=3;emote=Kappa;totalPyramidsBroken=1;user=O;userName=o;userId=1", "pyramidWidth,pyramidEmote,pyramidOwnerId,pyramidOwnerUsername,pyramidOwnerDisplayName" },
        { T.StreamUpdate, "title=T;gameId=1;gameName=G", "status,gameId,gameName" },
        { T.StreamOffline, "user=S", "endedAt" },
        { T.AdRun, "duration=30;isAutomatic=true;userId=1", "adLength,adLengthMs,adScheduled" },
        { T.PresentViewers, "count=0", "isTest" },
        { T.Announcement, "message=hi;messageId=m;announcementColor=purple;userId=1", "announceColor,msgId,rawInput" },
        { T.SharedChatAnnouncement, "message=hi;messageId=m;announcementColor=purple;isSharedChat=true;userId=1", "announceColor,fromSharedChat,messageStripped" },
        { T.SubscriberModeOn, "subscriberMode=true;uniqueChatMode=false", "subscriberOnly" },
        { T.SubscriberModeOff, "subscriberMode=false", "subscriberOnly" },
        { T.UniqueModeOn, "uniqueChatMode=true", "uniqueMode" },
        { T.UniqueModeOff, "uniqueChatMode=false", "uniqueMode" },
        { T.SharedChatBegin, "user=H;userName=h;userId=5", "host.userId,host.userLogin,host.userName" },
        { T.SharedChatUpdate, "user=H;userName=h;userId=5", "host.userId" },
        { T.SharedChatEnd, "user=H;userName=h;userId=5", "host.userId" },
        { T.GuestStarGuestUpdate, "moderatorUserName=m;user=G;userName=g;userId=5", "moderatorUserLogin,guestUserName,guestUserLogin,guestUserId" },
        { T.ChatMessage, "message=hi there;messageId=m;isBroadcaster=true;color=#112233;userId=1", "msgId,role,colorR,colorG,colorB,messageStripped,rawInputEscaped,input0,inputEscaped1,inputUrlEncoded0,userType,broadcastUserId" },
        { T.Cheer, "message=Cheer100 hi;bits=100;userId=1", "messageCheermotesStripped,rawInput" },
    };

    [Theory]
    [MemberData(nameof(Documented))]
    public void Every_trigger_family_has_the_documented_names(string type, string args, string expected)
    {
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            input[key] = int.TryParse(value, out var n) ? n : bool.TryParse(value, out var b) ? b : value;
        }

        var routed = _router.Route(new BotEvent(type, input));

        var e = Assert.Single(routed, x => x.Type == type);
        foreach (var name in expected.Split(','))
            Assert.True(e.Args.ContainsKey(name), $"{type} is missing Streamer.bot's '{name}'");
        foreach (var (key, value) in input)
            Assert.True(e.Args.ContainsKey(key), $"{type} lost Cuebot's '{key}'");
    }

    // ---------------------------------------------------------------- placeholders (documented, not sent by Twitch)

    private BotEvent RouteArgs(string type, params (string Key, object? Value)[] args) =>
        Assert.Single(_router.Route(new BotEvent(type, args.ToDictionary(p => p.Key, p => p.Value))), x => x.Type == type);

    [Fact]
    public void Variables_twitch_cannot_supply_are_present_with_neutral_values()
    {
        var sub = RouteArgs(T.Sub, ("user", "Fan"), ("userName", "fan"), ("userId", "1"), ("tier", "1000"));
        Assert.Equal(false, sub.Args["isMultiMonth"]);
        Assert.Equal(0, sub.Args["multiMonthDuration"]);
        Assert.Equal(0, sub.Args["multiMonthTenure"]);
        Assert.Equal(0, sub.Args["subscriptionTier"]);
        Assert.Equal("", sub.Args["userGroups"]);
        Assert.Equal(default(DateTime), sub.Args["userPreviousActive"]);

        Assert.Equal(0, RouteArgs(T.Resub, ("userId", "1"), ("cumulativeMonths", 5)).Args["multiMonthTenure"]);
        Assert.Equal(0, RouteArgs(T.SubGift, ("userId", "1"), ("recipientUserId", "2")).Args["cumulativeMonths"]);
        Assert.Equal(false, RouteArgs(T.GiftBomb, ("userId", "1"), ("total", 5)).Args["bonusGifts"]);
        Assert.Equal("", RouteArgs(T.ShoutoutCreated, ("userId", "5")).Args["shoutoutId"]);

        var online = RouteArgs(T.StreamOnline);
        Assert.Equal("", online.Args["game"]);
        Assert.Equal(0, online.Args["tagCount"]);
        Assert.Empty(Assert.IsType<List<string>>(online.Args["tags"]));
        Assert.Equal("", online.Args["tagsDelimited"]);

        var update = RouteArgs(T.StreamUpdate, ("title", "t"), ("gameId", "1"), ("gameName", "Art"));
        Assert.Equal("", update.Args["gameBoxArt"]);
        Assert.Equal("", update.Args["oldGameBoxArt"]);

        var session = RouteArgs(T.SharedChatBegin, ("user", "H"), ("userName", "h"), ("userId", "5"), ("isHost", false));
        Assert.Equal(false, session.Args["host.isSubscribed"]);
        Assert.Equal(false, session.Args["host.isModerator"]);
        Assert.Equal(false, session.Args["host.isVip"]);

        var shared = RouteArgs(T.ChatMessage, ("message", "hi"), ("messageId", "m1"), ("userId", "1"), ("sourceBroadcasterId", "77"));
        Assert.Equal(false, shared.Args["isAction"]);
        Assert.Equal(false, shared.Args["source.isSubscribed"]);
        Assert.Equal(false, shared.Args["source.isModerator"]);
        Assert.Equal(false, shared.Args["source.isVip"]);

        Assert.Equal(false, RouteArgs(T.PresentViewers, ("count", 0)).Args["isLive"]);
    }

    [Fact]
    public void Real_values_win_over_placeholders()
    {
        var sub = RouteArgs(T.Sub, ("userId", "1"), ("durationMonths", 3));
        Assert.Equal(true, sub.Args["isMultiMonth"]);
        Assert.Equal(3, sub.Args["multiMonthDuration"]);
        Assert.Equal(true, RouteArgs(T.PresentViewers, ("count", 0), ("isLive", true)).Args["isLive"]);
    }

    [Fact]
    public void Stream_online_gets_the_last_known_category_and_chat_knows_about_a_shared_chat_session()
    {
        _router.SeedChannel(new TwitchChannelInfo("1337", "streamer", "Streamer", "en", "509658", "Just Chatting", "Title", [], [], false));
        var online = RouteArgs(T.StreamOnline);
        Assert.Equal("Just Chatting", online.Args["game"]);
        Assert.Equal("509658", online.Args["gameId"]);

        Assert.Equal(false, RouteArgs(T.ChatMessage, ("message", "hi"), ("messageId", "m1"), ("userId", "1")).Args["inSharedChat"]);
        RouteArgs(T.SharedChatBegin, ("user", "H"), ("userName", "h"), ("userId", "5"), ("isHost", true));
        Assert.Equal(true, RouteArgs(T.ChatMessage, ("message", "hi"), ("messageId", "m2"), ("userId", "1")).Args["inSharedChat"]);
        RouteArgs(T.SharedChatEnd, ("user", "H"), ("userName", "h"), ("userId", "5"));
        Assert.Equal(false, RouteArgs(T.ChatMessage, ("message", "hi"), ("messageId", "m3"), ("userId", "1")).Args["inSharedChat"]);
    }
}
