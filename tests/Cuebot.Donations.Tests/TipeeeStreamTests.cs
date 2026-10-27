using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Tests.Support;
using Cuebot.Donations.TipeeeStream;

namespace Cuebot.Donations.Tests;

public sealed class TipeeeStreamMappingTests
{
    /// <summary>The donation sample of TipeeeStream's socket.io documentation (api.tipeeestream.com/api-doc/socketio).</summary>
    internal const string Donation = """
        {"appKey":"5f2a7c1e9b","event":{"id":359821,"type":"donation",
         "user":{"avatar":"5236","country":"FR","username":"lilelulo",
           "providers":[{"connectedAt":"2015-10-05T15:28:12+0200","code":"twitch","id":"36820986","username":"lilelulo"}],
           "created_at":"2015-09-23T13:54:47+0200","session_at":"2015-10-05T15:28:12+0200"},
         "ref":"561290126af3f","inserted_at":"2015-10-05T16:58:26+0200","created_at":"2015-10-05T16:58:26+0200",
         "parameters":{"formattedMessage":"take my moneeeeey!!","message":"take my moneeeeey!!","username":"anonym1","currency":"EUR","amount":1,"resub":0,"viewers":4},
         "formattedAmount":"€1.00","parameters.amount":1}}
        """;

    /// <summary>A newer-style donation (amount as a string, avatar and currency objects, fees) as seen in open-source clients.</summary>
    internal const string NewerDonation = """
        [{"appKey":"k","event":{"id":"77","type":"donation","origin":"tipeee","ref":77001,
          "user":{"username":"streamer","avatar":{"id":1,"path":"https://cdn.tipeee.test/a.png"},"currency":{"code":"EUR","symbol":"€"}},
          "created_at":"2026-03-01T20:00:00+0100",
          "parameters":{"username":"Bob","amount":"12.50","currency":"usd","fees":"0.62","message":"gg","campaignId":"c1","recurring":true}}}]
        """;

    private static JsonElement Parse(string json) => StreamlabsMappingTests.Parse(json);

    [Fact]
    public void Maps_the_documented_donation()
    {
        var e = TipeeeStreamEventMapper.Map(Parse(Donation))!;
        Assert.Equal(TipeeeStreamEventTypes.Donation, e.Type);
        Assert.False(e.Relayed);
        var a = e.Args;
        Assert.Equal(("anonym1", "anonym1", 1d, "EUR", "€1.00", "take my moneeeeey!!"),
            (a["user"], a["username"], a["amount"], a["currency"], a["formattedAmount"], a["message"]));
        Assert.Equal("359821", a["tipeeStreamId"]);
        Assert.Equal("5236", a["avatar"]);
        Assert.Equal("2015-10-05T16:58:26.0000000+02:00", a["timestamp"]);
        Assert.Equal("TipeeeStream", a["platform"]);
        Assert.Equal(false, a["isSimulation"]);
        Assert.Contains("tipeee:id:359821", e.Keys);
        Assert.Contains("tipeee:ref:561290126af3f", e.Keys);
    }

    [Fact]
    public void Reads_string_amounts_object_avatars_fees_and_array_wrapped_arguments()
    {
        var a = TipeeeStreamEventMapper.Map(Parse(NewerDonation))!.Args;
        Assert.Equal((12.5, "USD", "$12.50", 0.62), (a["amount"], a["currency"], a["formattedAmount"], a["fees"]));
        Assert.Equal("https://cdn.tipeee.test/a.png", a["avatar"]);
        Assert.Equal(true, a["recurring"]);
        Assert.Equal("c1", a["campaignId"]);
    }

    [Fact]
    public void Platform_events_are_relayed_and_unknown_types_ignored()
    {
        var sub = TipeeeStreamEventMapper.Map(Parse(
            """{"event":{"id":357246,"type":"subscription","ref":"TWITCH_subscription_55f436032cd02","created_at":"2015-09-12T16:26:11+0200","parameters":{"resub":"3","twitch_channel_id":"#lilelulo","username":"greg"},"parameters.amount":0}}"""))!;
        Assert.Equal((TipeeeStreamEventTypes.Subscription, true, "greg", 3, "Twitch"), (sub.Type, sub.Relayed, sub.Args["user"], sub.Args["months"], sub.Args["platform"]));
        var follow = TipeeeStreamEventMapper.Map(Parse("""{"event":{"id":1,"type":"follow","origin":"twitch","parameters":{"username":"amy"}}}"""))!;
        Assert.Equal((TipeeeStreamEventTypes.Follow, true), (follow.Type, follow.Relayed));
        var host = TipeeeStreamEventMapper.Map(Parse("""{"event":{"id":2,"type":"hosting","parameters":{"username":"zed","viewers":12}}}"""))!;
        Assert.Equal((TipeeeStreamEventTypes.Host, 12), (host.Type, host.Args["viewers"]));
        Assert.Null(TipeeeStreamEventMapper.Map(Parse("""{"event":{"id":3,"type":"merch"}}""")));
        Assert.Null(TipeeeStreamEventMapper.Map(Parse("\"text\"")));
    }

