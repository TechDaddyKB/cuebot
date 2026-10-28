using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuebot.Server.Tests;

public sealed class HttpServerTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cuebot-http-" + Guid.NewGuid().ToString("N"));
    private TestBot _bot = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "overlay.html"), "<h1>overlay</h1>");
        _bot = new TestBot(new ServerSettings
        {
            WebSocket = new StreamerBotWebSocketSettings { Enabled = false, Port = 8123 },
            Http = new HttpServerSettings { Enabled = true, Port = 0, Mappings = [new HttpMapping { Path = "/overlays", Folder = _folder }] },
        });
        await _bot.Manager.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Http.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _bot.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task GetActions_with_and_without_api_prefix()
    {
        _bot.AddAction("One");
        foreach (var path in new[] { "/api/GetActions", "/GetActions" })
        {
            var json = JsonNode.Parse(await _http.GetStringAsync(path))!;
            Assert.Equal(1, json["count"]!.GetValue<int>());
            Assert.Equal("One", json["actions"]![0]!["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Post_DoAction_runs_the_action_and_returns_204()
    {
        _bot.AddAction("Web");
        using var response = await _http.PostAsJsonAsync("/api/DoAction", new { action = new { name = "Web" }, args = new { key = "value" } });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _bot.WaitForRunAsync("Web");
        Assert.Equal("value", _bot.Capture.ByAction["Web"]["key"]);
    }

    [Fact]
    public async Task DoAction_rejects_get_unknown_actions_and_foreign_origins()
    {
        _bot.AddAction("Web");
        using var get = await _http.GetAsync("/api/DoAction?name=Web");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        using var missing = await _http.PostAsJsonAsync("/api/DoAction", new { action = new { name = "Nope" } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/DoAction")
        {
            Content = new StringContent("""{"action":{"name":"Web"}}""", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Origin", "https://evil.example");
        using var foreign = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/GetGlobals");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var blocked = await _http.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Empty(_bot.Runs);
    }

    [Fact]
    public async Task Globals_credits_and_first_words_endpoints()
    {
        _bot.Globals.SetValue("hp", 3L, persist: true);
        var globals = JsonNode.Parse(await _http.GetStringAsync("/api/GetGlobals"))!;
        Assert.Equal(3, globals["variables"]!["hp"]!["value"]!.GetValue<long>());
        var one = JsonNode.Parse(await _http.GetStringAsync("/api/GetGlobal?variable=hp"))!;
        Assert.Equal("hp", one["variable"]!["name"]!.GetValue<string>());

        var credits = JsonNode.Parse(await _http.GetStringAsync("/api/GetCredits"))!;
        Assert.NotNull(credits["credits"]);
        Assert.NotNull(credits["Events"]!["Follows"]);          // the documented HTTP shape
        Assert.NotNull(credits["User"]!["Moderator"]);
        Assert.NotNull(credits["TopBits"]!["All"]);
        var test = JsonNode.Parse(await _http.GetStringAsync("/TestCredits"))!;
        Assert.NotEmpty(test["Events"]!["Follows"]!.AsArray());
        using var clear = await _http.GetAsync("/api/ClearCredits");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var firstWords = await _http.GetAsync("/api/ClearFirstWordsCache");
        Assert.Equal(HttpStatusCode.NoContent, firstWords.StatusCode);
    }

    [Fact]
    public async Task Static_files_and_docks_are_served()
    {
        using var file = await _http.GetAsync("/overlays/overlay.html");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("text/html", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<h1>overlay</h1>", await file.Content.ReadAsStringAsync());

        using var missing = await _http.GetAsync("/overlays/nothing.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var chat = await _http.GetStringAsync("/feed/chat");
        Assert.Contains("\"port\":8123", chat);
        Assert.Contains("function cuebotConnect", chat);
        Assert.Contains("Chat: ['Message']", chat);
        var events = await _http.GetStringAsync("/feed/events");
        Assert.Contains("Chat: ['Activity']", events);
        Assert.Contains("/feed/chat", await _http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Cors_preflight_is_answered_for_local_pages_only()
    {
        using var local = new HttpRequestMessage(HttpMethod.Options, "/api/DoAction");
        local.Headers.Add("Origin", "http://localhost:5173");
        using var ok = await _http.SendAsync(local);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("http://localhost:5173", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var foreign = new HttpRequestMessage(HttpMethod.Options, "/api/DoAction");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var denied = await _http.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}

public sealed class UdpServerTests
{
    [Fact]
    public async Task DoAction_datagram_runs_the_action()
    {
        await using var bot = new TestBot(new ServerSettings { Udp = new UdpServerSettings { Enabled = true, Port = 0 } });
        await bot.Manager.StartAsync();
        var action = bot.AddAction("Udp");
        var port = bot.Manager.Udp!.Port;

        using var udp = new UdpClient();
        var byName = Encoding.UTF8.GetBytes("""{"action":{"name":"Udp"},"args":{"via":"udp"}}""");
        await udp.SendAsync(byName, new IPEndPoint(IPAddress.Loopback, port));
        await bot.WaitForRunAsync("Udp");
        Assert.Equal("udp", bot.Capture.ByAction["Udp"]["via"]);

        var byId = Encoding.UTF8.GetBytes("{\"request\":\"DoAction\",\"action\":{\"id\":\"" + action.Id + "\"},\"args\":{\"via\":\"id\"}}");
        await udp.SendAsync(byId, new IPEndPoint(IPAddress.Loopback, port));
        await StreamerBotWebSocketTests.WaitUntil(() => Equals(bot.Capture.ByAction["Udp"]["via"], "id"));

        // Garbage is ignored and the listener keeps working.
        await udp.SendAsync("not json"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, port));
        await udp.SendAsync(byName, new IPEndPoint(IPAddress.Loopback, port));
        await StreamerBotWebSocketTests.WaitUntil(() => Equals(bot.Capture.ByAction["Udp"]["via"], "udp"));
    }

    [Fact]
    public async Task Servers_stay_off_until_enabled_and_report_port_conflicts()
    {
        await using var bot = new TestBot();
        await bot.Manager.StartAsync();
        Assert.All(bot.Manager.Statuses, s => Assert.False(s.Running));
        Assert.All(bot.Manager.Statuses, s => Assert.Equal("Off", s.Message));
        Assert.Equal(8080, bot.Manager.Settings.WebSocket.Port);
        Assert.Equal(7474, bot.Manager.Settings.Http.Port);
        Assert.Equal(4242, bot.Manager.Settings.Udp.Port);
        Assert.Equal("127.0.0.1", bot.Manager.Settings.WebSocket.Address);

        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        bot.Manager.Settings.Http.Enabled = true;
        bot.Manager.Settings.Http.Port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        await bot.Manager.RestartAsync(ServerKind.Http);
        var status = bot.Manager.Statuses.Single(s => s.Kind == ServerKind.Http);
        Assert.False(status.Running);
        Assert.Contains("in use", status.Message);
    }
}
