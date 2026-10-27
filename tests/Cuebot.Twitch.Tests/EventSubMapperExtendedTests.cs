using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.EventSub;

namespace Cuebot.Twitch.Tests;

/// <summary>Mapping of every EventSub type Cuebot subscribes to, with payloads shaped like Twitch's reference examples.</summary>
public sealed class EventSubMapperExtendedTests
{
    private const string B = """ "broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer" """;
    private const string U = """ "user_id":"1234","user_login":"cool_user","user_name":"Cool_User" """;
    private const string M = """ "moderator_user_id":"999","moderator_user_login":"mod_login","moderator_user_name":"Mod" """;

    private static BotEvent Map(string type, string json, string broadcasterId = "1337") =>
        EventSubMapper.Map(type, JsonSerializer.Deserialize<JsonElement>(json), broadcasterId)
        ?? throw new Xunit.Sdk.XunitException($"{type} not mapped");

    private static string Obj(params string[] parts) => "{" + string.Join(",", parts) + "}";

    // ---------------------------------------------------------------- chat message details

    [Fact]
    public void Chat_message_has_kind_emote_ranges_reply_and_shared_chat_source()
    {
        var e = Map("channel.chat.message", Obj(B,
            """
            "chatter_user_id":"4145994","chatter_user_login":"viewer32","chatter_user_name":"Viewer32","message_id":"m-1",
            "message":{"text":"😀 Kappa hi Kappa LUL","fragments":[
              {"type":"text","text":"😀 ","cheermote":null,"emote":null,"mention":null},
              {"type":"emote","text":"Kappa","emote":{"id":"25","emote_set_id":"0","owner_id":"0","format":["static"]}},
              {"type":"text","text":" hi "},
              {"type":"emote","text":"Kappa","emote":{"id":"25"}},
              {"type":"text","text":" "},
              {"type":"emote","text":"LUL","emote":{"id":"425618"}}]},
            "color":"","badges":[{"set_id":"subscriber","id":"12","info":"16"}],"message_type":"channel_points_highlighted",
            "cheer":null,"channel_points_custom_reward_id":null,
            "reply":{"parent_message_id":"p-1","parent_message_body":"hello?","parent_user_id":"55","parent_user_name":"Asker","parent_user_login":"asker",
                     "thread_message_id":"t-1","thread_user_id":"55","thread_user_name":"Asker","thread_user_login":"asker"},
            "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner",
            "source_message_id":"src-1","source_badges":[]
            """));

        Assert.Equal("Message", e.Args["messageKind"]);
        Assert.Equal("25:2-6,11-15/425618:17-19", e.Args["emotes"]);
        Assert.Equal("Kappa,LUL", e.Args["emoteNames"]);
        Assert.Equal(3, e.Args["emoteCount"]);
        Assert.Equal(true, e.Args["isHighlighted"]);
        Assert.Equal(16, e.Args["monthsSubscribed"]);
        Assert.Equal("subscriber/12", e.Args["badges"]);
        Assert.Equal(true, e.Args["isReply"]);
        Assert.Equal("p-1", e.Args["replyParentMessageId"]);
        Assert.Equal("asker", e.Args["replyParentUserName"]);
        Assert.Equal("hello?", e.Args["replyParentMessage"]);
        Assert.Equal(true, e.Args["isSharedChat"]);
        Assert.Equal("2002", e.Args["sourceBroadcasterId"]);
        Assert.Equal("1337", e.Args["broadcasterId"]);
        Assert.Equal("src-1", e.Args["sourceMessageId"]);
    }

    [Fact]
    public void Chat_message_from_own_channel_in_shared_chat_is_not_shared()
    {
        var e = Map("channel.chat.message", Obj(B,
            """ "chatter_user_id":"1","message_id":"x","message":{"text":"hi","fragments":[]},"badges":[],"source_broadcaster_user_id":"1337" """));

        Assert.Equal(false, e.Args["isSharedChat"]);
        Assert.Equal("", e.Args["emotes"]);
        Assert.Equal(false, e.Args["isReply"]);
    }

    // ---------------------------------------------------------------- chat notifications

    private static string Notice(string type, string detail, string extra = "") => Obj(B,
        """
        "chatter_user_id":"49912639","chatter_user_login":"viewer23","chatter_user_name":"viewer23","chatter_is_anonymous":false,
        "color":"","badges":[],"system_message":"system text","message_id":"n-1","message":{"text":"","fragments":[]}
        """ + (extra.Length > 0 ? "," + extra : ""),
        $"\"notice_type\":\"{type}\"", $"\"{type}\":{detail}");

    [Fact]
    public void Announcement_notice()
    {
        var e = Map("channel.chat.notification", Notice("announcement", """{"color":"PURPLE"}""").Replace("\"text\":\"\"", "\"text\":\"Big news\""));

        Assert.Equal(TwitchEventTypes.Announcement, e.Type);
        Assert.Equal("Announcement", e.Args["messageKind"]);
        Assert.Equal("purple", e.Args["announcementColor"]);
        Assert.Equal("Big news", e.Args["message"]);
        Assert.Equal("viewer23", e.Args["user"]);
    }

