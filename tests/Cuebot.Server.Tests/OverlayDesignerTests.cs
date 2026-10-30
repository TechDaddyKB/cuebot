using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Server.Overlays;

namespace Cuebot.Server.Tests;

public sealed class OverlayDesignerTests : IAsyncLifetime
{
    private TestBot _bot = null!;
    private HttpClient _http = null!;
    private Overlay _overlay = null!;
    private OverlayLayer _widget = null!;
    private string _origin = "";

    public async Task InitializeAsync()
    {
        _bot = new TestBot(new ServerSettings { Overlays = new OverlayServerSettings { Enabled = true, Port = 0, WidgetPort = 0 } });
        _overlay = _bot.Overlays.Create("Main", 1920, 1080);
        _widget = _bot.Widgets.AddCustomWidget(_overlay.Id, "Chat", new WidgetCode("<p></p>", "", "", "{}", "{}"))!;
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
    private string Api => $"/design/api/{_overlay.Id}";

    private async Task<JsonNode> Post(string path, HttpContent content, HttpStatusCode expected = HttpStatusCode.OK, string? origin = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (origin is not null) request.Headers.Add("Origin", origin.Length == 0 ? _origin : origin);
        using var response = await _http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_designer_needs_the_editor_token()
    {
        using var page = await _http.GetAsync($"/design/{_overlay.Id}?{Auth}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/runtime/designer.js", await page.Content.ReadAsStringAsync());
        foreach (var path in new[] { $"/design/{_overlay.Id}", $"/design/{_overlay.Id}?editor={Uri.EscapeDataString(_overlay.Token)}", $"/design/{Guid.NewGuid()}?{Auth}" })
        {
            using var denied = await _http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using var foreign = new HttpRequestMessage(HttpMethod.Post, $"{Api}?{Auth}") { Content = Json(new JsonObject { ["layers"] = new JsonArray() }) };
        foreign.Headers.Add("Origin", "https://evil.example");
        using var blocked = await _http.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Single(_bot.Overlays.Get(_overlay.Id)!.Layers);
        Assert.StartsWith($"{_origin}/design/{_overlay.Id}?editor=cb_", _bot.Manager.DesignerUrl(_overlay.Id));
    }

    [Fact]
    public async Task Static_layers_chat_boxes_library_widgets_and_zips_can_be_added()
    {
        foreach (var type in new[] { "text", "image", "video", "slideshow", "chatbox", "chatbox-youtube", "alertbox" })
            await Post($"{Api}/add?{Auth}", Json(new JsonObject { ["type"] = type }));
        await Post($"{Api}/add?{Auth}", Json(new JsonObject { ["type"] = "element", ["elementId"] = _widget.ElementId!.Value.ToString() }));
        await Post($"{Api}/add?{Auth}", Json(new JsonObject { ["type"] = "script" }), HttpStatusCode.BadRequest);

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("html.txt").Open()))
            writer.Write("<b>zipped</b>");
        var imported = await Post($"{Api}/import?{Auth}&name=cool_widget.zip", new ByteArrayContent(zip.ToArray()));
        Assert.NotNull(imported["layer"]);

        var layers = _bot.Overlays.Get(_overlay.Id)!.Layers;
        Assert.Equal(["custom", "text", "image", "video", "slideshow", "custom", "custom", "custom", "custom", "custom"], layers.Select(l => l.Type));
        var text = layers[1];
        Assert.Equal((660.0, 480.0, "Your text here"), (text.X, text.Y, text.Settings!["text"]!.GetValue<string>()));
        Assert.Equal("Alert box", layers[7].Name);
        Assert.Equal(_widget.ElementId, layers[8].ElementId);   // the library widget is shared, not copied
        Assert.Equal("cool widget", layers[9].Name);
    }

    [Fact]
    public async Task Saving_checks_every_layer()
    {
        var state = await Post($"{Api}/add?{Auth}", Json(new JsonObject { ["type"] = "text" }));
        var layers = state["state"]!["overlay"]!["layers"]!.DeepClone().AsArray();
        var text = layers[1]!.AsObject();
        text["x"] = 99999;
        text["opacity"] = 3;
        text["enter"] = new JsonObject { ["name"] = "javascript:alert(1)", ["duration"] = 99 };
        text["groupId"] = Guid.NewGuid().ToString();   // a group of one is cleared
        layers.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = "custom", ["elementId"] = Guid.NewGuid().ToString(), ["name"] = "forged" });
        layers.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = "iframe", ["name"] = "unknown" });
        layers.Add(new JsonObject { ["id"] = text["id"]!.GetValue<string>(), ["type"] = "image", ["name"] = "duplicate id" });

        var saved = await Post($"{Api}?{Auth}", Json(new JsonObject { ["name"] = "Renamed", ["width"] = 1080, ["height"] = 1920, ["layers"] = layers }));
        var overlay = _bot.Overlays.Get(_overlay.Id)!;
        Assert.Equal(("Renamed", 1080, 1920), (overlay.Name, overlay.Width, overlay.Height));
        Assert.Equal(["Chat", "Text", "duplicate id"], overlay.Layers.Select(l => l.Name));
        var t = overlay.Layers[1];
        Assert.Equal((OverlayStore.MaxSize, 1.0, "none", 10.0, (Guid?)null), (t.X, t.Opacity, t.Enter.Name, t.Enter.Duration, t.GroupId));
        Assert.NotEqual(t.Id, overlay.Layers[2].Id);
        Assert.Equal(3, saved["overlay"]!["layers"]!.AsArray().Count);

        await Post($"{Api}?{Auth}", Json(new JsonObject { ["name"] = "x" }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_library_can_be_kept_from_the_designer()
    {
        var added = await Post($"{Api}/add?{Auth}", Json(new JsonObject { ["type"] = "image" }));
        var imageId = added["layer"]!.GetValue<string>();
        var saved = await Post($"{Api}/save-to-library?{Auth}", Json(new JsonObject { ["layerId"] = imageId }));
        var templateId = saved["element"]!.GetValue<string>();
        Assert.Contains(saved["state"]!["elements"]!.AsArray(), e => e!["type"]!.GetValue<string>() == "image");
        await Post($"{Api}/save-to-library?{Auth}", Json(new JsonObject { ["layerId"] = _widget.Id.ToString() }), HttpStatusCode.BadRequest);

        var widgetElement = _widget.ElementId!.Value;
        var inUse = await Post($"/design/api/library/{widgetElement}/delete?{Auth}&overlay={_overlay.Id}", Json(new JsonObject()), HttpStatusCode.BadRequest);
        Assert.Contains("Still used on Main", inUse["error"]!.GetValue<string>());

        var renamed = await Post($"/design/api/library/{widgetElement}/rename?{Auth}&overlay={_overlay.Id}", Json(new JsonObject { ["name"] = "Pink chat" }));
        Assert.Contains(renamed["elements"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "Pink chat"
            && e["usedBy"]!.AsArray().Single()!.GetValue<string>() == "Main");
        await Post($"/design/api/library/{widgetElement}/duplicate?{Auth}&overlay={_overlay.Id}", Json(new JsonObject()));
        await Post($"/design/api/library/{templateId}/delete?{Auth}&overlay={_overlay.Id}", Json(new JsonObject()));
        Assert.Equal(["Pink chat", "Pink chat copy"], _bot.Elements.All().Select(e => e.Name).Order());
        await Post($"/design/api/library/{widgetElement}/explode?{Auth}", Json(new JsonObject()), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Overlay_pages_get_static_settings_and_animations()
    {
        _bot.Overlays.Update(_overlay.Id, o =>
        {
            var text = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Text, o);
            text.Enter = new LayerAnimation { Name = "fadeInUp", Duration = 2 };
            text.Rotation = 15;
            o.Layers.Add(text);
        });
        var html = await _http.GetStringAsync(OverlayServer.PathFor(_bot.Overlays.Get(_overlay.Id)!));
        Assert.Contains("\"type\":\"text\"", html);
        Assert.Contains("Your text here", html);
        Assert.Contains("\"enter\":{\"name\":\"fadeInUp\",\"duration\":2}", html);
        Assert.Contains("\"rotation\":15", html);
        Assert.Contains("\"settings\":null", html);   // widget layers keep their settings to themselves

        var css = await _http.GetStringAsync("/runtime/overlay.css");
        foreach (var name in OverlayLayerTypes.EnterAnimations.Concat(OverlayLayerTypes.ExitAnimations).Where(n => n != "none"))
            Assert.Contains("@keyframes cuebot-" + name + " ", css);
    }
}
