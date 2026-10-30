using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Server.Overlays;

namespace Cuebot.Server.Tests;

public sealed class OverlayServerTests : IAsyncLifetime
{
    private TestBot _bot = null!;
    private HttpClient _http = null!;
    private Overlay _overlay = null!;

    public async Task InitializeAsync()
    {
        _bot = new TestBot(new ServerSettings { Overlays = new OverlayServerSettings { Enabled = true, Port = 0 } });
        _overlay = _bot.Overlays.Create("Main <Stage>", 1280, 720);
        await _bot.Manager.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Overlays.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _bot.DisposeAsync();
    }

    private int Port => _bot.Manager.Overlays.Port;

    private string Ws(Overlay overlay, string? token = null) =>
        $"/o/{overlay.Id}/ws?token={Uri.EscapeDataString(token ?? overlay.Token)}";

    [Fact]
    public async Task The_page_needs_the_overlays_token()
    {
        using var page = await _http.GetAsync(OverlayServer.PathFor(_overlay));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<title>Main &lt;Stage&gt;</title>", html);
        Assert.Contains("\"width\":1280", html);
        Assert.DoesNotContain("<Stage>", html);   // the name inside <script> cannot close it
        Assert.Contains("\\u003cstage\\u003e", html.ToLowerInvariant());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());

