using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Transfer;
using Cuebot.Obs;
using Cuebot.Obs.Streamlabs;
using Cuebot.Obs.Tests.Fakes;
using Cuebot.Twitch;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Transfer;

/// <summary>
/// A real Streamer.bot 1.0.7 export (TestData/streamerbot-exported-setup.sb, made by us in Streamer.bot: a small
/// streamer setup of 8 actions, 8 commands and a timer) imported through the Import page's path into the full app,
/// with Twitch, OBS and Streamlabs Desktop connected to their fakes. Each trigger is fired the way the platform fires
/// it and the outcome is compared with what Streamer.bot does: the chat text sent, the OBS and Streamlabs requests,
/// the file written, the globals. Streamer.bot imports commands disabled and Cuebot imports code disabled, so the test
/// enables what it uses, as a user does after reviewing the import.
/// </summary>
public sealed class StreamerBotSetupBehaviourTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "streamerbot-exported-setup.sb"));

    [Fact]
    public async Task Import_brings_every_action_command_and_the_timer()
    {
        await using var bot = await ImportedBot.StartAsync();

        Assert.Equal(
            ["E2E Follow alert", "E2E Fortune", "E2E Hello", "E2E Hydrate counter", "E2E Reminder", "E2E Reward", "E2E Shoutout (C#)", "E2E Stream online"],
            bot.Actions.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Empty(bot.Plan.Report.Placeholders);
        Assert.DoesNotContain(bot.Plan.Report.Warnings, w => w.Contains("which this Cuebot does not have"));
        // Streamer.bot imports commands and timers disabled; both copies of each command arrive.
        Assert.Equal(8, bot.Commands.Count);
        Assert.All(bot.Commands, c => Assert.False(c.Enabled));
        var timer = Assert.Single(bot.Profile.Snapshot.Timers);
        Assert.False(timer.Enabled);
        Assert.Equal(900, timer.IntervalSeconds);
        Assert.True(timer.Repeat);
        // Only the code action is held back.
        Assert.False(bot.Action("E2E Shoutout (C#)").Enabled);
        Assert.All(bot.Actions.Where(a => a.Name != "E2E Shoutout (C#)"), a => Assert.True(a.Enabled, a.Name));
    }

    [Fact]
    public async Task Hello_command_greets_the_chatter()
    {
        await using var bot = await ImportedBot.StartAsync();
        await bot.EnableCommandsOfAsync("E2E Hello");

        await bot.ChatAsync("!hello", "Viewer32");

        Assert.Equal(["Hello Viewer32, welcome in!"], await bot.ChatSentAsync(1));
        // Text that is not the command does nothing.
        await bot.ChatAsync("hello there", "Viewer32");
        await bot.ChatAsync("!hellothere", "Viewer32");
        // The commands listen to Twitch messages only (Streamer.bot "sources": 1).
        bot.Publish(new BotEvent(Cuebot.Core.Chat.ChatHub.MessageEvent,
            new Dictionary<string, object?> { ["platform"] = "YouTube", ["message"] = "!hello", ["user"] = "TubeViewer" }));
        await bot.ChatAsync("!hello", "Other_One");
        Assert.Equal(["Hello Viewer32, welcome in!", "Hello Other_One, welcome in!"], await bot.ChatSentAsync(2));
    }

    [Fact]
    public async Task Follow_shows_the_alert_for_three_seconds_then_thanks()
    {
        await using var bot = await ImportedBot.StartAsync();

        await bot.EventAsync("channel.follow", EventSubMessages.FollowEvent);

        Assert.Equal(["Thanks for the follow, Cool_User!"], await bot.ChatSentAsync(1));
        var toggles = bot.ObsRequests.Where(r => r.Type == "SetSceneItemEnabled").ToArray();
        Assert.Equal(2, toggles.Length);
        Assert.All(toggles, t => Assert.Equal("Alerts", t.Data.GetProperty("sceneName").GetString()));
        Assert.All(toggles, t => Assert.Equal(bot.Obs.Scenes["Alerts"].Single(i => i.Source == "Follow Alert").Id, t.Data.GetProperty("sceneItemId").GetInt32()));
        Assert.True(toggles[0].Data.GetProperty("sceneItemEnabled").GetBoolean());
        Assert.False(toggles[1].Data.GetProperty("sceneItemEnabled").GetBoolean());
        // The imported wait is exactly three seconds; the real-time gap only has to show it was honoured (at least that
        // long) and not doubled. Busy CI runners add more than a second on top (4.6 s seen on windows-latest).
        var delay = Assert.Single(bot.Action("E2E Follow alert").SubActions, s => s.Type == "core.delay");
        Assert.Equal("3000", delay.Parameters["milliseconds"]);
        var shown = toggles[1].At - toggles[0].At;
        Assert.InRange(shown.TotalMilliseconds, 2900, 5900);
        Assert.False(bot.Obs.Scenes["Alerts"].Single(i => i.Source == "Follow Alert").Enabled);
    }

    [Fact]
    public async Task Hydrate_counts_in_a_persisted_global_and_turns_legendary_after_nine()
    {
        await using var bot = await ImportedBot.StartAsync();
        await bot.EnableCommandsOfAsync("E2E Hydrate counter");

        for (var i = 1; i <= 11; i++)
        {
            await bot.ChatAsync("!hydrate", "Viewer32");
            await bot.ChatSentAsync(i);
        }

        string[] expected =
        [
            .. Enumerable.Range(1, 9).Select(n => $"Hydrate! ({n} so far)"),
            "That is 10 hydrates, legend!", "That is 11 hydrates, legend!",
        ];
        Assert.Equal(expected, bot.ChatTexts);
        Assert.Equal("11", bot.Globals.Get("hydrateCount"));
        Assert.True(bot.Globals.IsPersisted("hydrateCount"));
        Assert.False(bot.Globals.Contains("count"));   // %count% is an argument of the run, not a global
    }

    [Fact]
    public async Task Stream_online_switches_obs_and_streamlabs_scenes_and_logs_the_time()
    {
        await using var bot = await ImportedBot.StartAsync();
        // The export writes to C:\Users\docker\cuebot-e2e\stream-log.txt; on another machine the user picks a path.
        var log = Path.Combine(bot.DataDir, "stream-log.txt");
        File.WriteAllText(log, "earlier line\n");
        await bot.EditStepAsync("E2E Stream online", "core.writeFile", s => s.Parameters["path"] = log);

        var before = DateTime.Now;
        await bot.EventAsync("stream.online",
            """
            {"id":"9001","broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer",
             "type":"live","started_at":"2026-01-01T12:00:00Z"}
            """);
        await bot.RunsOfAsync("E2E Stream online", 1);

        Assert.Equal("Starting Soon", bot.Obs.CurrentScene);
        Assert.Contains(bot.ObsRequests, r => r.Type == "SetCurrentProgramScene" && r.Data.GetProperty("sceneName").GetString() == "Starting Soon");
        Assert.Equal("s-soon", bot.Slobs.ActiveScene);
        Assert.Contains(bot.Slobs.Calls, c => c is { Resource: "ScenesService", Method: "makeSceneActive" } && c.Args[0].GetString() == "s-soon");
        // Streamer.bot's %time% is hh:mm and %date% yyyy/MM/dd; Write To File appends the line and a line break.
        var lines = File.ReadAllLines(log);
        Assert.Equal(2, lines.Length);
        Assert.Equal("earlier line", lines[0]);
        var after = DateTime.Now;
        string Line(DateTime t) => $"Live at {t.ToString("hh:mm", CultureInfo.InvariantCulture)} on {t.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}";
        Assert.Contains(lines[1], new[] { Line(before), Line(after) });
        Assert.EndsWith("\n", File.ReadAllText(log));
    }

    [Fact]
    public async Task Shoutout_code_step_arrives_without_code_as_streamer_bot_exported_it()
    {
        await using var bot = await ImportedBot.StartAsync();

        // Streamer.bot 1.0.7 wrote this step with "byteCode": null and no references: the export has no source.
        var step = Assert.Single(bot.Action("E2E Shoutout (C#)").SubActions);
        Assert.Equal("code.csharp", step.Type);
        Assert.Equal("", step.Parameters["code"]);
        Assert.Contains(bot.Plan.Report.Warnings, w => w.Contains("E2E Shoutout (C#)") && w.Contains("no code"));
    }

    [Fact]
    public async Task Shoutout_code_sends_the_message_sets_the_global_and_argument_and_explains_usage()
    {
        await using var bot = await ImportedBot.StartAsync();
        await bot.EnableCommandsOfAsync("E2E Shoutout (C#)");
        // The user pastes the action's code back in (the export carries none) and enables the action.
        await bot.EditStepAsync("E2E Shoutout (C#)", "code.csharp", s => s.Parameters["code"] = ShoutoutCode);
        await bot.Profile.SetActionEnabledAsync("E2E Shoutout (C#)", true);

        await bot.ChatAsync("!so @CoolStreamer", "Viewer32");
        var run = (await bot.RunsOfAsync("E2E Shoutout (C#)", 1))[0];

        Assert.True(run.Success, run.Error);
        Assert.Equal(["Go check out CoolStreamer at https://twitch.tv/CoolStreamer !"], await bot.ChatSentAsync(1));
        Assert.Equal("CoolStreamer", bot.Globals.Get("lastShoutout"));
        Assert.True(bot.Globals.IsPersisted("lastShoutout"));
        Assert.Equal("CoolStreamer", run.Arguments?["shoutoutTarget"]);

        await bot.ChatAsync("!so", "Viewer32");
        run = (await bot.RunsOfAsync("E2E Shoutout (C#)", 2))[1];
        Assert.Equal("Usage: !so <username>", (await bot.ChatSentAsync(2))[1]);
        Assert.Equal("CoolStreamer", bot.Globals.Get("lastShoutout"));
        Assert.False(run.Arguments?.ContainsKey("shoutoutTarget") ?? false);
    }

    [Fact]
    public async Task Fortune_sends_exactly_one_of_its_three_lines_each_time()
    {
        await using var bot = await ImportedBot.StartAsync();
        await bot.EnableCommandsOfAsync("E2E Fortune");
        string[] fortunes =
        [
            "Fortune: today is a good day to stream.", "Fortune: a raid is in your future.", "Fortune: drink some water.",
        ];

        for (var i = 1; i <= 12; i++)
        {
            await bot.ChatAsync("!fortune", "Viewer32");
            await bot.RunsOfAsync("E2E Fortune", i);
            Assert.Equal(i, bot.ChatTexts.Count);
        }

        Assert.All(bot.ChatTexts, t => Assert.Contains(t, fortunes));
        Assert.True(bot.ChatTexts.Distinct().Count() > 1, "the random group always picked the same line");
    }

    [Fact]
    public async Task Reminder_timer_posts_every_fifteen_minutes()
    {
        await using var bot = await ImportedBot.StartAsync();
        await bot.Profile.EditAsync(p => p.Timers.Single().Enabled = true);

        bot.Timers.Tick();
        bot.Clock.Advance(TimeSpan.FromSeconds(899));
        bot.Timers.Tick();
        Assert.Empty(bot.Runs);

        bot.Clock.Advance(TimeSpan.FromSeconds(1));
        bot.Timers.Tick();
        Assert.Equal(["Enjoying the stream? Hit follow!"], await bot.ChatSentAsync(1));

        bot.Clock.Advance(TimeSpan.FromSeconds(900));
        bot.Timers.Tick();
        Assert.Equal(2, (await bot.ChatSentAsync(2)).Count);
    }

    [Fact]
    public async Task Reward_redemption_thanks_with_the_reward_name()
    {
        await using var bot = await ImportedBot.StartAsync();

        await bot.EventAsync("channel.channel_points_custom_reward_redemption.add",
            """
            {"id":"17fa2df1-ad76-4804-bfa5-a40ef63efe63","broadcaster_user_id":"1337","broadcaster_user_login":"streamer",
             "broadcaster_user_name":"Streamer","user_id":"9001","user_login":"cooler_user","user_name":"Cooler_User",
             "user_input":"","status":"unfulfilled",
             "reward":{"id":"92af127c-7326-4483-a52b-b0da0be61c01","title":"Hydrate break","cost":100,"prompt":"Drink up"},
             "redeemed_at":"2026-01-01T12:00:00Z"}
            """);

        Assert.Equal(["Thanks Cooler_User for redeeming Hydrate break"], await bot.ChatSentAsync(1));
    }

    /// <summary>The "E2E Shoutout (C#)" action's code as it was written in Streamer.bot.</summary>
    internal const string ShoutoutCode =
        """
        using System;

        public class CPHInline
        {
            public bool Execute()
            {
                if (!CPH.TryGetArg("input0", out string target) || string.IsNullOrWhiteSpace(target))
                {
                    CPH.SendMessage("Usage: !so <username>");
                    return false;
                }
                target = target.TrimStart('@');
                CPH.SendMessage($"Go check out {target} at https://twitch.tv/{target} !");
                CPH.SetGlobalVar("lastShoutout", target, true);
                CPH.SetArgument("shoutoutTarget", target);
                return true;
            }
        }
        """;

    /// <summary>The full app with the fixture imported and Twitch, OBS and Streamlabs Desktop connected to fakes.</summary>
    private sealed class ImportedBot : IAsyncDisposable
    {
        private readonly FakeEventSubServer _eventSub = new();
        private readonly FakeObsServer _obsServer = FakeObsServer.Start();
        private FakeEventSubConnection _session = null!;
        private FakeHttpHandler _twitchHttp = null!;
        private AppHost _host = null!;
        private int _notification;

        public string DataDir { get; } = Directory.CreateTempSubdirectory("cuebot-ui-sbsetup-").FullName;
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public FakeObsState Obs { get; private set; } = null!;
        public FakeStreamlabsServer Slobs { get; } = new();
        public ConcurrentQueue<(DateTime At, string Type, JsonElement Data)> ObsRequests { get; } = new();
        public ConcurrentQueue<ActionRunRecord> Runs { get; } = new();
        public ImportPlan Plan { get; private set; } = null!;

        public ProfileService Profile => _host.Get<ProfileService>();
        public GlobalVariables Globals => _host.Get<GlobalVariables>();
        public TimerService Timers => _host.Get<TimerService>();
        public IReadOnlyList<ActionDefinition> Actions => Profile.Snapshot.Actions.Where(a => a.Group == "Cuebot E2E").ToArray();
        public IReadOnlyList<CommandDefinition> Commands => _host.Get<CommandStore>().Snapshot.Where(c => c.Group == "Cuebot E2E").ToArray();
        public ActionDefinition Action(string name) => Actions.Single(a => a.Name == name);

        public IReadOnlyList<string> ChatTexts => _twitchHttp.Requests.Where(r => r.Path == "/helix/chat/messages")
            .Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("message").GetString()!).ToArray();

        public static async Task<ImportedBot> StartAsync()
        {
            var bot = new ImportedBot();
            await bot.InitializeAsync();
            return bot;
        }

        private async Task InitializeAsync()
        {
            Obs = new FakeObsState(_obsServer);
            Obs.AddScene("Alerts", "Follow Alert", "Sub Alert");
            Obs.AddScene("Starting Soon", "Countdown");
            Obs.Inputs["Follow Alert"] = new FakeObsState.Input();
            Obs.Inputs["Sub Alert"] = new FakeObsState.Input();
            Obs.Scenes["Alerts"][0].Enabled = false;
            var answer = _obsServer.Handler;
            _obsServer.Handler = (type, data) =>
            {
                ObsRequests.Enqueue((DateTime.UtcNow, type, data.ValueKind == JsonValueKind.Undefined ? data : data.Clone()));
                return answer(type, data);
            };
            Slobs.ExtraScenes.Add(("s-soon", "Starting Soon"));

            _twitchHttp = new FakeHttpHandler(Respond);
            var endpoints = new TwitchEndpoints(new Uri("https://id.test/oauth2/"), new Uri("https://api.test/helix/"), _eventSub.Url());
            _host = AppHost.Create(new AppPaths(DataDir), overrides: services =>
            {
                services.AddSingleton(sp => new TwitchService(sp.GetRequiredService<JsonFile<TwitchSettings>>(), new HttpClient(_twitchHttp),
                    sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), endpoints, TimeProvider.System,
                    EventSubTimeouts.Default with { InitialRetryDelay = TimeSpan.FromMilliseconds(50) }));
                services.AddSingleton(sp => new TimerService(sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<EventBus>(),
                    Clock, sp.GetServices<IPeriodicService>(), sp.GetRequiredService<AppLog>()));
            });

            // A first start seeds example actions ("!hello", follow thanks) that would answer too; this Cuebot has none.
            await Profile.EditAsync(p => p.Actions.Clear());

            // Import exactly as the Import page does: preview, keep every item, apply.
            var transfer = _host.Get<TransferService>();
            Plan = transfer.Preview(Fixture());
            Assert.Equal(PayloadKind.StreamerBot, Plan.Source);
            await transfer.ApplyAsync(Plan);

            _host.Get<Cuebot.Core.Chat.ChatHub>();
            var engine = _host.Get<ActionEngine>();
            engine.RunCompleted += r => Runs.Enqueue(r);
            engine.Start();

            var twitch = _host.Get<TwitchService>();
            twitch.Settings.ClientId = "client";
            twitch.Settings.AccessToken = "token";
            twitch.Settings.RefreshToken = "refresh";
            await twitch.ConnectAsync();
            _session = await _eventSub.NextConnectionAsync();
            // The longest keepalive Twitch offers, so the session stays up without the fake sending keepalives.
            await _session.SendAsync(EventSubMessages.Welcome("session-1", keepaliveSeconds: 600));
            await Until(() => twitch.State == ConnectionState.Connected, "Twitch connects");

            var obs = _host.Get<ObsService>();
            obs.Settings.Host = "localhost";
            obs.Settings.Port = _obsServer.Port;
            await obs.ConnectAsync();
            await Until(() => obs.State == ConnectionState.Connected, "OBS connects");

            var slobs = _host.Get<StreamlabsDesktopService>();
            slobs.Settings.Host = "localhost";
            slobs.Settings.Port = Slobs.Port;
            slobs.Settings.Token = FakeStreamlabsServer.Token;
            await slobs.ConnectAsync();
            await Until(() => slobs.State == ConnectionState.Connected, "Streamlabs Desktop connects");
        }

        private static HttpResponseMessage Respond(RecordedRequest r) => r.Path switch
        {
            "/oauth2/validate" => FakeHttpHandler.Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(new { client_id = "client", login = "streamer", scopes = TwitchScopes.All, user_id = "1337", expires_in = 14000 })),
            "/helix/eventsub/subscriptions" => FakeHttpHandler.Json(HttpStatusCode.Accepted, """{"data":[]}"""),
            "/helix/chat/messages" => FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"message_id":"m","is_sent":true}]}"""),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[]}"""),
        };

        /// <summary>Enables the commands an action's triggers use (Streamer.bot imports them disabled).</summary>
        public Task EnableCommandsOfAsync(string actionName)
        {
            var store = _host.Get<CommandStore>();
            var ids = Action(actionName).Triggers.SelectMany(t => t.Settings.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var used = store.Snapshot.Where(c => ids.Contains(c.Id.ToString())).ToArray();
            Assert.NotEmpty(used);
            foreach (var command in used)
            {
                var copy = store.Get(command.Id)!;
                copy.Enabled = true;
                store.Save(copy);
            }
            return Task.CompletedTask;
        }

        public void Publish(BotEvent e) => _host.Get<EventBus>().Publish(e);

        public Task EditStepAsync(string actionName, string stepType, Action<SubActionDefinition> edit) =>
            Profile.EditAsync(p => edit(p.Actions.Single(a => a.Name == actionName).SubActions.Single(s => s.Type == stepType)));

        public Task ChatAsync(string text, string user) => EventAsync("channel.chat.message", JsonSerializer.Serialize(new
        {
            broadcaster_user_id = "1337", broadcaster_user_login = "streamer", broadcaster_user_name = "Streamer",
            chatter_user_id = "4145994", chatter_user_login = user.ToLowerInvariant(), chatter_user_name = user,
            message_id = Guid.NewGuid().ToString(),
            message = new { text, fragments = new[] { new { type = "text", text } } },
            color = "#00FF7F", badges = Array.Empty<object>(), message_type = "text",
        }));

        public Task EventAsync(string type, string json) =>
            _session.SendAsync(EventSubMessages.Notification($"n{Interlocked.Increment(ref _notification)}", type, json));

        /// <summary>Waits until <paramref name="count"/> chat messages were sent, then a moment for any extra one.</summary>
        public async Task<IReadOnlyList<string>> ChatSentAsync(int count)
        {
            await Until(() => ChatTexts.Count >= count, $"{count} chat message(s) sent; got: {string.Join(" | ", ChatTexts)}");
            await Task.Delay(150);
            return ChatTexts;
        }

        public async Task<IReadOnlyList<ActionRunRecord>> RunsOfAsync(string actionName, int count)
        {
            await Until(() => Runs.Count(r => r.ActionName == actionName) >= count, $"{count} run(s) of '{actionName}'");
            return Runs.Where(r => r.ActionName == actionName).ToArray();
        }

        private static async Task Until(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out: {what}");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The app's log explains a failure (what was imported, what ran, what a sub-action reported).
            foreach (var entry in _host.Get<AppLog>().Recent()) TestContext.Current.TestOutputHelper?.WriteLine(entry.ToString());
            await _host.DisposeAsync();
            await _eventSub.DisposeAsync();
            await _obsServer.DisposeAsync();
            await Slobs.DisposeAsync();
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
        }
    }
}