    [Theory]
    [InlineData("bits_badge_tier", """{"tier":1000}""", TwitchEventTypes.BitsBadgeTier, "tier", 1000)]
    [InlineData("community_sub_gift", """{"id":"cg-1","total":5,"sub_tier":"1000","cumulative_total":20}""", TwitchEventTypes.GiftBomb, "total", 5)]
    [InlineData("sub_gift", """{"duration_months":3,"cumulative_total":null,"recipient_user_id":"7","recipient_user_name":"Lucky","recipient_user_login":"lucky","sub_tier":"2000","community_gift_id":"cg-1"}""",
        TwitchEventTypes.SubGift, "recipientUser", "Lucky")]
    [InlineData("gift_paid_upgrade", """{"gifter_is_anonymous":false,"gifter_user_id":"8","gifter_user_name":"Giver","gifter_user_login":"giver"}""",
        TwitchEventTypes.GiftPaidUpgrade, "gifter", "Giver")]
    [InlineData("prime_paid_upgrade", """{"sub_tier":"1000"}""", TwitchEventTypes.PrimePaidUpgrade, "tier", "1")]
    [InlineData("pay_it_forward", """{"gifter_is_anonymous":true,"gifter_user_id":null,"gifter_user_name":null,"gifter_user_login":null}""",
        TwitchEventTypes.PayItForward, "gifter", "Anonymous")]
    [InlineData("unraid", "{}", TwitchEventTypes.RaidCancelled, "user", "viewer23")]
    public void Notice_types(string notice, string detail, string expectedType, string key, object expected)
    {
        var e = Map("channel.chat.notification", Notice(notice, detail));

        Assert.Equal(expectedType, e.Type);
        Assert.Equal(expected, e.Args[key]);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("resub")]
    [InlineData("raid")]
    [InlineData("charity_donation")]
    public void Notices_covered_by_dedicated_subscriptions_are_skipped(string notice) =>
        Assert.Null(EventSubMapper.Map("channel.chat.notification", JsonSerializer.Deserialize<JsonElement>(Notice(notice, "{}"))));

    [Theory]
    [InlineData("shared_chat_sub", """{"sub_tier":"1000","is_prime":false,"duration_months":1}""", TwitchEventTypes.SharedChatSub)]
    [InlineData("shared_chat_resub", """{"cumulative_months":10,"duration_months":1,"streak_months":2,"sub_tier":"3000","is_prime":false}""", TwitchEventTypes.SharedChatResub)]
    [InlineData("shared_chat_sub_gift", """{"recipient_user_id":"7","recipient_user_name":"Lucky","recipient_user_login":"lucky","sub_tier":"1000"}""", TwitchEventTypes.SharedChatSubGift)]
    [InlineData("shared_chat_community_sub_gift", """{"id":"g","total":10,"sub_tier":"1000"}""", TwitchEventTypes.SharedChatGiftBomb)]
    [InlineData("shared_chat_gift_paid_upgrade", """{"gifter_is_anonymous":false,"gifter_user_name":"G"}""", TwitchEventTypes.SharedChatGiftPaidUpgrade)]
    [InlineData("shared_chat_prime_paid_upgrade", """{"sub_tier":"1000"}""", TwitchEventTypes.SharedChatPrimePaidUpgrade)]
    [InlineData("shared_chat_pay_it_forward", """{"gifter_is_anonymous":false,"gifter_user_name":"G"}""", TwitchEventTypes.SharedChatPayItForward)]
    [InlineData("shared_chat_raid", """{"user_id":"3","user_name":"Raider","user_login":"raider","viewer_count":42,"profile_image_url":""}""", TwitchEventTypes.SharedChatRaid)]
    [InlineData("shared_chat_announcement", """{"color":"BLUE"}""", TwitchEventTypes.SharedChatAnnouncement)]
    public void Shared_chat_notices(string notice, string detail, string expectedType)
    {
        var e = Map("channel.chat.notification", Notice(notice, detail,
            """ "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner","source_message_id":"s" """));

        Assert.Equal(expectedType, e.Type);
        Assert.Equal("2002", e.Args["sourceBroadcasterId"]);
        Assert.Equal(true, e.Args["isSharedChat"]);
    }

    [Fact]
    public void Anonymous_gift_bomb()
    {
        var e = Map("channel.chat.notification", Notice("community_sub_gift", """{"id":"g","total":3,"sub_tier":"1000"}""")
            .Replace("\"chatter_is_anonymous\":false", "\"chatter_is_anonymous\":true"));

        Assert.Equal("Anonymous", e.Args["user"]);
        Assert.Equal(true, e.Args["isAnonymous"]);
    }

    // ---------------------------------------------------------------- chat moderation