        var other = _bot.Overlays.Create("Other");
        foreach (var path in new[]
                 {
                     $"/o/{_overlay.Id}", $"/o/{_overlay.Id}?token=wrong", $"/o/{_overlay.Id}?token={other.Token}",
                     $"/o/{Guid.NewGuid()}?token={_overlay.Token}", $"/o/not-a-guid?token={_overlay.Token}", "/", "/o",
                 })
        {
            using var denied = await _http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [Fact]
    public async Task Only_get_is_allowed()
    {
        using var post = await _http.PostAsync(OverlayServer.PathFor(_overlay), new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task Runtime_files_are_served_and_others_are_not()
    {
        using var js = await _http.GetAsync("/runtime/overlay.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.StartsWith("text/javascript", js.Content.Headers.ContentType!.ToString());
        Assert.Contains("window.Cuebot", await js.Content.ReadAsStringAsync());

        using var css = await _http.GetAsync("/runtime/overlay.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);

        using var badge = await _http.GetAsync("/runtime/badge-moderator.svg");
        Assert.Equal(HttpStatusCode.OK, badge.StatusCode);
        Assert.Equal("image/svg+xml", badge.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("<svg", await badge.Content.ReadAsStringAsync());
        using var unknownBadge = await _http.GetAsync("/runtime/badge-nope.svg");
        Assert.Equal(HttpStatusCode.NotFound, unknownBadge.StatusCode);

        using var html = await _http.GetAsync("/runtime/overlay.html");
        Assert.Equal(HttpStatusCode.NotFound, html.StatusCode);
    }

    [Fact]
    public async Task Assets_are_served_immutable_and_sandboxed()
    {
        var name = await _bot.OverlayAssets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")), "logo.svg");

        using var asset = await _http.GetAsync("/assets/" + name);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("image/svg+xml", asset.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString());
        Assert.Equal("sandbox", asset.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", asset.Headers.GetValues("X-Content-Type-Options").Single());

        using var missing = await _http.GetAsync("/assets/" + new string('0', 64) + ".png");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var traversal = await _http.GetAsync("/assets/..%2Foverlays.json");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }

    [Fact]
    public async Task The_socket_says_hello_then_forwards_the_feed_for_its_overlay()
    {
        var other = _bot.Overlays.Create("Other");
        await using var client = await WsClient.ConnectAsync(Port, Ws(_overlay));
        await using var otherClient = await WsClient.ConnectAsync(Port, Ws(other));

        var hello = await client.NextAsync();
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.Equal(_overlay.Id.ToString("D"), hello["overlay"]!["id"]!.GetValue<string>());
        Assert.Equal(720, hello["overlay"]!["height"]!.GetValue<int>());
        await otherClient.NextAsync(m => m["type"]?.GetValue<string>() == "hello");
        await WaitForClientsAsync(2);

        _bot.OverlayFeed.Publish(OverlayMessage.Command("reload", other.Id));
        _bot.OverlayFeed.Publish(OverlayMessage.Event("follower-latest", new JsonObject { ["name"] = "kris" }));

        var follow = await client.NextAsync();
        Assert.Equal("follower-latest", follow["listener"]!.GetValue<string>());
        Assert.Equal("kris", follow["event"]!["name"]!.GetValue<string>());

        var reload = await otherClient.NextAsync();
        Assert.Equal("reload", reload["command"]!.GetValue<string>());
        var alsoFollow = await otherClient.NextAsync();
        Assert.Equal("follower-latest", alsoFollow["listener"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_socket_needs_the_token_and_a_local_or_own_origin()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => WsClient.ConnectAsync(Port, Ws(_overlay, "wrong")));
        await Assert.ThrowsAnyAsync<Exception>(() => WsClient.ConnectAsync(Port, Ws(_overlay), origin: "https://evil.example"));

        await using var own = await WsClient.ConnectAsync(Port, Ws(_overlay), origin: $"http://127.0.0.1:{Port}");
        Assert.Equal("hello", (await own.NextAsync())["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_lists_the_overlay_server_and_its_clients()
    {
        var status = _bot.Manager.Statuses.Single(s => s.Kind == ServerKind.Overlays);
        Assert.True(status.Running);
        Assert.Equal(Port, status.Port);

        await using var client = await WsClient.ConnectAsync(Port, Ws(_overlay));
        await WaitForClientsAsync(1);
        Assert.Equal(1, _bot.Manager.Statuses.Single(s => s.Kind == ServerKind.Overlays).Clients);
        Assert.Equal($"http://127.0.0.1:{Port}/o/{_overlay.Id}?token={_overlay.Token}", _bot.Manager.OverlayUrl(_overlay));
    }

    [Fact]
    public async Task A_restart_stops_feeding_the_old_listener()
    {
        await _bot.Manager.RestartAsync(ServerKind.Overlays);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{Port}");
        await using var client = await WsClient.ConnectAsync(Port, Ws(_overlay));
        await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");
        await WaitForClientsAsync(1);

        _bot.OverlayFeed.Publish(OverlayMessage.Event("tip-latest", new JsonObject { ["amount"] = 5 }));
        var tip = await client.NextAsync();
        Assert.Equal(5, tip["event"]!["amount"]!.GetValue<int>());
    }

    private async Task WaitForClientsAsync(int count)
    {
        for (var i = 0; i < 100 && _bot.Manager.Overlays.ClientCount < count; i++) await Task.Delay(20);
        Assert.Equal(count, _bot.Manager.Overlays.ClientCount);
    }
}

public sealed class OverlayOutboxTests
{
    private static OverlayMessage Chat(int i) => new($"chat{i}", Droppable: true);
    private static OverlayMessage Alert(int i) => new($"alert{i}");

    [Fact]
    public async Task Chat_makes_room_but_alerts_are_kept()
    {
        using var outbox = new OverlayOutbox();
        outbox.Enqueue(Alert(0));
        for (var i = 0; i < OverlayOutbox.SoftLimit; i++) outbox.Enqueue(Chat(i));
        outbox.Enqueue(Alert(1));

        var items = await outbox.TakeAllAsync(CancellationToken.None);
        Assert.Equal(OverlayOutbox.SoftLimit, items.Count);
        Assert.Equal("alert0", items[0].Json);
        Assert.Equal("chat2", items[1].Json);   // chat0 and chat1 made room
        Assert.Equal("alert1", items[^1].Json);
    }

    [Fact]
    public async Task Past_the_hard_limit_the_oldest_of_anything_goes()
    {
        using var outbox = new OverlayOutbox();
        for (var i = 0; i <= OverlayOutbox.HardLimit; i++) outbox.Enqueue(Alert(i));

        var items = await outbox.TakeAllAsync(CancellationToken.None);
        Assert.Equal(OverlayOutbox.HardLimit, items.Count);
        Assert.Equal("alert1", items[0].Json);
    }
}
