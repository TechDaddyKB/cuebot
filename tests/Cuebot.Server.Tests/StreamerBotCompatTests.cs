using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Tests;

/// <summary>
/// Streamer.bot WebSocket compatibility: event names, catalog, payload shapes and request shapes, checked against the
/// public docs (the official client's event list and types, the wiki's event pages, the request reference).
/// </summary>
public sealed class StreamerBotCompatTests
{
    private static string? S(JsonNode? node) => node?.GetValue<string>();

    // ---- Names ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("twitch.emoteModeOn", "Twitch", "ChatEmoteModeOn")]
    [InlineData("twitch.followerModeChanged", "Twitch", "ChatFollowerModeChanged")]
    [InlineData("twitch.slowModeOff", "Twitch", "ChatSlowModeOff")]
    [InlineData("twitch.subscriberModeOn", "Twitch", "ChatSubscriberModeOn")]
    [InlineData("twitch.uniqueModeOff", "Twitch", "ChatUniqueModeOff")]
    [InlineData("twitch.sharedChatBegin", "Twitch", "SharedChatSessionBegin")]
    [InlineData("twitch.sharedChatGiftBomb", "Twitch", "SharedChatCommunitySubGift")]
    [InlineData("twitch.sharedChatUserTimedOut", "Twitch", "SharedChatUserTimedout")]
    [InlineData("twitch.userWarned", "Twitch", "WarnedUser")]
    [InlineData("twitch.pyramid", "Twitch", "PyramidSuccess")]
    [InlineData("twitch.powerUp", "Twitch", "PowerUp")]
    [InlineData("twitch.automodMessageUpdated", "Twitch", "AutoModMessageUpdate")]
    [InlineData("twitch.broadcasterConnected", "Twitch", "BroadcasterEventSubConnected")]
    [InlineData("twitch.subGift", "Twitch", "GiftSub")]
    [InlineData("youtube.broadcastStatisticsUpdated", "YouTube", "StatisticsUpdated")]
    [InlineData("youtube.sponsorOnlyStarted", "YouTube", "NewSponsorOnlyStarted")]
    [InlineData("kick.chatConnected", "Kick", "BroadcasterChatConnected")]
    [InlineData("kick.sevenTvEmoteAdded", "Kick", "SevenTVEmoteAdded")]
    [InlineData("slobs.streamStarted", "StreamlabsDesktop", "StreamingStarted")]
    [InlineData("slobs.recordStopped", "StreamlabsDesktop", "RecordingStopped")]
    [InlineData("slobs.sceneChanged", "StreamlabsDesktop", "SceneChanged")]
    [InlineData("tits.itemHit", "ThrowingSystem", "ItemHit")]
    [InlineData("tits.websocketConnected", "ThrowingSystem", "WebsocketConnected")]
    [InlineData("pally.campaignTip", "Pallygg", "CampaignTip")]
    [InlineData("quotes.added", "Quote", "Added")]
    [InlineData("quotes.shown", "Quote", "Show")]
    [InlineData("pulsoid.heartRate", "Pulsoid", "HeartRatePulse")]
    [InlineData("hyperate.heartRate", "HypeRate", "HeartRatePulse")]
    [InlineData("hyperate.clipCreated", "HypeRate", "TwitchClipCreated")]
    [InlineData("streamlabs.merch", "Streamlabs", "Merchandise")]
    [InlineData("desktop.clipboardChanged", "System", "ClipboardChanged")]
    [InlineData("desktop.toastActivated", "Misc", "ToastActivation")]
    [InlineData("hotkeys.pressed", "Inputs", "InputKeyPress")]
    [InlineData("input.mouseClick", "Inputs", "InputMouseClick")]
    [InlineData("controller.button", "Inputs", "InputControllerButton")]
    [InlineData("controller.axis", "Inputs", "InputControllerAxis")]
    [InlineData("remote.connected", "StreamerBotRemote", "InstanceConnected")]
    [InlineData("remote.signal", "StreamerBotRemote", "InstanceSignal")]
    [InlineData("webhook.custom", "StreamerBot", "CustomWebook")]
    [InlineData("wavelink.channelMuteChanged", "Elgato", "WaveLinkV3ChannelMuteChanged")]
    [InlineData("wavelink.levelMeter", "Elgato", "WaveLinkV3LevelMeterUpdated")]
    [InlineData("elgato.controlCenter.devicePoweredOn", "Elgato", "ControlCenterDeviceOn")]
    [InlineData("elgato.controlCenter.brightnessChanged", "Elgato", "ControlCenterDeviceBrightnessChanged")]
    [InlineData("elgato.cameraHub.webcam.disconnected", "Elgato", "CameraHubWebcamrDisconnected")]
    [InlineData("elgato.cameraHub.webcam.zoomChanged", "Elgato", "CameraHubWebcamZoomChanged")]
    [InlineData("meldstudio.sceneChanged", "MeldStudio", "SceneChanged")]
    [InlineData("crowdcontrol.effectRequest", "CrowdControl", "EffectRequest")]
    [InlineData("donordrive.donation", "DonorDrive", "Donation")]
    [InlineData("tipeeestream.donation", "TipeeeStream", "Donation")]
    [InlineData("treatstream.treat", "TreatStream", "Treat")]
    [InlineData("streamloots.cardRedeemed", "StreamLoots", "CardRedeemed")]
    [InlineData("throne.giftPurchased", "Throne", "GiftPurchased")]
    [InlineData("midi.message", "Midi", "Message")]
    [InlineData("voicemod.voiceChanged", "VoiceMod", "VoiceLoaded")]
    [InlineData("lumia.alert", "Lumia", "Alert")]
    [InlineData("mtion.triggerSent", "Mtion", "TriggerSent")]
    [InlineData("obsbot.zoomChanged", "Obsbot", "ZoomChanged")]
    [InlineData("controller.connected", "Controller", "Connected")]
    public void Names_follow_streamerbot(string type, string source, string sbType) =>
        Assert.Equal(new StreamerBotEventName(source, sbType), StreamerBotEventMap.Map(type));