    [Fact]
    public void Message_delete_clear_and_clear_user()
    {
        var deleted = Map("channel.chat.message_delete", Obj(B,
            """ "target_user_id":"7734","target_user_name":"Uncool_viewer","target_user_login":"uncool_viewer","message_id":"ab24e0b0" """));
        Assert.Equal(TwitchEventTypes.ChatMessageDeleted, deleted.Type);
        Assert.Equal("ab24e0b0", deleted.Args["messageId"]);
        Assert.Equal("7734", deleted.Args["userId"]);

        Assert.Equal(TwitchEventTypes.ChatCleared, Map("channel.chat.clear", Obj(B)).Type);
        var cleared = Map("channel.chat.clear_user_messages", Obj(B,
            """ "target_user_id":"7734","target_user_name":"Uncool_viewer","target_user_login":"uncool_viewer" """));
        Assert.Equal(TwitchEventTypes.UserMessagesCleared, cleared.Type);
        Assert.Equal("uncool_viewer", cleared.Args["userName"]);
    }

    [Fact]
    public void Chat_settings_nulls_mean_off()
    {
        var e = Map("channel.chat_settings.update", Obj(B,
            """ "emote_mode":true,"follower_mode_duration_minutes":null,"slow_mode_wait_time_seconds":10,"subscriber_mode":false,"unique_chat_mode":false """));

        Assert.Equal(new TwitchChatSettings(true, false, 0, true, 10, false, false), EventSubMapper.ChatSettingsOf(e.Args));
    }

    [Fact]
    public void Whisper()
    {
        var e = Map("user.whisper.message",
            """{"from_user_id":"423374343","from_user_login":"glowingfish","from_user_name":"glowingfish","to_user_id":"424596340","to_user_login":"quotrok","to_user_name":"quotrok","whisper_id":"some-whisper-id","whisper":{"text":"a secret"}}""");

        Assert.Equal(TwitchEventTypes.Whisper, e.Type);
        Assert.Equal("Whisper", e.Args["messageKind"]);
        Assert.Equal("a secret", e.Args["message"]);
        Assert.Equal("glowingfish", e.Args["userName"]);
    }

    // ---------------------------------------------------------------- channel

    [Fact]
    public void Channel_update()
    {
        var e = Map("channel.update", Obj(B,
            """ "title":"Best Stream Ever","language":"en","category_id":"12453","category_name":"Grand Theft Auto","content_classification_labels":["MatureGame"] """));

        Assert.Equal(TwitchEventTypes.StreamUpdate, e.Type);
        Assert.Equal("Best Stream Ever", e.Args["title"]);
        Assert.Equal("Grand Theft Auto", e.Args["gameName"]);
        Assert.Equal("MatureGame", e.Args["contentLabels"]);
    }

    [Fact]
    public void Raids_in_and_out()
    {
        const string incoming = """{"from_broadcaster_user_id":"55","from_broadcaster_user_login":"raider","from_broadcaster_user_name":"Raider","to_broadcaster_user_id":"1337","to_broadcaster_user_login":"streamer","to_broadcaster_user_name":"Streamer","viewers":9001}""";
        const string outgoing = """{"from_broadcaster_user_id":"1337","from_broadcaster_user_login":"streamer","from_broadcaster_user_name":"Streamer","to_broadcaster_user_id":"66","to_broadcaster_user_login":"friend","to_broadcaster_user_name":"Friend","viewers":12}""";

        var raid = Map("channel.raid", incoming);
        var sent = Map("channel.raid", outgoing);

        Assert.Equal(TwitchEventTypes.Raid, raid.Type);
        Assert.Equal("Raider", raid.Args["user"]);
        Assert.Equal(TwitchEventTypes.RaidSend, sent.Type);
        Assert.Equal("Friend", sent.Args["user"]);
        Assert.Equal(12, sent.Args["viewers"]);
    }

    [Fact]
    public void Ad_break()
    {
        var e = Map("channel.ad_break.begin",
            """{"duration_seconds":"60","started_at":"2019-11-16T10:11:12.634234626Z","is_automatic":false,"broadcaster_user_id":"1337","requester_user_id":"1337","requester_user_login":"streamer","requester_user_name":"Streamer"}""".Replace("\"60\"", "60"));

        Assert.Equal(TwitchEventTypes.AdRun, e.Type);
        Assert.Equal(60, e.Args["duration"]);
        Assert.Equal(false, e.Args["isAutomatic"]);
    }

