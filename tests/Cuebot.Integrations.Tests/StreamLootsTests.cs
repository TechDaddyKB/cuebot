using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.StreamLoots;
using Cuebot.Integrations.Tests.Support;

namespace Cuebot.Integrations.Tests;

public sealed class StreamLootsTests : IAsyncLifetime
{
    private const string AlertId = "1a2b3c4d-5e6f-4a1b-9c8d-0123456789ab";
    private readonly TestEnvironment _env = new();
    private StreamLootsService _service = null!;

    // Shapes as StreamLoots sends them on the alerts stream (see PROTOCOL.md): quantity is a number on purchases.
    private const string Purchase = """
        {"imageUrl":"https://static.streamloots.com/x/chest.png","soundUrl":"https://static.streamloots.com/x/s.mp3",
         "message":"Alice just bought 3 chests!","data":{"type":"purchase","fields":[{"name":"quantity","value":3},{"name":"username","value":"Alice"}]}}
        """;

    private const string Gift = """
        {"message":"Bob gifted 2 chests to Carol","data":{"type":"purchase",
         "fields":[{"name":"username","value":"Bob"},{"name":"quantity","value":"2"},{"name":"giftee","value":"Carol"}]}}
        """;

    private const string Redemption = """
        {"imageUrl":"https://static.streamloots.com/x/card.png","soundUrl":"","videoUrl":"https://static.streamloots.com/x/card.webm",
         "message":"Dave redeemed Hydrate!","data":{"type":"redemption","cardId":"c1","cardSetId":"s1","cardName":"Hydrate",
         "description":"Drink water","fields":[{"name":"username","value":"Dave"},{"name":"message","value":"drink up"},{"name":"rarity","value":"Epic"}]}}
        """;

