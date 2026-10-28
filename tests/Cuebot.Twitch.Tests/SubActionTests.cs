using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Twitch.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Twitch.Tests;

/// <summary>Runs Twitch sub-actions in a real action engine against a fake Helix.</summary>
public sealed class SubActionTests : IDisposable
{
    private const string Rewards =
        """
        {"data":[{"id":"r1","title":"Hydrate","prompt":"Drink","cost":500,"background_color":"#00E5CB","is_enabled":true,"is_paused":false,
          "is_in_stock":true,"is_user_input_required":false,"should_redemptions_skip_request_queue":false,
          "max_per_stream_setting":{"is_enabled":false,"max_per_stream":0},
          "max_per_user_per_stream_setting":{"is_enabled":true,"max_per_user_per_stream":2},
          "global_cooldown_setting":{"is_enabled":true,"global_cooldown_seconds":60}}]}
        """;

    private const string ActivePoll =
        """{"data":[{"id":"p1","title":"Best?","status":"ACTIVE","duration":60,"choices":[{"id":"c1","title":"A","votes":3}]}]}""";

    private const string ActivePrediction =
        """
        {"data":[{"id":"pr1","title":"Win?","status":"ACTIVE","prediction_window":60,
          "outcomes":[{"id":"o1","title":"Yes","users":2,"channel_points":200,"color":"BLUE"},{"id":"o2","title":"No","users":1,"channel_points":50,"color":"PINK"}]}]}
        """;

    private readonly ApiHarness _h = new();
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-twitch-engine-").FullName;

    public SubActionTests()
    {
        _h.On("GET /helix/channel_points/custom_rewards", Rewards)
          .On("GET /helix/polls", ActivePoll)
          .On("GET /helix/predictions", ActivePrediction)
          .On("GET /helix/games", """{"data":[{"id":"509658","name":"Just Chatting","box_art_url":""}]}""")
          .On("GET /helix/channels", """{"data":[{"broadcaster_id":"1337","title":"Old","game_name":"Art","game_id":"1","tags":["Cozy","English"]}]}""")
          .On("GET /helix/chat/pins", PinnedMessage)
          .On("GET /helix/guest_star/session", GuestStarSession)
          .On("POST /helix/guest_star/session", GuestStarSession);
        _h.Api.Counters.SetGroup("r1", "Drinks");
    }

    private const string PinnedMessage =
        """
        {"data":[{"message_id":"pin-1","broadcaster_id":"1337","sender_user_id":"4242","sender_user_login":"target","sender_user_name":"Target",
          "pinned_by_user_id":"1337","pinned_by_user_login":"streamer","pinned_by_user_name":"Streamer",
          "message":{"text":"Read the rules","fragments":[]},"starts_at":"2026-01-01T12:00:00Z","ends_at":null,"updated_at":"2026-01-01T12:00:00Z"}]}
        """;

