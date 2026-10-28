using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Decks;
using Cuebot.Core.Points;
using Cuebot.Core.Quotes;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Representative scripts using the CPH methods Streamer.bot documents only on sub-action pages, in changelogs or in
/// its old wiki (quotes, points, Decks, OBS media/replay/colour, Streamlabs Desktop, Twitch polls/predictions/raids/
/// Guest Star, Stream Deck, VTube Studio, Voicemod, Wave Link, MIDI, T.I.T.S., toasts): they compile unchanged and run
/// against Cuebot's core services and fake modules.
/// </summary>
public sealed class CphStreamerBotGapTests : IDisposable
{
    private readonly Fakes _fakes = new();
    private readonly UserVariables _userVariables;
    private readonly QuoteStore _quotes;
    private readonly PointsService _points;
    private readonly DeckService _decks;
    private readonly UserRegistry _users;
    private readonly TestBot _bot;

    public CphStreamerBotGapTests()
    {
        var log = new Cuebot.Core.Logging.AppLog();
        var bus = new Cuebot.Core.Events.EventBus(log);
        _userVariables = new UserVariables(null, log);
        _quotes = new QuoteStore(null, log);
        _points = new PointsService(_userVariables, null, bus, log);
        _decks = new DeckService(null, bus, log);
        _users = new UserRegistry(null, bus, log);
        var steps = new Registry(_fakes.Steps(), []);
        _bot = new TestBot((b, api) =>
        [
            new CSharpCodeSubAction(new CSharpScriptEngine(api, new CphServices
            {
                Profile = b.Profile,
                Bus = b.Bus,
                Globals = b.Globals,
                UserVariables = _userVariables,
                Users = _users,
                Quotes = _quotes,
                Points = _points,
                Decks = _decks,
                Registry = steps,
            })),
        ], _fakes);
    }

    public void Dispose()
    {
        _bot.Dispose();
        _decks.Dispose();
        _points.Dispose();
        _quotes.Dispose();
        _users.Dispose();
        _userVariables.Dispose();
    }

    private Task<ActionRunRecord> RunAsync(string body) =>
        _bot.RunAsync(_bot.AddAction("Test" + Guid.NewGuid().ToString("N"), "code.csharp", $$"""
            using System;
            using System.Collections.Generic;

            public class CPHInline
            {
                public bool Execute()
                {
                    {{body}}
                    return true;
                }
            }
            """));

    private Dictionary<string, object?> Args => _bot.Capture.Args!;

