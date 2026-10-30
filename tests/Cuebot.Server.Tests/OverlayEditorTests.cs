using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Server.Tests;

public sealed class OverlayEditorTests : IAsyncLifetime
{
    private TestBot _bot = null!;
    private HttpClient _http = null!;
    private Overlay _overlay = null!;
    private OverlayLayer _layer = null!;
    private string _origin = "";

    public async Task InitializeAsync()
    {
        _bot = new TestBot(new ServerSettings { Overlays = new OverlayServerSettings { Enabled = true, Port = 0, WidgetPort = 0 } });
        _overlay = _bot.Overlays.Create("Main", 1280, 720);
        _layer = _bot.Widgets.AddCustomWidget(_overlay.Id, "Chat",
            new WidgetCode("<p>{text}</p>", "p{}", "", """{"text":{"type":"text","value":"hi"}}""", "{}"))!;
        await _bot.Manager.StartAsync();
        _bot.Manager.EnsureEditorToken();
        _origin = $"http://127.0.0.1:{_bot.Manager.Overlays.Port}";
        _http = new HttpClient { BaseAddress = new Uri(_origin) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _bot.DisposeAsync();
    }

    private string Auth => "editor=" + Uri.EscapeDataString(_bot.Manager.Settings.Overlays.EditorToken);
    private string Api => $"/edit/api/{_overlay.Id}/{_layer.Id}";

    private Task<HttpResponseMessage> Post(string path, string body, string? origin = "", string type = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, type) };
        if (origin is not null) request.Headers.Add("Origin", origin.Length == 0 ? _origin : origin);
        return _http.SendAsync(request);
    }

    [Fact]
    public async Task The_editor_needs_its_own_token_not_an_overlay_link()
    {
        using var page = await _http.GetAsync($"/edit/{_overlay.Id}/{_layer.Id}?{Auth}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/runtime/editor.js", html);
        Assert.Contains($"\"widgetPort\":{_bot.Manager.Overlays.WidgetPort}", html);
        Assert.Contains("\"kick\":[\"chat\"", html);

        foreach (var path in new[]
                 {
                     $"/edit/{_overlay.Id}/{_layer.Id}", $"/edit/{_overlay.Id}/{_layer.Id}?editor=wrong",
                     $"/edit/{_overlay.Id}/{_layer.Id}?editor={Uri.EscapeDataString(_overlay.Token)}", $"/edit/{_overlay.Id}/{Guid.NewGuid()}?{Auth}",
                 })
        {
            using var denied = await _http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        Assert.StartsWith($"{_origin}/edit/{_overlay.Id}/{_layer.Id}?editor=cb_", _bot.Manager.EditorUrl(_overlay.Id, _layer.Id));
    }

    [Fact]
    public async Task The_alert_box_opens_the_alert_designer_and_its_code_stays_reachable()
    {
        var box = _bot.Widgets.AddAlertBox(_overlay.Id)!;
        var page = await _http.GetStringAsync($"/edit/{_overlay.Id}/{box.Id}?{Auth}");
        Assert.Contains("/runtime/alerts.js", page);
        Assert.Contains("Cuebot alert designer", page);
        Assert.Contains($"\"layerId\":\"{box.Id}\"", page);
        var code = await _http.GetStringAsync($"/edit/{_overlay.Id}/{box.Id}?{Auth}&code=1");
        Assert.Contains("/runtime/editor.js", code);
        // Other widgets keep the widget editor.
        Assert.Contains("/runtime/editor.js", await _http.GetStringAsync($"/edit/{_overlay.Id}/{_layer.Id}?{Auth}"));
        foreach (var file in new[] { "alerts.js", "alerts.css" })
        {
            using var runtime = await _http.GetAsync($"/runtime/{file}");
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        }
    }

    [Fact]
    public async Task Code_is_loaded_and_saved_and_saving_reloads_the_overlay()
    {
        var code = JsonNode.Parse(await _http.GetStringAsync($"{Api}?{Auth}"))!;
        Assert.Equal("<p>{text}</p>", code["html"]!.GetValue<string>());

        await using var client = await WsClient.ConnectAsync(_bot.Manager.Overlays.Port, $"/o/{_overlay.Id}/ws?token={Uri.EscapeDataString(_overlay.Token)}");
        await client.NextAsync(m => m["type"]?.GetValue<string>() == "hello");

        var body = new JsonObject
        {
            ["html"] = "<b>{text}</b>", ["css"] = "", ["js"] = "", ["fields"] = """{"text":{"type":"text","value":"hi"}}""",
            ["data"] = """{"text":"saved"}""", ["cssOverride"] = ".chat { color: red; }",
        };
        using (var saved = await Post($"{Api}?{Auth}", body.ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stored = _bot.WidgetCode.Read(_layer.ElementId!.Value);
        Assert.Equal(("<b>{text}</b>", ".chat { color: red; }"), (stored.Html, stored.CssOverride));
        Assert.Equal("reload", (await client.NextAsync(m => m["type"]?.GetValue<string>() == "command"))["command"]!.GetValue<string>());

        body["fields"] = "[1,2]";
        using var bad = await Post($"{Api}?{Auth}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Posts_must_come_from_the_editor_page()
    {
        using var noOrigin = await Post($"{Api}?{Auth}", "{}", origin: null);
        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        using var foreign = await Post($"{Api}?{Auth}", "{}", origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("<p>{text}</p>", _bot.WidgetCode.Read(_layer.ElementId!.Value).Html);
    }

    [Fact]
    public async Task Drafts_preview_unsaved_changes_on_the_widget_port()
    {
        var draft = new JsonObject { ["html"] = "<i>{text}</i>", ["fields"] = """{"text":{"type":"text","value":"draft"}}""", ["data"] = "{}" };
        using var response = await Post($"{Api}/preview?{Auth}", draft.ToJsonString());
        var preview = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("draft", preview["fieldData"]!["text"]!.GetValue<string>());
        var src = preview["src"]!.GetValue<string>();
        Assert.StartsWith("/p/", src);

        using var widgets = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Overlays.WidgetPort}") };
        Assert.Contains("<i>draft</i>", await widgets.GetStringAsync(src));
        using var unknown = await widgets.GetAsync("/p/" + new string('0', 48));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var onPagePort = await _http.GetAsync(src);
        Assert.Equal(HttpStatusCode.NotFound, onPagePort.StatusCode);
        Assert.Equal("<p>{text}</p>", _bot.WidgetCode.Read(_layer.ElementId!.Value).Html);   // nothing saved
    }

    [Fact]
    public async Task Emulated_events_are_converted_but_not_broadcast()
    {
        var sent = new List<OverlayMessage>();
        using var subscription = _bot.OverlayFeed.Subscribe(sent.Add);

        using var chat = await Post($"/edit/api/emulate?{Auth}", """{"platform":"kick","kind":"chat"}""");
        var message = Assert.Single(JsonNode.Parse(await chat.Content.ReadAsStringAsync())!["messages"]!.AsArray())!;
        Assert.Equal("message", message["listener"]!.GetValue<string>());
        Assert.Equal("Hello from Kick KEKW", message["event"]!["data"]!["text"]!.GetValue<string>());

        using var unsupported = await Post($"/edit/api/emulate?{Auth}", """{"platform":"rumble","kind":"kicks"}""");
        Assert.Empty(JsonNode.Parse(await unsupported.Content.ReadAsStringAsync())!["messages"]!.AsArray());
        Assert.Empty(sent);   // only the editor's preview gets emulated events
    }

    [Fact]
    public async Task Uploads_become_assets_and_requests_are_answered()
    {
        using var upload = await Post($"/edit/api/assets?{Auth}&name=alert.png", "PNGDATA", type: "application/octet-stream");
        var url = JsonNode.Parse(await upload.Content.ReadAsStringAsync())!["url"]!.GetValue<string>();
        Assert.Matches("^/assets/[0-9a-f]{64}\\.png$", url);
        using var widgets = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_bot.Manager.Overlays.WidgetPort}") };
        Assert.Equal("PNGDATA", await widgets.GetStringAsync(url));

        using var script = await Post($"/edit/api/assets?{Auth}&name=evil.html", "<script>", type: "application/octet-stream");
        Assert.Equal(HttpStatusCode.BadRequest, script.StatusCode);

        using var request = await Post($"{Api}/request?{Auth}", """{"request":"cheer_filter","data":{"message":"Cheer10 hi"}}""");
        Assert.Equal("hi", JsonNode.Parse(await request.Content.ReadAsStringAsync())!["result"]!.GetValue<string>());
    }
}