    [Fact]
    public void Argument_dependent_names()
    {
        static StreamerBotEventName Map(string type, params (string Key, object? Value)[] args) =>
            StreamerBotEventMap.Map(type, args.ToDictionary(a => a.Key, a => a.Value));

        Assert.Equal(new StreamerBotEventName("Twitch", "SevenTVEmoteAdded"), Map("chat.emoteAdded", ("provider", "7TV"), ("platform", "twitch")));
        Assert.Equal(new StreamerBotEventName("YouTube", "BetterTTVEmoteRemoved"), Map("chat.emoteRemoved", ("provider", "BTTV"), ("platform", "youtube")));
        Assert.Equal(new StreamerBotEventName("Chat", "EmoteAdded"), Map("chat.emoteAdded", ("provider", "BTTV"), ("platform", "kick")));
        Assert.Equal(new StreamerBotEventName("Chat", "EmoteAdded"), Map("chat.emoteAdded", ("provider", "FFZ"), ("platform", "twitch")));
        Assert.Equal(new StreamerBotEventName("Misc", "TimedAction"), Map("core.timer", ("timerCron", "")));
        Assert.Equal(new StreamerBotEventName("System", "Cron"), Map("core.timer", ("timerCron", "*/5 * * * *")));
        Assert.Equal(new StreamerBotEventName("MeldStudio", "LayerVisbilityChanged"),
            Map("meldstudio.event", ("meldStudioEvent.event", "LayerVisibilityChanged")));
        Assert.Equal(new StreamerBotEventName("MeldStudio", "TrackMustedStateChanged"),
            Map("meldstudio.event", ("meldStudioEvent.event", "TrackMutedStateChanged")));
        Assert.Equal(new StreamerBotEventName("MeldStudio", "Event"), Map("meldstudio.event", ("meldStudioEvent.event", "SceneStaged")));
        Assert.Equal(new StreamerBotEventName("Kick", "FirstWords"), Map("users.firstWords", ("platform", "Kick")));
        Assert.Equal(new StreamerBotEventName("Misc", "FirstWords"), Map("users.firstWords"));
    }

