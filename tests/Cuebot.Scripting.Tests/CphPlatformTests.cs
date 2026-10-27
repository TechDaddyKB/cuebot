using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting.Tests;

/// <summary>CPH platform, custom event, ExecuteMethod and keyboard methods against fake platform modules.</summary>
public sealed class CphPlatformTests : IDisposable
{
    private readonly FakePlatforms _platforms = new();
    private readonly FakeKeyboard _keyboard = new();
    private readonly FakeAnnouncer _announcer = new();
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private TestBot _bot;

    public CphPlatformTests() => _bot = Create(keyboard: true, announcer: true);

    private TestBot Create(bool keyboard, bool announcer)
    {
        var bot = new TestBot((b, api) =>
        [
            new CSharpCodeSubAction(new CSharpScriptEngine(api, new CphServices
            {
                Profile = b.Profile,
                Bus = b.Bus,
                Keyboard = keyboard ? _keyboard : null,
                ChatModeration = new ChatModerationService([], announcer ? [_announcer] : []),
            })),
        ], _platforms);
        bot.Bus.Subscribe(e => _events.Enqueue(e));
        return bot;
    }

    public void Dispose() => _bot.Dispose();

    private Task<ActionRunRecord> RunAsync(string code, Dictionary<string, object?>? args = null) =>
        _bot.RunAsync(_bot.AddAction("Test" + Guid.NewGuid().ToString("N"), "code.csharp", code), args);

    private static string Inline(string body) =>
        $$"""
        using System;
        using Twitch.Common.Models.Api;

        public class CPHInline
        {
            public bool Execute()
            {
                {{body}}
                return true;
            }
        }
        """;

    [Fact]
    public async Task Twitch_user_methods_return_streamerbot_typed_results()
    {
        var run = await RunAsync(Inline("""
            TwitchUserInfo broadcaster = CPH.TwitchGetBroadcaster();
            TwitchUserInfo bot = CPH.TwitchGetBot();
            TwitchUserInfo ann = CPH.TwitchGetUserInfoByLogin("@ann");
            TwitchUserInfo byId = CPH.TwitchGetUserInfoById("42");
            TwitchUserInfo nobody = CPH.TwitchGetUserInfoByLogin("nobody");
            TwitchUserInfoEx ex = CPH.TwitchGetExtendedUserInfoByLogin("ann");
            CPH.SetArgument("broadcaster", broadcaster.UserName + "/" + broadcaster.UserLogin + "/" + broadcaster.UserId);
            CPH.SetArgument("bot", bot == null ? "none" : bot.UserLogin);
            CPH.SetArgument("ann", ann.UserName + "/" + byId.UserLogin);
            CPH.SetArgument("nobody", nobody == null);
            CPH.SetArgument("ex", $"{ex.UserName}|{ex.Game}|{ex.ChannelTitle}|{ex.IsAffiliate}|{ex.IsFollowing}|{ex.IsSubscribed}|{ex.SubscriptionTier}|{ex.CreatedAt.ToUniversalTime():yyyy}|{ex.AccountAge > 0}");
            """));

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.Args!;
        Assert.Equal("Streamer/streamer/1", args["broadcaster"]);
        Assert.Equal("none", args["bot"]);
        Assert.Equal("Ann/ann", args["ann"]);
        Assert.Equal(true, args["nobody"]);
        Assert.Equal("Ann|Chess|Playing chess|True|True|True|1000|2015|True", args["ex"]);
        // The sub-action style functions' result arguments do not leak into the run.
        Assert.False(args.ContainsKey("targetUser"));
        Assert.False(args.ContainsKey("isFollowing"));
    }