    [Fact]
    public async Task Quote_and_points_methods_use_cuebots_quotes_and_currencies()
    {
        _userVariables.Set("twitch", "42", "ann", "seen", 1, true);
        _userVariables.Set("twitch", "43", "bob", "seen", 1, true);

        var run = await RunAsync("""
            int id = CPH.AddQuoteForTwitch("42", "Never give up", true);
            QuoteData quote = CPH.GetQuote(id);
            CPH.SetArgument("quote", $"{quote.Id}|{quote.Quote}|{quote.UserId}|{quote.Platform}|{CPH.GetQuoteCount()}");
            CPH.SetArgument("deleted", CPH.DeleteQuote(id) + "/" + CPH.DeleteQuote(id) + "/" + CPH.GetQuoteCount());
            CPH.SetArgument("trovo", CPH.AddQuoteForTrovo("1", "x"));

            long balance = CPH.AddPoints(null, "ann", "twitch", 100);
            bool spent = CPH.SpendPoints("", "@Ann", "twitch", 30);
            bool tooMuch = CPH.SpendPoints("", "ann", "twitch", 1000);
            bool sent = CPH.TransferPoints(null, "ann", "twitch", "bob", "twitch", 20);
            List<PointsLeaderboardEntry> top = CPH.GetLeaderboard(null, 2);
            CPH.SetArgument("points", $"{balance}|{spent}|{tooMuch}|{sent}|{CPH.GetPoints(null, "ann", "twitch")}|{CPH.GetPoints(null, "bob", "twitch")}|{top[0].UserName}:{top[0].Points}");
            CPH.SetArgument("unknown", CPH.AddPoints(null, "nobody", "twitch", 5));
            CPH.SetGroupMultiplier(null, "VIPs", 1.5);
            CPH.SetArgument("multiplier", FormattableString.Invariant($"{CPH.GetGroupMultiplier(null, "VIPs")}/{CPH.GetUserMultiplier(null, "ann", "twitch")}"));
            PointDefinition points = CPH.GetPointDefinition(null);
            CPH.SetArgument("definition", points.Name + "/" + CPH.GetPointDefinitions().Count);
            CPH.ResetUserPoints(null, "bob", "twitch");
            CPH.SetArgument("reset", CPH.GetPoints(null, "bob", "twitch"));
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal("1|Never give up|42|Twitch|1", Args["quote"]);
        Assert.Equal("True/False/0", Args["deleted"]);
        Assert.Equal(-1, Args["trovo"]);
        Assert.Equal("100|True|False|True|50|20|ann:50", Args["points"]);
        Assert.Equal(0L, Args["unknown"]);
        Assert.Equal("1.5/1", Args["multiplier"]);
        Assert.Equal("points/1", Args["definition"]);
        Assert.Equal(0L, Args["reset"]);
        Assert.Contains(_bot.Log.Recent(), e => e.Message.Contains("AddQuoteForTrovo is not supported in Cuebot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deck_methods_change_cuebots_decks()
    {
        var deck = new DeckDefinition { Name = "Main" };
        deck.Pages[0].Items.Add(new DeckItem { Name = "Go" });
        deck.Pages.Add(new DeckPage { Name = "Two", Items = { new DeckItem { Name = "Back" } } });
        _decks.Save(deck);

        var run = await RunAsync("""
            CPH.DeckItemSetTitle("Main", "Go", "Ready", "#FF0000");
            CPH.DeckItemSetValue("Main", "Go", 42);
            CPH.DeckItemSetBackground("Main", "Go", "#00FF00");
            CPH.DeckNotify("Main", "Hi", "There");
            """);

        Assert.True(run.Success, run.Error);
        var item = _decks.View(deck.Id)!["items"]![0]!;
        Assert.Equal("Ready", (string?)item["title"]);
        Assert.Equal("42", (string?)item["value"]);
        Assert.Equal("#00FF00", (string?)item["background"]);

        run = await RunAsync("""CPH.DeckItemToggleState("Main", "Go");""");
        Assert.True(run.Success, run.Error);
        Assert.Equal(1, (int?)_decks.View(deck.Id)!["items"]![0]!["state"]);

        run = await RunAsync("""CPH.DeckPageNext("Main");""");
        Assert.True(run.Success, run.Error);
        Assert.Equal("Back", (string?)_decks.View(deck.Id)!["items"]![0]!["name"]);
        run = await RunAsync("""CPH.DeckPageSet("Main", 1);""");
        Assert.True(run.Success, run.Error);
        Assert.Equal("Go", (string?)_decks.View(deck.Id)!["items"]![0]!["name"]);
    }

    [Fact]
    public async Task Obs_methods_send_obs_websocket_requests_and_steps()
    {
        var run = await RunAsync("""
            CPH.ObsMediaPause("Scene", "Clip");
            CPH.ObsSetMediaState("Scene", "Clip", 4);
            CPH.ObsSetReplayBufferState(2);
            CPH.ObsPauseRecording();
            CPH.ObsSetColorSourceColor("Scene", "Bg", "#336699");
            CPH.SetArgument("abgr", CPH.ObsConvertRgb(255, 0x33, 0x66, 0x99) + "/" + CPH.ObsConvertColorHex("80336699"));
            CPH.ObsSourceMuteToggle("Scene", "Mic");
            CPH.ObsSetSourceMuteState("Scene", "Mic", 0, 1);
            CPH.SetArgument("connection", CPH.ObsGetConnectionByName("studio b"));
            CPH.SetArgument("random", CPH.ObsSetRandomGroupSourceVisible("Scene", "Cams"));
            CPH.SetArgument("batch", CPH.ObsSendBatchRaw("[{\"requestType\":\"GetVersion\"}]", true));
            CPH.SetArgument("groupSources", string.Join(",", CPH.ObsGetGroupSources("Scene", "Cams")));
            """);

        Assert.True(run.Success, run.Error);
        var requests = _fakes.All("obs.request").ToList();
        Assert.Equal(["TriggerMediaInputAction", "TriggerMediaInputAction", "SaveReplayBuffer", "PauseRecord", "SetInputSettings", "ToggleInputMute", "SetInputMute", "GetGroupSceneItemList"],
            requests.Select(r => r.GetProperty("requestType").GetString()));
        Assert.Equal("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE", requests[0].GetProperty("requestData").GetProperty("mediaAction").GetString());
        Assert.Equal("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_NEXT", requests[1].GetProperty("requestData").GetProperty("mediaAction").GetString());
        Assert.Equal(0xFF996633L, requests[4].GetProperty("requestData").GetProperty("inputSettings").GetProperty("color").GetInt64());
        Assert.Equal("Studio B", requests[6].GetProperty("connection").GetString());   // connection 1 is the second OBS connection
        Assert.True(requests[6].GetProperty("requestData").GetProperty("inputMuted").GetBoolean());
        Assert.Equal($"{0xFF996633L}/{0x80996633L}", Args["abgr"]);
        Assert.Equal(1, Args["connection"]);
        Assert.Equal("Cam 2", Args["random"]);
        Assert.Equal("Cams", _fakes.LastStep("obs.randomGroupSource")["group"]);
        Assert.Equal("[{\"requestStatus\":{\"result\":true}}]", Args["batch"]);
        Assert.True(_fakes.Last("obs.requestBatch").GetProperty("haltOnFailure").GetBoolean());
        Assert.Equal("Cam 1,Cam 2", Args["groupSources"]);
        Assert.False(Args.ContainsKey("obsSourceName"));   // sub-action result arguments do not leak
    }

    [Fact]
    public async Task Slobs_methods_drive_streamlabs_desktop()
    {
        var run = await RunAsync("""
            CPH.SlobsSetScene("Gaming");
            CPH.SlobsShowSource("Gaming", "Cam");
            CPH.SlobsSetSourceVisibilityState("Gaming", "Cam", 2, StreamlabsDesktopOutputType.Vertical);
            CPH.SlobsToggleFilter("Gaming", "Cam", "Blur");
            CPH.SlobsSourceMute("Gaming", "Mic", "");
            CPH.SetArgument("visible", CPH.SlobsIsSourceVisible("Gaming", "Cam") + "/" + CPH.SlobsIsSourceVisible("Gaming", "Overlay"));
            CPH.SetArgument("live", CPH.SlobsIsStreaming() + "/" + CPH.SlobsIsRecording());
            CPH.SetArgument("group", string.Join(",", CPH.SlobsGetGroupSources("Gaming", "Cams")));
            CPH.SetArgument("other", CPH.SlobsIsConnected(1));
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal("Gaming", _fakes.LastStep("slobs.setScene")["scene"]);
        Assert.Equal(["Visible", "Toggle"], _fakes.AllSteps("slobs.setSourceVisibility").Select(s => s["state"]));
        Assert.Equal("Disable", _fakes.LastStep("slobs.setFilterState")["state"]);   // Blur was enabled
        Assert.Equal("Mute", _fakes.LastStep("slobs.setSourceMute")["state"]);
        Assert.Equal("True/False", Args["visible"]);
        Assert.Equal("True/False", Args["live"]);
        Assert.Equal("Cam A", Args["group"]);
        Assert.Equal(false, Args["other"]);
    }

    [Fact]
    public async Task Twitch_channel_methods_map_to_the_twitch_module()
    {
        var run = await RunAsync("""
            CPH.SetArgument("poll", CPH.TwitchPollCreate("Best?", new List<string> { "A", "B" }, 5, 0, 100));
            CPH.TwitchPollTerminate("p1");
            CPH.SetArgument("prediction", CPH.TwitchPredictionCreate("Win?", "Yes", "No", 120));
            CPH.TwitchPredictionResolve("pr1", "o2");
            CPH.SetArgument("raid", CPH.TwitchStartRaidById("99"));
            CPH.SetArgument("tag", CPH.TwitchAddChannelTag("English"));
            CPH.SetArgument("tags", CPH.TwitchSetChannelTags(new List<string> { "A", "B" }));
            CPH.SetArgument("vip", CPH.TwitchAddVip("@ann"));
            CPH.TwitchFollowMode(true, 10);
            CPH.TwitchSlowMode();
            CPH.SetArgument("reward", CPH.UpdateReward("r1", "Hydrate!", null, 50, "#00FF00"));
            CPH.UpdateRewardCooldown("r1", 30, true);
            CPH.TwitchRewardGroupToggleEnable("Games");
            CPH.SetArgument("sub", CPH.TwitchIsUserSubscribed("42", out string tier) + "/" + tier);
            GuestSession session = CPH.TwitchGetGuestStarSession();
            CPH.SetArgument("guests", session.Id + "/" + session.Guests.Count + "/" + session.Guests[0].UserLogin);
            CPH.SendYouTubeMessage("hi", true, "b1");
            CPH.SendYouTubeMessageToAllMonitored("all");
            CPH.SendTrovoMessage("x");
            CPH.SetArgument("bits", CPH.TwitchGetBitsDonatedByUserId("42"));
            """);

        Assert.True(run.Success, run.Error);
        foreach (var key in new[] { "poll", "raid", "tag", "tags", "vip", "reward" }) Assert.True(Args[key] is true, key);
        Assert.Equal("pr9", Args["prediction"]);
        Assert.Equal("True/1000", Args["sub"]);
        Assert.Equal("gs1/1/bob", Args["guests"]);
        Assert.Equal(0L, Args["bits"]);

        var poll = _fakes.Last("twitch.createPoll");
        Assert.Equal("A\nB", poll.GetProperty("choices").GetString());
        Assert.Equal("15", poll.GetProperty("duration").GetString());   // clamped to Twitch's minimum
        Assert.Equal("100", poll.GetProperty("pointsPerVote").GetString());
        Assert.Equal("Terminate", _fakes.Last("twitch.endPoll").GetProperty("mode").GetString());
        Assert.Equal("Yes\nNo", _fakes.Last("twitch.createPrediction").GetProperty("outcomes").GetString());
        Assert.Equal("o2", _fakes.Last("twitch.resolvePrediction").GetProperty("winner").GetString());
        Assert.Equal("id:99", _fakes.Last("twitch.startRaid").GetProperty("user").GetString());
        Assert.Equal("A,B", _fakes.Last("twitch.setTags").GetProperty("tags").GetString());
        Assert.Equal("ann", _fakes.Last("twitch.addVip").GetProperty("user").GetString());
        Assert.Equal("10", _fakes.Last("twitch.followerMode").GetProperty("minutes").GetString());
        Assert.Equal("30", _fakes.Last("twitch.slowMode").GetProperty("seconds").GetString());
        Assert.Equal("50", _fakes.Last("twitch.updateReward").GetProperty("cost").GetString());
        Assert.Equal("", _fakes.Last("twitch.updateReward").GetProperty("prompt").GetString());
        Assert.Equal("#00FF00", _fakes.Last("twitch.setRewardColor").GetProperty("color").GetString());
        Assert.Equal("90", _fakes.Last("twitch.setRewardCooldown").GetProperty("seconds").GetString());   // 60 + 30
        Assert.Equal("Toggle", _fakes.Last("twitch.setRewardGroupEnabled").GetProperty("state").GetString());
        Assert.Equal(["b1", "b1", "b2"], _fakes.All("youtube.sendMessage").Select(m => m.GetProperty("broadcastId").GetString()));
        Assert.Contains(_bot.Log.Recent(), e => e.Message.Contains("SendTrovoMessage is not supported in Cuebot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task App_integration_methods_map_to_their_modules()
    {
        var run = await RunAsync("""
            CPH.StreamDeckSetBackgroundColor("live", "#FF0000");
            CPH.StreamDeckSetTitle("live", "LIVE", 1);
            CPH.StreamDeckSetValue("ads", "30s");
            CPH.SetArgument("tint", CPH.VTubeStudioColorTintByNames("#FF000080", 0.5, new List<string> { "Hair" }));
            VTSModelPosition position = CPH.VTubeStudioGetModelPosition();
            CPH.SetArgument("position", FormattableString.Invariant($"{position.PositionX}/{position.Size}"));
            CPH.SetArgument("hear", CPH.VoiceModHearMyVoiceOn() + "/" + CPH.VoiceModGetVoiceChangerStatus());
            string input = CPH.WaveLinkGetInputIdentifier("Music");
            CPH.SetArgument("wave", input + "/" + CPH.WaveLinkInputGetVolume(input, "Stream") + "/" + CPH.WaveLinkInputGetFilterIdentifier(input, "EQ"));
            CPH.WaveLinkInputFilterBypassBypassed(input, "Local");
            CPH.MidiSendRawByName("Synth", 0x90, 2, 60, 100);
            CPH.MidiSendNoteOn(Guid.Empty, 1, 64, 90, 250, true);
            CPH.SetArgument("throw", CPH.ThrowingSystemThrowItemByName("Ball", 0.1, 3));
            CPH.LumiaSendCommand("red");
            CPH.ShowToastNotification("Hello", "World", "Cuebot");
            CPH.ResetCredits();
            CPH.SetArgument("sound", CPH.PlaySoundFromFolder(@"C:\Sounds", 0.5f, true));
            Dictionary<string, object> pronouns = CPH.PronounLookup("ann");
            CPH.SetArgument("pronoun", pronouns["pronoun"]);
            """);

        Assert.True(run.Success, run.Error);
        Assert.StartsWith("data:image/svg+xml;base64,", _fakes.Last("streamdeck.setBackground").GetProperty("image").GetString());
        Assert.Equal("1", _fakes.Last("streamdeck.setTitle").GetProperty("state").GetString());
        Assert.Equal("ads", _fakes.Last("streamdeck.setValue").GetProperty("buttonId").GetString());

        var tint = _fakes.Last("vtubestudio.request");
        Assert.Equal("ColorTintRequest", tint.GetProperty("messageType").GetString());
        Assert.Equal(128, tint.GetProperty("data").GetProperty("colorTint").GetProperty("colorA").GetInt32());
        Assert.Equal("Hair", tint.GetProperty("data").GetProperty("artMeshMatcher").GetProperty("nameExact")[0].GetString());
        Assert.Equal(true, Args["tint"]);
        Assert.Equal("0.25/-40", Args["position"]);
        Assert.Equal("True/False", Args["hear"]);
        Assert.Equal("ch1/70/fx1", Args["wave"]);
        Assert.Equal("Bypassed", _fakes.Last("wavelink.setFilterBypass").GetProperty("state").GetString());
        Assert.Equal("91 3C 64", _fakes.Last("midi.sendRaw").GetProperty("data").GetString());   // Note On, channel 2
        Assert.Equal(Guid.Empty.ToString(), _fakes.Last("midi.sendNoteOn").GetProperty("device").GetString());
        Assert.True(_fakes.Last("midi.sendNoteOn").GetProperty("noteOff").GetBoolean());
        Assert.Equal(true, Args["throw"]);
        Assert.Equal(3, _fakes.Last("tits.throwItemByName").GetProperty("amount").GetInt32());
        Assert.Equal("red", _fakes.Last("lumia.sendCommand").GetProperty("command").GetString());
        Assert.Equal("World\nCuebot", _fakes.Last("desktop.notify").GetProperty("message").GetString());
        Assert.Single(_fakes.All("credits.reset"));
        Assert.Equal(1.5, Args["sound"]);
        Assert.Equal("true", _fakes.LastStep("audio.playSoundFromFolder")["subfolders"]);
        Assert.Equal("50", _fakes.LastStep("audio.playSoundFromFolder")["volume"]);
        Assert.Equal("she/her", Args["pronoun"]);
    }

    /// <summary>Platform functions and sub-actions shaped like the real modules'; every call is recorded.</summary>
    private sealed class Fakes : IScriptFunctionProvider
    {
        private readonly ConcurrentQueue<(string Name, JsonElement Args)> _calls = new();
        private readonly ConcurrentQueue<(string TypeId, Dictionary<string, string> Parameters)> _steps = new();

        public IEnumerable<JsonElement> All(string name) => _calls.Where(c => c.Name == name).Select(c => c.Args);
        public JsonElement Last(string name) => All(name).Last();
        public IEnumerable<Dictionary<string, string>> AllSteps(string typeId) => _steps.Where(s => s.TypeId == typeId).Select(s => s.Parameters);
        public Dictionary<string, string> LastStep(string typeId) => AllSteps(typeId).Last();

        public IEnumerable<ISubActionHandler> Steps() =>
        [
            Step("obs.randomGroupSource", ["connection", "group", "resultArgument"], (c, _) => c.Args["obsSourceName"] = "Cam 2"),
            Step("slobs.setScene", ["scene"]),
            Step("slobs.setSourceVisibility", ["scene", "source", "state"]),
            Step("slobs.setFilterState", ["source", "filter", "state"]),
            Step("slobs.setSourceMute", ["source", "state"]),
            Step("audio.playSoundFromFolder", ["folder", "subfolders", "volume", "wait", "tag", "useFileName"], (c, _) => c.Args["soundDuration"] = 1.5),
            Step("pronouns.addForUser", ["user", "prefix"], (c, _) =>
            {
                c.Args["hasPronouns"] = true;
                c.Args["pronoun"] = "she/her";
            }),
        ];

        public IEnumerable<ScriptFunction> GetFunctions() =>
        [
            Fn("obs.request", (_, a) => a.GetString("requestType") switch
            {
                "GetGroupSceneItemList" => new
                {
                    sceneItems = new[] { new { sourceName = "Cam 2", sceneItemIndex = 0 }, new { sourceName = "Cam 1", sceneItemIndex = 1 } },
                },
                _ => null,
            }),
            Fn("obs.connections", (_, _) => new[] { new { name = "OBS" }, new { name = "Studio B" } }),
            Fn("obs.requestBatch", (_, _) => new[] { new { requestStatus = new { result = true } } }),
            Fn("slobs.call", (_, a) => (a.GetString("resource"), a.GetString("method")) switch
            {
                ("StreamingService", "getModel") => new { streamingStatus = "live", recordingStatus = "offline" },
                ("ScenesService", "getScenes") => new[]
                {
                    new
                    {
                        id = "s1", name = "Gaming",
                        nodes = new object[]
                        {
                            new { id = "n1", name = "Cam", visible = true, sceneNodeType = "item", parentId = "" },
                            new { id = "n2", name = "Overlay", visible = false, sceneNodeType = "item", parentId = "" },
                            new { id = "f1", name = "Cams", visible = true, sceneNodeType = "folder", parentId = "" },
                            new { id = "n3", name = "Cam A", visible = true, sceneNodeType = "item", parentId = "f1" },
                        },
                    },
                },
                ("SourcesService", "getSourcesByName") => new[] { new { sourceId = "src1" } },
                ("SourceFiltersService", "getFilters") => (object)new[] { new { name = "Blur", visible = true } },
                _ => null,
            }),
            Fn("twitch.createPoll", (_, _) => null),
            Fn("twitch.endPoll", (_, _) => null),
            Writes("twitch.createPrediction", (c, _) => c.Args["predictionId"] = "pr9"),
            Writes("twitch.resolvePrediction", (c, _) => c.Args["success"] = true),
            Writes("twitch.startRaid", (c, _) => c.Args["startRaidSuccess"] = true),
            Fn("twitch.addTag", (_, _) => null),
            Fn("twitch.setTags", (_, _) => null),
            Fn("twitch.addVip", (_, _) => null),
            Fn("twitch.followerMode", (_, _) => null),
            Fn("twitch.slowMode", (_, _) => null),
            Writes("twitch.updateReward", (c, _) => c.Args["rewardCost"] = 50),
            Fn("twitch.setRewardColor", (_, _) => null),
            Writes("twitch.getRewardInfo", (c, _) => c.Args["rewardCooldown"] = 60),
            Fn("twitch.setRewardCooldown", (_, _) => null),
            Writes("twitch.setRewardGroupEnabled", (c, _) => c.Args["rewardsChanged"] = 2),
            Writes("twitch.getSubStatus", (c, _) =>
            {
                c.Args["isSubscribed"] = true;
                c.Args["subTier"] = "1000";
            }),
            Writes("twitch.getGuestStarSession", (c, _) =>
            {
                c.Args["guestStarSessionId"] = "gs1";
                c.Args["guestStarGuestCount"] = 1;
                c.Args["guestStarGuest0UserName"] = "bob";
                c.Args["guestStarGuest0Slot"] = "1";
            }),
            Fn("youtube.sendMessage", (_, _) => null),
            Fn("youtube.getMonitoredBroadcasts", (_, _) => new[] { new { id = "b1" }, new { id = "b2" } }),
            Fn("streamdeck.setBackground", (_, _) => 1),
            Fn("streamdeck.setTitle", (_, _) => 1),
            Fn("streamdeck.setValue", (_, _) => 1),
            Fn("vtubestudio.request", (_, _) => new { matchedArtMeshes = 3 }),
            Fn("vtubestudio.getCurrentModel", (_, _) => new { loaded = true, positionX = 0.25, positionY = 0.0, rotation = 0.0, size = -40.0 }),
            Fn("voicemod.setHearMyself", (_, a) => a.GetString("state") == "On"),
            Fn("voicemod.getState", (_, _) => null),
            Fn("wavelink.getChannels", (_, _) => new[]
            {
                new
                {
                    id = "ch1", name = "Music", volume = 100,
                    mixes = new[] { new { id = "com.elgato.mix.local", name = "Monitor Mix", volume = 40 }, new { id = "com.elgato.mix.stream", name = "Stream Mix", volume = 70 } },
                    effects = new[] { new { id = "fx1", name = "EQ", enabled = true } },
                },
            }),
            Fn("wavelink.setFilterBypass", (_, _) => true),
            Fn("midi.sendRaw", (_, _) => true),
            Fn("midi.sendNoteOn", (_, _) => true),
            Fn("tits.throwItemByName", (_, _) => true),
            Fn("lumia.sendCommand", (_, _) => true),
            Fn("desktop.notify", (_, _) => "System"),
            Fn("credits.reset", (_, _) => true),
        ];

        private ScriptFunction Fn(string name, Func<ActionRunContext, JsonElement, object?> body) =>
            new(name, name, [], (context, args, _) =>
            {
                _calls.Enqueue((name, args.Clone()));
                return Task.FromResult(body(context, args));
            });

        private ScriptFunction Writes(string name, Action<ActionRunContext, JsonElement> body) =>
            Fn(name, (context, args) =>
            {
                body(context, args);
                return null;
            });

        private FakeStep Step(string typeId, string[] keys, Action<ActionRunContext, ParameterValues>? body = null) =>
            new(typeId, keys, (context, p) =>
            {
                _steps.Enqueue((typeId, keys.ToDictionary(k => k, k => p[k])));
                body?.Invoke(context, p);
            });
    }

    private sealed class FakeStep(string typeId, string[] keys, Action<ActionRunContext, ParameterValues> body) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => "Test";
        public string DisplayName => typeId;
        public IReadOnlyList<ParameterDescriptor> Parameters => keys.Select(k => new ParameterDescriptor(k, k)).ToArray();

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            body(context, parameters);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