    [Theory]
    [InlineData("channel.shared_chat.begin", TwitchEventTypes.SharedChatBegin)]
    [InlineData("channel.shared_chat.update", TwitchEventTypes.SharedChatUpdate)]
    [InlineData("channel.shared_chat.end", TwitchEventTypes.SharedChatEnd)]
    public void Shared_chat_sessions(string type, string expected)
    {
        var e = Map(type, Obj(B,
            """
            "session_id":"2b64a92a","host_broadcaster_user_id":"1337","host_broadcaster_user_name":"Streamer","host_broadcaster_user_login":"streamer",
            "participants":[{"broadcaster_user_id":"1337","broadcaster_user_name":"Streamer","broadcaster_user_login":"streamer"},
                            {"broadcaster_user_id":"2002","broadcaster_user_name":"Partner","broadcaster_user_login":"partner"}]
            """));

        Assert.Equal(expected, e.Type);
        Assert.Equal("streamer,partner", e.Args["participants"]);
        Assert.Equal(2, e.Args["participantCount"]);
        Assert.Equal(true, e.Args["isHost"]);
    }

    // ---------------------------------------------------------------- bits and power-ups

    [Fact]
    public void Bits_use_power_up_and_cheer()
    {
        var powerUp = Map("channel.bits.use", Obj(B, U,
            """ "bits":50,"type":"power_up","power_up":{"type":"gigantify_an_emote","emote":{"id":"25","name":"Kappa"},"message_effect_id":null},"message":{"text":"Kappa","fragments":[{"type":"emote","text":"Kappa","emote":{"id":"25"}}]} """));
        var cheer = Map("channel.bits.use", Obj(B, U, """ "bits":100,"type":"cheer","power_up":null,"message":{"text":"Cheer100","fragments":[]} """));

        Assert.Equal(TwitchEventTypes.PowerUp, powerUp.Type);
        Assert.Equal("gigantify_an_emote", powerUp.Args["powerUpType"]);
        Assert.Equal("Kappa", powerUp.Args["powerUpEmoteName"]);
        Assert.Equal("25:0-4", powerUp.Args["emotes"]);
        Assert.Equal(TwitchEventTypes.BitsUsed, cheer.Type);
        Assert.Equal(100, cheer.Args["bits"]);
    }

    // ---------------------------------------------------------------- channel points

    [Fact]
    public void Redemption_update_and_automatic_reward()
    {
        var update = Map("channel.channel_points_custom_reward_redemption.update", Obj(B, U,
            """ "id":"red-1","user_input":"pogchamp","status":"fulfilled","reward":{"id":"r1","title":"title","cost":100,"prompt":"reward prompt"},"redeemed_at":"2020-07-15T17:16:03.17106713Z" """));
        Assert.Equal(TwitchEventTypes.RewardRedemptionUpdated, update.Type);
        Assert.Equal("fulfilled", update.Args["status"]);

        var automatic = Map("channel.channel_points_automatic_reward_redemption.add", Obj(B, U,
            """ "id":"auto-1","reward":{"type":"send_highlighted_message","channel_points":100,"emote":null},"message":{"text":"Hello world","fragments":[]},"redeemed_at":"2024-08-12T21:14:34.260398045Z" """));
        Assert.Equal(TwitchEventTypes.AutomaticRewardRedemption, automatic.Type);
        Assert.Equal("send_highlighted_message", automatic.Args["rewardType"]);
        Assert.Equal(100, automatic.Args["rewardCost"]);
        Assert.Equal("Hello world", automatic.Args["message"]);
    }

    [Theory]
    [InlineData("channel.channel_points_custom_reward.add", TwitchEventTypes.RewardCreated)]
    [InlineData("channel.channel_points_custom_reward.update", TwitchEventTypes.RewardUpdated)]
    [InlineData("channel.channel_points_custom_reward.remove", TwitchEventTypes.RewardDeleted)]
    public void Reward_definition_changes(string type, string expected)
    {
        var e = Map(type, Obj(B,
            """ "id":"9001","is_enabled":true,"is_paused":false,"is_in_stock":true,"title":"Cool Reward","cost":100,"prompt":"reward prompt","is_user_input_required":true,"background_color":"#FA1ED2" """));

        Assert.Equal(expected, e.Type);
        Assert.Equal("Cool Reward", e.Args["rewardTitle"]);
        Assert.Equal("#FA1ED2", e.Args["backgroundColor"]);
    }

    // ---------------------------------------------------------------- polls and predictions

    private const string PollChoices =
        """ "id":"1243456","title":"Aren't shoes just really hats for your feet?","choices":[{"id":"123","title":"Yeah!","bits_votes":5,"channel_points_votes":7,"votes":12},{"id":"124","title":"No!","bits_votes":10,"channel_points_votes":4,"votes":14}],"started_at":"2020-07-15T17:16:03.17106713Z" """;

    [Theory]
    [InlineData("channel.poll.begin", "", TwitchEventTypes.PollCreated)]
    [InlineData("channel.poll.progress", "", TwitchEventTypes.PollUpdated)]
    [InlineData("channel.poll.end", "completed", TwitchEventTypes.PollCompleted)]
    [InlineData("channel.poll.end", "terminated", TwitchEventTypes.PollTerminated)]
    [InlineData("channel.poll.end", "archived", TwitchEventTypes.PollArchived)]
    public void Polls(string type, string status, string expected)
    {
        var e = Map(type, Obj(B, PollChoices, $"\"status\":\"{status}\""));

        Assert.Equal(expected, e.Type);
        Assert.Equal(2, e.Args["choiceCount"]);
        Assert.Equal("No!", e.Args["choice1Title"]);
        Assert.Equal(26, e.Args["totalVotes"]);
        Assert.Equal("No!", e.Args["winningChoice"]);
    }