    /// <summary>Cuebot names under a documented source that Streamer.bot does not document (Cuebot additions).</summary>
    private static readonly HashSet<string> Additions = new(StringComparer.Ordinal)
    {
        "Twitch.GiftSubscriptions", "Misc.FirstWords", "Misc.PresentViewers",
    };

    [Fact]
    public void Every_explicit_and_resolved_name_under_a_streamerbot_source_is_documented()
    {
        var names = StreamerBotEventMap.Explicit.Values
            .Concat(StreamerBotEventMap.Resolvers.Values.SelectMany(r => r.Names))
            .Concat(StreamerBotEventMap.ServerEvents)
            .Where(n => StreamerBotEventNames.IsDocumentedSource(n.Source))
            .Where(n => !StreamerBotEventNames.IsDocumented(n.Source, n.Type) && !Additions.Contains(n.ToString()))
            .Select(n => n.ToString());
        Assert.Empty(names);
    }

    [Fact]
    public void Catalog_equals_what_Map_emits()
    {
        string[] types = ["twitch.chatMessage", "twitch.emoteModeOn", "core.timer", "chat.emoteAdded", "users.firstWords", "slobs.streamStarted"];
        var catalog = StreamerBotEventMap.BuildCatalog(types);
        var expected = types.SelectMany(StreamerBotEventMap.PossibleNames).Concat(StreamerBotEventMap.ServerEvents)
            .Select(n => n.ToString()).ToHashSet();
        var actual = catalog.SelectMany(p => p.Value.Select(t => $"{p.Key}.{t}")).ToHashSet();
        Assert.Equal(expected.Order(), actual.Order());

        // The built-in catalog (no registry) covers every explicit and resolved name.
        foreach (var name in StreamerBotEventMap.Explicit.Values.Concat(StreamerBotEventMap.Resolvers.Values.SelectMany(r => r.Names)))
        {
            Assert.Contains(name.Type, StreamerBotEventMap.Catalog[name.Source]);
        }
    }

    [Fact]
    public async Task GetEvents_matches_the_catalog_of_the_registered_triggers()
    {
        await using var bot = new TestBot();
        var response = await new StreamerBotRequests(bot.Services).HandleAsync("GetEvents", default);
        var events = response["events"]!.AsObject();
        var expected = StreamerBotEventMap.BuildCatalog(bot.Registry.Triggers.SelectMany(t => t.EventTypes));
        Assert.Equal(expected.Keys.Order(), events.Select(p => p.Key).Order());
        foreach (var (source, types) in expected) Assert.Equal(types, events[source]!.AsArray().Select(S));
        Assert.Equal(["Action", "ActionCompleted", "SubAction"], events["Raw"]!.AsArray().Select(S));
        Assert.Contains("ChatEmoteModeOn", events["Twitch"]!.AsArray().Select(S));
        Assert.Contains("StreamingStarted", events["StreamlabsDesktop"]!.AsArray().Select(S));
    }

    // ---- Shapes --------------------------------------------------------------------------------------------------