    public Task InitializeAsync()
    {
        _env.Settings.Value.StreamLoots.AlertsUrl = "https://widgets.streamloots.com/alerts/" + AlertId;
        _service = new StreamLootsService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast, new Uri("https://streamloots.test/"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _env.Dispose();
    }

    [Theory]
    [InlineData("https://widgets.streamloots.com/alerts/" + AlertId, AlertId)]
    [InlineData("https://widgets.streamloots.com/alerts/" + AlertId + "/media-stream", AlertId)]
    [InlineData("  " + AlertId + "  ", AlertId)]
    [InlineData("https://www.streamloots.com/alice/widgets/alerts", null)]
    [InlineData("https://example.com/", null)]
    [InlineData("", null)]
    [InlineData("not an id", null)]
    public void Parses_the_alerts_widget_url(string input, string? id)
    {
        var (parsed, error) = StreamLootsProtocol.ParseAlertId(input);
        Assert.Equal(id, parsed);
        Assert.Equal(id is null, error is not null);
    }

    [Fact]
    public void The_settings_page_url_gets_a_helpful_error() =>
        Assert.Contains("settings page", StreamLootsProtocol.ParseAlertId("https://www.streamloots.com/alice/widgets/alerts").Error);

    [Fact]
    public async Task Opens_the_media_stream_and_publishes_purchases_gifts_and_redemptions()
    {
        await _service.ConnectAsync();
        var stream = await _env.Server.NextAsync();
        Assert.Equal($"https://streamloots.test/alerts/{AlertId}/media-stream", stream.Url!.ToString());
        await _env.WaitForEventAsync(StreamLootsEvents.Connected);

        stream.Push(Purchase);
        stream.Push(Gift);
        stream.Push(Redemption);
        stream.Push("""{"data":{}}""");
        stream.Push("garbage");
        var purchase = await _env.WaitForEventAsync(StreamLootsEvents.PackPurchased);
        Assert.Equal("Alice", purchase.Args["user"]);
        Assert.Equal(3, purchase.Args["quantity"]);
        Assert.Equal(false, purchase.Args["isGift"]);
        Assert.Equal("Alice just bought 3 chests!", purchase.Args["alertMessage"]);
        Assert.Equal("https://static.streamloots.com/x/chest.png", purchase.Args["imageUrl"]);
        Assert.Equal("StreamLoots", purchase.Args["service"]);

        var gift = await _env.WaitForEventAsync(StreamLootsEvents.PackGifted);
        Assert.Equal("Bob", gift.Args["user"]);
        Assert.Equal("Carol", gift.Args["giftee"]);
        Assert.Equal(2, gift.Args["quantity"]);
        Assert.Equal(false, gift.Args["communityGift"]);

        var card = await _env.WaitForEventAsync(StreamLootsEvents.CardRedeemed);
        Assert.Equal("Hydrate", card.Args["cardName"]);
        Assert.Equal("epic", card.Args["cardRarity"]);
        Assert.Equal("drink up", card.Args["message"]);
        Assert.Equal("Drink water", card.Args["cardDescription"]);
        Assert.Equal(true, card.Args["hasVideo"]);
        Assert.Equal(false, card.Args["isTest"]);
        using (var fields = JsonDocument.Parse((string)card.Args["fields"]!))
            Assert.Equal("Epic", fields.RootElement.GetProperty("rarity").GetString());
        Assert.Contains("Dave redeemed Hydrate", _service.LastAlert);
        Assert.Equal(3, _env.Published.Count(e => e.Type.StartsWith("streamloots.pack") || e.Type == StreamLootsEvents.CardRedeemed));

        // The stream ended: disconnected, then reconnected.
        stream.Close("the server ended the event stream");
        await _env.WaitForEventAsync(StreamLootsEvents.Disconnected);
        await _env.Server.NextAsync();
        await Wait.UntilAsync(() => _env.Events(StreamLootsEvents.Connected).Length == 2, "reconnected");
        Assert.DoesNotContain(AlertId, _env.AllLogText());
        Assert.DoesNotContain(AlertId, _service.StatusMessage);
    }

    [Fact]
    public void Community_gifts_and_unknown_alerts()
    {
        var community = StreamLootsProtocol.Map("""{"data":{"type":"random-community-gift","fields":[{"name":"username","value":"Eve"},{"name":"quantity","value":"5"}]}}""")!;
        Assert.Equal("packGifted", community.Name);
        Assert.Equal(true, community.Args["communityGift"]);
        Assert.Equal(5, community.Args["quantity"]);

        var other = StreamLootsProtocol.Map("""{"message":"Frank reacted","data":{"type":"reaction","fields":[{"name":"username","value":"Frank"}]}}""")!;
        Assert.Equal("alert", other.Name);
        Assert.Equal("reaction", other.Args["alertType"]);
        Assert.Equal("Frank", other.Args["user"]);
    }

    [Fact]
    public async Task Missing_url_fails_fast_and_a_404_stops_reconnecting()
    {
        _env.Settings.Value.StreamLoots.AlertsUrl = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Equal(ConnectionState.Error, _service.State);

        _env.Settings.Value.StreamLoots.AlertsUrl = AlertId;
        _env.Server.Reject = _ => new HttpRequestException("HTTP 404", null, HttpStatusCode.NotFound);
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Error, "error");
        Assert.Contains("did not accept the alert id", _service.StatusMessage);
        await Task.Delay(100);
        Assert.Single(_env.Server.Attempts);
    }

    [Fact]
    public void Test_events_feed_and_credits_shape()
    {
        foreach (var kind in _service.TestEventKinds) Assert.True(_service.SendTestEvent(kind), kind);
        Assert.False(_service.SendTestEvent("nope"));
        var card = Assert.Single(_env.Events(StreamLootsEvents.CardRedeemed));
        Assert.Equal(true, card.Args["isTest"]);

        var adapter = new StreamLootsFeedAdapter();
        var feed = adapter.ToFeedItem(card)!;
        Assert.Equal(FeedKind.Redemption, feed.Kind);
        Assert.Equal("[Test] Cuebot Tester redeemed Hydrate", feed.Title);
        var gift = adapter.ToFeedItem(_env.Events(StreamLootsEvents.PackGifted).First())!;
        Assert.Equal(FeedKind.Other, gift.Kind);
        Assert.Equal("2 chests", gift.Amount);
        Assert.Null(adapter.ToFeedItem(new BotEvent(StreamLootsEvents.Connected, new Dictionary<string, object?>())));
    }