    private const string Outcomes =
        """ "id":"1243456","title":"Aren't shoes just really hats for your feet?","outcomes":[{"id":"1243456","title":"Yeah!","color":"blue","users":10,"channel_points":15000,"top_predictors":[]},{"id":"2243456","title":"No!","color":"pink","users":4,"channel_points":2000}],"started_at":"2020-07-15T17:16:03.17106713Z","locks_at":"2020-07-15T17:21:03.17106713Z" """;

    [Theory]
    [InlineData("channel.prediction.begin", "", TwitchEventTypes.PredictionCreated)]
    [InlineData("channel.prediction.progress", "", TwitchEventTypes.PredictionUpdated)]
    [InlineData("channel.prediction.lock", "", TwitchEventTypes.PredictionLocked)]
    [InlineData("channel.prediction.end", "resolved", TwitchEventTypes.PredictionCompleted)]
    [InlineData("channel.prediction.end", "canceled", TwitchEventTypes.PredictionCanceled)]
    public void Predictions(string type, string status, string expected)
    {
        var e = Map(type, Obj(B, Outcomes, $"\"status\":\"{status}\"", "\"winning_outcome_id\":\"2243456\""));

        Assert.Equal(expected, e.Type);
        Assert.Equal("Yeah!", e.Args["outcome0Title"]);
        Assert.Equal(14, e.Args["totalUsers"]);
        Assert.Equal(17000, e.Args["totalChannelPoints"]);
        Assert.Equal("No!", e.Args["winningOutcome"]);
    }

    // ---------------------------------------------------------------- hype train, goals, charity

    [Theory]
    [InlineData("channel.hype_train.begin", TwitchEventTypes.HypeTrainStart)]
    [InlineData("channel.hype_train.progress", TwitchEventTypes.HypeTrainUpdate)]
    [InlineData("channel.hype_train.end", TwitchEventTypes.HypeTrainEnd)]
    public void Hype_train(string type, string expected)
    {
        var e = Map(type, Obj(B,
            """
            "id":"1b0AsbInCHZW2SQFQkCzqN07Ib2","total":137,"progress":137,"goal":500,"level":2,
            "top_contributions":[{"user_id":"123","user_login":"pogchamp","user_name":"PogChamp","type":"bits","total":50},
                                 {"user_id":"456","user_login":"kappa","user_name":"Kappa","type":"subscription","total":45}],
            "shared_train_participants":null,"started_at":"2020-07-15T17:16:03.17106713Z","expires_at":"2020-07-15T17:16:11.17106713Z",
            "type":"golden_kappa","is_shared_train":false,"all_time_high_level":4
            """));

        Assert.Equal(expected, e.Type);
        Assert.Equal(2, e.Args["level"]);
        Assert.Equal(27, e.Args["percent"]);
        Assert.Equal(true, e.Args["isGoldenKappa"]);
        Assert.Equal("PogChamp", e.Args["topBitsUser"]);
        Assert.Equal(45, e.Args["topSubTotal"]);
        Assert.Equal("PogChamp", e.Args["topContributor"]);
    }

    [Theory]
    [InlineData("channel.goal.begin", TwitchEventTypes.GoalBegin)]
    [InlineData("channel.goal.progress", TwitchEventTypes.GoalProgress)]
    [InlineData("channel.goal.end", TwitchEventTypes.GoalEnd)]
    public void Goals(string type, string expected)
    {
        var e = Map(type, Obj(B,
            """ "id":"12345-cool-event","type":"subscription","description":"Help me get partner!","is_achieved":true,"current_amount":120,"target_amount":220,"started_at":"2021-07-15T17:16:03.17106713Z" """));

        Assert.Equal(expected, e.Type);
        Assert.Equal("subscription", e.Args["goalType"]);
        Assert.Equal(120, e.Args["currentAmount"]);
        Assert.Equal(true, e.Args["isAchieved"]);
    }

    [Fact]
    public void Charity_donation_and_progress()
    {
        var donation = Map("channel.charity_campaign.donate", Obj(B, U,
            """ "id":"a1b2c3","campaign_id":"123-abc","charity_name":"Example name","charity_description":"d","charity_logo":"","charity_website":"https://www.example.com","amount":{"value":10000,"decimal_places":2,"currency":"USD"} """));
        Assert.Equal(TwitchEventTypes.CharityDonation, donation.Type);
        Assert.Equal("Cool_User", donation.Args["user"]);
        Assert.Equal("100.00", donation.Args["amount"]);
        Assert.Equal("USD", donation.Args["currency"]);

        var progress = Map("channel.charity_campaign.progress", Obj(B,
            """ "id":"123-abc","charity_name":"Example name","current_amount":{"value":260000,"decimal_places":2,"currency":"USD"},"target_amount":{"value":1500000,"decimal_places":2,"currency":"USD"} """));
        Assert.Equal(TwitchEventTypes.CharityProgress, progress.Type);
        Assert.Equal("2600.00", progress.Args["currentAmount"]);
        Assert.Equal("15000.00", progress.Args["targetAmount"]);
        Assert.Equal(TwitchEventTypes.CharityStarted, Map("channel.charity_campaign.start", Obj(B)).Type);
        Assert.Equal(TwitchEventTypes.CharityCompleted, Map("channel.charity_campaign.stop", Obj(B)).Type);
    }