    [Fact]
    public void Simulated_alerts_are_flagged()
    {
        var e = TipeeeStreamEventMapper.Map(Parse("""{"event":{"id":4,"type":"donation","ref":"simulation_abc","parameters":{"username":"Sim","amount":2,"currency":"EUR"}}}"""))!;
        Assert.Equal(true, e.Args["isSimulation"]);
        Assert.Equal(false, e.Args["isTest"]);
    }
}

public sealed class TipeeeStreamServiceTests : IAsyncLifetime
{
    private const string Key = "TIPEEE-SECRET-api-key-0123456789";
    private const string Open = """0{"sid":"s1","upgrades":[],"pingInterval":25000,"pingTimeout":60000}""";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-tipeee-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private HttpStatusCode _keyStatus = HttpStatusCode.OK;
    private bool _lookupFails;
    private JsonFile<DonationsSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private TipeeeStreamService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.TipeeeStream.ApiKey = Key;
        _settings.Value.TipeeeStream.Username = "lilelulo";
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _http = new FakeHttpHandler(Respond);
        _service = new TipeeeStreamService(_settings, bus, _log, _server.Create, TestTimings.Fast,
            new TipeeeStreamApi(new HttpClient(_http), new Uri("https://api.tipeee.test/")));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r) => r.Uri.AbsolutePath switch
    {
        "/v2.0/site/socket" => _lookupFails
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : FakeHttpHandler.Json("""{"code":200,"message":"success","datas":{"port":"443","host":"https:\/\/sso.tipeee.test"}}"""),
        "/v1.0/events.json" => _keyStatus == HttpStatusCode.OK
            ? FakeHttpHandler.Json("""{"message":"success","datas":{"items":[],"total_count":0}}""")
            : FakeHttpHandler.Json("""{"message":"authentification is required"}""", _keyStatus),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    [Fact]
    public async Task Looks_up_the_server_joins_the_room_and_publishes_donations_once()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal("sso.tipeee.test", socket.Url!.Host);
        Assert.Contains($"access_token={Key}", socket.Url.Query);
        Assert.Contains("EIO=3", socket.Url.Query);
        Assert.Contains(_http.Requests, r => r.Uri.Query.Contains("apiKey=" + Key, StringComparison.Ordinal));

        socket.Push(Open);
        Assert.Equal($$"""42["join-room",{"room":"{{Key}}","username":"lilelulo"}]""", await socket.NextSentAsync());
        socket.Push("40");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.Single(Events(TipeeeStreamEventTypes.Connected));

        var frame = "42[\"new-event\"," + TipeeeStreamMappingTests.Donation.ReplaceLineEndings("") + "]";
        socket.Push(frame);
        socket.Push(frame);
        await Wait.UntilAsync(() => Events(TipeeeStreamEventTypes.Donation).Length == 1, "donation");
        await Task.Delay(100);
        Assert.Single(Events(TipeeeStreamEventTypes.Donation));
        Assert.Equal("anonym1 donated €1.00", Assert.Single(_service.RecentEvents()).Summary);

        // Relayed platform events are off by default.
        socket.Push("""42["new-event",{"event":{"id":9,"type":"follow","ref":"TWITCH_follow_1","parameters":{"username":"amy"}}}]""");
        await Task.Delay(100);
        Assert.Empty(Events(TipeeeStreamEventTypes.Follow));
    }

    [Fact]
    public async Task Falls_back_to_the_known_server_when_the_lookup_fails()
    {
        _lookupFails = true;
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal("sso-cf.tipeeestream.com", socket.Url!.Host);
    }

    [Fact]
    public async Task A_rejected_key_fails_connect_without_leaking_it()
    {
        _keyStatus = HttpStatusCode.Unauthorized;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Contains("rejected the API key", ex.Message);
        Assert.DoesNotContain(Key, _service.StatusMessage);
        Assert.Equal(ConnectionState.Error, _service.State);
    }

    [Fact]
    public void Test_events_are_local_and_marked()
    {
        foreach (var kind in _service.TestEventKinds) Assert.True(_service.SendTestEvent(kind));
        var donation = Assert.Single(Events(TipeeeStreamEventTypes.Donation));
        Assert.Equal((true, "€5.00"), (donation.Args["isTest"], donation.Args["formattedAmount"]));
        Assert.Single(Events(TipeeeStreamEventTypes.Follow));   // tests bypass the relay switch
        Assert.False(_service.SendTestEvent("nope"));
        Assert.Empty(_http.Requests);
    }
}
