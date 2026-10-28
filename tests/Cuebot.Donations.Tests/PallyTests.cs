using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Pally;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class PallyMappingTests
{
    /// <summary>The sample event of docs.pally.gg/advanced/websockets, verbatim.</summary>
    internal const string Tip = """
        {"type":"campaigntip.notify","payload":{"campaignTip":{"createdAt":"2024-03-13T18:02:33.743Z","displayName":"Someone","grossAmountInCents":500,
         "id":"b1w2pjwjtb9fx0v1se9ex4n2","message":"","netAmountInCents":500,"processingFeeInCents":0,"updatedAt":"2024-03-13T18:02:33.743Z"},
         "page":{"id":"1627451579049x550722173620715500","slug":"pally","title":"Pally.gg's Team Page","url":"https://pally.gg/p/pally"}}}
        """;

    [Fact]
    public void Maps_the_documented_tip_with_streamerbot_names()
    {
        var e = PallyEventMapper.Map(StreamlabsMappingTests.Parse(Tip))!;
        Assert.Equal(PallyEventTypes.CampaignTip, e.Type);
        Assert.Empty(e.Keys);   // replays are wanted; PallyService marks them instead of dropping them
        Assert.Equal(("b1w2pjwjtb9fx0v1se9ex4n2", false), (e.Args["tipId"], e.Args["isReplay"]));
        var a = e.Args;
        // Streamer.bot's types: decimals, the fee in cents an int, createdAt a DateTime.
        Assert.Equal(("Someone", 5m, "$5.00", 500m), (a["tipDisplayName"], a["tipGrossAmount"], a["tipGrossAmountFormatted"], a["tipGrossAmountInCents"]));
        Assert.Equal((5m, 0, 0m), (a["tipNetAmount"], a["tipProccessingFeeInCents"], a["tipProcessingFee"]));
        Assert.IsType<DateTime>(a["createdAt"]);
        Assert.Equal(("1627451579049x550722173620715500", "pally", "Pally.gg's Team Page", "https://pally.gg/p/pally"),
            (a["campaignId"], a["campaignSlug"], a["campaignTitle"], a["campaignUrl"]));
        Assert.Equal(("Someone", 5d, "USD", false), (a["user"], a["amount"], a["currency"], a["isTest"]));
    }

    [Fact]
    public void Ignores_other_messages_and_flags_echoes()
    {
        Assert.Null(PallyEventMapper.Map(StreamlabsMappingTests.Parse("""{"type":"something.else","payload":{}}""")));
        Assert.Null(PallyEventMapper.Map(StreamlabsMappingTests.Parse("""{"type":"campaigntip.notify","payload":{}}""")));
        var echo = PallyEventMapper.Map(StreamlabsMappingTests.Parse(PallyEventMapper.Sample(PallyEventMapper.EchoPrefix + "1")))!;
        Assert.Equal((true, true), (echo.Args["isEcho"], echo.Args["isTest"]));
        Assert.Equal("""{"type":"echo","payload":{"a":1}}""", PallyEventMapper.Echo("""{"a":1}"""));
    }

    [Fact]
    public void Feed_shows_the_campaign()
    {
        var e = PallyEventMapper.Map(StreamlabsMappingTests.Parse(Tip))!;
        var item = new PallyChatAdapter().ToFeedItem(new BotEvent(e.Type, e.Args))!;
        Assert.Equal(("Pally.gg", "Someone tipped $5.00 to Pally.gg's Team Page", "$5.00"), (item.Platform, item.Title, item.Amount));
    }
}

public sealed class PallyServiceTests : IAsyncLifetime
{
    private const string Key = "PALLY-SECRET-api-key-abcdef123456";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-pally-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private JsonFile<DonationsSettings> _settings = null!;
    private PallyService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.Pally.ApiKey = Key;
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new PallyService(_settings, bus, _log, _server.Create, new Uri("wss://events.pally.test"),
            TestTimings.Fast with { KeepAlive = TimeSpan.FromMilliseconds(150), OpenTimeout = TimeSpan.FromMilliseconds(300) });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    [Fact]
    public void Builds_firehose_and_page_urls()
    {
        Assert.Equal($"wss://events.pally.test/?auth={Key}&channel=firehose", _service.Url().AbsoluteUri);
        _settings.Value.Pally.PageSlug = "my page";
        Assert.Equal($"wss://events.pally.test/?auth={Key}&channel=activity-feed&room=my%20page", _service.Url().AbsoluteUri);
    }

    [Fact]
    public async Task Connects_pings_publishes_tips_and_replays_and_reconnects()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.Single(Events(PallyEventTypes.Connected));

        Assert.Equal("ping", await socket.NextSentAsync());
        socket.Push("pong");
        var tip = PallyMappingTests.Tip.ReplaceLineEndings("");
        socket.Push(tip);
        // "Replay Alert" in the activity feed re-broadcasts the tip to every websocket (docs.pally.gg/features/activity-feed);
        // Pally sends a tip only once otherwise, so the second copy is a replay: published again, marked isReplay.
        socket.Push(tip);
        await Wait.UntilAsync(() => Events(PallyEventTypes.CampaignTip).Length == 2, "tip and replay");
        var tips = Events(PallyEventTypes.CampaignTip);
        Assert.Equal((false, true), (tips[0].Args["isReplay"], tips[1].Args["isReplay"]));
        Assert.Equal("Someone tipped $5.00 to Pally.gg's Team Page", _service.RecentEvents()[0].Summary);

        socket.Close();
        await _server.NextAsync();
        await Wait.UntilAsync(() => Events(PallyEventTypes.Disconnected).Length == 1, "disconnected");
    }

    [Fact]
    public async Task A_missing_pong_drops_the_connection()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal("ping", await socket.NextSentAsync());
        var second = await _server.NextAsync();   // no pong → reconnect
        Assert.NotSame(socket, second);
    }

    [Fact]
    public async Task A_refused_upgrade_is_reported_as_a_bad_key()
    {
        _server.Reject = _ => true;
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Error, "error");
        Assert.Equal("Pally.gg rejected the API key", _service.StatusMessage);
        Assert.DoesNotContain(Key, _service.StatusMessage);
    }

    [Fact]
    public async Task Echo_test_goes_through_the_connection_and_comes_back_as_a_test()
    {
        Assert.False(await _service.SendEchoTestAsync());
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.True(await _service.SendEchoTestAsync());
        string sent;
        do sent = await socket.NextSentAsync(); while (sent == "ping");
        Assert.StartsWith("""{"type":"echo","payload":{"type":"campaigntip.notify""", sent);
        var inner = StreamlabsMappingTests.Parse(sent).GetProperty("payload").GetRawText();
        socket.Push(inner);   // what Pally sends back
        await Wait.UntilAsync(() => Events(PallyEventTypes.CampaignTip).Length == 1, "echoed tip");
        Assert.Equal(true, Events(PallyEventTypes.CampaignTip)[0].Args["isTest"]);
    }

    [Fact]
    public void Local_test_event()
    {
        Assert.True(_service.SendTestEvent("campaignTip"));
        Assert.False(_service.SendTestEvent("x"));
        Assert.Equal(true, Assert.Single(Events(PallyEventTypes.CampaignTip)).Args["isTest"]);
    }
}