    // ---------------------------------------------------------------- shoutouts, shield mode

    [Fact]
    public void Shoutouts_and_shield_mode()
    {
        var created = Map("channel.shoutout.create", Obj(B, M,
            """ "to_broadcaster_user_id":"626262","to_broadcaster_user_login":"sandysanderman","to_broadcaster_user_name":"SandySanderman","started_at":"2022-07-26T17:00:03.17106713Z","viewer_count":860,"cooldown_ends_at":"2022-07-26T17:02:03.17106713Z","target_cooldown_ends_at":"2022-07-26T18:00:03.17106713Z" """));
        Assert.Equal(TwitchEventTypes.ShoutoutCreated, created.Type);
        Assert.Equal("SandySanderman", created.Args["user"]);
        Assert.Equal(860, created.Args["viewers"]);
        Assert.Equal("Mod", created.Args["moderator"]);

        var received = Map("channel.shoutout.receive", Obj(B,
            """ "from_broadcaster_user_id":"626262","from_broadcaster_user_login":"sandysanderman","from_broadcaster_user_name":"SandySanderman","viewer_count":860,"started_at":"2022-07-26T17:00:03.17106713Z" """));
        Assert.Equal(TwitchEventTypes.ShoutoutReceived, received.Type);
        Assert.Equal("sandysanderman", received.Args["userName"]);

        Assert.Equal(TwitchEventTypes.ShieldModeBegin, Map("channel.shield_mode.begin", Obj(B, M, "\"started_at\":\"2022-07-26T17:00:03.17106713Z\"")).Type);
        Assert.Equal(TwitchEventTypes.ShieldModeEnd, Map("channel.shield_mode.end", Obj(B, M, "\"ended_at\":\"2022-07-27T01:30:23.17106713Z\"")).Type);
    }

    // ---------------------------------------------------------------- moderation

    [Fact]
    public void Ban_and_timeout()
    {
        var ban = Map("channel.ban", Obj(B, U, M, """ "reason":"Offensive language","banned_at":"2020-07-15T18:15:11.17106713Z","ends_at":null,"is_permanent":true """));
        var timeout = Map("channel.ban", Obj(B, U, M, """ "reason":"Spam","banned_at":"2020-07-15T18:15:11.17106713Z","ends_at":"2020-07-15T18:25:11.17106713Z","is_permanent":false """));

        Assert.Equal(TwitchEventTypes.UserBanned, ban.Type);
        Assert.Equal("Offensive language", ban.Args["reason"]);
        Assert.Equal("Mod", ban.Args["moderator"]);
        Assert.Equal(TwitchEventTypes.UserTimedOut, timeout.Type);
        Assert.Equal(600, timeout.Args["duration"]);
    }

    [Theory]
    [InlineData("channel.unban", TwitchEventTypes.UserUnbanned)]
    [InlineData("channel.moderator.add", TwitchEventTypes.ModeratorAdded)]
    [InlineData("channel.moderator.remove", TwitchEventTypes.ModeratorRemoved)]
    [InlineData("channel.vip.add", TwitchEventTypes.VipAdded)]
    [InlineData("channel.vip.remove", TwitchEventTypes.VipRemoved)]
    [InlineData("channel.warning.acknowledge", TwitchEventTypes.WarningAcknowledged)]
    public void User_moderation_events(string type, string expected)
    {
        var e = Map(type, Obj(B, U, M));

        Assert.Equal(expected, e.Type);
        Assert.Equal("Cool_User", e.Args["user"]);
        Assert.Equal("1234", e.Args["userId"]);
    }

