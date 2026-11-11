using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Transfer;
using Cuebot.StreamApps.Rahituber;
using Cuebot.StreamApps.Tests.Support;

namespace Cuebot.StreamApps.Tests;

public sealed class RahituberFeatureTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cuebot-rahituber-tests-" + Guid.NewGuid().ToString("N"));

    public RahituberFeatureTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _env.Dispose();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>A fake RahiTuber: answers like its HTTP handler (application/json, the state printed with %d).</summary>
    private sealed class FakeRahituber(Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        private int _inFlight;

        public List<(Uri Url, long At)> Requests { get; } = [];
        public int MaxInFlight { get; private set; }
        public TimeSpan Delay { get; init; }
        public Action<string>? OnQuery { get; init; }

        public static HttpResponseMessage Reply(string query)
        {
            var active = JsonDocument.Parse(query).RootElement[1].GetInt32();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"state\":1923874, \"active\":{active}}}\n", Encoding.UTF8, "application/json"),
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Requests)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
                Requests.Add((request.RequestUri!, Stopwatch.GetTimestamp()));
            }
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                OnQuery?.Invoke(Query(request.RequestUri!));
                return respond is null ? Reply(Query(request.RequestUri!)) : await respond(request);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public string[] Queries() { lock (Requests) return Requests.Select(r => Query(r.Url)).ToArray(); }
    }

    /// <summary>The query as RahiTuber reads it (it URL-decodes, then parses the JSON array).</summary>
    private static string Query(Uri url) => Uri.UnescapeDataString(url.Query.TrimStart('?'));

    private RahituberService Create(HttpMessageHandler handler, bool folder = false)
    {
        var rahi = new RahituberService(_env.Settings, _env.Log, new HttpClient(handler)) { RequestGap = TimeSpan.Zero };
        if (folder) rahi.Settings.Folder = _folder;
        return rahi;
    }

    private const string Avatar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <layers>
            <hotkeys>
                <hotkey enabled="true" name="2" key="1" timeout="5" useTimeout="true" activeType="0" canTrigger="0"/>
                <hotkey enabled="true" name="Hat" timeout="5" useTimeout="false" activeType="1" canTrigger="0"/>
                <hotkey enabled="true" name="Hat" activeType="0"/>
                <hotkey enabled="false" name="Mouth open " activeType="0" canTrigger="1"/>
                <hotkey enabled="true" name="Forever" activeType="2" canTrigger="2"/>
                <hotkey key="77" ctrl="true" timeout="2.5" useTimeout="true" toggle="false"/>
            </hotkeys>
        </layers>
        """;

    /// <summary>RahiTuber's folder: config.xml pointing at an avatar (saved without ".xml", like RahiTuber does).</summary>
    private string WriteFolder(string avatar = Avatar)
    {
        var avatarPath = Path.Combine(_folder, "avatars", "Demo");
        Directory.CreateDirectory(Path.GetDirectoryName(avatarPath)!);
        File.WriteAllText(avatarPath + ".xml", avatar);
        File.WriteAllText(Path.Combine(_folder, "config.xml"),
            $"""<Config><Common listenHTTP="true" httpPort="8000" lastLayerSet="{avatarPath}"/></Config>""");
        File.WriteAllText(Path.Combine(_folder, "buildnumber.txt"), "14.51\n");
        return avatarPath + ".xml";
    }

    [Fact]
    public async Task States_go_by_name_or_position_with_press_and_release()
    {
        var fake = new FakeRahituber();
        var rahi = Create(fake);
        await rahi.SetStateAsync("Happy face", true);
        await rahi.SetStateAsync(" 3 ", false);
        await rahi.HoldStateAsync("Wave \"hi\"", TimeSpan.FromMilliseconds(10));

        Assert.All(fake.Requests, r => Assert.Equal("http://127.0.0.1:8000/state", r.Url.GetLeftPart(UriPartial.Path)));
        Assert.Equal(["[\"Happy face\",1]", "[3,0]", "[\"Wave \\\"hi\\\"\",1]", "[\"Wave \\\"hi\\\"\",0]"], fake.Queries());
        // Valid JSON arrays, as RahiTuber's parser needs.
        Assert.Equal("Wave \"hi\"", JsonDocument.Parse(fake.Queries()[2]).RootElement[0].GetString());
        Assert.Equal(ConnectionState.Connected, rahi.State);
        await Assert.ThrowsAsync<ArgumentException>(() => rahi.SetStateAsync(" ", true));
    }

    [Fact]
    public void The_query_stays_escaped_on_the_wire()
    {
        // RahiTuber decodes into a buffer as long as the escaped query; unescaped brackets would lose the final "]".
        var rahi = Create(new FakeRahituber());
        var raw = rahi.StateUrl("Hat", true).AbsoluteUri;
        Assert.Contains("?%5B%22Hat%22%2C1%5D", raw);
        Assert.Contains("%5B3%2C0%5D", rahi.StateUrl("3", false).AbsoluteUri);
    }

    [Fact]
    public async Task Stopping_during_a_hold_still_lets_go()
    {
        var fake = new FakeRahituber();
        var rahi = Create(fake);
        using var stop = new CancellationTokenSource();
        var hold = rahi.HoldStateAsync("Hat", TimeSpan.FromSeconds(30), stop.Token);
        await Wait.UntilAsync(() => fake.Queries().Length == 1, "press");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hold);
        Assert.Equal(["[\"Hat\",1]", "[\"Hat\",0]"], fake.Queries());
    }

    [Fact]
    public async Task Parallel_requests_are_sent_one_at_a_time()
    {
        var fake = new FakeRahituber { Delay = TimeSpan.FromMilliseconds(5) };
        var rahi = Create(fake);
        rahi.RequestGap = TimeSpan.FromMilliseconds(20);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => rahi.SetStateAsync($"State {i}", true))));

        Assert.Equal(10, fake.Requests.Count);
        Assert.Equal(1, fake.MaxInFlight);
        // A gap after each request, as RahiTuber reads its queue once per frame.
        var times = fake.Requests.Select(r => r.At).Order().ToArray();
        for (var i = 1; i < times.Length; i++)
            Assert.True(Stopwatch.GetElapsedTime(times[i - 1], times[i]) >= TimeSpan.FromMilliseconds(20), $"gap {i}");
    }

    [Fact]
    public async Task Quick_presses_of_one_state_are_spaced_past_the_repeat_guard()
    {
        var fake = new FakeRahituber();
        var rahi = Create(fake);
        await rahi.SetStateAsync("Hat", true);
        await rahi.SetStateAsync("Hat", true);
        await rahi.SetStateAsync("Wave", true);
        var t = fake.Requests.Select(r => r.At).ToArray();
        Assert.True(Stopwatch.GetElapsedTime(t[0], t[1]) >= TimeSpan.FromMilliseconds(240), "same state spaced");
        Assert.True(Stopwatch.GetElapsedTime(t[1], t[2]) < TimeSpan.FromMilliseconds(200), "other states are not");

        rahi.Settings.SameStateGapMs = 0;
        var before = Stopwatch.GetTimestamp();
        await rahi.SetStateAsync("Wave", true);
        Assert.True(Stopwatch.GetElapsedTime(before) < TimeSpan.FromMilliseconds(200), "spacing off");
    }

    [Fact]
    public async Task Only_rahitubers_answer_counts()
    {
        var fake = new FakeRahituber();
        var rahi = Create(fake);
        Assert.Equal("RahiTuber answers at 127.0.0.1:8000", await rahi.TestAsync());
        // The check uses a name no avatar has, with -1, so nothing changes.
        Assert.Equal($"[\"{RahituberService.ProbeState}\",-1]", fake.Queries().Single());

        // Another program on the port (RahiTuber does not say when it could not get the port).
        var other = Create(new FakeRahituber(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Directory listing</html>", Encoding.UTF8, "text/html"),
        })));
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => other.SetStateAsync("Hat", true));
        Assert.Contains("Something else answers at 127.0.0.1:8000", ex.Message);
        Assert.Equal(ConnectionState.Error, other.State);
        var json = Create(new FakeRahituber(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        })));
        await Assert.ThrowsAsync<StreamAppRequestException>(() => json.TestAsync());

        Assert.True(RahituberService.IsRahituberReply("application/json", "{\"state\":-12, \"active\":-1}\n"));
        Assert.False(RahituberService.IsRahituberReply("text/plain", "{\"state\":0, \"active\":1}"));
    }

    [Fact]
    public async Task Closed_ports_and_refusals_are_explained()
    {
        var closed = Create(new FakeRahituber(_ => throw new HttpRequestException("Connection refused")));
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => closed.TestAsync());
        Assert.Contains("Control States via HTTP", ex.Message);
        Assert.Contains("another program may have port 8000", ex.Message);
        Assert.Contains("RahiTuber_Log.txt", ex.Message);
        Assert.Equal(ConnectionState.Error, closed.State);

        var wrong = Create(new FakeRahituber(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        Assert.Contains("answered 400", (await Assert.ThrowsAsync<StreamAppRequestException>(() => wrong.SetStateAsync("Happy", true))).Message);
    }

    [Fact]
    public void Avatar_files_list_the_states_in_order_with_their_types()
    {
        var path = WriteFolder();
        Assert.Equal(path, RahituberFiles.AvatarPath(_folder));
        var states = RahituberFiles.ReadStates(path);
        Assert.Equal(["2", "Hat", "Hat", "Mouth open ", "Forever", "5"], states.Select(s => s.Target));
        Assert.Equal([RahituberStateType.Toggle, RahituberStateType.Held, RahituberStateType.Toggle, RahituberStateType.Toggle,
            RahituberStateType.Permanent, RahituberStateType.Held], states.Select(s => s.Type));
        Assert.Equal([5, null, 5, 5, 5, 2.5], states.Select(s => s.Timeout));
        Assert.False(states[3].Enabled);
        Assert.Equal(RahituberStateCondition.WhileTalking, states[3].Condition);
        Assert.Equal(RahituberStateCondition.WhileIdle, states[4].Condition);
        Assert.Equal("Toggle, off after 5 s", states[0].Describe());
        Assert.Equal("Toggle, off after 5 s, only while talking, switched off in RahiTuber", states[3].Describe());
        Assert.StartsWith("Permanent (can't be undone)", states[4].Describe());

        // Like RahiTuber: place or name, the first one in the list wins.
        var avatar = new RahituberAvatar(path, states);
        Assert.Equal(0, avatar.Find("2")!.Index);
        Assert.Equal(1, avatar.Find("Hat")!.Index);
        Assert.Equal(5, avatar.Find("5")!.Index);
        Assert.Null(avatar.Find("hat"));

        // No config.xml: RahiTuber falls back to lastLayers.xml.
        File.Delete(Path.Combine(_folder, "config.xml"));
        Assert.Null(RahituberFiles.AvatarPath(_folder));
        File.WriteAllText(Path.Combine(_folder, "lastLayers.xml"), "<layers><hotkeys/></layers>");
        Assert.Equal(Path.Combine(_folder, "lastLayers.xml"), RahituberFiles.AvatarPath(_folder));
        Assert.Empty(RahituberFiles.ReadStates(Path.Combine(_folder, "lastLayers.xml")));
    }

    [Fact]
    public async Task With_the_folder_set_names_are_checked_suggested_and_reloaded()
    {
        var path = WriteFolder();
        var fake = new FakeRahituber();
        var rahi = Create(fake, folder: true);
        Assert.Equal(["2", "Hat", "Hat", "Mouth open ", "Forever", "5"], rahi.StateTargets());
        var step = RahituberSubActions.Create(rahi).Single();
        Assert.Equal(rahi.StateTargets(), step.Parameters.Single(p => p.Key == "state").Suggestions!());

        Assert.Equal("RahiTuber 14.51 answers at 127.0.0.1:8000; avatar Demo: 6 states", await rahi.TestAsync());

        // A name with a space at its end is sent as RahiTuber has it (it compares exact text).
        Assert.Equal(3, (await rahi.SetStateAsync("Mouth open", true))!.Index);
        Assert.Equal("[\"Mouth open \",1]", fake.Queries()[^1]);
        Assert.Contains("only switches it while you talk", rahi.StatusMessage);
        Assert.Contains("switched off in RahiTuber", rahi.StatusMessage);

        await rahi.SetStateAsync("Forever", true);
        Assert.Contains("Permanent", rahi.StatusMessage);
        await rahi.SetStateAsync("Hat", false);
        Assert.DoesNotContain("Note", rahi.StatusMessage);
        await rahi.SetStateAsync("Nope", true);
        Assert.Contains("'Nope' is not a state in Demo.xml", rahi.StatusMessage);
        Assert.Equal("[\"Nope\",1]", fake.Queries()[^1]);

        // Saving the avatar in RahiTuber is picked up.
        File.WriteAllText(path, """<layers><hotkeys><hotkey name="Glasses" activeType="1"/></hotkeys></layers>""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(["Glasses"], rahi.StateTargets());

        rahi.Settings.Folder = Path.Combine(_folder, "missing");
        Assert.Null(rahi.Avatar);
        Assert.Contains("No RahiTuber files", rahi.AvatarProblem);
    }

    [Fact]
    public async Task Held_states_are_not_spaced_and_hold_on_a_toggle_state_is_explained()
    {
        WriteFolder();
        var fake = new FakeRahituber();
        var rahi = Create(fake, folder: true);
        var before = Stopwatch.GetTimestamp();
        await rahi.HoldStateAsync("Hat", TimeSpan.Zero);
        Assert.True(Stopwatch.GetElapsedTime(before) < TimeSpan.FromMilliseconds(200), "held press and let go are not spaced");

        await rahi.HoldStateAsync("2", TimeSpan.Zero);
        Assert.Contains(_env.Log.Recent(), e => e.Message.Contains("works like a press") && e.Message.Contains("after 5 s"));
    }

    [Fact]
    public async Task Rahitubers_log_confirms_each_request()
    {
        WriteFolder();
        var log = Path.Combine(_folder, RahituberFiles.LogFile);
        File.WriteAllText(log, "Setup Complete!\nState change added to queue: Hat = 1\n");
        var fake = new FakeRahituber
        {
            // RahiTuber logs the decoded id and the active value.
            OnQuery = q =>
            {
                var root = JsonDocument.Parse(q).RootElement;
                var id = root[0].ValueKind == JsonValueKind.String ? root[0].GetString() : root[0].GetRawText();
                File.AppendAllText(log, $"HTTP Message received: GET /state?... HTTP/1.1\nState change added to queue: {id} = {root[1].GetInt32()}\n");
            },
        };
        var rahi = Create(fake, folder: true);
        rahi.LogWait = TimeSpan.FromMilliseconds(300);

        await rahi.SetStateAsync("Hat", true);
        await rahi.LastLogCheck;
        Assert.EndsWith("confirmed by RahiTuber_Log.txt", rahi.StatusMessage);

        // Not in the log (an older line does not count).
        var silent = Create(new FakeRahituber(), folder: true);
        silent.LogWait = TimeSpan.FromMilliseconds(200);
        await silent.SetStateAsync("Hat", true);
        await silent.LastLogCheck;
        Assert.DoesNotContain("confirmed", silent.StatusMessage);
    }

    [Fact]
    public async Task Script_functions_drive_rahituber()
    {
        WriteFolder();
        var fake = new FakeRahituber();
        var rahi = Create(fake, folder: true);
        var functions = new RahituberScriptFunctions(rahi).GetFunctions().ToDictionary(f => f.Name);
        Assert.Equal(["rahituber.check", "rahituber.getStates", "rahituber.hold", "rahituber.press", "rahituber.release"],
            functions.Keys.Order().ToArray());
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

        await functions["rahituber.press"].Invoke(null!, Args(new { state = "Hat" }), default);
        await functions["rahituber.release"].Invoke(null!, Args(new { state = "Hat" }), default);
        await functions["rahituber.hold"].Invoke(null!, Args(new { state = 5, seconds = 0 }), default);
        Assert.Equal(["[\"Hat\",1]", "[\"Hat\",0]", "[5,1]", "[5,0]"], fake.Queries());

        var states = JsonSerializer.SerializeToElement(await functions["rahituber.getStates"].Invoke(null!, Args(new { }), default));
        Assert.Equal(6, states.GetArrayLength());
        Assert.Equal("Held", states[1].GetProperty("type").GetString());
        Assert.Equal("WhileIdle", states[4].GetProperty("condition").GetString());
        Assert.StartsWith("RahiTuber 14.51", (string?)await functions["rahituber.check"].Invoke(null!, Args(new { }), default));
    }

    [Fact]
    public void The_guides_example_bundle_uses_the_step_as_it_is()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuebot.slnx"))) dir = dir.Parent;
        var bundle = TransferCodec.ParseBundle(File.ReadAllText(Path.Combine(dir!.FullName,
            "website", "docs", "guides", "apps", "vtuber", "rahituber-examples.cuebot")));
        var step = RahituberSubActions.Create(Create(new FakeRahituber())).Single();
        var steps = bundle.Actions.SelectMany(a => a.SubActions).Where(s => s.Type == step.TypeId).ToArray();
        Assert.Equal(4, steps.Length);
        foreach (var s in steps)
        {
            Assert.All(s.Parameters.Keys, k => Assert.Contains(step.Parameters, p => p.Key == k));
            Assert.Contains(s.Parameters["how"], step.Parameters.Single(p => p.Key == "how").Choices);
        }
    }
}
