using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Integrations.CrowdControl;
using Cuebot.Integrations.Tests.Support;

namespace Cuebot.Integrations.Tests;

public sealed class CrowdControlTests : IAsyncLifetime
{
    private const string Key = "ovk_9f8e7d6c5b4a39281706";
    private readonly TestEnvironment _env = new();
    private CrowdControlService _service = null!;

    // Captured from wss://pubsub.crowdcontrol.live (2026-10-27) for a refused key.
    private const string Refused = """{"domain":"direct","type":"subscription-result","payload":{"success":[],"failure":["pub/self","prv/self","overlay/self"]},"timestamp":1793141569013}""";
    private const string Accepted = """{"domain":"direct","type":"subscription-result","payload":{"success":["pub/self","prv/self","overlay/self"],"failure":[]},"timestamp":1793141569013}""";

    // Field names as documented for the prv domain (GameSessionEffectPayload); names may be sortable strings.
    private const string Request = """
        {"domain":"prv","type":"effect-request","timestamp":1793141600000,"payload":{"requestID":"7b0c1d2e-0000-4000-8000-000000000001",
         "timestamp":1793141600000,"effect":{"effectID":"creeper","name":{"public":"Spawn Creeper","sort":"spawn creeper"},"type":"game",
         "description":"Ssss","image":"https://resources.crowdcontrol.live/images/Minecraft/icons/creeper.png","duration":15,"category":["Mobs"]},
         "game":{"gameID":"Minecraft","name":{"public":"Minecraft","sort":"minecraft"}},
         "gamePack":{"gamePackID":"MinecraftServer","name":"Minecraft (Server)","platform":"pc"},
         "requester":{"ccUID":"cc-alice","name":"Alice","profile":"twitch","originID":"1001","image":"https://img/alice.png"},
         "userDisplay":{"ccUID":"cc-alice","name":"Alice","safeName":"alice","badges":[]},
         "target":{"ccUID":"cc-streamer","name":"Streamer","profile":"twitch","originID":"2002","image":""},
         "quantity":2,"price":300,"unitPrice":150,"message":"boom",
         "parameters":{"size":{"type":"options","title":"Size","value":"big","name":"Big"}},"sourceDetails":{"type":"crowd-control"}}}
        """;