    private static JsonObject Data(string type, Dictionary<string, object?> args) =>
        StreamerBotEnvelope.FromBotEvent(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)))
            .Message["data"]!.AsObject();

    private static Dictionary<string, object?> TwitchUser(string display = "Ann", string login = "ann", string id = "42") =>
        new() { ["user"] = display, ["userName"] = login, ["userId"] = id };

    [Fact]
    public void Twitch_chat_message_has_the_documented_message_and_user_objects()
    {
        var args = TwitchUser();
        args["message"] = "hi Kappa";
        args["messageId"] = "m1";
        args["color"] = "#FF0000";
        args["isModerator"] = true;
        args["isSubscriber"] = true;
        args["badges"] = "moderator/1,subscriber/12";
        args["emotes"] = "25:3-7";
        args["monthsSubscribed"] = 12;
        var d = Data("twitch.chatMessage", args);

        var m = d["message"]!;
        Assert.Equal("hi Kappa", S(m["message"]));
        Assert.Equal("m1", S(m["msgId"]));
        Assert.Equal("ann", S(m["username"]));
        Assert.Equal("Ann", S(m["displayName"]));
        Assert.Equal(3, m["role"]!.GetValue<int>());
        Assert.True(m["subscriber"]!.GetValue<bool>());
        Assert.Equal("Kappa", S(m["emotes"]![0]!["name"]));
        Assert.Equal(3, m["emotes"]![0]!["startIndex"]!.GetValue<int>());
        Assert.Equal("moderator", S(m["badges"]![0]!["name"]));
        Assert.Equal("ann", S(d["user"]!["login"]));
        Assert.Equal("Ann", S(d["user"]!["name"]));
        Assert.Equal("twitch", S(d["user"]!["type"]));
        Assert.Equal("hi Kappa", S(d["text"]));
        Assert.Equal("twitch.chatMessage", S(d["cuebotEvent"]));
    }

    [Fact]
    public void Twitch_follow_sub_resub_gift_and_bomb()
    {
        var follow = Data("twitch.follow", new(TwitchUser()) { ["followedAt"] = "2026-01-01T00:00:00Z" });
        Assert.Equal("42", S(follow["user_id"]));
        Assert.Equal("ann", S(follow["user_login"]));
        Assert.Equal("Ann", S(follow["user_name"]));
        Assert.Equal("2026-01-01T00:00:00Z", S(follow["followed_at"]));

        var sub = Data("twitch.sub", new(TwitchUser()) { ["tier"] = "2" });
        Assert.Equal(2, sub["subTier"]!.GetValue<int>());
        Assert.Equal("ann", S(sub["userName"]));
        Assert.Equal("Ann", S(sub["displayName"]));
        Assert.Equal("ann", S(sub["user"]!["login"]));

        var resub = Data("twitch.resub", new(TwitchUser()) { ["tier"] = "1", ["cumulativeMonths"] = 6, ["streakMonths"] = 3, ["message"] = "yay" });
        Assert.Equal(6, resub["cumulativeMonths"]!.GetValue<int>());
        Assert.True(resub["shareStreak"]!.GetValue<bool>());
        Assert.Equal("yay", S(resub["message"]));

        var gift = Data("twitch.subGift", new(TwitchUser())
        {
            ["recipientUser"] = "Bob", ["recipientUserName"] = "bob", ["recipientUserId"] = "7", ["tier"] = "1",
            ["durationMonths"] = 3, ["cumulativeTotal"] = 10, ["communityGiftId"] = "g1",
        });
        Assert.Equal("7", S(gift["recipientUserId"]));
        Assert.Equal("bob", S(gift["recipientUsername"]));
        Assert.Equal("Bob", S(gift["recipientDisplayName"]));
        Assert.Equal(3, gift["monthsGifted"]!.GetValue<int>());
        Assert.Equal(10, gift["totalSubsGifted"]!.GetValue<int>());
        Assert.True(gift["fromSubBomb"]!.GetValue<bool>());

        var bomb = Data("twitch.giftBomb", new(TwitchUser()) { ["total"] = 5, ["cumulativeTotal"] = 50, ["tier"] = "1" });
        Assert.Equal(5, bomb["gifts"]!.GetValue<int>());
        Assert.Equal(50, bomb["totalGifts"]!.GetValue<int>());
    }

    [Fact]
    public void Twitch_cheer_raid_and_reward_redemption()
    {
        var cheer = Data("twitch.cheer", new(TwitchUser()) { ["bits"] = 100, ["message"] = "Cheer100 go" });
        Assert.Equal(100, cheer["bits"]!.GetValue<int>());
        Assert.Equal("Cheer100 go", S(cheer["message"]));
        Assert.Equal("ann", S(cheer["username"]));
        Assert.True(cheer["hasBits"]!.GetValue<bool>());

        var raid = Data("twitch.raid", new(TwitchUser()) { ["viewers"] = 12 });
        Assert.Equal(12, raid["viewers"]!.GetValue<int>());
        Assert.Equal(12, raid["viewerCount"]!.GetValue<int>());
        Assert.Equal("ann", S(raid["from_broadcaster_user_login"]));
        Assert.Equal("Ann", S(raid["from_broadcaster_user_name"]));

        var redemption = Data("twitch.rewardRedemption", new(TwitchUser())
        {
            ["rewardId"] = "r1", ["rewardTitle"] = "Show Image", ["rewardCost"] = 500, ["rewardPrompt"] = "p",
            ["userInput"] = "hello", ["redemptionId"] = "x1", ["status"] = "unfulfilled",
        });
        Assert.Equal("Show Image", S(redemption["reward"]!["title"]));     // the HTML overlay guide reads data.reward.title
        Assert.Equal(500, redemption["reward"]!["cost"]!.GetValue<int>());
        Assert.Equal("x1", S(redemption["id"]));
        Assert.Equal("hello", S(redemption["user_input"]));
        Assert.Equal("ann", S(redemption["user_login"]));
        Assert.Equal("Show Image", S(redemption["rewardTitle"]));          // the flat args stay
    }

    [Fact]
    public void YouTube_message_and_member_events_carry_the_youtube_user()
    {
        var args = new Dictionary<string, object?>
        {
            ["platform"] = "youtube", ["user"] = "Ann Channel", ["userName"] = "Ann Channel", ["userId"] = "UC1",
            ["message"] = "hello", ["messageId"] = "e1", ["publishedAt"] = "2026-01-01T00:00:00Z", ["isBroadcaster"] = true,
            ["isMember"] = true, ["channelUrl"] = "https://www.youtube.com/channel/UC1", ["userProfileUrl"] = "pic",
        };
        var d = Data("youtube.chatMessage", args);
        Assert.Equal("hello", S(d["message"]));
        Assert.Equal("e1", S(d["eventId"]));
        Assert.Equal("UC1", S(d["user"]!["id"]));
        Assert.Equal("Ann Channel", S(d["user"]!["name"]));
        Assert.True(d["user"]!["isOwner"]!.GetValue<bool>());
        Assert.True(d["user"]!["isSponsor"]!.GetValue<bool>());
        Assert.Equal("pic", S(d["user"]!["profileImageUrl"]));
        Assert.Equal("https://www.youtube.com/channel/UC1", S(d["user"]!["url"]));

        var sponsor = Data("youtube.newSponsor", args);
        Assert.Equal("UC1", S(sponsor["user"]!["id"]));
        Assert.Equal("youtube", S(sponsor["user"]!["type"]));
    }

    [Fact]
    public void Kick_chat_and_subscription_carry_the_user_object()
    {
        var args = new Dictionary<string, object?>
        {
            ["platform"] = "kick", ["user"] = "Ann", ["userName"] = "Ann", ["userId"] = "9", ["userSlug"] = "ann",
            ["message"] = "hey", ["messageId"] = "k1", ["isSubscriber"] = true,
        };
        var chat = Data("kick.chatMessage", args);
        Assert.Equal("hey", S(chat["message"]));
        Assert.Equal("hey", S(chat["text"]));
        Assert.Equal("ann", S(chat["user"]!["login"]));
        Assert.Equal("kick", S(chat["user"]!["type"]));
        Assert.True(chat["user"]!["subscribed"]!.GetValue<bool>());

        var sub = Data("kick.subscription", args);
        Assert.Equal("9", S(sub["user"]!["id"]));
    }

    [Fact]
    public void General_events_carry_documented_fields()
    {
        var command = Data("command.triggered", new(TwitchUser())
        {
            ["platform"] = "twitch", ["command"] = "!hi", ["counter"] = 3, ["userCounter"] = 1, ["input"] = "there",
        });
        Assert.Equal("!hi", S(command["command"]));
        Assert.Equal(3, command["counter"]!.GetValue<int>());
        Assert.Equal("there", S(command["message"]));
        Assert.Equal("Ann", S(command["user"]!["display"]));

        var timer = Data("core.timer", new() { ["timerId"] = "t1", ["timerName"] = "Ads", ["timerCron"] = "" });
        Assert.Equal("t1", S(timer["id"]));
        Assert.Equal("Ads", S(timer["name"]));

        var heart = Data("pulsoid.heartRate", new() { ["heartRate"] = 72 });
        Assert.Equal(72, heart["heartRate"]!.GetValue<int>());
        Assert.NotNull(heart["measuredAt"]);
    }

    // ---- End to end ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A real WebSocket client on loopback, subscribing like the official client's <c>subscribe: '*'</c> (GetEvents,
    /// then Subscribe with its result), receives documented names and shapes, and the Raw action events.
    /// </summary>
    [Fact]
    public async Task Official_client_style_subscription_receives_documented_events()
    {
        await using var bot = new TestBot(new ServerSettings
        {
            WebSocket = new StreamerBotWebSocketSettings { Enabled = true, Port = 0, AuthenticationEnabled = false },
        });
        await bot.Manager.StartAsync();
        await using var client = await WsClient.ConnectAsync(bot.Manager.WebSocket.Port);
        Assert.Equal("Hello", S((await client.NextAsync(m => S(m["request"]) == "Hello"))["request"]));

        var events = (await client.RequestAsync("GetEvents"))["events"]!;
        var subscribed = await client.RequestAsync("Subscribe", new { events });
        Assert.Equal("ok", S(subscribed["status"]));
        Assert.Contains("RewardRedemption", subscribed["events"]!["Twitch"]!.AsArray().Select(S));

        bot.Bus.Publish(new BotEvent("twitch.rewardRedemption", new Dictionary<string, object?>(TwitchUser())
        {
            ["rewardTitle"] = "Show Image", ["rewardId"] = "r1", ["rewardCost"] = 5,
        }));
        var redemption = await client.NextAsync(WsClient.IsEvent("Twitch", "RewardRedemption"));
        Assert.Equal("Show Image", S(redemption["data"]!["reward"]!["title"]));
        Assert.True(DateTimeOffset.TryParse(S(redemption["timeStamp"]), out _));

        bot.Bus.Publish(new BotEvent("twitch.emoteModeOn", new Dictionary<string, object?>(TwitchUser())));
        await client.NextAsync(WsClient.IsEvent("Twitch", "ChatEmoteModeOn"));

        bot.Bus.Publish(new BotEvent("slobs.streamStarted", new Dictionary<string, object?>()));
        await client.NextAsync(WsClient.IsEvent("StreamlabsDesktop", "StreamingStarted"));

        bot.Bus.Publish(new BotEvent("youtube.chatMessage", new Dictionary<string, object?>
        {
            ["platform"] = "youtube", ["user"] = "Zed", ["userId"] = "UC9", ["message"] = "yo",
        }));
        var yt = await client.NextAsync(WsClient.IsEvent("YouTube", "Message"));
        Assert.Equal("UC9", S(yt["data"]!["user"]!["id"]));

        var action = bot.AddAction("Raw test");
        await client.RequestAsync("DoAction", new { action = new { name = "Raw test" }, args = new { greeting = "hi" } });

        var started = await client.NextAsync(WsClient.IsEvent("Raw", "Action"));
        Assert.Equal(action.Id.ToString(), S(started["data"]!["actionId"]));
        Assert.Equal("Raw test", S(started["data"]!["name"]));
        Assert.Equal("hi", S(started["data"]!["arguments"]!["greeting"]));
        var runId = S(started["data"]!["id"]);

        var step = await client.NextAsync(WsClient.IsEvent("Raw", "SubAction"));
        Assert.Equal(action.Id.ToString(), S(step["data"]!["parentId"]));
        Assert.Equal("Raw test", S(step["data"]!["parentName"]));
        Assert.Equal(runId, S(step["data"]!["id"]));
        Assert.Equal("Capture", S(step["data"]!["name"]));
        Assert.NotNull(step["data"]!["type"]);

        var done = await client.NextAsync(WsClient.IsEvent("Raw", "ActionCompleted"));
        Assert.Equal(runId, S(done["data"]!["id"]));
        Assert.Equal(action.Id.ToString(), S(done["data"]!["actionId"]));
        Assert.True(done["data"]!["success"]!.GetValue<bool>());
        Assert.False(done["data"]!["excludeFromHistory"]!.GetValue<bool>());
        Assert.True(done["data"]!["duration"]!.GetValue<double>() >= 0);
        Assert.NotNull(done["data"]!["completedAt"]);
        Assert.Equal("hi", S(done["data"]!["arguments"]!["greeting"]));
    }

    // ---- Requests ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_responses_have_the_documented_fields()
    {
        await using var bot = new TestBot();
        var requests = new StreamerBotRequests(bot.Services);
        Task<JsonObject> Ask(string request, object? body = null) =>
            requests.HandleAsync(request, System.Text.Json.JsonSerializer.SerializeToElement(body ?? new { }));

        bot.AddAction("Counted", null, new TriggerDefinition { Type = "core.timer" });
        var actions = await Ask("GetActions");
        Assert.Equal(1, actions["actions"]![0]!["trigger_count"]!.GetValue<int>());

        var info = (await Ask("GetInfo"))["info"]!;
        Assert.Equal("websocketServer", S(info["source"]));
        Assert.NotNull(info["mode"]);
        Assert.NotNull(info["osVersion"]);

        var emotes = (await Ask("TwitchGetEmotes"))["emotes"]!;
        foreach (var key in new[] { "userEmotes", "bttvEmotes", "ffzEmotes", "sevenTvEmotes" }) Assert.NotNull(emotes[key]);

        bot.UserVariables.Set("twitch", "42", "Ann", "tuna", 3L, persist: true);
        var one = await Ask("TwitchGetUserGlobal", new { userId = "42", variable = "tuna" });
        Assert.Equal("tuna", S(one["variable"]!["name"]));
        Assert.Equal(3, one["variable"]!["value"]!.GetValue<long>());
        var all = await Ask("TwitchGetUserGlobal", new { userId = "42" });
        Assert.Equal("tuna", S(all["variables"]![0]!["name"]));

        var credits = await Ask("GetCredits");
        foreach (var key in new[] { "follows", "cheers", "subs", "resubs", "giftsubs", "giftbombs", "raided", "rewardredemptions" })
            Assert.NotNull(credits["events"]![key]);
        Assert.NotNull(credits["users"]!["moderators"]);
        Assert.NotNull(credits["hypeTrain"]!["conductors"]);
        Assert.NotNull(credits["top"]!["allBits"]);

        var pronouns = await Ask("GetUserPronouns", new { platform = "twitch", userLogin = "ann" });
        Assert.Equal("ann", S(pronouns["userLogin"]));

        var commands = await Ask("GetCommands");
        Assert.NotNull(commands["commands"]);
    }
}