    [Fact]
    public void Warning_unban_requests_suspicious_users()
    {
        var warned = Map("channel.warning.send", Obj(B, U, M, """ "reason":"cut it out","chat_rules_cited":["Rule 1","Rule 2"] """));
        Assert.Equal(TwitchEventTypes.UserWarned, warned.Type);
        Assert.Equal("Rule 1, Rule 2", warned.Args["chatRulesCited"]);

        var request = Map("channel.unban_request.create", Obj(B, U, """ "id":"60","text":"unban me","created_at":"2023-11-16T10:11:12.634234626Z" """));
        Assert.Equal(TwitchEventTypes.UnbanRequestCreated, request.Type);
        Assert.Equal("unban me", request.Args["message"]);

        Assert.Equal(TwitchEventTypes.UnbanRequestApproved,
            Map("channel.unban_request.resolve", Obj(B, U, M, """ "id":"60","resolution_text":"ok","status":"approved" """)).Type);
        Assert.Equal(TwitchEventTypes.UnbanRequestDenied,
            Map("channel.unban_request.resolve", Obj(B, U, M, """ "id":"60","resolution_text":"no","status":"denied" """)).Type);
        Assert.Null(EventSubMapper.Map("channel.unban_request.resolve",
            JsonSerializer.Deserialize<JsonElement>(Obj(B, U, """ "id":"60","status":"canceled" """))));

        var suspicious = Map("channel.suspicious_user.update", Obj(B, U, M, """ "low_trust_status":"restricted" """));
        Assert.Equal(TwitchEventTypes.SuspiciousUserUpdate, suspicious.Type);
        Assert.Equal("restricted", suspicious.Args["lowTrustStatus"]);

        var message = Map("channel.suspicious_user.message", Obj(B, U,
            """ "low_trust_status":"active_monitoring","shared_ban_channel_ids":["100","200"],"types":["ban_evader"],"ban_evasion_evaluation":"likely","message":{"message_id":"101010","text":"bad stuff pogchamp","fragments":[]} """));
        Assert.Equal(TwitchEventTypes.SuspiciousUserMessage, message.Type);
        Assert.Equal("bad stuff pogchamp", message.Args["message"]);
        Assert.Equal("100,200", message.Args["sharedBanChannelIds"]);
    }

    [Fact]
    public void Automod_held_updated_and_terms()
    {
        var held = Map("automod.message.hold", Obj(B, U,
            """ "message_id":"bad-id","message":{"text":"This is a bad message","fragments":[]},"category":"aggressive","level":3,"held_at":"2022-12-02T15:00:00.00Z" """));
        Assert.Equal(TwitchEventTypes.AutomodMessageHeld, held.Type);
        Assert.Equal("aggressive", held.Args["category"]);
        Assert.Equal("This is a bad message", held.Args["message"]);

        var updated = Map("automod.message.update", Obj(B, U, M, """ "message_id":"bad-id","message":{"text":"x"},"category":"aggressive","level":3,"status":"Approved" """));
        Assert.Equal(TwitchEventTypes.AutomodMessageUpdated, updated.Type);
        Assert.Equal("Approved", updated.Args["status"]);

        foreach (var (action, expected) in new[]
                 {
                     ("add_blocked", TwitchEventTypes.BlockedTermsAdded), ("remove_blocked", TwitchEventTypes.BlockedTermsDeleted),
                     ("add_permitted", TwitchEventTypes.PermittedTermsAdded), ("remove_permitted", TwitchEventTypes.PermittedTermsDeleted),
                 })
        {
            var e = Map("automod.terms.update", Obj(B, M, $"\"action\":\"{action}\"", """ "from_automod":false,"terms":["bad","worse"] """));
            Assert.Equal(expected, e.Type);
            Assert.Equal("bad, worse", e.Args["terms"]);
            Assert.Equal("Mod", e.Args["user"]);
        }
    }

    [Fact]
    public void Every_subscribed_type_has_a_mapping()
    {
        foreach (var spec in EventSubSubscriptions.All.Concat(EventSubSubscriptions.Bot))
        {
            var payload = spec.Type switch
            {
                "channel.chat.notification" => Notice("announcement", """{"color":"BLUE"}"""),
                "channel.unban_request.resolve" => Obj(B, U, "\"status\":\"approved\""),
                "automod.terms.update" => Obj(B, M, "\"action\":\"add_blocked\",\"terms\":[]"),
                "channel.moderate" => Obj(B, M, "\"action\":\"shared_chat_unban\",\"shared_chat_unban\":{" + U + "}"),
                _ => Obj(B, U),
            };
            Assert.True(EventSubMapper.Map(spec.Type, JsonSerializer.Deserialize<JsonElement>(payload), "1337") is not null, spec.Type);
        }
    }

    // ---------------------------------------------------------------- watch streaks, modiversaries

    [Fact]
    public void Watch_streak_and_modiversary_notices()
    {
        var streak = Map("channel.chat.notification", Notice("watch_streak", """{"streak_count":7,"channel_points_awarded":450}"""));
        Assert.Equal(TwitchEventTypes.WatchStreak, streak.Type);
        Assert.Equal(7, streak.Args["streak"]);
        Assert.Equal(450, streak.Args["channelPointsAwarded"]);
        Assert.Equal("viewer23", streak.Args["userName"]);

        var mod = Map("channel.chat.notification", Notice("modiversary", """{"months":12}"""));
        Assert.Equal(TwitchEventTypes.Modiversary, mod.Type);
        Assert.Equal(12, mod.Args["months"]);

        var shared = Map("channel.chat.notification", Notice("shared_chat_modiversary", """{"months":3}""",
            """ "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner" """));
        Assert.Equal(TwitchEventTypes.SharedChatModiversary, shared.Type);
        Assert.Equal(3, shared.Args["months"]);
        Assert.Equal(true, shared.Args["isSharedChat"]);
    }

