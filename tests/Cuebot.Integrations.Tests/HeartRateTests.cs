using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.Tests.Support;

namespace Cuebot.Integrations.Tests;

public sealed class PulsoidTests : IAsyncLifetime
{
    private const string Token = "PULSOID-secret-access-token-123456";
    private readonly TestEnvironment _env = new();
    private readonly GlobalVariables _globals;
    private HeartRateHub _hub = null!;
    private PulsoidService _pulsoid = null!;

    public PulsoidTests() => _globals = new GlobalVariables(null, _env.Log);

    public Task InitializeAsync()
    {
        _env.Settings.Value.Pulsoid.AccessToken = Token;
        _hub = new HeartRateHub(_globals, TimeProvider.System);
        _pulsoid = new PulsoidService(_env.Settings, _hub, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast, new Uri("wss://pulsoid.test/api/"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pulsoid.DisposeAsync();
        _globals.Dispose();
        _env.Dispose();
    }

    [Fact]
    public async Task Sends_the_token_as_a_bearer_header_and_publishes_readings()
    {
        await _pulsoid.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        await Wait.UntilAsync(() => _pulsoid.State == ConnectionState.Connected, "connected");
        Assert.Equal("wss://pulsoid.test/api/v2/data/real_time?types=heart_rate", socket.Url!.ToString());
        Assert.DoesNotContain(Token, socket.Url.ToString());
        Assert.Equal("Bearer " + Token, socket.Headers["Authorization"]);

        socket.Push("""{"type":"heart_rate","timestamp":"2026-10-17T12:00:00.123Z","heartRate":{"bpm":85}}""");
        socket.Push("""{"type":"battery_level","timestamp":"2026-10-17T12:00:00.123Z","batteryLevel":{"percent":67}}""");
        socket.Push("""{"type":"heart_rate","timestamp":"2026-10-17T12:00:01.123Z","heartRate":{"bpm":91}}""");
        await Wait.UntilAsync(() => _env.Events(HeartRateEventTypes.Pulsoid.HeartRate).Length == 2, "two readings");
        var second = _env.Events(HeartRateEventTypes.Pulsoid.HeartRate)[1];
        Assert.Equal(91, second.Args["heartRate"]);
        Assert.Equal(85, second.Args["previousHeartRate"]);
        Assert.Equal("Pulsoid", second.Args["source"]);
        Assert.Equal(91, _pulsoid.Latest!.Bpm);
        Assert.Equal(91, _hub.Get("Latest")!.Bpm);
        Assert.Equal("91", _globals.Get("heartRate"));
        Assert.Equal("91", _globals.Get("pulsoidHeartRate"));
        Assert.Contains("91 BPM", _pulsoid.StatusMessage);
    }

    [Fact]
    public async Task Rejected_token_stops_without_leaking_it()
    {
        _env.Server.Reject = _ => FakeServer.Status(401);
        await _pulsoid.ConnectAsync();
        await Wait.UntilAsync(() => _pulsoid.State == ConnectionState.Error, "error");
        Assert.Contains("rejected the access token", _pulsoid.StatusMessage);
        await Task.Delay(100);
        Assert.Single(_env.Server.Attempts);
        Assert.DoesNotContain(Token, _env.AllLogText());
        Assert.DoesNotContain(Token, _pulsoid.StatusMessage);
    }

    [Fact]
    public async Task Legacy_endpoint_and_reconnect()
    {
        _env.Settings.Value.Pulsoid.UseLegacyV1 = true;
        await _pulsoid.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        Assert.Equal("wss://pulsoid.test/api/v1/data/real_time", socket.Url!.ToString());
        socket.Push("""{"measured_at":1625310655000,"data":{"heart_rate":72}}""");
        Assert.Equal(1625310655L, (await _env.WaitForEventAsync(HeartRateEventTypes.Pulsoid.HeartRate)).Args["measuredAt"]);
        socket.Close();
        await _env.WaitForEventAsync(HeartRateEventTypes.Pulsoid.Disconnected);
        var again = await _env.Server.NextAsync();
        again.Push("80");
        await _env.WaitForEventAsync(HeartRateEventTypes.Pulsoid.HeartRate, e => e.Args["heartRate"] is 80);
        Assert.Equal(2, _env.Events(HeartRateEventTypes.Pulsoid.Connected).Length);
    }

    [Theory]
    [InlineData("""{"type":"heart_rate","timestamp":"x","heartRate":{"bpm":85}}""", 85)]
    [InlineData("""{"measured_at":1,"data":{"heart_rate":40}}""", 40)]
    [InlineData("123", 123)]
    [InlineData("""{"type":"ingestion_pause","timestamp":"x","ingestionPause":{"id":"a"}}""", null)]
    [InlineData("not json", null)]
    public void Parses_frames(string frame, int? bpm) => Assert.Equal(bpm, PulsoidProtocol.ParseBpm(frame));

    [Theory]
    [InlineData("""{"measured_at":1704063600000,"data":{"heart_rate":40}}""", 1704063600L)]
    [InlineData("""{"type":"heart_rate","timestamp":"2024-01-01T00:00:00Z","heartRate":{"bpm":85}}""", 1704067200L)]
    [InlineData("123", null)]
    public void Parses_measured_at_as_unix_seconds(string frame, long? measuredAt) =>
        Assert.Equal(measuredAt, PulsoidProtocol.ParseMeasuredAt(frame));
}

public sealed class HypeRateTests : IAsyncLifetime
{
    private const string ApiKey = "HYPERATE-secret-api-key-ABCDEFGHIJ";
    private readonly TestEnvironment _env = new();
    private HypeRateService _hypeRate = null!;

    public Task InitializeAsync()
    {
        _env.Settings.Value.HypeRate.ApiKey = ApiKey;
        _env.Settings.Value.HypeRate.SessionId = "internal-testing";
        _hypeRate = new HypeRateService(_env.Settings, new HeartRateHub(null, TimeProvider.System), _env.Bus, _env.Log, _env.Server.Create,
            TestTimings.Fast, new Uri("wss://hyperate.test/socket/websocket"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _hypeRate.DisposeAsync();
        _env.Dispose();
    }

    private static string Reply(string topic, string reference, string status = "ok") =>
        "{\"topic\":\"" + topic + "\",\"event\":\"phx_reply\",\"payload\":{\"status\":\"" + status + "\",\"response\":{}},\"ref\":" + reference + "}";

    private async Task<(FakeSocket Socket, JsonElement Join)> JoinAsync()
    {
        var socket = await _env.Server.NextAsync();
        var join = await socket.NextJsonAsync();
        socket.Push(Reply("hr:internal-testing", join.GetProperty("ref").GetRawText()));
        await Wait.UntilAsync(() => _hypeRate.State == ConnectionState.Connected, "joined");
        return (socket, join);
    }

    [Fact]
    public async Task Joins_the_channel_publishes_heart_rate_and_clips_and_sends_heartbeats()
    {
        // Heartbeats go unanswered until the pulse and clip checks are done; at 250 ms a loaded runner can take
        // longer than the 3 missed heartbeats that make the service reconnect.
        await _hypeRate.DisposeAsync();
        _hypeRate = new HypeRateService(_env.Settings, new HeartRateHub(null, TimeProvider.System), _env.Bus, _env.Log, _env.Server.Create,
            TestTimings.Fast with { HeartbeatInterval = TimeSpan.FromSeconds(1) }, new Uri("wss://hyperate.test/socket/websocket"));
        await _hypeRate.ConnectAsync();
        var (socket, join) = await JoinAsync();
        Assert.Equal($"wss://hyperate.test/socket/websocket?token={ApiKey}", socket.Url!.ToString());
        Assert.Equal("hr:internal-testing", join.GetProperty("topic").GetString());
        Assert.Equal("phx_join", join.GetProperty("event").GetString());
        var clips = await socket.NextJsonAsync();
        Assert.Equal("clips:internal-testing", clips.GetProperty("topic").GetString());

        socket.Push("""{"event":"hr_update","payload":{"hr":79},"ref":null,"topic":"hr:internal-testing"}""");
        var pulse = await _env.WaitForEventAsync(HeartRateEventTypes.HypeRate.HeartRate);
        Assert.Equal(79, pulse.Args["heartRate"]);
        Assert.Equal("HypeRate", pulse.Args["source"]);

        socket.Push("""{"ref":null,"payload":{"twitch_slug":"FuriousTruffle-AmSu"},"topic":"clips:internal-testing","event":"clip:created"}""");
        var clip = await _env.WaitForEventAsync(HeartRateEventTypes.HypeRate.ClipCreated);
        Assert.Equal("https://clips.twitch.tv/FuriousTruffle-AmSu", clip.Args["clipUrl"]);

        // Heartbeats keep coming while answered.
        for (var i = 0; i < 3; i++)
        {
            var beat = await socket.NextJsonAsync(m => m.GetProperty("event").GetString() == "heartbeat");
            Assert.Equal("phoenix", beat.GetProperty("topic").GetString());
            socket.Push(Reply("phoenix", beat.GetProperty("ref").GetRawText()));
        }
        Assert.Equal(ConnectionState.Connected, _hypeRate.State);
        Assert.Single(_env.Events(HeartRateEventTypes.HypeRate.Connected));

        var logs = _env.AllLogText() + _hypeRate.StatusMessage;
        Assert.DoesNotContain(ApiKey, logs);
    }

    [Fact]
    public async Task Unanswered_heartbeats_reconnect_and_rejoin()
    {
        await _hypeRate.ConnectAsync();
        await JoinAsync();
        await _env.WaitForEventAsync(HeartRateEventTypes.HypeRate.Disconnected, e => ((string)e.Args["reason"]!).Contains("heartbeat"));
        var (_, join) = await JoinAsync();
        Assert.Equal("phx_join", join.GetProperty("event").GetString());
        Assert.Equal(2, _env.Events(HeartRateEventTypes.HypeRate.Connected).Length);
    }

    [Fact]
    public async Task Channel_error_reconnects_and_join_refusal_stops()
    {
        await _hypeRate.ConnectAsync();
        var (socket, _) = await JoinAsync();
        socket.Push("""{"event":"phx_error","payload":{},"ref":null,"topic":"hr:internal-testing"}""");
        await _env.WaitForEventAsync(HeartRateEventTypes.HypeRate.Disconnected);

        var next = await _env.Server.NextAsync();
        var join = await next.NextJsonAsync();
        next.Push(Reply("hr:internal-testing", join.GetProperty("ref").GetRawText(), "error"));
        await Wait.UntilAsync(() => _hypeRate.State == ConnectionState.Error, "error");
        Assert.Contains("refused to join", _hypeRate.StatusMessage);
        Assert.False(_hypeRate.IsRunning);
    }

    [Fact]
    public async Task Missing_settings_and_rejected_key()
    {
        _env.Settings.Value.HypeRate.SessionId = "";
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => _hypeRate.ConnectAsync());
        Assert.Contains("HypeRate ID", missing.Message);

        _env.Settings.Value.HypeRate.SessionId = "ABCD";
        _env.Server.Reject = _ => FakeServer.Status(403);
        await _hypeRate.ConnectAsync();
        await Wait.UntilAsync(() => _hypeRate.State == ConnectionState.Error, "error");
        Assert.Contains("rejected the API key", _hypeRate.StatusMessage);
        Assert.DoesNotContain(ApiKey, _env.AllLogText());
    }

    [Fact]
    public void Phoenix_framing()
    {
        using var join = JsonDocument.Parse(PhoenixProtocol.Join("hr:ABC", 3));
        Assert.Equal("hr:ABC", join.RootElement.GetProperty("topic").GetString());
        Assert.Equal("phx_join", join.RootElement.GetProperty("event").GetString());
        Assert.Equal(3, join.RootElement.GetProperty("ref").GetInt32());
        Assert.Equal(JsonValueKind.Object, join.RootElement.GetProperty("payload").ValueKind);
        using var beat = JsonDocument.Parse(PhoenixProtocol.Heartbeat(4));
        Assert.Equal("phoenix", beat.RootElement.GetProperty("topic").GetString());
        Assert.Equal("heartbeat", beat.RootElement.GetProperty("event").GetString());
        Assert.Equal("wss://app.hyperate.io/socket/websocket?token=a%2Bb", PhoenixProtocol.Endpoint(PhoenixProtocol.DefaultBase, "a+b").ToString());
    }
}

public sealed class HeartRateTriggerTests
{
    private static BotEvent Pulse(int bpm) => new(HeartRateEventTypes.Pulsoid.HeartRate, new Dictionary<string, object?> { ["heartRate"] = bpm });

    private static bool Fire(ITriggerType trigger, TriggerDefinition definition, int bpm, Dictionary<string, string>? settings = null) =>
        trigger.Matches(Pulse(bpm), definition, ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    private static ITriggerType Get(string id, TimeProvider? clock = null) =>
        new HeartRateTriggers("Pulsoid", HeartRateEventTypes.Pulsoid, clock ?? TimeProvider.System, clips: false).GetTriggers().Single(t => t.TypeId == id);

    [Fact]
    public void Threshold_logic_has_hysteresis()
    {
        Assert.Equal((true, true), HeartRateThreshold.Evaluate(false, 120, 120, 5, above: true));
        Assert.Equal((false, true), HeartRateThreshold.Evaluate(true, 130, 120, 5, above: true));
        Assert.Equal((false, true), HeartRateThreshold.Evaluate(true, 116, 120, 5, above: true));
        Assert.Equal((false, false), HeartRateThreshold.Evaluate(true, 114, 120, 5, above: true));
        Assert.Equal((true, true), HeartRateThreshold.Evaluate(false, 60, 60, 5, above: false));
        Assert.Equal((false, true), HeartRateThreshold.Evaluate(true, 64, 60, 5, above: false));
        Assert.Equal((false, false), HeartRateThreshold.Evaluate(true, 66, 60, 5, above: false));
        Assert.Equal((false, false), HeartRateThreshold.Evaluate(false, 61, 60, 5, above: false));
    }

    [Fact]
    public void Above_trigger_fires_once_per_crossing_per_trigger_instance()
    {
        var above = Get("pulsoid.heartRateAbove");
        Assert.Equal([HeartRateEventTypes.Pulsoid.HeartRate], above.EventTypes);
        var a = new TriggerDefinition();
        var b = new TriggerDefinition();
        var s = new Dictionary<string, string> { ["threshold"] = "100", ["hysteresis"] = "10" };
        Assert.False(Fire(above, a, 95, s));
        Assert.True(Fire(above, a, 101, s));
        Assert.False(Fire(above, a, 120, s));
        Assert.False(Fire(above, a, 95, s));   // inside the hysteresis band: still "above"
        Assert.False(Fire(above, a, 100, s));
        Assert.False(Fire(above, a, 89, s));   // re-armed
        Assert.True(Fire(above, a, 100, s));
        Assert.True(Fire(above, b, 130, s));   // separate state

        var below = Get("pulsoid.heartRateBelow");
        var c = new TriggerDefinition();
        Assert.True(Fire(below, c, 55));       // default 60
        Assert.False(Fire(below, c, 62));
        Assert.False(Fire(below, c, 58));
        Assert.False(Fire(below, c, 66));
        Assert.True(Fire(below, c, 59));
    }

    [Fact]
    public void Changed_trigger_is_throttled_by_time_and_change()
    {
        var clock = new ManualClock();
        var changed = Get("pulsoid.heartRateChanged", clock);
        var t = new TriggerDefinition();
        var s = new Dictionary<string, string> { ["minInterval"] = "5", ["minChange"] = "3" };
        Assert.True(Fire(changed, t, 80, s));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(Fire(changed, t, 90, s));  // too soon
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(Fire(changed, t, 82, s));  // too small
        var args = new Dictionary<string, object?>();
        Assert.True(changed.Matches(Pulse(84), t, ParameterValues.From(changed.Parameters, s), args));
        Assert.Equal(80.0, args["lastTriggeredHeartRate"]);
        Assert.Equal(4.0, args["heartRateChange"]);
    }

    [Fact]
    public void Each_source_has_its_own_category_and_triggers()
    {
        var pulsoid = new HeartRateTriggers("Pulsoid", HeartRateEventTypes.Pulsoid, TimeProvider.System, clips: false).GetTriggers().ToArray();
        var hypeRate = new HeartRateTriggers("HypeRate", HeartRateEventTypes.HypeRate, TimeProvider.System, clips: true).GetTriggers().ToArray();
        Assert.All(pulsoid, t => Assert.Equal("Pulsoid", t.Category));
        Assert.All(hypeRate, t => Assert.Equal("HypeRate", t.Category));
        Assert.Equal(6, pulsoid.Length);
        Assert.Equal(7, hypeRate.Length);
        Assert.Contains(hypeRate, t => t.TypeId == "hyperate.clipCreated");
        Assert.All(pulsoid, t => Assert.StartsWith("pulsoid.", t.TypeId));
    }
}