    private const string GuestStarSession =
        """
        {"data":[{"id":"s1","guests":[
          {"slot_id":"0","is_live":true,"user_id":"1337","user_display_name":"Streamer","user_login":"streamer","volume":100,
           "audio_settings":{"is_host_enabled":true,"is_guest_enabled":true,"is_available":true},
           "video_settings":{"is_host_enabled":true,"is_guest_enabled":true,"is_available":true}},
          {"slot_id":"1","is_live":false,"user_id":"55","user_display_name":"Guest","user_login":"guest","volume":70,
           "audio_settings":{"is_host_enabled":true,"is_guest_enabled":false,"is_available":true},
           "video_settings":{"is_host_enabled":true,"is_guest_enabled":true,"is_available":true}}]}]}
        """;

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(bool Ok, Dictionary<string, object?> Args, AppLog Log)> RunAsync(string type, Dictionary<string, string>? parameters = null,
        Dictionary<string, object?>? args = null)
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_root));
        foreach (var spec in TwitchSubActions.All) services.AddSingleton<ISubActionHandler>(new TwitchSubAction(spec, () => _h.Api));
        var capture = new CaptureSubAction();
        services.AddSingleton<ISubActionHandler>(capture);
        await using var provider = services.BuildServiceProvider();
        var action = new ActionDefinition
        {
            Name = "test",
            SubActions =
            [
                new SubActionDefinition { Type = type, Parameters = parameters ?? [] },
                new SubActionDefinition { Type = CaptureSubAction.Id },
            ],
        };
        await provider.GetRequiredService<ActionEngine>().RunAsync(action,
            args ?? new Dictionary<string, object?> { ["user"] = "Viewer", ["userId"] = "4242", ["messageId"] = "msg-9" });
        return (capture.Args is not null, capture.Args ?? [], provider.GetRequiredService<AppLog>());
    }

    public static TheoryData<string, string, string, string, string, string> HelixCases => new()
    {
        // type, parameters (k=v;k=v), method, path, query, body checks (path=json;...)
        { "twitch.sendMessage", "message=hi", "POST", "/helix/chat/messages", "", "broadcaster_id=\"1337\";sender_id=\"1337\";message=\"hi\"" },
        { "twitch.replyToMessage", "message=re", "POST", "/helix/chat/messages", "", "reply_parent_message_id=\"msg-9\"" },
        { "twitch.sendWhisper", "user=target;message=psst", "POST", "/helix/whispers", "from_user_id=1337&to_user_id=4242", "message=\"psst\"" },
        { "twitch.sendAnnouncement", "message=Hello;color=Purple", "POST", "/helix/chat/announcements", "broadcaster_id=1337&moderator_id=1337", "message=\"Hello\";color=\"purple\"" },
        { "twitch.deleteMessage", "", "DELETE", "/helix/moderation/chat", "broadcaster_id=1337&moderator_id=1337&message_id=msg-9", "" },
        { "twitch.clearChat", "", "DELETE", "/helix/moderation/chat", "broadcaster_id=1337&moderator_id=1337", "" },
        { "twitch.getPresentViewers", "", "GET", "/helix/chat/chatters", "broadcaster_id=1337&moderator_id=1337&first=1000", "" },
        { "twitch.setTitle", "title=New title", "PATCH", "/helix/channels", "broadcaster_id=1337", "title=\"New title\"" },
        { "twitch.setGame", "game=just chatting", "PATCH", "/helix/channels", "broadcaster_id=1337", "game_id=\"509658\"" },
        { "twitch.setTags", "tags=English, Cozy #Chill", "PATCH", "/helix/channels", "broadcaster_id=1337", "tags=[\"English\",\"Cozy\",\"Chill\"]" },
        { "twitch.addTag", "tag=New", "PATCH", "/helix/channels", "broadcaster_id=1337", "tags=[\"Cozy\",\"English\",\"New\"]" },
        { "twitch.removeTag", "tag=#cozy", "PATCH", "/helix/channels", "broadcaster_id=1337", "tags=[\"English\"]" },
        { "twitch.clearTags", "", "PATCH", "/helix/channels", "broadcaster_id=1337", "tags=[]" },
        { "twitch.setBrandedContent", "enabled=true", "PATCH", "/helix/channels", "broadcaster_id=1337", "is_branded_content=true" },
        { "twitch.setContentLabel", "label=Gambling;enabled=true", "PATCH", "/helix/channels", "broadcaster_id=1337",
            "content_classification_labels[0].id=\"Gambling\";content_classification_labels[0].is_enabled=true" },
        { "twitch.updateStreamInfo", "title=Only the title", "PATCH", "/helix/channels", "broadcaster_id=1337", "title=\"Only the title\";!game_id;!tags" },
        { "twitch.getChannelInfo", "", "GET", "/helix/channels", "broadcaster_id=1337", "" },
        { "twitch.getStreamInfo", "", "GET", "/helix/streams", "user_id=1337", "" },
        { "twitch.createMarker", "description=clip it", "POST", "/helix/streams/markers", "", "user_id=\"1337\";description=\"clip it\"" },
        { "twitch.createClip", "", "POST", "/helix/clips", "broadcaster_id=1337&has_delay=false", "" },
        { "twitch.runAd", "length=90", "POST", "/helix/channels/commercial", "", "broadcaster_id=\"1337\";length=90" },
        { "twitch.snoozeAd", "", "POST", "/helix/channels/ads/schedule/snooze", "broadcaster_id=1337", "" },
        { "twitch.getAdSchedule", "", "GET", "/helix/channels/ads", "broadcaster_id=1337", "" },
        { "twitch.startRaid", "user=@Target", "POST", "/helix/raids", "from_broadcaster_id=1337&to_broadcaster_id=4242", "" },
        { "twitch.cancelRaid", "", "DELETE", "/helix/raids", "broadcaster_id=1337", "" },
        { "twitch.sendShoutout", "user=target", "POST", "/helix/chat/shoutouts", "from_broadcaster_id=1337&to_broadcaster_id=4242&moderator_id=1337", "" },
        { "twitch.ban", "user=target;reason=spam", "POST", "/helix/moderation/bans", "broadcaster_id=1337&moderator_id=1337",
            "data.user_id=\"4242\";data.reason=\"spam\";!data.duration" },
        { "twitch.timeout", "user=id:99;duration=300;reason=calm down", "POST", "/helix/moderation/bans", "broadcaster_id=1337&moderator_id=1337",
            "data.user_id=\"99\";data.duration=300" },
        { "twitch.unban", "user=target", "DELETE", "/helix/moderation/bans", "broadcaster_id=1337&moderator_id=1337&user_id=4242", "" },
        { "twitch.untimeout", "", "DELETE", "/helix/moderation/bans", "broadcaster_id=1337&moderator_id=1337&user_id=4242", "" },
        { "twitch.warn", "user=target;reason=be nice", "POST", "/helix/moderation/warnings", "broadcaster_id=1337&moderator_id=1337",
            "data.user_id=\"4242\";data.reason=\"be nice\"" },
        { "twitch.addVip", "user=target", "POST", "/helix/channels/vips", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.removeVip", "user=target", "DELETE", "/helix/channels/vips", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.addModerator", "user=target", "POST", "/helix/moderation/moderators", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.removeModerator", "user=target", "DELETE", "/helix/moderation/moderators", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.emoteOnly", "enabled=true", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337", "emote_mode=true" },
        { "twitch.followerMode", "enabled=true;minutes=10", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337",
            "follower_mode=true;follower_mode_duration=10" },
        { "twitch.slowMode", "enabled=true;seconds=5", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337",
            "slow_mode=true;slow_mode_wait_time=5" },
        { "twitch.slowMode", "enabled=false", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337", "slow_mode=false;!slow_mode_wait_time" },
        { "twitch.subscriberMode", "enabled=false", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337", "subscriber_mode=false" },
        { "twitch.uniqueChat", "enabled=true", "PATCH", "/helix/chat/settings", "broadcaster_id=1337&moderator_id=1337", "unique_chat_mode=true" },
        { "twitch.setShieldMode", "enabled=true", "PUT", "/helix/moderation/shield_mode", "broadcaster_id=1337&moderator_id=1337", "is_active=true" },
        { "twitch.getShieldMode", "", "GET", "/helix/moderation/shield_mode", "broadcaster_id=1337&moderator_id=1337", "" },
        { "twitch.addBlockedTerm", "term=badword", "POST", "/helix/moderation/blocked_terms", "broadcaster_id=1337&moderator_id=1337", "text=\"badword\"" },
        { "twitch.resolveAutomod", "messageId=held-1;decision=Deny", "POST", "/helix/moderation/automod/message", "",
            "user_id=\"1337\";msg_id=\"held-1\";action=\"DENY\"" },
        { "twitch.createPoll", "title=Best?;choices=A\nB\nC;duration=120;pointsPerVote=10", "POST", "/helix/polls", "",
            "broadcaster_id=\"1337\";title=\"Best?\";duration=120;choices[2].title=\"C\";channel_points_voting_enabled=true;channel_points_per_vote=10" },
        { "twitch.endPoll", "mode=Archive", "PATCH", "/helix/polls", "", "id=\"p1\";status=\"ARCHIVED\"" },
        { "twitch.endPoll", "", "PATCH", "/helix/polls", "", "id=\"p1\";status=\"TERMINATED\"" },
        { "twitch.getPoll", "", "GET", "/helix/polls", "broadcaster_id=1337&first=1", "" },
        { "twitch.createPrediction", "title=Win?;outcomes=Yes\nNo;window=60", "POST", "/helix/predictions", "",
            "title=\"Win?\";prediction_window=60;outcomes[1].title=\"No\"" },
        { "twitch.lockPrediction", "", "PATCH", "/helix/predictions", "", "id=\"pr1\";status=\"LOCKED\"" },
        { "twitch.cancelPrediction", "", "PATCH", "/helix/predictions", "", "id=\"pr1\";status=\"CANCELED\"" },
        { "twitch.resolvePrediction", "winner=no", "PATCH", "/helix/predictions", "", "status=\"RESOLVED\";winning_outcome_id=\"o2\"" },
        { "twitch.resolvePrediction", "winner=1", "PATCH", "/helix/predictions", "", "winning_outcome_id=\"o1\"" },
        { "twitch.getPrediction", "", "GET", "/helix/predictions", "broadcaster_id=1337&first=1", "" },
        { "twitch.createReward", "title=Stretch;cost=250;prompt=Do it", "POST", "/helix/channel_points/custom_rewards", "broadcaster_id=1337",
            "title=\"Stretch\";cost=250;prompt=\"Do it\";is_enabled=true;is_user_input_required=false" },
        { "twitch.setRewardEnabled", "reward=hydrate;enabled=false", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "is_enabled=false" },
        { "twitch.setRewardPaused", "reward=r1;paused=true", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "is_paused=true" },
        { "twitch.setRewardCost", "reward=Hydrate;cost=750", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "cost=750" },
        { "twitch.setRewardTitle", "reward=Hydrate;title=Water", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "title=\"Water\"" },
        { "twitch.setRewardPrompt", "reward=Hydrate;prompt=Sip", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "prompt=\"Sip\"" },
        { "twitch.setRewardColor", "reward=Hydrate;color=FF0000", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "background_color=\"#FF0000\"" },
        { "twitch.setRewardCooldown", "reward=Hydrate;seconds=90", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_global_cooldown_enabled=true;global_cooldown_seconds=90" },
        { "twitch.setRewardMaxPerStream", "reward=Hydrate;max=0", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_max_per_stream_enabled=false;!max_per_stream" },
        { "twitch.setRewardMaxPerUser", "reward=Hydrate;max=3", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_max_per_user_per_stream_enabled=true;max_per_user_per_stream=3" },
        { "twitch.deleteReward", "reward=Hydrate", "DELETE", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1", "" },
        { "twitch.getRewardInfo", "reward=Hydrate", "GET", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&only_manageable_rewards=true", "" },
        { "twitch.updateRedemption", "rewardId=r1;redemptionId=red-1;status=Cancel", "PATCH", "/helix/channel_points/custom_rewards/redemptions",
            "broadcaster_id=1337&reward_id=r1&id=red-1", "status=\"CANCELED\"" },
        { "twitch.updateRedemption", "rewardId=r1;redemptionId=red-1", "PATCH", "/helix/channel_points/custom_rewards/redemptions",
            "broadcaster_id=1337&reward_id=r1&id=red-1", "status=\"FULFILLED\"" },
        { "twitch.getUserInfo", "user=target", "GET", "/helix/channels", "broadcaster_id=4242", "" },
        { "twitch.getFollowAge", "user=target", "GET", "/helix/channels/followers", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.getSubStatus", "", "GET", "/helix/subscriptions", "broadcaster_id=1337&user_id=4242", "" },
        { "twitch.getFollowerCount", "", "GET", "/helix/channels/followers", "broadcaster_id=1337&first=1", "" },
        { "twitch.getSubscriberCount", "", "GET", "/helix/subscriptions", "broadcaster_id=1337&first=1", "" },
        { "twitch.getTeamInfo", "user=target", "GET", "/helix/teams/channel", "broadcaster_id=4242", "" },
        { "twitch.getCharityCampaign", "", "GET", "/helix/charity/campaigns", "broadcaster_id=1337", "" },
        { "twitch.getBroadcasterInfo", "", "GET", "/helix/users", "id=1337", "" },
        { "twitch.getRandomUsers", "count=2", "GET", "/helix/chat/chatters", "broadcaster_id=1337&moderator_id=1337&first=1000", "" },
        { "twitch.getPresentUser", "index=0", "GET", "/helix/chat/chatters", "broadcaster_id=1337&moderator_id=1337&first=1000", "" },

        // Pinned messages
        { "twitch.pinMessage", "duration=60", "PUT", "/helix/chat/pins", "broadcaster_id=1337&moderator_id=1337&message_id=msg-9&duration_seconds=60", "" },
        { "twitch.pinMessage", "messageId=m2;duration=5000", "PUT", "/helix/chat/pins",
            "broadcaster_id=1337&moderator_id=1337&message_id=m2&duration_seconds=1800", "" },
        { "twitch.unpinMessage", "", "DELETE", "/helix/chat/pins", "broadcaster_id=1337&moderator_id=1337&message_id=pin-1", "" },
        { "twitch.updatePinnedDuration", "messageId=pin-2;duration=120", "PATCH", "/helix/chat/pins",
            "broadcaster_id=1337&moderator_id=1337&message_id=pin-2&duration_seconds=120", "" },
        { "twitch.updatePinnedDuration", "duration=0", "PATCH", "/helix/chat/pins", "broadcaster_id=1337&moderator_id=1337&message_id=pin-1", "" },
        { "twitch.getPinnedMessage", "", "GET", "/helix/chat/pins", "broadcaster_id=1337&moderator_id=1337", "" },

        // Reward update, counters and groups ("Hydrate" is in the group "Drinks")
        { "twitch.updateReward", "reward=Hydrate;title=Water;cost=2;operator=Multiply", "PATCH", "/helix/channel_points/custom_rewards",
            "broadcaster_id=1337&id=r1", "title=\"Water\";cost=1000;!prompt" },
        { "twitch.updateReward", "reward=Hydrate;prompt=Sip", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "prompt=\"Sip\";!cost;!title" },
        { "twitch.getRewardCounter", "reward=Hydrate", "GET", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&only_manageable_rewards=true", "" },
        { "twitch.resetRewardCounter", "reward=Hydrate", "GET", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&only_manageable_rewards=true", "" },
        { "twitch.resetRewardUserCounters", "reward=r1", "GET", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&only_manageable_rewards=true", "" },
        { "twitch.setRewardGroupEnabled", "group=Drinks;state=Disabled", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_enabled=false;!is_paused" },
        { "twitch.setRewardGroupPaused", "group=drinks;state=Toggle", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_paused=true;!is_enabled" },
        { "twitch.configureRewards", "disable=Drinks;pause=Hydrate", "PATCH", "/helix/channel_points/custom_rewards", "broadcaster_id=1337&id=r1",
            "is_enabled=false;is_paused=true" },

        // Guest Star
        { "twitch.getGuestStarSettings", "", "GET", "/helix/guest_star/channel_settings", "broadcaster_id=1337&moderator_id=1337", "" },
        { "twitch.updateGuestStarSettings", "slotCount=4;layout=TILED_LAYOUT;moderatorSendLive=On", "PUT", "/helix/guest_star/channel_settings",
            "broadcaster_id=1337", "slot_count=4;group_layout=\"TILED_LAYOUT\";is_moderator_send_live_enabled=true;!is_browser_source_audio_enabled;!regenerate_browser_sources" },
        { "twitch.getGuestStarSession", "", "GET", "/helix/guest_star/session", "broadcaster_id=1337&moderator_id=1337", "" },
        { "twitch.createGuestStarSession", "", "POST", "/helix/guest_star/session", "broadcaster_id=1337", "" },
        { "twitch.endGuestStarSession", "", "DELETE", "/helix/guest_star/session", "broadcaster_id=1337&session_id=s1", "" },
        { "twitch.getGuestStarInvites", "", "GET", "/helix/guest_star/invites", "broadcaster_id=1337&moderator_id=1337&session_id=s1", "" },
        { "twitch.sendGuestStarInvite", "user=target", "POST", "/helix/guest_star/invites",
            "broadcaster_id=1337&moderator_id=1337&session_id=s1&guest_id=4242", "" },
        { "twitch.deleteGuestStarInvite", "user=id:55;sessionId=s9", "DELETE", "/helix/guest_star/invites",
            "broadcaster_id=1337&moderator_id=1337&session_id=s9&guest_id=55", "" },
        { "twitch.assignGuestStarSlot", "user=id:55;slot=2", "POST", "/helix/guest_star/slot",
            "broadcaster_id=1337&moderator_id=1337&session_id=s1&guest_id=55&slot_id=2", "" },
        { "twitch.updateGuestStarSlot", "slot=1;toSlot=3", "PATCH", "/helix/guest_star/slot",
            "broadcaster_id=1337&moderator_id=1337&session_id=s1&source_slot_id=1&destination_slot_id=3", "" },
        { "twitch.deleteGuestStarSlot", "user=id:55;slot=2;reinvite=true", "DELETE", "/helix/guest_star/slot",
            "broadcaster_id=1337&moderator_id=1337&session_id=s1&guest_id=55&slot_id=2&should_reinvite_guest=true", "" },
        { "twitch.updateGuestStarSlotSettings", "slot=2;audio=Off;volume=50", "PATCH", "/helix/guest_star/slot_settings",
            "broadcaster_id=1337&moderator_id=1337&session_id=s1&slot_id=2&is_audio_enabled=false&volume=50", "" },
    };

    [Theory]
    [MemberData(nameof(HelixCases))]
    public async Task Sub_action_sends_the_documented_Helix_request(string type, string parameters, string method, string path, string query, string body)
    {
        var (ok, _, log) = await RunAsync(type, Parse(parameters));

        Assert.True(ok, string.Join(" | ", log.Recent().Select(e => e.Message)));
        var request = _h.Last;
        Assert.Equal(method, request.Method.Method);
        Assert.Equal(path, request.Path);
        Assert.Equal(query, request.Query);
        Assert.Equal("client", request.ClientId);
        Assert.Equal("Bearer broadcaster-token", request.Authorization);
        if (body.Length == 0) return;
        var json = JsonSerializer.Deserialize<JsonElement>(request.Body);
        foreach (var check in body.Split(';'))
        {
            if (check.StartsWith('!'))
            {
                Assert.Null(Find(json, check[1..]));
                continue;
            }
            var (key, expected) = (check[..check.IndexOf('=')], check[(check.IndexOf('=') + 1)..]);
            var actual = Find(json, key) ?? throw new Xunit.Sdk.XunitException($"'{key}' missing in {request.Body}");
            Assert.Equal(expected, actual.GetRawText());
        }
    }

    /// <summary>Sub-actions on Cuebot's own state (no Helix call); covered by <see cref="Local_state_sub_actions"/>.</summary>
    private static readonly string[] LocalSubActions = ["twitch.getLatestSubscriber", "twitch.getSubCounter", "twitch.setSubCounter"];

    [Fact]
    public void Every_sub_action_is_covered_by_a_request_format_case()
    {
        var covered = HelixCases.Select(row => (string)row[0]).Concat(LocalSubActions).ToHashSet();
        Assert.All(TwitchSubActions.All, s => Assert.Contains(s.TypeId, covered));
        Assert.Equal(TwitchSubActions.All.Count, TwitchSubActions.All.Select(s => s.TypeId).Distinct().Count());
    }

    [Fact]
    public async Task Get_actions_write_results_into_arguments()
    {
        _h.On("GET /helix/channels/followers", """{"total":1,"data":[{"user_id":"4242","user_name":"Target","user_login":"target","followed_at":"2020-01-01T00:00:00Z"}]}""")
          .On("GET /helix/streams", """{"data":[]}""")
          .On("POST /helix/clips", """{"data":[{"id":"FunnyClip","edit_url":"https://clips.twitch.tv/FunnyClip/edit"}]}""");

        var follow = (await RunAsync("twitch.getFollowAge")).Args;
        Assert.Equal(true, follow["isFollowing"]);
        Assert.StartsWith("2020-01-01", (string)follow["followedAt"]!);
        Assert.Contains("year", (string)follow["followAge"]!);

        var user = (await RunAsync("twitch.getUserInfo", new() { ["user"] = "target" })).Args;
        Assert.Equal("Target", user["targetUser"]);
        Assert.Equal("4242", user["targetUserId"]);
        Assert.Equal("affiliate", user["targetBroadcasterType"]);
        Assert.Equal("Art", user["targetGame"]);

        Assert.Equal(false, (await RunAsync("twitch.getStreamInfo")).Args["isLive"]);
        Assert.Equal("https://clips.twitch.tv/FunnyClip", (await RunAsync("twitch.createClip")).Args["clipUrl"]);

        var reward = (await RunAsync("twitch.getRewardInfo", new() { ["reward"] = "Hydrate" })).Args;
        Assert.Equal(500, reward["rewardCost"]);
        Assert.Equal(60, reward["rewardCooldown"]);
        Assert.Equal(2, reward["rewardMaxPerUser"]);
        Assert.Equal(true, reward["rewardIsManageable"]);

        _h.On("GET /helix/chat/chatters",
            """{"data":[{"user_id":"1337","user_login":"streamer","user_name":"Streamer"},{"user_id":"5","user_login":"a","user_name":"A"},{"user_id":"6","user_login":"b","user_name":"B"}],"pagination":{},"total":3}""");
        var random = (await RunAsync("twitch.getRandomUsers", new() { ["count"] = "5" })).Args;
        Assert.Equal(2, random["randomUserCount"]);
        Assert.DoesNotContain("Streamer", new[] { random["randomUser0"], random["randomUser1"] });

        var broadcaster = (await RunAsync("twitch.getBroadcasterInfo")).Args;
        Assert.Equal("1337", broadcaster["broadcasterUserId"]);
        Assert.Equal(true, broadcaster["broadcasterIsAffiliate"]);

        var prediction = (await RunAsync("twitch.getPrediction")).Args;
        Assert.Equal("Win?", prediction["predictionTitle"]);
        Assert.Equal("No", prediction["predictionOutcome1"]);
    }

    /// <summary>Streamer.bot's documented output names (docs: 3.api/1.sub-actions/twitch/**), per sub-action.</summary>
    public static TheoryData<string, string, string> StreamerBotOutputs => new()
    {
        { "twitch.createClip", "", "createClipSuccess,createClipId,createClipCreatedAt,createClipUrl" },
        { "twitch.createMarker", "description=hi", "streamMarkerSuccess,streamMarkerId,streamMarkerCreatedAt,streamMarkerDescription,streamMarkerPosition" },
        { "twitch.setBrandedContent", "enabled=true", "success" },
        { "twitch.setGame", "game=just chatting", "gameSuccess,gameName,gameId,gameBoxArt" },
        { "twitch.setTitle", "title=T", "titleSuccess" },
        { "twitch.clearChat", "", "clearSuccess" },
        { "twitch.getFollowAge", "", "isFollowing,followDate,followAgeLong,followAgeShort,followAgeDays,followAgeMinutes,followAgeSeconds,followUser,followUserName,followUserId,followAge.years,followAge.months,followAge.days,followAge.hours,followAge.minutes,followAge.seconds" },
        { "twitch.ban", "user=target", "bannedUser,bannedUserName,bannedUserId,bannedUserType,banResult" },
        { "twitch.timeout", "user=target;duration=60", "timedOutUser,timedOutUserName,timedOutUserId,timedOutUserType,timeoutResult" },
        { "twitch.unban", "user=target", "unbannedUser,unbannedUserName,unbannedUserId,unbannedUserType,unbanResult" },
        { "twitch.untimeout", "user=target", "unTimedOutUser,unTimedOutUserName,unTimedOutUserId,unTimedOutUserType,unTimedOutResult" },
        { "twitch.warn", "user=target;reason=r", "warnedUser,warnedUserName,warnedUserId,warnedUserType,warnedResult" },
        { "twitch.sendShoutout", "user=target", "shoutoutSuccess" },
        { "twitch.startRaid", "user=target", "startRaidSuccess" },
        { "twitch.getAdSchedule", "", "prerollFree,prerollFreeSeconds,snoozeCount" },
        { "twitch.snoozeAd", "", "snooze.success,snoozeCount" },
        { "twitch.getStreamInfo", "", "viewerCount" },
        { "twitch.createPoll", "title=Q;choices=A|B", "pollId,success" },
        { "twitch.getPoll", "", "active,poll.Title,poll.Id,poll.Duration,poll.choices.count,poll.choice0.title,poll.choice0.id,poll.choice0.votes,poll.choice0.rewardVotes,poll.choice0.totalVotes,poll.votes,poll.rewardVotes,poll.totalVotes" },
        { "twitch.createPrediction", "title=Q;outcomes=A|B", "success,prediction.Id" },
        { "twitch.getPrediction", "", "active,prediction.Id,prediction.Title,prediction.PredictionWindow,prediction.outcome0.id,prediction.outcome0.title,prediction.outcome0.users,prediction.outcome0.points,prediction.outcome0.color" },
        { "twitch.lockPrediction", "", "active,locked,prediction.Id,prediction.outcome1.title" },
        { "twitch.cancelPrediction", "", "active,cancelled,prediction.Id" },
        { "twitch.resolvePrediction", "winner=No", "success,prediction.winningIndex,prediction.winningOutcome.id,prediction.winningCutcome.title,prediction.winningOutcome.title,prediction.winningOutcome.users,prediction.winningOutcome.points,prediction.winningOutcome.color" },
        { "twitch.getRewardInfo", "reward=Hydrate", "rewardId,rewardCost,rewardTitle,rewardBackgroundColor,rewardEnabled,rewardPaused,rewardPrompt,rewardSkipRequestQueue,rewardMaxPerStream,rewardMaxPerUserPerStream,rewardGlobalCooldown,rewardIsOurs" },
        { "twitch.getSubCounter", "", "subCounter.count,subCounter.rollover,subCounter.rolloverCount" },
        { "twitch.setSubCounter", "count=3", "setResult" },
        { "twitch.getBroadcasterInfo", "", "broadcastUser,broadcastUserName,broadcastUserId,broadcastUserType,broadcastIsAffiliate,broadcastIsPartner,broadcasterProfileUrl,broadcasterOfflineUrl,broadcasterCreatedAt,broadcasterDescription" },
        { "twitch.getPresentUser", "index=0", "presentUsersCount,presentUser,presentUserName,presentUserId,presentUserType" },
        { "twitch.getRandomUsers", "count=1", "randomUser0,randomUserName0,randomUserId0,randomUserType0" },
        { "twitch.getUserInfo", "user=target", "addTargetResult,targetUser,targetUserName,targetUserId,targetDescription,targetDescriptionEscaped,targetUserProfileImageUrl,targetUserProfileImageUrlEscaped,targetUserType,targetIsAffiliate,targetIsPartner,targetIsSubscribed,targetSubscriptionTier,targetIsModerator,targetIsVip,targetChannelTitle,game,gameId,createdAt,accountAge,tagCount,tag0,tags,tagsDelimited" },
    };

    [Theory]
    [MemberData(nameof(StreamerBotOutputs))]
    public async Task Sub_actions_write_Streamer_bot_output_names(string type, string parameters, string expected)
    {
        const string prediction =
            """{"data":[{"id":"pr1","title":"Win?","status":"STATUS","winning_outcome_id":"WIN","prediction_window":60,"outcomes":[{"id":"o1","title":"Yes","users":2,"channel_points":200,"color":"BLUE"},{"id":"o2","title":"No","users":1,"channel_points":50,"color":"PINK"}]}]}""";
        _h.On("GET /helix/channels/followers", """{"total":1,"data":[{"user_id":"4242","user_name":"Target","user_login":"target","followed_at":"2020-01-01T00:00:00Z"}]}""")
          .On("GET /helix/streams", """{"data":[]}""")
          .On("POST /helix/clips", """{"data":[{"id":"FunnyClip","edit_url":"https://clips.twitch.tv/FunnyClip/edit"}]}""")
          .On("POST /helix/streams/markers", """{"data":[{"id":"mk1","position_seconds":125,"description":"hi"}]}""")
          .On("GET /helix/channels/ads", """{"data":[{"next_ad_at":0,"last_ad_at":0,"duration":60,"preroll_free_time":90,"snooze_count":2,"snooze_refresh_at":0}]}""")
          .On("POST /helix/channels/ads/schedule/snooze", """{"data":[{"snooze_count":1,"snooze_refresh_at":0,"next_ad_at":0}]}""")
          .On("POST /helix/polls", ActivePoll)
          .On("POST /helix/predictions", ActivePrediction)
          .On("GET /helix/subscriptions", """{"data":[{"user_id":"4242","user_login":"target","user_name":"Target","tier":"2000","is_gift":false}]}""")
          .On("GET /helix/chat/chatters", """{"data":[{"user_id":"5","user_login":"a","user_name":"A"}],"pagination":{},"total":1}""")
          .On("POST /helix/raids", """{"data":[{"created_at":"2026-01-01T12:00:00Z","is_mature":false}]}""");
        var status = type switch { "twitch.lockPrediction" => "LOCKED", "twitch.cancelPrediction" => "CANCELED", _ => "RESOLVED" };
        _h.On("PATCH /helix/predictions", prediction.Replace("STATUS", status).Replace("WIN", status == "RESOLVED" ? "o2" : ""));

        var values = parameters.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);
        var (ok, args, log) = await RunAsync(type, values,
            new Dictionary<string, object?> { ["user"] = "Viewer", ["userName"] = "viewer", ["userId"] = "4242" });

        Assert.True(ok, string.Join(" | ", log.Recent().Select(e => e.Message)));
        foreach (var name in expected.Split(','))
            Assert.True(args.ContainsKey(name), $"{type} did not set Streamer.bot's '{name}'");
    }

    [Fact]
    public async Task Streamer_bot_output_values()
    {
        _h.On("GET /helix/channels/followers", """{"total":1,"data":[{"user_id":"4242","user_name":"Target","user_login":"target","followed_at":"2020-01-01T00:00:00Z"}]}""");

        var ban = (await RunAsync("twitch.ban", new() { ["user"] = "target" })).Args;
        Assert.Equal("Target", ban["bannedUser"]);
        Assert.Equal("target", ban["bannedUserName"]);
        Assert.Equal("4242", ban["bannedUserId"]);
        Assert.Equal("twitch", ban["bannedUserType"]);
        Assert.Equal(true, ban["banResult"]);

        var poll = (await RunAsync("twitch.getPoll")).Args;
        Assert.Equal(true, poll["active"]);
        Assert.Equal("Best?", poll["poll.Title"]);
        Assert.Equal("A", poll["poll.choice0.title"]);
        Assert.Equal(3, poll["poll.choice0.totalVotes"]);

        var follow = (await RunAsync("twitch.getFollowAge")).Args;
        Assert.Equal("target", follow["followUserName"]);
        Assert.Matches(@"^\d+ years?", (string)follow["followAgeLong"]!);
        Assert.Matches(@"^\d+y", (string)follow["followAgeShort"]!);
        Assert.IsType<int>(follow["followAge.years"]);

        var user = (await RunAsync("twitch.getUserInfo", new() { ["user"] = "target" })).Args;
        Assert.Equal("affiliate", user["targetUserType"]);
        Assert.Equal(true, user["targetIsAffiliate"]);
        Assert.Equal("p.png", user["targetUserProfileImageUrl"]);
        Assert.Equal(2, user["tagCount"]);
        Assert.Equal("Cozy", user["tag0"]);
        Assert.Equal("Cozy,English", user["tagsDelimited"]);
    }

    [Fact]
    public async Task New_get_actions_write_results_into_arguments()
    {
        var pinned = (await RunAsync("twitch.getPinnedMessage")).Args;
        Assert.Equal(true, pinned["isPinned"]);
        Assert.Equal("pin-1", pinned["pinnedMessageId"]);
        Assert.Equal("Read the rules", pinned["pinnedMessage"]);
        Assert.Equal("Target", pinned["pinnedUser"]);
        Assert.Equal("", pinned["pinnedUntil"]);

        var session = (await RunAsync("twitch.getGuestStarSession")).Args;
        Assert.Equal("s1", session["guestStarSessionId"]);
        Assert.Equal(2, session["guestStarGuestCount"]);
        Assert.Equal("Guest", session["guestStarGuest1"]);
        Assert.Equal("1", session["guestStarGuest1Slot"]);
        Assert.Equal(70, session["guestStarGuest1Volume"]);

        _h.On("GET /helix/guest_star/invites", """{"data":[{"user_id":"55","invited_at":"2026-01-01T12:00:00Z","status":"READY","is_video_enabled":true}]}""");
        var invites = (await RunAsync("twitch.getGuestStarInvites")).Args;
        Assert.Equal(1, invites["guestStarInviteCount"]);
        Assert.Equal("READY", invites["guestStarInvite0Status"]);

        _h.On("GET /helix/chat/chatters", """{"data":[{"user_id":"5","user_login":"a","user_name":"A"},{"user_id":"6","user_login":"b","user_name":"B"}],"pagination":{}}""");
        var present = (await RunAsync("twitch.getPresentUser", new() { ["index"] = "1" })).Args;
        Assert.Equal("B", present["presentUser"]);
        Assert.Equal("6", present["presentUserId"]);
        Assert.Equal(2, present["presentUsersCount"]);
        Assert.Equal(false, (await RunAsync("twitch.getPresentUser", new() { ["index"] = "7" })).Args["presentUser"]);

        _h.Api.Counters.CountRedemption("r1", "4242");
        _h.Api.Counters.CountRedemption("r1", "4242");
        _h.Api.Counters.CountRedemption("r1", "1");
        var counters = (await RunAsync("twitch.getRewardCounter", new() { ["reward"] = "Hydrate" })).Args;
        Assert.Equal(3, counters["counter"]);
        Assert.Equal(2, counters["userCounter"]);
        await RunAsync("twitch.resetRewardUserCounters", new() { ["reward"] = "Hydrate" });
        Assert.Equal((3, 0), _h.Api.Counters.GetRewardCounter("r1", "4242"));
        await RunAsync("twitch.resetRewardCounter", new() { ["reward"] = "Hydrate" });
        Assert.Equal((0, 0), _h.Api.Counters.GetRewardCounter("r1", "4242"));
    }

    [Fact]
    public async Task Local_state_sub_actions()
    {
        _h.Settings.Value.SubCounter.Rollover = 50;
        var set = (await RunAsync("twitch.setSubCounter", new() { ["count"] = "12", ["rolloverCount"] = "3" })).Args;
        Assert.Equal(true, set["setResult"]);
        var get = (await RunAsync("twitch.getSubCounter")).Args;
        Assert.Equal(12, get["subCounter.count"]);
        Assert.Equal(50, get["subCounter.rollover"]);
        Assert.Equal(3, get["subCounter.rolloverCount"]);
        Assert.Equal(false, (await RunAsync("twitch.setSubCounter", new() { ["count"] = "lots" })).Args["setResult"]);
        Assert.Equal(12, _h.Api.Counters.SubCounter.Count);

        _h.Api.Counters.SetLatestSubscriber("4242", "target", "Target", "2", true, DateTimeOffset.UnixEpoch);
        var latest = (await RunAsync("twitch.getLatestSubscriber")).Args;
        Assert.Equal("Target", latest["latestSubscriber.userName"]);
        Assert.Equal("target", latest["latestSubscriber.userLogin"]);
        Assert.Equal("Tier2", latest["latestSubscriber.tier"]);
        Assert.Equal(true, latest["latestSubscriber.isGift"]);
        Assert.Empty(_h.Http.Requests);
    }

    [Fact]
    public async Task Reward_groups_skip_rewards_already_in_the_state_and_report_unknown_names()
    {
        var (_, enabled, _) = await RunAsync("twitch.setRewardGroupEnabled", new() { ["group"] = "Drinks", ["state"] = "Enabled" });
        Assert.Equal(0, enabled["rewardsChanged"]);
        Assert.DoesNotContain(_h.Http.Requests, r => r.Method == HttpMethod.Patch);

        var (ok, _, log) = await RunAsync("twitch.configureRewards", new() { ["enable"] = "No such reward" });
        Assert.False(ok);
        Assert.Contains(log.Recent(), e => e.Message.Contains("No such reward"));

        var (_, none, _) = await RunAsync("twitch.configureRewards", new() { ["disableNotConfigured"] = "true" });
        Assert.Equal(0, none["rewardsChanged"]);
        _h.Api.Counters.SetGroup("r1", "");
        var (_, notConfigured, _) = await RunAsync("twitch.configureRewards", new() { ["disableNotConfigured"] = "true" });
        Assert.Equal(1, notConfigured["rewardsChanged"]);
    }

    [Theory]
    [InlineData(500, 100, "Set", 100)]
    [InlineData(500, 100, "Add", 600)]
    [InlineData(500, 600, "Subtract", 1)]
    [InlineData(500, 1.5, "Multiply", 750)]
    [InlineData(500, 4, "Divide", 125)]
    [InlineData(500, 0, "Divide", 500)]
    public void Reward_cost_operators(int current, double amount, string op, int expected) =>
        Assert.Equal(expected, TwitchSubActions.ApplyCost(current, amount, op));

    [Fact]
    public async Task Missing_scope_fails_the_action_with_a_clear_message_and_sends_nothing()
    {
        _h.Settings.Value.Scopes = [TwitchScopes.ReadChat, TwitchScopes.WriteChat];

        var (ok, _, log) = await RunAsync("twitch.ban", new() { ["user"] = "id:1" });

        Assert.False(ok);
        Assert.Contains(log.Recent(), e => e.Message.Contains("moderator:manage:banned_users"));
        Assert.Empty(_h.Http.Requests);
    }

    [Fact]
    public async Task Resolve_prediction_by_title_guards_against_the_wrong_prediction()
    {
        var error = await Assert.ThrowsAsync<TwitchApiException>(() => _h.Api.ResolvePredictionAsync("Yes", "Another question"));
        Assert.Contains("Win?", error.Message);
        Assert.DoesNotContain(_h.Http.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Ending_a_poll_without_an_active_one_is_a_clear_error()
    {
        _h.On("GET /helix/polls", """{"data":[{"id":"p1","title":"Old","status":"COMPLETED","choices":[]}]}""");
        var error = await Assert.ThrowsAsync<TwitchApiException>(() => _h.Api.EndPollAsync());
        Assert.Contains("no active poll", error.Message);
    }

    [Fact]
    public async Task Script_functions_mirror_sub_actions_and_return_written_arguments()
    {
        _h.On("POST /helix/streams/markers", """{"data":[{"id":"mk1","position_seconds":125,"description":"hi"}]}""");
        var functions = TwitchSubActions.ScriptFunctions(() => _h.Api).ToDictionary(f => f.Name);
        Assert.Contains("twitch.sendMessage", functions.Keys);
        Assert.Equal(["message", "sendAs"], functions["twitch.sendMessage"].ParameterNames);

        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_root));
        var capture = new ScriptCapture(functions["twitch.createMarker"]);
        services.AddSingleton<ISubActionHandler>(capture);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ActionEngine>().RunAsync(new ActionDefinition
        {
            Name = "script",
            SubActions = [new SubActionDefinition { Type = ScriptCapture.Id }],
        });

        var result = Assert.IsType<Dictionary<string, object?>>(capture.Result);
        Assert.Equal("mk1", result["markerId"]);
        Assert.Equal(125, result["markerPosition"]);
    }

    private static Dictionary<string, string> Parse(string parameters) =>
        parameters.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    /// <summary>Resolves "a.b[2].c" in a JSON element; null when missing.</summary>
    private static JsonElement? Find(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            var name = part;
            int? index = null;
            if (part.EndsWith(']'))
            {
                var open = part.IndexOf('[');
                index = int.Parse(part[(open + 1)..^1]);
                name = part[..open];
            }
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) return null;
            current = next;
            if (index is { } i)
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= i) return null;
                current = current[i];
            }
        }
        return current;
    }

    private sealed class CaptureSubAction : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    private sealed class ScriptCapture(Cuebot.Core.Scripting.ScriptFunction function) : ISubActionHandler
    {
        public const string Id = "test.script";
        public object? Result { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Script";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Result = await function.Invoke(context, JsonSerializer.SerializeToElement(new { description = "hi" }), ct);
            return SubActionResult.Next;
        }
    }
}
