using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Representative Streamer.bot-style scripts using the CPH methods, enums and classes added for parity (rewards,
/// pins, moderation, clips, cooldowns, Kick, YouTube, Meld Studio, OBS filters, Parse, Newtonsoft.Json): they
/// compile unchanged and run against fake platform modules.
/// </summary>
public sealed class CphStreamerBotScriptTests : IDisposable
{
    private readonly Fakes _fakes = new();
    private readonly CommandStore _commands;
    private readonly CommandService _commandService;
    private readonly UserVariables _userVariables;
    private readonly TestBot _bot;

    public CphStreamerBotScriptTests()
    {
        var log = new Cuebot.Core.Logging.AppLog();
        _commands = new CommandStore(null, log);
        _userVariables = new UserVariables(null, log);
        TestBot? self = null;
        _bot = new TestBot((b, api) =>
        {
            self = b;
            return
            [
                new CSharpCodeSubAction(new CSharpScriptEngine(api, new CphServices
                {
                    Profile = b.Profile,
                    Bus = b.Bus,
                    Globals = b.Globals,
                    Commands = _commands,
                    UserVariables = _userVariables,
                    CommandService = new CommandService(_commands, new GroupStore(null, b.Log), b.Bus, b.Log),
                })),
            ];
        }, _fakes);
        _commandService = new CommandService(_commands, new GroupStore(null, _bot.Log), _bot.Bus, _bot.Log);
    }

    public void Dispose()
    {
        _bot.Dispose();
        _userVariables.Dispose();
        _commands.Dispose();
    }

    private Task<ActionRunRecord> RunAsync(string code, Dictionary<string, object?>? args = null) =>
        _bot.RunAsync(_bot.AddAction("Test" + Guid.NewGuid().ToString("N"), "code.csharp", code), args);

    private static string Inline(string body, string usings = "") =>
        $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        {{usings}}

        public class CPHInline
        {
            public bool Execute()
            {
                {{body}}
                return true;
            }
        }
        """;

    private Dictionary<string, object?> Args => _bot.Capture.Args!;

    [Fact]
    public async Task Shared_code_without_execute_compiles_and_only_fails_when_run_directly()
    {
        // Streamer.bot's starter pack keeps shared methods in a CPHInline with no Execute(), called by Execute C# Method.
        var run = await RunAsync("public class CPHInline\n{\n    public bool Increase() { return true; }\n}");
        Assert.False(run.Success);
        Assert.Contains("can only be called with Execute C# Method", run.Error);
        Assert.DoesNotContain("compile error", run.Error);
    }

    [Fact]
    public async Task Reward_methods_return_streamerbot_classes()
    {
        var run = await RunAsync(Inline("""
            List<TwitchReward> rewards = CPH.TwitchGetRewards();
            TwitchReward first = rewards[0];
            CPH.SetArgument("rewards", $"{rewards.Count}|{first.Id}|{first.Title}|{first.Cost}|{first.Enabled}|{first.Paused}|{first.IsOurs}|{first.InputRequired}");
            CPH.EnableReward(first.Id);
            CPH.DisableReward(first.Id);
            CPH.PauseReward(first.Id);
            CPH.UnPauseReward(first.Id);
            CPH.UpdateRewardMaxPerStream(first.Id, 5, true);
            CPH.UpdateRewardMaxPerUserPerStream(first.Id, 2);
            TwitchRewardCounter counter = CPH.TwitchGetRewardCounter(first.Id, true);
            TwitchRewardCounter user = CPH.TwitchGetRewardUserCounter("ann", first.Id, true);
            TwitchRewardCounter byId = CPH.TwitchGetRewardUserCounterById("42", first.Id);
            List<TwitchRewardCounter> users = CPH.TwitchGetRewardUserCounters(first.Id, true);
            CPH.SetArgument("counters", $"{counter.Count}|{user.Count}|{user.UserLogin}|{byId.UserId}|{users.Count}|{users[0].UserId}:{users[0].Count}");
            CPH.SetArgument("fulfil", CPH.TwitchRedemptionFulfill(first.Id, "red1"));
            CPH.SetArgument("cancel", CPH.TwitchRedemptionCancel(first.Id, "red2"));
            CPH.TwitchResetRewardCounter(first.Id);
            CPH.TwitchResetRewardUserCounters(first.Id);
            CPH.TwitchResetUserRewardCounter(first.Id, "42");
            CPH.TwitchResetUserRewardCounters("42", true);
            CPH.SetArgument("points", CPH.TwitchGetChannelPointsUsedByUserId("42"));
            """));

        Assert.True(run.Success, run.Error);
        Assert.Equal("1|r1|Hydrate|100|True|False|True|False", Args["rewards"]);
        Assert.Equal("7|3|ann|42|1|42:3", Args["counters"]);
        Assert.Equal(true, Args["fulfil"]);
        Assert.Equal(true, Args["cancel"]);
        Assert.Equal(0L, Args["points"]);
        Assert.False(Args.ContainsKey("counter"));   // sub-action result arguments do not leak

        Assert.Equal(["true", "false"], _fakes.All("twitch.setRewardEnabled").Select(a => a.GetProperty("enabled").GetString()));
        Assert.Equal(["true", "false"], _fakes.All("twitch.setRewardPaused").Select(a => a.GetProperty("paused").GetString()));
        Assert.Equal("15", _fakes.Last("twitch.setRewardMaxPerStream").GetProperty("max").GetString());   // 10 + 5
        Assert.Equal("2", _fakes.Last("twitch.setRewardMaxPerUser").GetProperty("max").GetString());
        Assert.Equal(["Fulfil", "Cancel"], _fakes.All("twitch.updateRedemption").Select(a => a.GetProperty("status").GetString()));
        Assert.Equal("id:42", _fakes.Last("twitch.resetUserRewardCounter").GetProperty("user").GetString());
        Assert.Contains(_bot.Log.Recent(), e => e.Message.Contains("TwitchGetChannelPointsUsedByUserId is not supported in Cuebot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chat_pin_and_moderation_methods_map_to_the_twitch_module()
    {
        var run = await RunAsync(Inline("""
            CPH.TwitchReplyToMessage("hi", "msg1", false);
            CPH.SetArgument("delete", CPH.TwitchDeleteChatMessage("msg2", true));
            CPH.SetArgument("clear", CPH.TwitchClearChatMessages());
            CPH.SetArgument("pin", CPH.TwitchPinChatMessage("msg3", 120));
            CPH.SetArgument("sendPin", CPH.TwitchSendAndPinMessage("Rules: be nice"));
            var pinned = CPH.TwitchGetPinnedMessage();
            CPH.SetArgument("pinned", pinned.Id + "|" + pinned.Message + "|" + pinned.UserName);
            CPH.SetArgument("update", CPH.TwitchUpdatePinnedMessageDuration(60));
            CPH.SetArgument("unpin", CPH.TwitchUnpinMessage());
            CPH.SetArgument("warn", CPH.TwitchWarnUser("@troll", "be nice"));
            CPH.SetArgument("block", CPH.TwitchAddBlockedTerm("badword"));
            CPH.SetArgument("unblock", CPH.TwitchRemoveBlockedTerm("badword"));
            CPH.SetArgument("unblockMissing", CPH.TwitchRemoveBlockedTerm("nothing"));
            CPH.SetArgument("terms", string.Join(",", CPH.TwitchGetBlockedTerms()));
            CPH.SetArgument("approve", CPH.TwitchApproveAutoHeldMessage("held1"));
            CPH.SetArgument("deny", CPH.TwitchDenyAutoHeldMessage("held2"));
            CPH.SetArgument("suspicious", CPH.TwitchAddRestrictedSuspiciousStatus("42"));
            List<TwitchUserInfo> mods = CPH.TwitchGetMods();
            List<TwitchUserInfo> vips = CPH.TwitchGetVips();
            CPH.SetArgument("roles", mods[0].UserLogin + "/" + vips.Count);
            """));

        Assert.True(run.Success, run.Error);
        foreach (var key in new[] { "delete", "clear", "pin", "sendPin", "update", "unpin", "warn", "block", "unblock", "approve", "deny" })
            Assert.True(Args[key] is true, key);
        Assert.Equal(false, Args["unblockMissing"]);
        Assert.Equal(false, Args["suspicious"]);
        Assert.Equal("pin1|Rules|Ann", Args["pinned"]);
        Assert.Equal("badword,other", Args["terms"]);
        Assert.Equal("modann/0", Args["roles"]);

        var reply = _fakes.Last("twitch.replyToMessage");
        Assert.Equal("msg1", reply.GetProperty("messageId").GetString());
        Assert.Equal("Broadcaster", reply.GetProperty("sendAs").GetString());
        Assert.Equal("120", _fakes.Last("twitch.pinMessage").GetProperty("duration").GetString());
        Assert.Equal("troll", _fakes.Last("twitch.warn").GetProperty("user").GetString());
        Assert.Equal(["Allow", "Deny"], _fakes.All("twitch.resolveAutomod").Select(a => a.GetProperty("decision").GetString()));
    }

    [Fact]
    public async Task Clip_overloads_query_the_right_channel_category_and_range()
    {
        var run = await RunAsync(Inline("""
            List<ClipData> all = CPH.GetAllClips(true);
            ClipData clip = all[0];
            CPH.SetArgument("clip", $"{clip.Id}|{clip.Title}|{clip.ViewCount}|{clip.BroadcasterId}|{clip.Duration}|{clip.IsFeatured}|{clip.CreatedAt.ToUniversalTime():yyyy}");
            CPH.GetClips(5);
            CPH.GetClipsForGame(509658, 3);
            CPH.GetClipsForUser("@ann", TimeSpan.FromDays(7));
            CPH.GetClipsForUser(42, 2);
            CPH.GetClipsForUserById("43", new DateTime(2025, 1, 1), new DateTime(2025, 2, 1), 10, false);
            """));

        Assert.True(run.Success, run.Error);
        Assert.Equal("c1|Big play|99|1|29.5|True|2025", Args["clip"]);
        var calls = _fakes.All("twitch.getClips").ToList();
        Assert.Equal(6, calls.Count);
        Assert.True(calls[0].GetProperty("featured").GetBoolean());
        Assert.Equal("", calls[0].GetProperty("user").GetString());
        Assert.Equal("5", calls[1].GetProperty("count").GetString());
        Assert.Equal("509658", calls[2].GetProperty("gameId").GetString());
        Assert.Equal("ann", calls[3].GetProperty("user").GetString());
        Assert.NotEqual("", calls[3].GetProperty("startedAt").GetString());
        Assert.Equal("id:42", calls[4].GetProperty("user").GetString());
        Assert.Equal("id:43", calls[5].GetProperty("user").GetString());
        Assert.False(calls[5].GetProperty("featured").GetBoolean());
        Assert.StartsWith("2025-01-01", calls[5].GetProperty("startedAt").GetString());
    }

    [Fact]
    public async Task Command_cooldown_and_counter_methods_change_cuebot_commands()
    {
        var command = new CommandDefinition { Name = "Hug", Commands = ["!hug"], GlobalCooldownSeconds = 30, UserCooldownSeconds = 10 };
        _commands.Save(command);
        _commands.Increment(command.Id, CommandStore.UserKey("Twitch", "42", "ann"));
        _commands.Increment(command.Id, CommandStore.UserKey("Twitch", "42", "ann"));
        _commands.Increment(command.Id, CommandStore.UserKey("YouTube", "UC9", "yt"));

        var run = await RunAsync(Inline($$"""
            string id = "{{command.Id}}";
            CPH.CommandAddToGlobalCooldown(id, 40);
            CPH.CommandAddToUserCooldown(id, "42", Platform.Twitch, 20);
            CPH.SetArgument("afterAdd", true);
            CommandCounter counter = CPH.CommandGetUserCounterById("42", Platform.Twitch, id);
            CPH.SetArgument("counter", counter.Count + "|" + counter.Platform + "|" + CPH.CommandGetCounter(id));
            CPH.CommandResetUserCounter(id, "42", Platform.Twitch);
            CPH.SetArgument("afterReset", CPH.CommandGetUserCounterById("42", Platform.Twitch, id).Count);
            CPH.CommandResetUsersCounters(id, Platform.YouTube, true);
            CPH.SetArgument("youtube", CPH.CommandGetUserCounterById("UC9", Platform.YouTube, id).Count);
            """));

        Assert.True(run.Success, run.Error);
        Assert.Equal("2|Twitch|3", Args["counter"]);
        Assert.Equal(0, Args["afterReset"]);
        Assert.Equal(0, Args["youtube"]);
    }

    [Fact]
    public async Task Command_cooldowns_add_reset_and_remove_like_streamerbot()
    {
        // The script's CPH and this test share the cooldown tracker through one CommandService.
        var services = new CphServices { Profile = _bot.Profile, Commands = _commands, CommandService = _commandService };
        var command = new CommandDefinition { Name = "Hug", Commands = ["!hug"], GlobalCooldownSeconds = 30, UserCooldownSeconds = 10 };
        _commands.Save(command);
        var key = command.Id.ToString();
        var user = CommandStore.UserKey("Twitch", "42", "");
        var cph = new CphProxy(services, new ScriptApi([]), null, CancellationToken.None);

        cph.CommandAddToGlobalCooldown(key, 40);
        cph.CommandAddToUserCooldown(key, "42", Streamer.bot.Plugin.Interface.Enums.Platform.Twitch, 20);
        Assert.InRange(_commandService.Cooldowns.GlobalRemaining(key).TotalSeconds, 39, 40);
        Assert.InRange(_commandService.Cooldowns.UserRemaining(key, user).TotalSeconds, 19, 20);

        cph.CommandAddToAllUserCooldowns(key, 5);
        Assert.InRange(_commandService.Cooldowns.UserRemaining(key, user).TotalSeconds, 24, 25);

        cph.CommandResetGlobalCooldown(key);   // back to the full 30 s, not zero
        cph.CommandResetUserCooldown(key, "42");
        Assert.InRange(_commandService.Cooldowns.GlobalRemaining(key).TotalSeconds, 29, 30);
        Assert.InRange(_commandService.Cooldowns.UserRemaining(key, user).TotalSeconds, 9, 10);

        cph.CommandRemoveUserCooldown(key, "42", Streamer.bot.Plugin.Interface.Enums.Platform.Twitch);
        Assert.Equal(TimeSpan.Zero, _commandService.Cooldowns.UserRemaining(key, user));
        Assert.True(_commandService.Cooldowns.GlobalRemaining(key) > TimeSpan.Zero);
        cph.CommandRemoveGlobalCooldown(key);
        cph.CommandRemoveAllUserCooldowns(key);
        Assert.Equal(TimeSpan.Zero, _commandService.Cooldowns.GlobalRemaining(key));
    }

    [Fact]
    public async Task Bulk_user_variable_methods_increment_and_create()
    {
        _userVariables.Set("twitch", "1", "ann", "points", 10L, true);
        _userVariables.Set("twitch", "2", "bob", "points", 5L, true);
        _userVariables.Set("twitch", "3", "cat", "points", 1L, false);
        _userVariables.Set("kick", "7", "kat", "points", 1L, true);

        var run = await RunAsync(Inline("""
            CPH.IncrementAllTwitchUsersVar("points", 2, true);
            CPH.IncrementTwitchUsersVarById(new List<string> { "1", "9" }, "points", 3);
            CPH.IncrementOrCreateTwitchUsersVarById(new List<string> { "2", "9" }, "points", 4);
            CPH.IncrementAllKickUsersVarById(new List<string> { "7", "8" }, "points", 1);
            CPH.IncrementOrCreateKickUsersVarById(new List<string> { "8" }, "points", 6);
            CPH.SetKickUsersVarById(new List<string> { "7", "8" }, "vip", true);
            CPH.IncrementAllYouTubeUsersVar("points", 1);
            """));

        Assert.True(run.Success, run.Error);
        Assert.Equal(15L, _userVariables.Get("twitch", "1", "points"));   // 10 + 2 + 3
        Assert.Equal(11L, _userVariables.Get("twitch", "2", "points"));   // 5 + 2 + 4
        Assert.Equal(1L, _userVariables.Get("twitch", "3", "points"));    // not persisted: untouched
        Assert.Equal(4L, _userVariables.Get("twitch", "9", "points"));    // created by IncrementOrCreate only
        Assert.Equal(2L, _userVariables.Get("kick", "7", "points"));
        Assert.Equal(6L, _userVariables.Get("kick", "8", "points"));
        Assert.Equal(true, _userVariables.Get("kick", "8", "vip"));
    }

    [Fact]
    public async Task YouTube_and_Kick_methods_map_to_their_modules()
    {
        var run = await RunAsync(Inline("""
            YouTubeUserInfo me = CPH.YouTubeGetBroadcaster();
            CPH.SetArgument("me", me.UserName + "|" + me.UserLogin + "|" + me.UserId);
            CPH.SetArgument("bot", CPH.YouTubeGetBot() == null);
            CPH.SetArgument("title", CPH.YouTubeSetTitle("Live!"));
            CPH.SetArgument("category", CPH.YouTubeSetCategory("Gaming", "vid1"));
            CPH.SetArgument("badCategory", CPH.YouTubeSetCategory("Underwater basket weaving"));
            CPH.SetArgument("meta", CPH.YouTubeSetMetaData("T", "D", "unlisted", "Music"));
            CPH.SetArgument("tags", CPH.YouTubeAddTags(new List<string> { "a", "b" }));
            CPH.SetArgument("ban", CPH.YouTubeBanUserById("UC2"));
            CPH.SetArgument("timeout", CPH.YouTubeTimeoutUserByName("Troll", 300));
            CPH.SendYouTubeMessageToLatestMonitored("hello", true);

            CPH.SetArgument("kickBan", CPH.KickBanUser("@spam", "spam"));
            CPH.SetArgument("kickTimeout", CPH.KickTimeoutUser("spam", 90));
            CPH.SetArgument("kickReply", CPH.KickReplyToMessage("hey", "k1", true, true));
            CPH.SetArgument("kickDelete", CPH.KickDeleteChatMessage("k2"));
            CPH.SetArgument("kickTitle", CPH.KickSetTitle("Kick title"));
            KickCategory category = CPH.KickSetCategory("Just Chatting");
            CPH.SetArgument("kickCategory", category.Name);
            """));

        Assert.True(run.Success, run.Error);
        Assert.Equal("Me|@me|UC1", Args["me"]);
        Assert.Equal(true, Args["bot"]);
        foreach (var key in new[] { "title", "category", "meta", "tags", "ban", "timeout", "kickBan", "kickTimeout", "kickReply", "kickDelete", "kickTitle" })
            Assert.True(Args[key] is true, key);
        Assert.Equal(false, Args["badCategory"]);
        Assert.Equal("Just Chatting", Args["kickCategory"]);

        Assert.Equal(["20", "10"], _fakes.All("youtube.setCategory").Select(a => a.GetProperty("categoryId").GetString()));
        Assert.Equal("unlisted", _fakes.Last("youtube.setPrivacy").GetProperty("privacy").GetString());
        Assert.Equal("300", _fakes.Last("youtube.timeoutUser").GetProperty("duration").GetString());
        Assert.Equal(2, _fakes.Last("youtube.addTags").GetProperty("tags").GetArrayLength());
        Assert.Equal("spam", _fakes.Last("kick.ban").GetProperty("user").GetString());
        Assert.Equal("90", _fakes.Last("kick.timeout").GetProperty("seconds").GetString());
        Assert.Equal("k1", _fakes.Last("kick.reply").GetProperty("replyTo").GetString());
    }

    [Fact]
    public async Task Meld_and_obs_filter_methods_send_the_right_requests()
    {
        var run = await RunAsync(Inline("""
            CPH.SetArgument("connect", CPH.MeldStudioConnect());
            CPH.SetArgument("connected", CPH.MeldStudioIsConnected(0));
            CPH.SetArgument("index", CPH.MeldStudioGetConnectionByName("Default"));
            CPH.SetArgument("scene", CPH.MeldStudioShowSceneByName("Gaming"));
            CPH.SetArgument("layer", CPH.MeldStudioShowLayerByName("Gaming", "Alerts"));
            CPH.SetArgument("hide", CPH.MeldStudioHideLayerByName("Gaming", "Alerts"));
            CPH.SetArgument("otherConnection", CPH.MeldStudioShowScene("s1", 3));
            CPH.MeldStudioDisconnect();

            CPH.SetArgument("enabled", CPH.ObsIsFilterEnabled("Scene", "Cam", "Blur"));
            CPH.ObsSetFilterState("Scene", "Cam", "Blur", 1);
            CPH.ObsToggleFilter("Scene", "Glow");
            CPH.ObsHideSourcesFilters("Scene", "Cam");
            CPH.ObsSetRandomFilterState("Scene", "Cam", 0);
            CPH.SetArgument("chapter", CPH.ObsCreateRecordChapter("Boss fight"));
            """));

        Assert.True(run.Success, run.Error);
        foreach (var key in new[] { "connect", "connected", "scene", "layer", "hide", "enabled", "chapter" })
            Assert.True(Args[key] is true, key);
        Assert.Equal(0, Args["index"]);
        Assert.Equal(false, Args["otherConnection"]);
        Assert.Equal("Alerts", _fakes.Last("meldstudio.hideLayer").GetProperty("layer").GetString());
        Assert.Single(_fakes.All("meldstudio.disconnect"));

        var obs = _fakes.All("obs.request").Select(a => a.GetProperty("requestType").GetString() + ":" + a.GetProperty("requestData").GetRawText()).ToList();
        Assert.Contains(obs, r => r.StartsWith("SetSourceFilterEnabled:", StringComparison.Ordinal) && r.Contains("\"Cam\"") && r.Contains("\"Blur\"") && r.Contains("false"));
        // Toggle reads the scene's filter first, then flips it.
        Assert.Contains(obs, r => r.StartsWith("SetSourceFilterEnabled:", StringComparison.Ordinal) && r.Contains("\"Scene\"") && r.Contains("\"Glow\"") && r.Contains("false"));
        Assert.Equal(3 + 1, obs.Count(r => r.StartsWith("SetSourceFilterEnabled:", StringComparison.Ordinal) && r.Contains("\"Cam\"")));   // Blur, hide x2, random
        Assert.Contains(obs, r => r == "CreateRecordChapter:{\"chapterName\":\"Boss fight\"}");
    }

    [Fact]
    public async Task Parse_events_and_newtonsoft_work_in_a_typical_script()
    {
        var run = await RunAsync(Inline("""
            var values = new Dictionary<string, object> { ["userName"] = "Ann", ["points"] = 1500 };
            CPH.SetArgument("parsed", CPH.Parse("%userName% has %points% points", values));
            CPH.SetArgument("parsedRun", CPH.Parse("hello %user%"));

            EventType type = CPH.GetEventType();
            EventSource source = CPH.GetSource();
            switch (type)
            {
                case EventType.TwitchFollow: CPH.SetArgument("event", "follow:" + source); break;
                case EventType.TwitchCheer: CPH.SetArgument("event", "cheer"); break;
                default: CPH.SetArgument("event", type.ToString()); break;
            }

            var json = JsonConvert.SerializeObject(new { name = "Ann", tags = new[] { "a", "b" } });
            JObject parsed = JObject.Parse(json);
            CPH.SetArgument("json", json + "|" + parsed["tags"][1]);
            CPH.SetArgument("reason", TwitchBlockReason.Spam + "/" + TwitchBlockContext.Whisper + "/" + StreamlabsDesktopOutputType.Vertical);
            var quote = new QuoteData { Id = 3, Quote = "hi" };
            CPH.SetArgument("quote", quote.Id + quote.Quote);
            """, "using Newtonsoft.Json;\nusing Newtonsoft.Json.Linq;"),
            new() { ["user"] = "Bob", ["eventType"] = "twitch.follow" });

        Assert.True(run.Success, run.Error);
        Assert.Equal("Ann has 1500 points", Args["parsed"]);
        Assert.Equal("hello Bob", Args["parsedRun"]);
        Assert.Equal("follow:Twitch", Args["event"]);
        Assert.Equal("{\"name\":\"Ann\",\"tags\":[\"a\",\"b\"]}|b", Args["json"]);
        Assert.Equal("Spam/Whisper/Vertical", Args["reason"]);
        Assert.Equal("3hi", Args["quote"]);
    }

    [Fact]
    public async Task Source_argument_is_an_EventType_inside_csharp_and_the_code_outside()
    {
        var run = await RunAsync(Inline("""
            EventType source = (EventType)args["__source"];
            CPH.SetArgument("isFollow", source == EventType.TwitchFollow);
            CPH.SetArgument("sourceName", args["__source"].ToString());
            CPH.TryGetArg("__source", out EventType viaTryGet);
            CPH.SetArgument("viaTryGet", (int)viaTryGet);
            CPH.SetArgument("eventType", CPH.GetEventType().ToString());
            """), new() { ["__source"] = 101 });

        Assert.True(run.Success, run.Error);
        Assert.Equal(true, Args["isFollow"]);
        Assert.Equal("TwitchFollow", Args["sourceName"]);
        Assert.Equal(101, Args["viaTryGet"]);
        Assert.Equal("TwitchFollow", Args["eventType"]);
        Assert.Equal(101, Args["__source"]);   // put back for later steps and %__source%

        var none = await RunAsync(Inline("""CPH.SetArgument("none", ((EventType)args["__source"]).ToString());"""), new() { ["__source"] = 0 });
        Assert.True(none.Success, none.Error);
        Assert.Equal("None", Args["none"]);
    }

    [Fact]
    public async Task Newtonsoft_is_usable_from_script_bodies_too()
    {
        var run = await RunAsync("""
            using Newtonsoft.Json.Linq;
            var o = JObject.Parse("{\"a\": 5}");
            Args["a"] = (int)o["a"];
            return true;
            """);

        Assert.True(run.Success, run.Error);
        Assert.Equal(5, Args["a"]);
    }

    /// <summary>Platform functions shaped like the real modules'; every call is recorded.</summary>
    private sealed class Fakes : IScriptFunctionProvider
    {
        private readonly ConcurrentQueue<(string Name, JsonElement Args)> _calls = new();

        public IEnumerable<JsonElement> All(string name) => _calls.Where(c => c.Name == name).Select(c => c.Args);
        public JsonElement Last(string name) => All(name).Last();

        public IEnumerable<ScriptFunction> GetFunctions() =>
        [
            Fn("twitch.getBroadcaster", (_, _) => new { userId = "1", userName = "streamer" }),
            Fn("twitch.getBot", (_, _) => null),
            Fn("twitch.getUser", (_, _) => null),
            Fn("twitch.getRewards", (_, _) => new[]
            {
                new { Id = "r1", Title = "Hydrate", Prompt = "", Cost = 100, IsEnabled = true, IsPaused = false, IsUserInputRequired = false, IsManageable = true },
            }),
            Fn("twitch.setRewardEnabled", (_, _) => null),
            Fn("twitch.setRewardPaused", (_, _) => null),
            Writes("twitch.getRewardInfo", (c, _) => c.Args["rewardMaxPerStream"] = 10),
            Fn("twitch.setRewardMaxPerStream", (_, _) => null),
            Fn("twitch.setRewardMaxPerUser", (_, _) => null),
            Writes("twitch.getRewardCounter", (c, a) =>
            {
                c.Args["counter"] = 7;
                c.Args["userCounter"] = a.GetString("user").Length > 0 ? 3 : 0;
            }),
            Fn("twitch.getRewardUserCounters", (_, _) => new Dictionary<string, int> { ["42"] = 3 }),
            Fn("twitch.updateRedemption", (_, _) => null),
            Fn("twitch.resetRewardCounter", (_, _) => null),
            Fn("twitch.resetRewardUserCounters", (_, _) => null),
            Fn("twitch.resetUserRewardCounter", (_, _) => true),
            Fn("twitch.resetUserRewardCounters", (_, _) => 1),
            Fn("twitch.replyToMessage", (_, _) => null),
            Fn("twitch.deleteMessage", (_, _) => null),
            Fn("twitch.clearChat", (_, _) => null),
            Fn("twitch.pinMessage", (_, _) => null),
            Fn("twitch.sendAndPinMessage", (_, _) => "pin1"),
            Fn("twitch.unpinMessage", (_, _) => null),
            Fn("twitch.updatePinnedDuration", (_, _) => null),
            Writes("twitch.getPinnedMessage", (c, _) =>
            {
                c.Args["isPinned"] = true;
                c.Args["pinnedMessageId"] = "pin1";
                c.Args["pinnedMessage"] = "Rules";
                c.Args["pinnedUser"] = "Ann";
            }),
            Fn("twitch.warn", (_, _) => null),
            Writes("twitch.addBlockedTerm", (c, _) => c.Args["blockedTermId"] = "t1"),
            Fn("twitch.removeBlockedTerm", (_, a) => a.GetString("term") == "badword"),
            Fn("twitch.getBlockedTerms", (_, _) => new[] { new { Id = "t1", Text = "badword" }, new { Id = "t2", Text = "other" } }),
            Fn("twitch.resolveAutomod", (_, _) => null),
            Fn("twitch.getModerators", (_, _) => new[] { new { UserId = "5", UserLogin = "modann", UserName = "ModAnn" } }),
            Fn("twitch.getVips", (_, _) => Array.Empty<object>()),
            Fn("twitch.getClips", (_, _) => new[]
            {
                new
                {
                    Id = "c1", Url = "https://clips.twitch.tv/c1", Title = "Big play", ViewCount = 99, BroadcasterId = "1", CreatorId = "2",
                    CreatedAt = "2025-03-01T10:00:00Z", Duration = 29.5, IsFeatured = true,
                },
            }),
            Fn("youtube.getBroadcaster", (_, _) => new { userName = "Me", userId = "UC1", customUrl = "@me", isSignedIn = true }),
            Fn("youtube.getBot", (_, _) => new { userName = "", userId = "", isSignedIn = false }),
            Fn("youtube.setTitle", (_, _) => null),
            Fn("youtube.setCategory", (_, _) => null),
            Fn("youtube.setMetadata", (_, _) => null),
            Fn("youtube.setPrivacy", (_, _) => null),
            Fn("youtube.addTags", (_, _) => new[] { "a", "b" }),
            Fn("youtube.banUser", (_, _) => null),
            Fn("youtube.timeoutUser", (_, _) => null),
            Fn("youtube.sendMessageToLatestMonitored", (_, _) => null),
            Fn("kick.ban", (_, _) => null),
            Fn("kick.timeout", (_, _) => null),
            Fn("kick.reply", (_, _) => "k9"),
            Fn("kick.deleteMessage", (_, _) => null),
            Fn("kick.setTitle", (_, _) => null),
            Fn("kick.setCategory", (_, _) => "Just Chatting"),
            Fn("meldstudio.connect", (_, _) => true),
            Fn("meldstudio.disconnect", (_, _) => true),
            Fn("meldstudio.isConnected", (_, _) => true),
            Fn("meldstudio.showScene", (_, _) => "Gaming"),
            Fn("meldstudio.showSceneByName", (_, _) => "Gaming"),
            Fn("meldstudio.showLayer", (_, _) => true),
            Fn("meldstudio.hideLayer", (_, _) => false),
            Fn("obs.request", (_, a) => a.GetString("requestType") switch
            {
                "GetSourceFilter" => new { filterEnabled = true },
                "GetSourceFilterList" => (object)new { filters = new[] { new { filterName = "Blur" }, new { filterName = "Glow" } } },
                _ => null,
            }),
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
    }
}
