using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Decks;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server.Http;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Tests;

/// <summary>Cuebot Decks served by the HTTP server: index, page, state, server-sent events, press and page changes.</summary>
public sealed class DeckRouteTests : IAsyncLifetime
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly DeckService _decks;
    private readonly DeckDefinition _deck;
    private readonly ServerSettings _settings = new() { Http = new HttpServerSettings { Enabled = true, Port = 0 } };
    private HttpApiServer _server = null!;
    private HttpClient _http = null!;

    public DeckRouteTests()
    {
        _bus = new EventBus(_log);
        _decks = new DeckService(null, _bus, _log);
        _deck = new DeckDefinition
        {
            Name = "Stream <Deck>",
            Pages =
            [
                new DeckPage { Items = [new DeckItem { Name = "Hello", States = [new DeckItemState { Title = "Hi" }] }] },
                new DeckPage { Name = "Two" },
            ],
        };
        _decks.Save(_deck);
    }

    public async Task InitializeAsync()
    {
        _server = new HttpApiServer(() => _settings, new StreamerBotRequests(new ServerServices { Bus = _bus, Log = _log }), _decks);
        await _server.StartAsync(IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _log.Dispose();
    }

    [Fact]
    public async Task Index_and_page_are_served_with_the_deck_id()
    {
        var index = await _http.GetStringAsync("/deck");
        Assert.Contains($"/deck/{_deck.Id}", index);
        Assert.Contains("Stream &lt;Deck&gt;", index);

        var page = await _http.GetStringAsync($"/deck/{_deck.Id}");
        Assert.Contains($"\"{_deck.Id}\"", page);
        Assert.DoesNotContain("CUEBOT_DECK", page);

        using var missing = await _http.GetAsync($"/deck/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var byName = await _http.GetAsync("/deck/Stream");
        Assert.Equal(HttpStatusCode.NotFound, byName.StatusCode);
    }

    [Fact]
    public async Task State_press_and_page_round_trip()
    {
        var state = JsonNode.Parse(await _http.GetStringAsync($"/deck/{_deck.Id}/state"))!;
        Assert.Equal("Hi", state["items"]![0]!["title"]!.GetValue<string>());

        var events = new List<BotEvent>();
        _bus.Subscribe(e => { lock (events) events.Add(e); });
        using var press = await _http.PostAsJsonAsync($"/deck/{_deck.Id}/press", new { item = _deck.Pages[0].Items[0].Id });
        Assert.Equal(HttpStatusCode.NoContent, press.StatusCode);
        lock (events) Assert.Equal("127.0.0.1", Assert.Single(events, e => e.Type == DeckEvents.ItemPressed).Args["deckClient"]);

        using var unknown = await _http.PostAsJsonAsync($"/deck/{_deck.Id}/press", new { item = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var get = await _http.GetAsync($"/deck/{_deck.Id}/press");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        using var page = await _http.PostAsJsonAsync($"/deck/{_deck.Id}/page", new { delta = 1 });
        Assert.Equal(HttpStatusCode.NoContent, page.StatusCode);
        Assert.Equal(1, JsonNode.Parse(await _http.GetStringAsync($"/deck/{_deck.Id}/state"))!["page"]!.GetValue<int>());
        using var set = await _http.PostAsJsonAsync($"/deck/{_deck.Id}/page", new { page = 1 });
        Assert.Equal(0, JsonNode.Parse(await _http.GetStringAsync($"/deck/{_deck.Id}/state"))!["page"]!.GetValue<int>());
    }

    [Fact]
    public async Task Press_from_a_foreign_origin_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/deck/{_deck.Id}/press")
        {
            Content = new StringContent($"{{\"item\":\"{_deck.Pages[0].Items[0].Id}\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Events_stream_sends_the_state_then_changes_and_notifications()
    {
        using var response = await _http.GetAsync($"/deck/{_deck.Id}/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        async Task<(string Kind, JsonNode Data)> NextAsync()
        {
            string? kind = null;
            while (true)
            {
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (line is null) throw new EndOfStreamException();
                if (line.StartsWith("event: ")) kind = line[7..];
                else if (line.StartsWith("data: ") && kind is not null) return (kind, JsonNode.Parse(line[6..])!);
            }
        }

        var first = await NextAsync();
        Assert.Equal("state", first.Kind);
        _decks.SetTitle(_deck.Id.ToString(), "Hello", "Changed", null, 0);
        var changed = await NextAsync();
        Assert.Equal(("state", "Changed"), (changed.Kind, changed.Data["items"]![0]!["title"]!.GetValue<string>()));
        _decks.Notify(_deck.Id.ToString(), "Hey", "there");
        var notify = await NextAsync();
        Assert.Equal(("notify", "Hey"), (notify.Kind, notify.Data["title"]!.GetValue<string>()));
    }
}