    public Task InitializeAsync()
    {
        _env.Settings.Value.CrowdControl.OverlayUrl = "https://overlay.crowdcontrol.live/events?key=" + Key;
        _service = new CrowdControlService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create,
            TestTimings.Fast with { KeepAliveInterval = TimeSpan.FromMilliseconds(200) }, new Uri("wss://pubsub.test/"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _env.Dispose();
    }

    private static string Replace(string json, string from, string to) => json.Replace(from, to, StringComparison.Ordinal);

    [Theory]
    [InlineData("https://overlay.crowdcontrol.live/events?key=" + Key, Key, false)]
    [InlineData("https://overlay.crowdcontrol.live/#/events?theme=dark&key=" + Key, Key, false)]
    [InlineData(Key, Key, false)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJjY1VJRCI6ImEifQ.c2ln", "eyJhbGciOiJIUzI1NiJ9.eyJjY1VJRCI6ImEifQ.c2ln", true)]
    [InlineData("https://overlay.crowdcontrol.live/events", null, false)]
    [InlineData("", null, false)]
    [InlineData("bad key!", null, false)]
    public void Parses_the_overlay_url(string input, string? value, bool isToken)
    {
        var (credential, error) = CrowdControlProtocol.ParseCredential(input);
        Assert.Equal(value, credential?.Value);
        Assert.Equal(value is null, error is not null);
        if (credential is not null) Assert.Equal(isToken, credential.IsToken);
    }

    [Fact]
    public async Task Subscribes_like_the_overlay_publishes_private_effects_and_pings()
    {
        await _service.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        Assert.Equal("wss://pubsub.test/", socket.Url!.ToString());
        var subscribe = await socket.NextJsonAsync();
        Assert.Equal("subscribe", subscribe.GetProperty("action").GetString());
        Assert.Equal(Key, subscribe.GetProperty("data").GetProperty("key").GetString());
        Assert.Equal(["pub/self", "prv/self", "overlay/self"], subscribe.GetProperty("data").GetProperty("topics").EnumerateArray().Select(t => t.GetString()));
        socket.Push(Accepted);
        await _env.WaitForEventAsync(CrowdControlEvents.Connected);

        socket.Push(Request);
        socket.Push(Replace(Request, "\"domain\":\"prv\"", "\"domain\":\"pub\""));   // public copy: ignored
        socket.Push(Request);                                                         // duplicate: ignored
        var request = await _env.WaitForEventAsync(CrowdControlEvents.EffectRequest);
        Assert.Equal("Spawn Creeper", request.Args["effectName"]);
        Assert.Equal("creeper", request.Args["effectId"]);
        Assert.Equal("Minecraft", request.Args["gameName"]);
        Assert.Equal("Minecraft (Server)", request.Args["gamePackName"]);
        Assert.Equal("Alice", request.Args["user"]);
        Assert.Equal("1001", request.Args["userId"]);
        Assert.Equal("twitch", request.Args["userPlatform"]);
        Assert.Equal(300L, request.Args["price"]);
        Assert.Equal(150L, request.Args["unitPrice"]);
        Assert.Equal(2, request.Args["quantity"]);
        Assert.Equal("Size: Big", request.Args["parameters"]);
        Assert.Equal("boom", request.Args["message"]);
        Assert.Equal(15.0, request.Args["effectDuration"]);
        Assert.Equal("Mobs", request.Args["effectCategory"]);
        Assert.Equal("Crowd Control", request.Args["service"]);

        socket.Push(Replace(Request, "effect-request", "effect-success"));
        socket.Push(Replace(Replace(Request, "effect-request", "timed-effect-update"), "\"quantity\":2", "\"status\":\"begin\",\"timeRemaining\":15000,\"quantity\":2"));
        await _env.WaitForEventAsync(CrowdControlEvents.EffectSuccess);
        var started = await _env.WaitForEventAsync(CrowdControlEvents.TimedEffectStarted);
        Assert.Equal(15000L, started.Args["timeRemaining"]);
        Assert.Equal(15.0, started.Args["timeRemainingSeconds"]);
        Assert.Single(_service.TimedEffects);
        socket.Push(Replace(Replace(Request, "effect-request", "timed-effect-update"), "\"quantity\":2", "\"status\":\"pause\",\"timeRemaining\":9000,\"quantity\":2"));
        var paused = await _env.WaitForEventAsync(CrowdControlEvents.TimedEffectUpdated);
        Assert.Equal("pause", paused.Args["timedStatus"]);
        Assert.Equal("pause", _service.TimedEffects.Single().Status);
        socket.Push(Replace(Replace(Request, "effect-request", "timed-effect-update"), "\"quantity\":2", "\"status\":\"end\",\"timeRemaining\":0,\"quantity\":2"));
        await _env.WaitForEventAsync(CrowdControlEvents.TimedEffectEnded);
        Assert.Empty(_service.TimedEffects);

        socket.Push(Replace(Replace(Request, "effect-request", "effect-failure"), "\"quantity\":2", "\"temporary\":true,\"quantity\":2"));
        Assert.Equal(true, (await _env.WaitForEventAsync(CrowdControlEvents.EffectFailure)).Args["temporary"]);
        socket.Push(Replace(Request, "effect-request", "effect-refund"));
        await _env.WaitForEventAsync(CrowdControlEvents.EffectRefund);

        socket.Push("""
            {"domain":"prv","type":"coin-exchange","payload":{"amount":1000,"bonus":100,"coinType":"paid","source":"twitch-bits","transactionID":"tx-1",
             "timestamp":1793141700000,"exchanger":{"ccUID":"cc-bob","name":"Bob","profile":"twitch","originID":"3003","image":""},
             "userDisplay":{"name":"Bob","safeName":"bob","badges":[]},"target":{"ccUID":"cc-streamer","name":"Streamer","profile":"twitch","originID":"2002","image":""}}}
            """);
        var coins = await _env.WaitForEventAsync(CrowdControlEvents.CoinExchange);
        Assert.Equal(1000L, coins.Args["amount"]);
        Assert.Equal(100L, coins.Args["bonus"]);
        Assert.Equal("twitch-bits", coins.Args["source"]);
        Assert.Equal("Bob", coins.Args["user"]);

        // Session events arrive on the public domain.
        socket.Push("""
            {"domain":"pub","type":"game-session-start","payload":{"gameSessionID":"gs-1","gamePackID":"MinecraftServer","ccUID":"cc-streamer",
             "game":{"gameID":"Minecraft","name":{"public":"Minecraft","sort":"minecraft"}},"gamePack":{"gamePackID":"MinecraftServer","name":"Minecraft (Server)"},
             "profileType":"twitch","createdAt":1793141500,"updatedAt":1793141500}}
            """);
        var start = await _env.WaitForEventAsync(CrowdControlEvents.GameSessionStart);
        Assert.Equal("gs-1", start.Args["gameSessionId"]);
        Assert.Equal("Minecraft", _service.Session!.GameName);
        socket.Push("""{"domain":"pub","type":"game-session-pause","payload":{"gameSessionID":"gs-1","gamePausedAt":1793141800000,"gamePausedUntil":1793141860000}}""");
        Assert.Equal(true, (await _env.WaitForEventAsync(CrowdControlEvents.GameSessionPaused)).Args["paused"]);
        Assert.True(_service.Session.Paused);
        socket.Push("""{"domain":"pub","type":"game-session-stop","payload":{"gameSessionID":"gs-1"}}""");
        await _env.WaitForEventAsync(CrowdControlEvents.GameSessionEnd);
        Assert.Null(_service.Session);

        Assert.Single(_env.Events(CrowdControlEvents.EffectRequest));

        // Keep-alive: a ping, answered with "pong".
        Assert.Equal("""{"action":"ping"}""", await socket.NextSentAsync());
        socket.Push("pong");
        Assert.DoesNotContain(Key, _env.AllLogText());
        Assert.Equal(ConnectionState.Connected, _service.State);
    }

    [Fact]
    public async Task A_refused_key_stops_without_retrying_or_leaking_it()
    {
        await _service.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        await socket.NextSentAsync();
        socket.Push(Refused);
        await Wait.UntilAsync(() => _service.State == ConnectionState.Error, "error");
        Assert.Contains("refused the overlay key", _service.StatusMessage);
        await Task.Delay(100);
        Assert.Single(_env.Server.Attempts);
        Assert.Empty(_env.Events(CrowdControlEvents.Connected));
        Assert.DoesNotContain(Key, _env.AllLogText());
    }

    [Fact]
    public async Task Unanswered_ping_reconnects()
    {
        await _service.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        await socket.NextSentAsync();
        socket.Push(Accepted);
        await _env.WaitForEventAsync(CrowdControlEvents.Connected);
        Assert.Equal("""{"action":"ping"}""", await socket.NextSentAsync());
        // No pong: the session ends after the request timeout and a new socket subscribes again.
        var again = await _env.Server.NextAsync();
        Assert.Equal("subscribe", (await again.NextJsonAsync()).GetProperty("action").GetString());
        var disconnected = await _env.WaitForEventAsync(CrowdControlEvents.Disconnected);
        Assert.Contains("ping", (string)disconnected.Args["reason"]!);
    }

    [Fact]
    public void Test_events_feed_triggers_and_sub_action_state()
    {
        foreach (var kind in _service.TestEventKinds) Assert.True(_service.SendTestEvent(kind), kind);
        Assert.False(_service.SendTestEvent("unknown"));
        var success = Assert.Single(_env.Events(CrowdControlEvents.EffectSuccess));
        Assert.Equal(true, success.Args["isTest"]);

        var feed = new CrowdControlFeedAdapter();
        var item = feed.ToFeedItem(success)!;
        Assert.Equal(FeedKind.Redemption, item.Kind);
        Assert.Equal("[Test] Cuebot Tester redeemed Spawn Creeper", item.Title);
        Assert.Equal("150 coins", item.Amount);
        Assert.Equal(FeedKind.Other, feed.ToFeedItem(Assert.Single(_env.Events(CrowdControlEvents.CoinExchange)))!.Kind);
        Assert.Null(feed.ToFeedItem(Assert.Single(_env.Events(CrowdControlEvents.EffectRequest))));

        var triggers = new CrowdControlTriggers().GetTriggers().ToArray();
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        foreach (var id in new[] { CrowdControlEvents.CoinExchange, CrowdControlEvents.EffectFailure, CrowdControlEvents.EffectRequest, CrowdControlEvents.EffectSuccess,
                     CrowdControlEvents.GameSessionEnd, CrowdControlEvents.GameSessionStart, CrowdControlEvents.TimedEffectEnded, CrowdControlEvents.TimedEffectStarted,
                     CrowdControlEvents.TimedEffectUpdated })
            Assert.Contains(triggers, t => t.TypeId == id);
        bool Matches(string id, BotEvent e, Dictionary<string, string> settings)
        {
            var t = triggers.Single(x => x.TypeId == id);
            return t.Matches(e, new TriggerDefinition(), ParameterValues.From(t.Parameters, settings), new Dictionary<string, object?>());
        }
        Assert.True(Matches(CrowdControlEvents.EffectSuccess, success, new() { ["effectName"] = "spawn creeper", ["gameName"] = "Minecraft" }));
        Assert.True(Matches(CrowdControlEvents.EffectSuccess, success, new() { ["effectName"] = "spawn_creeper" }));
        Assert.False(Matches(CrowdControlEvents.EffectSuccess, success, new() { ["minPrice"] = "151" }));
        var coins = _env.Events(CrowdControlEvents.CoinExchange).Single();
        Assert.True(Matches(CrowdControlEvents.CoinExchange, coins, new() { ["coinType"] = "paid", ["minAmount"] = "500" }));
        Assert.False(Matches(CrowdControlEvents.CoinExchange, coins, new() { ["coinType"] = "free" }));

        // Test events also drive the session state: the last sample is the session end.
        Assert.Null(_service.Session);
        _service.SendTestEvent("gameSessionStart");
        Assert.Equal("Minecraft", _service.Session!.GameName);
    }
}
