using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class StreamlabsServiceTests : IAsyncLifetime
{
    private const string Token = "SL-SECRET-socket-token-1234567890";
    private const string Open = """0{"sid":"s1","upgrades":[],"pingInterval":25000,"pingTimeout":20000}""";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-donations-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private JsonFile<DonationsSettings> _settings = null!;
    private StreamlabsService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.Streamlabs.SocketToken = Token;
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new StreamlabsService(_settings, bus, _log, _server.Create, new Uri("https://sockets.test"), TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private async Task<FakeSocket> ConnectAsync()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        socket.Push(Open);
        Assert.Equal("40", await socket.NextSentAsync());
        socket.Push("""40{"sid":"n"}""");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        return socket;
    }

    [Fact]
    public async Task Connects_with_the_token_publishes_donations_once_and_reconnects()
    {
        var socket = await ConnectAsync();
        Assert.Equal("sockets.test", socket.Url!.Host);
        Assert.Contains($"token={Token}", socket.Url.Query);
        Assert.Contains("EIO=4", socket.Url.Query);
        Assert.Single(Events(StreamlabsEventTypes.Connected));
        Assert.Contains("Socket.IO v4", _service.StatusMessage);

        var frame = "42[\"event\"," + StreamlabsMappingTests.Donation.ReplaceLineEndings("") + "]";
        socket.Push(frame);
        socket.Push(frame);   // Streamlabs re-sends: published once
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Donation).Length == 1, "donation");
        await Task.Delay(100);
        var donation = Assert.Single(Events(StreamlabsEventTypes.Donation));
        Assert.Equal("$13.37", donation.Args["formattedAmount"]);
        var recent = Assert.Single(_service.RecentEvents());
        Assert.Equal("test donated $13.37", recent.Summary);
        Assert.False(recent.IsTest);

        socket.Close();
        var second = await _server.NextAsync();
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Disconnected).Length == 1, "disconnected");
        second.Push(Open);
        await second.NextSentAsync();
        second.Push("40");
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Connected).Length == 2, "reconnected");

        second.Push(frame);   // still a duplicate after reconnecting
        await Task.Delay(100);
        Assert.Single(Events(StreamlabsEventTypes.Donation));
    }

    [Fact]
    public async Task Relayed_platform_events_are_published_only_when_enabled()
    {
        var socket = await ConnectAsync();
        const string follow = """42["event",{"type":"follow","message":[{"id":"1","name":"alice","_id":"f1"}],"for":"twitch_account"}]""";
        socket.Push(follow);
        socket.Push("42[\"event\"," + StreamlabsMappingTests.Donation.ReplaceLineEndings("") + "]");
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Donation).Length == 1, "donation");
        Assert.Empty(Events(StreamlabsEventTypes.Follow));

        _service.Settings.RelayPlatformEvents = true;
        socket.Push(follow.Replace("f1", "f2", StringComparison.Ordinal));
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Follow).Length == 1, "follow");
        Assert.Equal("Twitch", Events(StreamlabsEventTypes.Follow)[0].Args["platform"]);
    }

    [Fact]
    public async Task Falls_back_to_socket_io_v2_and_remembers_it()
    {
        _server.Reject = url => url.Query.Contains("EIO=4", StringComparison.Ordinal);
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Contains("EIO=3", socket.Url!.Query);
        socket.Push(Open);
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.Equal(3, _service.Settings.EngineIoVersion);
        Assert.Contains("v2", _service.StatusMessage);
    }

    [Fact]
    public async Task Test_events_are_local_marked_and_never_deduplicated()
    {
        Assert.True(_service.SendTestEvent("donation"));
        Assert.True(_service.SendTestEvent("donation"));
        Assert.True(_service.SendTestEvent("merch"));
        Assert.False(_service.SendTestEvent("nope"));
        Assert.Equal(2, Events(StreamlabsEventTypes.Donation).Length);
        Assert.All(_published, e => Assert.True(e.Args["isTest"] is true));
        Assert.StartsWith("[Test] ", _service.RecentEvents()[0].Summary);
        Assert.Empty(_server.Urls);   // nothing was sent anywhere
        Assert.Equal(["donation", "charity", "merch", "loyalty"], _service.TestEventKinds);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Connect_requires_a_token_and_never_logs_it()
    {
        _settings.Value.Streamlabs.SocketToken = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Equal(ConnectionState.Error, _service.State);

        _settings.Value.Streamlabs.SocketToken = Token;
        _server.Reject = _ => true;   // every attempt fails, with the token in the URL
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _log.Recent().Count(e => e.Source == "Streamlabs") >= 3, "retries");
        await _service.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _service.State);
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, _service.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_connects_only_when_configured()
    {
        _settings.Value.Streamlabs.AutoConnect = false;
        await _service.StartAsync();
        Assert.False(_service.IsRunning);
        _settings.Value.Streamlabs.AutoConnect = true;
        await _service.StartAsync();
        Assert.True(_service.IsRunning);
        await _server.NextAsync();
    }
}