    [Fact]
    public void Trigger_filters()
    {
        var triggers = new StreamLootsTriggers().GetTriggers().ToArray();
        Assert.All(triggers, t => Assert.Equal("StreamLoots", t.Category));
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        bool Matches(string id, Dictionary<string, object?> args, Dictionary<string, string> settings)
        {
            var t = triggers.Single(x => x.TypeId == id);
            return t.Matches(new BotEvent(id, args), new TriggerDefinition(), ParameterValues.From(t.Parameters, settings), new Dictionary<string, object?>());
        }

        var card = new Dictionary<string, object?> { ["cardName"] = "Hydrate", ["cardRarity"] = "epic" };
        Assert.True(Matches(StreamLootsEvents.CardRedeemed, card, []));
        Assert.True(Matches(StreamLootsEvents.CardRedeemed, card, new() { ["cardName"] = "hydrate, Dance", ["cardRarity"] = "epic" }));
        Assert.False(Matches(StreamLootsEvents.CardRedeemed, card, new() { ["cardRarity"] = "legendary" }));
        var gift = new Dictionary<string, object?> { ["quantity"] = 2, ["communityGift"] = true };
        Assert.True(Matches(StreamLootsEvents.PackGifted, gift, []));
        Assert.False(Matches(StreamLootsEvents.PackGifted, gift, new() { ["minQuantity"] = "3" }));
        Assert.False(Matches(StreamLootsEvents.PackGifted, gift, new() { ["includeCommunity"] = "false" }));
    }
}

public sealed class SseTransportTests
{
    [Fact]
    public void Parser_follows_the_event_stream_rules()
    {
        var parser = new SseParser();
        var events = new List<SseEvent>();
        foreach (var line in new[] { ":", "", "data: {\"a\":1}", "", "event: ping", "data: x", "", "data: line1", "data:line2", "id: 7", "", "retry: 10", "" })
            if (parser.Line(line) is { } e) events.Add(e);
        Assert.Equal(3, events.Count);
        Assert.Equal(new SseEvent("message", "{\"a\":1}", null), events[0]);
        Assert.Equal("ping", events[1].Type);
        Assert.Equal(new SseEvent("message", "line1\nline2", "7"), events[2]);
    }

    [Fact]
    public async Task Reads_message_events_and_ignores_keep_alive_comments()
    {
        var body = ":\n\ndata: {\"n\":1}\n\n:\n\nevent: other\ndata: skip\n\ndata: {\"n\":2}\n\n";
        using var http = new HttpClient(new StubHandler(_ => Response(new MemoryStream(Encoding.UTF8.GetBytes(body)))));
        await using var sse = new SseTransport(http, TimeSpan.FromSeconds(5));
        await sse.ConnectAsync(new Uri("https://sse.test/stream"), null, CancellationToken.None);
        Assert.Equal("{\"n\":1}", await sse.ReceiveAsync(CancellationToken.None));
        Assert.Equal("{\"n\":2}", await sse.ReceiveAsync(CancellationToken.None));
        Assert.Null(await sse.ReceiveAsync(CancellationToken.None));
        Assert.Equal("the server ended the event stream", sse.CloseReason);
    }

    [Fact]
    public async Task Sends_the_event_stream_accept_header_and_reports_http_errors()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new StubHandler(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        await using var sse = new SseTransport(http, TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sse.ConnectAsync(new Uri("https://sse.test/x"), null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains(seen!.Headers.Accept, a => a.MediaType == "text/event-stream");

        using var html = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") }));
        await using var notSse = new SseTransport(html, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => notSse.ConnectAsync(new Uri("https://sse.test/x"), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_silent_stream_times_out()
    {
        using var http = new HttpClient(new StubHandler(_ => Response(new SilentStream())));
        await using var sse = new SseTransport(http, TimeSpan.FromMilliseconds(200));
        await sse.ConnectAsync(new Uri("https://sse.test/x"), null, CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => sse.ReceiveAsync(CancellationToken.None));
        Assert.Contains("no data or keep-alive", sse.CloseReason);
    }

    private static HttpResponseMessage Response(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    /// <summary>A stream that never delivers data (until cancelled).</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
