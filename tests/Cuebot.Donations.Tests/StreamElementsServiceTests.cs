using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class StreamElementsServiceTests : IAsyncLifetime
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.SE-SECRET-jwt-payload.signature123";
    private const string ChannelJson = """{"_id":"5ad23dcc18fff500d78c5348","username":"techdaddy","displayName":"TechDaddy","provider":"twitch"}""";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-se-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private HttpStatusCode _channelStatus = HttpStatusCode.OK;
    private JsonFile<DonationsSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private StreamElementsService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.StreamElements.JwtToken = Jwt;
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _http = new FakeHttpHandler(Respond);
        _service = new StreamElementsService(_settings, bus, _log, _server.Create, new Uri("wss://astro.test"), new Uri("https://realtime.test"),
            TestTimings.Fast, new StreamElementsApi(new HttpClient(_http), new Uri("https://api.test/kappa/v2/")));
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
        "/kappa/v2/channels/me" => _channelStatus == HttpStatusCode.OK
            ? FakeHttpHandler.Json(ChannelJson)
            : FakeHttpHandler.Json("""{"statusCode":401,"error":"Unauthorized","message":"Invalid token"}""", _channelStatus),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private static string Activity(string activity) =>
        """{"id":"01HPPM2TQ6CCMFQGHEN8MM1NN0","ts":"2024-02-15T14:52:42Z","type":"message","topic":"channel.activities","room":"5ad23dcc18fff500d78c5348","data":"""
        + activity.ReplaceLineEndings("") + "}";

    private async Task<(FakeSocket Socket, JsonElement Subscribe)> ConnectAstroAsync()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        var subscribe = JsonDocument.Parse(await socket.NextSentAsync()).RootElement.Clone();
        socket.Push($$$"""{"id":"r1","ts":"2024-02-15T14:52:42Z","type":"response","nonce":"{{{subscribe.GetProperty("nonce").GetString()}}}","data":{"message":"successfully subscribed to topic","topic":"channel.activities","room":"5ad23dcc18fff500d78c5348"}}""");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        return (socket, subscribe);
    }

    [Fact]
    public async Task Astro_subscribes_with_the_jwt_and_publishes_tips_once()
    {
        var (socket, subscribe) = await ConnectAstroAsync();
        Assert.Equal("wss://astro.test/", socket.Url!.AbsoluteUri);
        Assert.Equal("subscribe", subscribe.GetProperty("type").GetString());
        var data = subscribe.GetProperty("data");
        Assert.Equal(("channel.activities", Jwt, "jwt"),
            (data.GetProperty("topic").GetString(), data.GetProperty("token").GetString(), data.GetProperty("token_type").GetString()));
        Assert.Equal("5ad23dcc18fff500d78c5348", _service.Settings.ChannelId);
        Assert.Contains("TechDaddy", _service.StatusMessage);
        Assert.Equal("Bearer " + Jwt, _http.Requests[0].Authorization);

        socket.Push(Activity(StreamElementsMappingTests.Tip));
        socket.Push(Activity(StreamElementsMappingTests.Tip));   // an update of the same activity
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");
        await Task.Delay(100);
        Assert.Single(Events(StreamElementsEventTypes.Tip));
        Assert.Equal("Styler tipped $4.20", _service.RecentEvents()[0].Summary);

        // Relayed follows only with the option on.
        const string follow = """{"type":"follow","provider":"twitch","channel":"c","data":{"username":"jtv","displayName":"jtv","providerId":"14027"},"_id":"f1","activityId":"f1"}""";
        socket.Push(Activity(follow));
        // Messages are handled in order: once a second tip is out, the follow was handled with the option still off.
        socket.Push(Activity(StreamElementsMappingTests.Tip.Replace("65ce2539", "75ce2539", StringComparison.Ordinal)
            .Replace("67b5f39d", "77b5f39d", StringComparison.Ordinal)));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 2, "second tip");
        Assert.Empty(Events(StreamElementsEventTypes.Follow));
        _service.Settings.RelayPlatformEvents = true;
        socket.Push(Activity(follow.Replace("f1", "f2", StringComparison.Ordinal)));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Follow).Length == 1, "follow");
    }

    [Fact]
    public async Task Astro_reconnect_message_resumes_with_the_reconnect_token_without_resubscribing()
    {
        var (socket, _) = await ConnectAstroAsync();
        socket.Push("""{"id":"x","ts":"2024-02-15T14:52:42Z","type":"reconnect","data":{"reconnect_token":"resume-123"}}""");
        var resumed = await _server.NextAsync();
        Assert.Equal("?reconnect_token=resume-123", resumed.Url!.Query);
        Assert.True(await resumed.SilentForAsync(TimeSpan.FromMilliseconds(150)));   // subscriptions are restored by the server
        resumed.Push("""{"type":"welcome","data":{}}""");

        resumed.Close();
        var fresh = await _server.NextAsync();   // the token is used once
        Assert.Equal("", fresh.Url!.Query);
        Assert.Contains("\"subscribe\"", await fresh.NextSentAsync());
    }

    [Fact]
    public async Task Astro_unauthorized_response_is_an_error_without_the_token()
    {
        // Recorded as it happens: the error only lasts until the (fast) retry, too short to poll for on a slow runner.
        var rejected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.StatusChanged += () =>
        {
            if (_service.State == ConnectionState.Error && _service.StatusMessage is var message && message.Contains("rejected"))
                rejected.TrySetResult(message);
        };
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        var subscribe = JsonDocument.Parse(await socket.NextSentAsync()).RootElement;
        socket.Push($$$"""{"id":"e","type":"response","nonce":"{{{subscribe.GetProperty("nonce").GetString()}}}","error":"err_unauthorized","data":{"message":"invalid token"}}""");
        var status = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(Jwt, status, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains(Jwt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_jwt_fails_the_connection_up_front()
    {
        _channelStatus = HttpStatusCode.Unauthorized;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Contains("rejected", ex.Message);
        Assert.Equal(ConnectionState.Error, _service.State);
        Assert.Empty(_server.Urls);
    }

    [Fact]
    public async Task Legacy_realtime_authenticates_over_socket_io_and_maps_events()
    {
        _service.Settings.Transport = StreamElementsTransport.LegacyRealtime;
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal("realtime.test", socket.Url!.Host);
        Assert.Contains("EIO=3", socket.Url.Query);
        socket.Push("""0{"sid":"s","upgrades":[],"pingInterval":25000,"pingTimeout":5000}""");
        Assert.Equal($$"""42["authenticate",{"method":"jwt","token":"{{Jwt}}"}]""", await socket.NextSentAsync());
        socket.Push("40");
        socket.Push("""42["authenticated",{"clientId":"c","channelId":"5ad23dcc18fff500d78c5348","project":"x","message":"authenticated"}]""");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.Contains("legacy", _service.StatusMessage);

        socket.Push("42[\"event\"," + StreamElementsMappingTests.Tip.ReplaceLineEndings("") + "]");
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");
    }

    [Fact]
    public async Task Test_events_are_published_locally()
    {
        foreach (var kind in _service.TestEventKinds) Assert.True(_service.SendTestEvent(kind));
        Assert.Single(Events(StreamElementsEventTypes.Tip));
        Assert.Single(Events(StreamElementsEventTypes.Merch));
        Assert.Single(Events(StreamElementsEventTypes.Redemption));
        Assert.Equal(3, _service.RecentEvents().Count);
        Assert.All(_service.RecentEvents(), r => Assert.True(r.IsTest));
        Assert.Empty(_server.Urls);
        await Task.CompletedTask;
    }
}