    [Fact]
    public async Task Twitch_moderation_and_stream_methods_report_success_as_bool()
    {
        var run = await RunAsync(Inline("""
            CPH.SetArgument("whisper", CPH.SendWhisper("ann", "hi"));
            CPH.SetArgument("ban", CPH.TwitchBanUser("ann", "spam"));
            CPH.SetArgument("banFail", CPH.TwitchBanUser("fail"));
            CPH.SetArgument("timeout", CPH.TwitchTimeoutUser("@ann", 60, "calm down"));
            CPH.SetArgument("unban", CPH.TwitchUnbanUser("ann"));
            CPH.SetArgument("ad", CPH.TwitchRunCommercial(10));
            CPH.SetArgument("title", CPH.SetChannelTitle("New title"));
            GameInfo game = CPH.SetChannelGame("chess");
            CPH.SetArgument("game", game.Id + ":" + game.Name);
            CPH.SetArgument("noGame", CPH.SetChannelGame("nothing") == null);
            ClipData clip = CPH.CreateClip();
            CPH.SetArgument("clip", clip.Id + "|" + clip.Url + "|" + clip.BroadcasterId + "|" + clip.CreatorName);
            StreamMarker marker = CPH.CreateStreamMarker("highlight");
            CPH.SetArgument("marker", marker.Id + "@" + marker.Position + ":" + marker.Description);
            CPH.TwitchAnnounce("Hello", false, "Default");
            """));

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.Args!;
        Assert.Equal(true, args["whisper"]);
        Assert.Equal(true, args["ban"]);
        Assert.Equal(false, args["banFail"]);
        Assert.Equal(true, args["timeout"]);
        Assert.Equal(true, args["unban"]);
        Assert.Equal(true, args["ad"]);
        Assert.Equal(true, args["title"]);
        Assert.Equal("743:Chess", args["game"]);
        Assert.Equal(true, args["noGame"]);
        Assert.Equal("Clip1|https://clips.twitch.tv/Clip1|1|streamer", args["clip"]);
        Assert.Equal("m1@125:highlight", args["marker"]);
        Assert.False(args.ContainsKey("clipId"));
        Assert.False(args.ContainsKey("gameId"));

        Assert.Contains(_platforms.Calls, c => c.Name == "twitch.sendWhisper" && c.Args.GetProperty("sendAs").GetString() == "Broadcaster");
        Assert.Contains(_platforms.Calls, c => c.Name == "twitch.timeout" && c.Args.GetProperty("duration").GetInt32() == 60
                                               && c.Args.GetProperty("user").GetString() == "ann");
        Assert.Contains(_platforms.Calls, c => c.Name == "twitch.runAd" && c.Args.GetProperty("length").GetInt32() == 30);
        Assert.Contains(_platforms.Calls, c => c.Name == "twitch.sendAnnouncement" && c.Args.GetProperty("color").GetString() == "primary");
        Assert.Contains(_bot.Log.Recent(), e => e.Message.Contains("TwitchBanUser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task YouTube_and_Kick_account_methods_map_module_results()
    {
        var run = await RunAsync(Inline("""
            YouTubeBroadcastInfo latest = CPH.YouTubeGetLatestMonitoredBroadcast();
            CPH.SetArgument("yt", latest.Id + "|" + latest.Title + "|" + latest.ChannelId + "|" + latest.LiveChatId);
            CPH.SetArgument("ytAlias", CPH.YouTubeGetLatestBroadcast().Id);
            CPH.SetArgument("ytAll", CPH.YouTubeGetMonitoredBroadcasts().Count);
            KickUserInfo kick = CPH.KickGetBroadcaster();
            CPH.SetArgument("kick", kick.UserName + "|" + kick.UserId);
            CPH.SetArgument("kickBot", CPH.KickGetBot() == null);
            """));

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.Args!;
        Assert.Equal("vid1|Live now|UC1|chat1", args["yt"]);
        Assert.Equal("vid1", args["ytAlias"]);
        Assert.Equal(1, args["ytAll"]);
        Assert.Equal("kicker|77", args["kick"]);
        Assert.Equal(true, args["kickBot"]);
    }

    [Fact]
    public async Task ExecuteMethod_runs_a_method_of_another_codes_class()
    {
        const string helper =
            """
            public class CPHInline
            {
                private int _calls;

                public bool Execute() => true;

                public bool Greet()
                {
                    _calls++;
                    CPH.SetArgument("greeting", "hello " + args["user"] + " #" + _calls);
                    return true;
                }

                public void Mark() => CPH.SetArgument("marked", true);

                public bool No() => false;
            }
            """;
        var helperAction = new ActionDefinition { Name = "Helper action" };
        helperAction.SubActions.Add(new SubActionDefinition
        {
            Type = "code.csharp",
            Parameters = { ["code"] = helper, ["timeoutSeconds"] = "30", ["name"] = "Shared code" },
        });
        _bot.Profile.Profile.Actions.Add(helperAction);
        _bot.Profile.NotifyChanged();

        var run = await RunAsync(Inline("""
            CPH.SetArgument("r1", CPH.ExecuteMethod("shared code", "Greet"));
            CPH.SetArgument("r2", CPH.ExecuteMethod("Helper action", "Greet"));
            CPH.SetArgument("r3", CPH.ExecuteMethod("Shared code", "Mark"));
            CPH.SetArgument("r4", CPH.ExecuteMethod("Shared code", "No"));
            CPH.SetArgument("missingMethod", CPH.ExecuteMethod("Shared code", "Nope"));
            CPH.SetArgument("missingCode", CPH.ExecuteMethod("Nothing", "Greet"));
            """), new() { ["user"] = "Ann" });

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.Args!;
        Assert.Equal(true, args["r1"]);
        Assert.Equal(true, args["r2"]);
        Assert.Equal("hello Ann #2", args["greeting"]);
        Assert.Equal(true, args["r3"]);
        Assert.Equal(true, args["marked"]);
        Assert.Equal(false, args["r4"]);
        Assert.Equal(false, args["missingMethod"]);
        Assert.Equal(false, args["missingCode"]);
    }

    [Fact]
    public async Task TriggerEvent_fires_the_custom_code_event_with_the_run_arguments()
    {
        var run = await RunAsync(Inline("""
            CPH.TriggerEvent("bossDefeated");
            CPH.TriggerEvent("quiet", false);
            """), new() { ["user"] = "Ann" });

        Assert.True(run.Success, run.Error);
        var events = _events.Where(e => e.Type == CodeTriggerRegistry.EventType).ToList();
        Assert.Contains(events, e => Equals(e.Args["eventName"], "bossDefeated") && Equals(e.Args["user"], "Ann"));
        Assert.Contains(events, e => Equals(e.Args["eventName"], "quiet") && !e.Args.ContainsKey("user"));
    }

    [Fact]
    public async Task SendAction_uses_the_action_announcer_or_falls_back_to_chat()
    {
        var run = await RunAsync(Inline("""CPH.SendAction("waves");"""));
        Assert.True(run.Success, run.Error);
        Assert.Equal(["waves"], _announcer.Sent);

        _bot.Dispose();
        _bot = Create(keyboard: false, announcer: false);
        run = await RunAsync(Inline("""CPH.SendAction("waves again");"""));
        Assert.True(run.Success, run.Error);
        Assert.Contains(_platforms.Calls, c => c.Name == "twitch.sendMessage" && c.Args.GetProperty("message").GetString() == "waves again");
    }

    [Fact]
    public async Task KeyboardPress_goes_to_the_keyboard_output_and_throws_without_one()
    {
        var run = await RunAsync(Inline("""CPH.KeyboardPress("^{W}");"""));
        Assert.True(run.Success, run.Error);
        Assert.Equal(["^{W}"], _keyboard.Keys);

        _bot.Dispose();
        _bot = Create(keyboard: false, announcer: false);
        run = await RunAsync(Inline("""CPH.KeyboardPress("{W}");"""));
        Assert.False(run.Success);
        Assert.Contains("CPH.KeyboardPress is not supported", run.Error);
    }

    [Fact]
    public async Task Platform_methods_without_their_module_throw_NotSupported()
    {
        _bot.Dispose();
        _bot = new TestBot((b, api) => [new CSharpCodeSubAction(new CSharpScriptEngine(api, new CphServices { Profile = b.Profile }))]);

        var run = await RunAsync(Inline("""CPH.TwitchGetBroadcaster();"""));

        Assert.False(run.Success);
        Assert.Contains("CPH.TwitchGetBroadcaster is not supported without the 'twitch.getBroadcaster' script function", run.Error);
    }

    private sealed class FakeKeyboard : IKeyboardOutput
    {
        public List<string> Keys { get; } = [];

        public Task SendKeysAsync(string keys, CancellationToken ct = default)
        {
            Keys.Add(keys);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAnnouncer : IChatAnnouncer
    {
        public List<string> Sent { get; } = [];
        public string Platform => "Twitch";
        public bool Supports(ChatAnnouncementKind kind) => kind == ChatAnnouncementKind.Action;

        public Task AnnounceAsync(string text, ChatAnnouncementKind kind, CancellationToken ct = default)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }
    }

    /// <summary>Twitch, YouTube and Kick script functions shaped like the real modules' (sub-action style ones write arguments).</summary>
    private sealed class FakePlatforms : IScriptFunctionProvider
    {
        public ConcurrentQueue<(string Name, JsonElement Args)> Calls { get; } = new();

        public IEnumerable<ScriptFunction> GetFunctions() =>
        [
            Fn("twitch.getBroadcaster", (_, _) => new { userId = "1", userName = "streamer", connected = true }),
            Fn("twitch.getBot", (_, _) => null),
            Fn("twitch.getUser", (_, a) => a.GetString("user") switch
            {
                "ann" or "id:42" => new { Id = "42", Login = "ann", DisplayName = "Ann", BroadcasterType = "affiliate" },
                "id:1" => new { Id = "1", Login = "streamer", DisplayName = "Streamer", BroadcasterType = "" },
                _ => (object?)null,
            }),
            Writes("twitch.getUserInfo", (c, a) =>
            {
                c.Args["targetFound"] = a.GetString("user") == "ann";
                if (a.GetString("user") != "ann") return;
                c.Args["targetUser"] = "Ann";
                c.Args["targetUserName"] = "ann";
                c.Args["targetUserId"] = "42";
                c.Args["targetBroadcasterType"] = "affiliate";
                c.Args["targetCreatedAt"] = "2015-03-01T10:00:00.0000000+00:00";
                c.Args["targetGame"] = "Chess";
                c.Args["targetTitle"] = "Playing chess";
            }),
            Writes("twitch.getFollowAge", (c, _) => c.Args["isFollowing"] = true),
            Writes("twitch.getSubStatus", (c, _) =>
            {
                c.Args["isSubscribed"] = true;
                c.Args["subTier"] = "1000";
            }),
            Fn("twitch.sendWhisper", (_, _) => null),
            Fn("twitch.sendMessage", (_, _) => null),
            Fn("twitch.sendAnnouncement", (_, _) => null),
            Fn("twitch.ban", (_, a) => a.GetString("user") == "fail" ? throw new InvalidOperationException("Twitch said no") : null),
            Fn("twitch.timeout", (_, _) => null),
            Fn("twitch.unban", (_, _) => null),
            Fn("twitch.runAd", (_, _) => null),
            Fn("twitch.setTitle", (_, _) => null),
            Writes("twitch.setGame", (c, a) =>
            {
                if (a.GetString("game") != "chess") throw new InvalidOperationException("No Twitch category matches");
                c.Args["gameId"] = "743";
                c.Args["gameName"] = "Chess";
            }),
            Writes("twitch.createClip", (c, _) =>
            {
                c.Args["clipId"] = "Clip1";
                c.Args["clipUrl"] = "https://clips.twitch.tv/Clip1";
                c.Args["clipEditUrl"] = "https://clips.twitch.tv/Clip1/edit";
            }),
            Writes("twitch.createMarker", (c, _) =>
            {
                c.Args["markerId"] = "m1";
                c.Args["markerPosition"] = 125;
            }),
            Fn("youtube.getBroadcaster", (_, _) => new { userName = "Me", userId = "UC1", isSignedIn = true }),
            Fn("youtube.getLatestMonitoredBroadcast", (_, _) => Broadcast),
            Fn("youtube.getMonitoredBroadcasts", (_, _) => new[] { Broadcast }),
            Fn("kick.getBroadcaster", (_, _) => new Dictionary<string, object?>
            {
                ["broadcastUser"] = "kicker", ["broadcastUserName"] = "kicker", ["broadcastUserId"] = "77", ["botUser"] = "", ["botUserId"] = "",
            }),
        ];

        private static Dictionary<string, object?> Broadcast => new()
        {
            ["id"] = "vid1", ["title"] = "Live now", ["description"] = "", ["privacy"] = "public", ["status"] = "live",
            ["liveChatId"] = "chat1", ["url"] = "https://youtu.be/vid1",
        };

        private ScriptFunction Fn(string name, Func<ActionRunContext, JsonElement, object?> body) =>
            new(name, name, [], (context, args, _) =>
            {
                Calls.Enqueue((name, args.Clone()));
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
