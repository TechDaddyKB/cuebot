using System.Net;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Server.Overlays;

namespace Cuebot.Server.Tests;

public sealed class OverlayWidgetTests : IAsyncLifetime
{
    private const string Fields = """{"color":{"type":"colorpicker","value":"#00ff00"},"widgetDuration":{"type":"hidden","value":3}}""";

    private TestBot _bot = null!;
    private HttpClient _page = null!;
    private HttpClient _widgets = null!;
    private Overlay _overlay = null!;
    private OverlayLayer _layer = null!;

    public async Task InitializeAsync()
    {
        _bot = new TestBot(new ServerSettings { Overlays = new OverlayServerSettings { Enabled = true, Port = 0, WidgetPort = 0 } });
        _overlay = _bot.Overlays.Create("Main", 1280, 720);
        _layer = _bot.Widgets.AddCustomWidget(_overlay.Id, "Chat",
            new WidgetCode("<div style=\"color:{color}\">{{color}}</div>", "body { color: {color}; }", "var c = '{color}';", Fields, "{}"))!;
        _overlay = _bot.Overlays.Get(_overlay.Id)!;
        await _bot.Manager.StartAsync();
        _page = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Overlays.Port}") };
        _widgets = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Overlays.WidgetPort}") };
    }

    public async Task DisposeAsync()
    {
        _page.Dispose();
        _widgets.Dispose();
        await _bot.DisposeAsync();
    }

    private string Token => Uri.EscapeDataString(_overlay.Token);

    private string WidgetPath(Guid layerId) => $"/w/{_overlay.Id}/{layerId}?key={OverlayWidgetHost.FrameKey(_overlay.Token, layerId)}";

    [Fact]
    public async Task The_page_lists_layers_and_the_widget_port()
    {
        var html = await _page.GetStringAsync(OverlayServer.PathFor(_overlay));
        Assert.Contains($"\"id\":\"{_layer.Id}\"", html);
        Assert.Contains($"\"widgetPort\":{_bot.Manager.Overlays.WidgetPort}", html);
        Assert.NotEqual(_bot.Manager.Overlays.Port, _bot.Manager.Overlays.WidgetPort);
    }

    [Fact]
    public async Task A_layer_gives_its_settings_and_a_keyed_document_on_the_widget_port()
    {
        var layer = JsonNode.Parse(await _page.GetStringAsync($"/o/{_overlay.Id}/l/{_layer.Id}?token={Token}"))!;
        Assert.Equal("#00ff00", layer["fieldData"]!["color"]!.GetValue<string>());
        Assert.Equal(3, layer["widgetDuration"]!.GetValue<int>());
        var src = layer["src"]!.GetValue<string>();
        Assert.StartsWith($"/w/{_overlay.Id}/{_layer.Id}?key=", src);
        Assert.DoesNotContain(_overlay.Token, src);

        using var doc = await _widgets.GetAsync(src);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        var html = await doc.Content.ReadAsStringAsync();
        Assert.Contains("<div style=\"color:#00ff00\">#00ff00</div>", html);
        Assert.Contains("body { color: #00ff00; }", html);
        Assert.Contains("var c = '#00ff00';", html);
        Assert.Contains("/runtime/se-widget.js", html);
        Assert.Contains(OverlayWidgetHost.JQueryUrl, html);
        Assert.Equal("no-referrer", doc.Headers.GetValues("Referrer-Policy").Single());

        using var shim = await _widgets.GetAsync("/runtime/se-widget.js");
        Assert.Equal(HttpStatusCode.OK, shim.StatusCode);
    }

    [Fact]
    public async Task Streamlabs_widgets_get_the_streamlabs_runtime_and_an_alert_box_carries_its_code_for_each_alert()
    {
        var chat = _bot.Widgets.AddCustomWidget(_overlay.Id, "SL chat", WidgetImport.FromPanes(
            "<div id=\"log\"></div><script type=\"text/template\" id=\"chatlist_item\"><div>{from}</div></script>",
            "body { color: {text_color}; }", "document.addEventListener('onEventReceived', function () {});", "", ""))!;
        var alert = _bot.Widgets.AddCustomWidget(_overlay.Id, "SL alert", WidgetImport.FromPanes(
            "<div id=\"alert-wrap\">{messageTemplate}</div>", "", "var who = '{name}'; // </script>", "", ""))!;
        _overlay = _bot.Overlays.Get(_overlay.Id)!;

        var chatHtml = await _widgets.GetStringAsync(WidgetPath(chat.Id));
        Assert.Contains("/runtime/sl-widget.js", chatHtml);
        Assert.Contains("body { color: #ffffff; }", chatHtml);
        Assert.Contains("id=\"chatlist_item\"><div>{from}</div>", chatHtml);   // the runtime fills it per message

        var alertHtml = await _widgets.GetStringAsync(WidgetPath(alert.Id));
        Assert.Contains("window.__slAlertHtml = ", alertHtml);
        Assert.DoesNotContain("<div id=\"alert-wrap\">", alertHtml);   // not shown until an alert comes
        Assert.DoesNotContain("// </script>", alertHtml);               // carried as an escaped string

        var se = await _widgets.GetStringAsync(WidgetPath(_layer.Id));
        Assert.DoesNotContain("sl-widget.js", se);
        using var runtime = await _widgets.GetAsync("/runtime/sl-widget.js");
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
    }

    [Fact]
    public async Task Keys_open_one_layer_and_the_widget_port_serves_nothing_else()
    {
        var other = _bot.Overlays.Create("Other");
        var otherKey = OverlayWidgetHost.FrameKey(other.Token, _layer.Id);
        var key = OverlayWidgetHost.FrameKey(_overlay.Token, _layer.Id);
        foreach (var path in new[]
                 {
                     $"/w/{_overlay.Id}/{_layer.Id}", $"/w/{_overlay.Id}/{_layer.Id}?key=wrong", $"/w/{_overlay.Id}/{_layer.Id}?key={otherKey}",
                     $"/w/{_overlay.Id}/{Guid.NewGuid()}?key={key}", OverlayServer.PathFor(_overlay), $"/o/{_overlay.Id}/l/{_layer.Id}?token={Token}",
                 })
        {
            using var response = await _widgets.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        _bot.Overlays.RegenerateToken(_overlay.Id);   // a new link changes every frame key
        using var stale = await _widgets.GetAsync($"/w/{_overlay.Id}/{_layer.Id}?key={key}");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }

    [Fact]
    public async Task Widget_requests_are_answered_on_the_socket()
    {
        await using var client = await WsClient.ConnectAsync(_bot.Manager.Overlays.Port, $"/o/{_overlay.Id}/ws?token={Token}");
        await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");
        _bot.Globals.SetValue("deaths", 7L, false);
        _bot.Globals.SetValue("secret", "hunter2", false);

        async Task<JsonObject> Ask(string id, string request, JsonObject data)
        {
            await client.SendRawAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["layer"] = _layer.Id.ToString(), ["request"] = request, ["data"] = data }.ToJsonString());
            return await client.NextAsync(m => m["type"]?.GetValue<string>() == "response" && m["id"]?.GetValue<string>() == id);
        }

        Assert.Null((await Ask("1", "store_set", new JsonObject { ["key"] = "votes", ["value"] = new JsonObject { ["yes"] = 2 } }))["error"]);
        var update = await client.NextAsync(m => m["listener"]?.GetValue<string>() == "kvstore:update");
        Assert.Equal("customWidget.votes", update["event"]!["data"]!["key"]!.GetValue<string>());
        Assert.Equal(2, (await Ask("2", "store_get", new JsonObject { ["key"] = "votes" }))["result"]!["yes"]!.GetValue<int>());

        Assert.Equal(7, (await Ask("3", "counter_get", new JsonObject { ["name"] = "deaths" }))["result"]!["value"]!.GetValue<double>());
        Assert.Equal(0, (await Ask("4", "counter_get", new JsonObject { ["name"] = "secret" }))["result"]!["value"]!.GetValue<double>());
        Assert.Equal("great stream", (await Ask("5", "cheer_filter", new JsonObject { ["message"] = "Cheer100 great  stream Kappa50" }))["result"]!.GetValue<string>());
        Assert.Equal("hi", (await Ask("6", "sanitize", new JsonObject { ["message"] = "hi" }))["result"]!["result"]!["message"]!.GetValue<string>());
        Assert.Contains("Unknown", (await Ask("7", "delete_everything", new JsonObject()))["error"]!.GetValue<string>());
        Assert.Equal(_layer.Id.ToString(), (await Ask("8", "store_get", new JsonObject { ["key"] = "x" }))["layer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_alert_box_can_speak_through_cuebot()
    {
        var box = _bot.Widgets.AddAlertBox(_overlay.Id)!;
        _bot.AlertQueue.Muted = true;
        await using var client = await WsClient.ConnectAsync(_bot.Manager.Overlays.Port, $"/o/{_overlay.Id}/ws?token={Token}");
        var hello = await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");
        Assert.Equal((false, true), (hello["alerts"]!["paused"]!.GetValue<bool>(), hello["alerts"]!["muted"]!.GetValue<bool>()));
        Assert.Empty(hello["polls"]!.AsArray());
        Assert.Null(hello["giveaway"]);
        Assert.Null(hello["media"]!["current"]);
        _bot.ChatPolls.StartPoll("Snack?", ["Chips", "Fruit"], 0);
        var poll = await client.NextAsync(m => m["listener"]?.GetValue<string>() == "cuebot-poll");
        Assert.Equal("Snack?", poll["event"]!["title"]!.GetValue<string>());

        async Task<JsonObject> Ask(string id, OverlayLayer layer, string text)
        {
            await client.SendRawAsync(new JsonObject
            {
                ["type"] = "request", ["id"] = id, ["layer"] = layer.Id.ToString(), ["request"] = "cuebot_tts",
                ["data"] = new JsonObject { ["text"] = text, ["alias"] = "Narrator" },
            }.ToJsonString());
            return await client.NextAsync(m => m["type"]?.GetValue<string>() == "response" && m["id"]?.GetValue<string>() == id);
        }

        Assert.True((await Ask("1", box, "Kris tipped $5"))["result"]!["queued"]!.GetValue<bool>());
        Assert.Contains("speak:Narrator:Kris tipped $5:True:False:False", _bot.Speaker.Log);   // filters on, not waiting
        Assert.Contains("alert box", (await Ask("2", _layer, "I am a bought widget"))["error"]!.GetValue<string>());
        Assert.False((await Ask("3", box, "   "))["result"]!["queued"]!.GetValue<bool>());
        await Ask("4", box, new string('a', 600));
        Assert.Contains(_bot.Speaker.Log, l => l == $"speak:Narrator:{new string('a', OverlayWidgetHost.MaxTtsLength)}:True:False:False");

        _bot.AlertQueue.Paused = true;
        Assert.Equal("pause", (await client.NextAsync(m => m["type"]?.GetValue<string>() == "command"))["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_media_player_moves_the_queue_on_and_only_for_what_plays()
    {
        var player = _bot.Widgets.AddBuiltIn(_overlay.Id, "media")!;
        await _bot.Media.RequestAsync("okVideo0001", "Kick", "a", "Ann", free: true);
        await _bot.Media.RequestAsync("okVideo0002", "Kick", "a", "Ann", free: true);
        await using var client = await WsClient.ConnectAsync(_bot.Manager.Overlays.Port, $"/o/{_overlay.Id}/ws?token={Token}");
        var hello = await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");
        var playing = hello["media"]!["current"]!["id"]!.GetValue<string>();

        async Task<JsonObject> Ask(string id, OverlayLayer layer, string item)
        {
            await client.SendRawAsync(new JsonObject
            {
                ["type"] = "request", ["id"] = id, ["layer"] = layer.Id.ToString(), ["request"] = "cuebot_media",
                ["data"] = new JsonObject { ["action"] = "ended", ["id"] = item },
            }.ToJsonString());
            return await client.NextAsync(m => m["type"]?.GetValue<string>() == "response" && m["id"]?.GetValue<string>() == id);
        }

        Assert.Contains("media request player", (await Ask("1", _layer, playing))["error"]!.GetValue<string>());
        Assert.False((await Ask("2", player, "something-else"))["result"]!["moved"]!.GetValue<bool>());
        Assert.True((await Ask("3", player, playing))["result"]!["moved"]!.GetValue<bool>());
        Assert.False((await Ask("4", player, playing))["result"]!["moved"]!.GetValue<bool>());   // a second player's report
        Assert.Equal("Video okVideo0002", _bot.Media.Current!.Title);
        await client.SendRawAsync(new JsonObject
        {
            ["type"] = "request", ["id"] = "5", ["layer"] = player.Id.ToString(), ["request"] = "cuebot_media",
            ["data"] = new JsonObject { ["action"] = "failed", ["id"] = _bot.Media.Current.Id },
        }.ToJsonString());
        var failed = await client.NextAsync(m => m["type"]?.GetValue<string>() == "response" && m["id"]?.GetValue<string>() == "5");
        Assert.True(failed["result"]!["moved"]!.GetValue<bool>());
        Assert.Null(_bot.Media.Current);
    }

    [Fact]
    public async Task Changing_the_overlay_or_its_widget_reloads_the_page()
    {
        await using var client = await WsClient.ConnectAsync(_bot.Manager.Overlays.Port, $"/o/{_overlay.Id}/ws?token={Token}");
        await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");

        _bot.Widgets.MoveLayer(_overlay.Id, _layer.Id, 10, 10, 300, 200);
        Assert.Equal("reload", (await client.NextAsync(m => m["type"]?.GetValue<string>() == "command"))["command"]!.GetValue<string>());

        _bot.WidgetCode.Write(_layer.ElementId!.Value, new WidgetCode("<p>new</p>", "", "", "{}", "{}"));
        Assert.Equal("reload", (await client.NextAsync(m => m["type"]?.GetValue<string>() == "command"))["command"]!.GetValue<string>());
    }
}
