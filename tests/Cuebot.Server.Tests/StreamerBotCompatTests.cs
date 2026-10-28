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

    // ---- Account details, emotes, pronouns, chat options ----------------------------------------------------------

    private static Func<string, object?, Task<JsonObject>> Asker(TestBot bot)
    {
        var requests = new StreamerBotRequests(bot.Services);
        return (request, body) => requests.HandleAsync(request, System.Text.Json.JsonSerializer.SerializeToElement(body ?? new { }));
    }

    [Fact]
    public async Task GetBroadcaster_fills_the_accounts_from_the_platform_modules()
    {
        await using var bot = new TestBot(options: StreamerBotFixtures.Options());
        var response = await Asker(bot)("GetBroadcaster", null);

        var twitch = response["platforms"]!["twitch"]!;
        Assert.Equal("Streamer", S(twitch["broadcastUser"]));
        Assert.Equal("streamer", S(twitch["broadcastUserName"]));
        Assert.Equal("1001", S(twitch["broadcastUserId"]));
        Assert.True(twitch["broadcastIsAffiliate"]!.GetValue<bool>());
        Assert.False(twitch["broadcastIsPartner"]!.GetValue<bool>());
        Assert.Equal("HelperBot", S(twitch["botUser"]));
        Assert.Equal("helperbot", S(twitch["botUserName"]));
        Assert.Equal("1002", S(twitch["botUserId"]));
        Assert.Equal("Connected", S(twitch["state"]));

        var youtube = response["platforms"]!["youtube"]!;
        Assert.Equal("Streamer Channel", S(youtube["broadcastUserName"]));
        Assert.Equal("UC1001", S(youtube["broadcastUserId"]));
        Assert.Equal("https://example.com/yt.png", S(youtube["broadcastUserProfileImage"]));

        var kick = response["platforms"]!["kick"]!;
        Assert.Equal("kickstreamer", S(kick["broadcasterLogin"]));
        Assert.Equal("KickStreamer", S(kick["broadcasterUserName"]));
        Assert.Equal("5001", S(kick["broadcasterUserId"]));
        Assert.Equal("https://example.com/kick.png", S(kick["broadcasterProfileUrl"]));

        Assert.Equal(["twitch"], response["connected"]!.AsArray().Select(S));
        Assert.Equal(["youtube", "kick"], response["disconnected"]!.AsArray().Select(S));
    }

    [Fact]
    public async Task GetBroadcaster_without_platform_modules_reports_the_connection_states()
    {
        await using var bot = new TestBot();
        var response = await Asker(bot)("GetBroadcaster", null);
        Assert.Equal("Connected", S(response["platforms"]!["twitch"]!["state"]));
        Assert.Null(response["platforms"]!["kick"]);
        Assert.Equal(["twitch"], response["connected"]!.AsArray().Select(S));
        Assert.Equal(["youtube"], response["disconnected"]!.AsArray().Select(S));
    }

    [Fact]
    public async Task Emote_lists_come_from_the_loaded_sets_and_the_emotes_seen_in_chat()
    {
        await using var bot = new TestBot(options: StreamerBotFixtures.Options());
        bot.Services.Emotes!.FindEmotes("Twitch", "Kappa", new Dictionary<string, object?> { ["emotes"] = "25:0-4" });
        var emotes = (await Asker(bot)("TwitchGetEmotes", null))["emotes"]!;

        var seven = Assert.Single(emotes["sevenTvEmotes"]!.AsArray())!;
        Assert.Equal("catJAM", S(seven["name"]));
        Assert.Equal("7TVGlobal", S(seven["type"]));
        Assert.True(seven["zeroWidth"]!.GetValue<bool>());
        Assert.Equal("BTTVGlobal", S(Assert.Single(emotes["bttvEmotes"]!.AsArray())!["type"]));
        Assert.Equal("OMEGALUL", S(Assert.Single(emotes["ffzEmotes"]!.AsArray())!["name"]));
        var kappa = Assert.Single(emotes["userEmotes"]!.AsArray())!;
        Assert.Equal("Kappa", S(kappa["name"]));
        Assert.Equal("twitch", S(kappa["type"]));
        Assert.StartsWith("https://static-cdn.jtvnw.net/emoticons/v2/25/", S(kappa["imageUrl"]));
    }

    [Fact]
    public async Task Chat_payloads_list_third_party_emotes_and_cheermotes()
    {
        await using var bot = new TestBot(options: StreamerBotFixtures.Options());
        var services = new StreamerBotPayloadServices(bot.Services.Pronouns, bot.Services.Emotes);
        JsonObject Payload(string type, Dictionary<string, object?> args) =>
            StreamerBotEnvelope.FromBotEvent(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)), services)
                .Message["data"]!.AsObject();

        var chat = Payload("twitch.chatMessage", new(TwitchUser()) { ["message"] = "😀 Kappa catJAM", ["emotes"] = "25:2-6" });
        var emotes = chat["message"]!["emotes"]!.AsArray();
        Assert.Equal(["Twitch", "7TV"], emotes.Select(e => S(e!["type"])));
        Assert.Equal(8, emotes[1]!["startIndex"]!.GetValue<int>());     // code points, as Twitch counts them
        Assert.Equal(13, emotes[1]!["endIndex"]!.GetValue<int>());
        Assert.Equal(0, emotes[0]!["bits"]!.GetValue<int>());

        var cheer = Payload("twitch.cheer", new(TwitchUser()) { ["bits"] = 1100, ["message"] = "Cheer1000 hi Cheer100" });
        var cheermotes = cheer["cheerEmotes"]!.AsArray();
        Assert.Equal([1000, 100], cheermotes.Select(c => c!["bits"]!.GetValue<int>()));
        Assert.Equal("#1db2a5", S(cheermotes[0]!["color"]));
        Assert.Equal(13, cheermotes[1]!["startIndex"]!.GetValue<int>());
        Assert.Contains("/actions/cheer/dark/animated/100/", S(cheermotes[1]!["imageUrl"]));

        // Words that do not add up to the cheer's bits are not guessed at.
        var unclear = Payload("twitch.cheer", new(TwitchUser()) { ["bits"] = 50, ["message"] = "Level99 Cheer50 x1" });
        Assert.Empty(unclear["cheerEmotes"]!.AsArray());
    }

    [Fact]
    public async Task GetUserPronouns_and_user_payloads_carry_streamer_bot_pronoun_forms()
    {
        await using var bot = new TestBot(options: StreamerBotFixtures.Options());
        var ask = Asker(bot);

        var ann = (await ask("GetUserPronouns", new { platform = "twitch", userLogin = "Ann" }))["pronoun"]!;
        Assert.True(ann["userFound"]!.GetValue<bool>());
        Assert.Equal("She", S(ann["pronounSubject"]));
        Assert.Equal("Her", S(ann["pronounObject"]));
        Assert.Equal("Her", S(ann["pronounPossessive"]));
        Assert.Equal("Hers", S(ann["pronounPronoun"]));
        Assert.Equal("Herself", S(ann["pronounReflexive"]));
        Assert.Equal("Was", S(ann["pronounPastTense"]));
        Assert.Equal("is", S(ann["pronounCurrentTenseLower"]));
        Assert.Equal("s", S(ann["pronounPluralLower"]));
        Assert.Equal("(She/Her)", S(ann["pronouns"]));
        Assert.Equal("She/Her", S(ann["pronounsPlain"]));

        var theo = (await ask("GetUserPronouns", new { platform = "twitch", userLogin = "theo" }))["pronoun"]!;
        Assert.Equal("Themselves", S(theo["pronounReflexive"]));
        Assert.Equal("Were", S(theo["pronounPastTense"]));
        Assert.Equal("", S(theo["pronounPluralLower"]));

        var nobody = (await ask("GetUserPronouns", new { platform = "twitch", userLogin = "nobody" }))["pronoun"]!;
        Assert.False(nobody["userFound"]!.GetValue<bool>());
        Assert.Equal("", S(nobody["pronounSubject"]));

        // Payloads only use what is already known: ann was looked up above, bob never was.
        var services = new StreamerBotPayloadServices(bot.Services.Pronouns, bot.Services.Emotes);
        var chat = StreamerBotEnvelope.FromBotEvent(new BotEvent("twitch.chatMessage", TwitchUser()), services).Message["data"]!;
        Assert.Equal("She", S(chat["user"]!["pronouns"]!["pronounSubject"]));
        var bob = StreamerBotEnvelope.FromBotEvent(new BotEvent("twitch.chatMessage", TwitchUser("Bob", "bob", "7")), services).Message["data"]!;
        Assert.False(bob["user"]!["pronouns"]!["userFound"]!.GetValue<bool>());
        Assert.False(bot.Services.Pronouns!.TryGetCached("bob").Known);
    }

    [Fact]
    public async Task SendMessage_passes_bot_internal_and_reply_options_to_the_chat_sender()
    {
        await using var bot = new TestBot();
        var ask = Asker(bot);
        await ask("SendMessage", new { platform = "twitch", message = "one", bot = true, @internal = false, replyId = "m1" });
        await ask("SendMessage", new { platform = "twitch", message = "two", bot = "false" });
        await ask("SendMessage", new { platform = "twitch", message = "three" });

        var sent = bot.Sender.SentWithOptions.ToArray();
        Assert.Equal(new ChatSendOptionsView(true, false, "m1"), View(sent[0].Options));
        Assert.Equal(new ChatSendOptionsView(false, true, null), View(sent[1].Options));
        Assert.Equal(["one", "two", "three"], bot.Sender.Sent);
        Assert.Equal(2, sent.Length);   // no options: the plain send, as before

        static ChatSendOptionsView View(Cuebot.Core.Chat.ChatSendOptions o) => new(o.Bot, o.Internal, o.ReplyTo);
    }

    private readonly record struct ChatSendOptionsView(bool? Bot, bool Internal, string? ReplyTo);

    [Fact]
    public async Task GetGlobal_and_named_GetUserGlobal_answer_in_both_documented_and_client_shapes()
    {
        await using var bot = new TestBot();
        var ask = Asker(bot);
        bot.Globals.SetValue("score", 4L, persist: true);
        bot.UserVariables.Set("twitch", "42", "Ann", "tuna", 3L, persist: true);

        var global = await ask("GetGlobal", new { variable = "score" });
        Assert.Equal(4, global["variable"]!["value"]!.GetValue<long>());               // the request reference
        Assert.Equal(4, global["variables"]!["score"]!["value"]!.GetValue<long>());    // what client.getGlobal reads

        var user = await ask("TwitchGetUserGlobal", new { userId = "42", variable = "tuna" });
        Assert.Equal(3, user["variable"]!["value"]!.GetValue<long>());
        Assert.Equal("tuna", S(user["variables"]![0]!["name"]));                      // client.getUserGlobal finds it here

        Assert.True(StreamerBotRequests.IsKnown("TrovoGetUserGlobals"));
        Assert.Equal(0, (await ask("TrovoGetUserGlobals", new { variable = "tuna" }))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Custom_events_also_go_out_as_Custom_Event_for_customEventResponse()
    {
        await using var bot = new TestBot(new ServerSettings
        {
            WebSocket = new StreamerBotWebSocketSettings { Enabled = true, Port = 0, AuthenticationEnabled = false },
        });
        await bot.Manager.StartAsync();
        await using var client = await WsClient.ConnectAsync(bot.Manager.WebSocket.Port);
        await client.RequestAsync("Subscribe", new { events = new { Custom = new[] { "Event", "CodeEvent" } } });
        Assert.Contains("Event", (await client.RequestAsync("GetEvents"))["events"]!["Custom"]!.AsArray().Select(S));

        await client.RequestAsync("ExecuteCodeTrigger", new { triggerName = "sb:client:res:1", args = new { score = 3 } });
        var custom = await client.NextAsync(WsClient.IsEvent("Custom", "Event"));
        Assert.Equal("sb:client:res:1", S(custom["data"]!["eventName"]));
        Assert.Equal(3, custom["data"]!["args"]!["score"]!.GetValue<int>());
        await client.NextAsync(WsClient.IsEvent("Custom", "CodeEvent"));
    }

    [Fact]
    public void Newly_shaped_events_have_their_documented_fields()
    {
        var cleared = Data("twitch.chatCleared", TwitchUser("Me", "me", "1"));
        Assert.Equal("clear", S(cleared["moderation_action"]));
        Assert.Equal("1", S(cleared["created_by_user_id"]));

        var untimeout = Data("twitch.userUntimedOut", new(TwitchUser()) { ["moderatorUserName"] = "mod", ["moderatorUserId"] = "9" });
        Assert.Equal("untimeout", S(untimeout["moderation_action"]));
        Assert.Equal("ann", S(untimeout["target_user_login"]));

        var poll = Data("twitch.pollCreated", new()
        {
            ["pollId"] = "p1", ["choiceCount"] = 0,
            ["poll._json"] = """{"channel_points_voting":{"is_enabled":true,"amount_per_vote":25}}""",
        });
        Assert.True(poll["channel_points_voting"]!["is_enabled"]!.GetValue<bool>());
        Assert.Equal(25, poll["channel_points_voting"]!["amount_per_vote"]!.GetValue<long>());

        var charity = Data("twitch.charityDonation", new(TwitchUser())
        {
            ["amount"] = "5.5", ["currency"] = "EUR", ["charity.amount.valueMinor"] = 550L, ["charity.amount.currency"] = "EUR",
        });
        Assert.Equal(550, charity["amount"]!["value"]!.GetValue<long>());
        Assert.Equal("EUR", S(charity["amount"]!["currency"]));

        var donation = Data("streamlabs.donation", new() { ["donationFrom"] = "Ann", ["donationAmount"] = 5.0, ["donationFormattedAmount"] = "$5.00" });
        Assert.Equal("Ann", S(donation["from"]));
        Assert.Equal("$5.00", S(donation["formattedAmount"]));

        var obs = Data("obs.connected", new() { ["studioVersion"] = "31.0.0", ["host"] = "127.0.0.1", ["obsConnection"] = "Main" });
        Assert.Equal("31.0.0", S(obs["obs.studioVersion"]));
        Assert.Equal("Main", S(obs["obs.name"]));
        Assert.Equal("127.0.0.1", S(obs["obs.host"]));
    }

    [Fact]
    public void File_watcher_changes_carry_the_file_contents()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cuebot-fw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var text = Path.Combine(dir, "now playing.txt");
            File.WriteAllText(text, "Song\r\nArtist");
            var lines = Data("core.fileChanged", new() { ["fullPath"] = text, ["fileName"] = "now playing.txt" });
            Assert.Equal(["Song", "Artist"], lines["lines"]!.AsArray().Select(S));
            Assert.Equal("now playing", S(lines["name"]));
            Assert.Null(lines["data"]);

            var json = Path.Combine(dir, "stats.json");
            File.WriteAllText(json, """{"wins": 3, "name": "run"}""");
            var data = Data("core.fileChanged", new() { ["fullPath"] = json });
            Assert.Equal(3, data["data"]!["wins"]!.GetValue<int>());
            Assert.Null(data["lines"]);

            var created = Data("core.fileCreated", new() { ["fullPath"] = text });
            Assert.Null(created["lines"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