    // ---------------------------------------------------------------- shared chat moderation (channel.moderate v2)

    private const string Source = """ "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner" """;

    [Fact]
    public void Shared_chat_moderation_actions_map_and_own_channel_actions_are_ignored()
    {
        var ban = Map("channel.moderate", Obj(B, M, Source, """ "action":"shared_chat_ban","shared_chat_ban":{""" + U + ""","reason":"spam"} """));
        Assert.Equal(TwitchEventTypes.SharedChatUserBanned, ban.Type);
        Assert.Equal("Cool_User", ban.Args["user"]);
        Assert.Equal("1234", ban.Args["userId"]);
        Assert.Equal("spam", ban.Args["reason"]);
        Assert.Equal("Mod", ban.Args["moderator"]);
        Assert.Equal("partner", ban.Args["sourceBroadcasterUserName"]);
        Assert.Equal(true, ban.Args["isSharedChat"]);

        var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");
        var timeout = Map("channel.moderate", Obj(B, M, Source,
            $$""" "action":"shared_chat_timeout","shared_chat_timeout":{{{U}},"reason":"","expires_at":"{{expires}}"} """));
        Assert.Equal(TwitchEventTypes.SharedChatUserTimedOut, timeout.Type);
        Assert.InRange((int)timeout.Args["duration"]!, 590, 600);

        Assert.Equal(TwitchEventTypes.SharedChatUserUnbanned,
            Map("channel.moderate", Obj(B, M, Source, """ "action":"shared_chat_unban","shared_chat_unban":{""" + U + "}")).Type);
        Assert.Equal(TwitchEventTypes.SharedChatUserUntimedOut,
            Map("channel.moderate", Obj(B, M, Source, """ "action":"shared_chat_untimeout","shared_chat_untimeout":{""" + U + "}")).Type);

        var deleted = Map("channel.moderate", Obj(B, M, Source,
            """ "action":"shared_chat_delete","shared_chat_delete":{""" + U + ""","message_id":"m-5","message_body":"rude"} """));
        Assert.Equal(TwitchEventTypes.SharedChatMessageDeleted, deleted.Type);
        Assert.Equal("m-5", deleted.Args["messageId"]);
        Assert.Equal("rude", deleted.Args["message"]);

        Assert.Null(EventSubMapper.Map("channel.moderate",
            JsonSerializer.Deserialize<JsonElement>(Obj(B, M, """ "action":"ban","ban":{""" + U + ""","reason":"x"} """)), "1337"));
    }

    // ---------------------------------------------------------------- guest star

    [Fact]
    public void Guest_star_events()
    {
        var begin = Map("channel.guest_star_session.begin", Obj(B, """ "session_id":"s-1","started_at":"2026-01-01T12:00:00Z" """));
        Assert.Equal(TwitchEventTypes.GuestStarSessionBegin, begin.Type);
        Assert.Equal("s-1", begin.Args["sessionId"]);

        var end = Map("channel.guest_star_session.end", Obj(B,
            """ "session_id":"s-1","started_at":"x","ended_at":"y","host_user_id":"1337","host_user_login":"streamer","host_user_name":"Streamer" """));
        Assert.Equal(TwitchEventTypes.GuestStarSessionEnd, end.Type);
        Assert.Equal("Streamer", end.Args["host"]);
        Assert.Equal("y", end.Args["endedAt"]);

        var guest = Map("channel.guest_star_guest.update", Obj(B, M,
            """
            "session_id":"s-1","guest_user_id":"55","guest_user_login":"guest","guest_user_name":"Guest","slot_id":"2","state":"live",
            "host_user_id":"1337","host_user_login":"streamer","host_user_name":"Streamer","host_video_enabled":true,"host_audio_enabled":false,"host_volume":80
            """));
        Assert.Equal(TwitchEventTypes.GuestStarGuestUpdate, guest.Type);
        Assert.Equal("Guest", guest.Args["user"]);
        Assert.Equal("55", guest.Args["userId"]);
        Assert.Equal("2", guest.Args["slotId"]);
        Assert.Equal("live", guest.Args["state"]);
        Assert.Equal(80, guest.Args["hostVolume"]);
        Assert.Equal(true, guest.Args["hostVideoEnabled"]);

        var settings = Map("channel.guest_star_settings.update", Obj(B,
            """ "is_moderator_send_live_enabled":true,"slot_count":5,"is_browser_source_audio_enabled":false,"group_layout":"tiled" """));
        Assert.Equal(TwitchEventTypes.GuestStarSettingsUpdate, settings.Type);
        Assert.Equal(5, settings.Args["slotCount"]);
        Assert.Equal("tiled", settings.Args["groupLayout"]);
    }
}
