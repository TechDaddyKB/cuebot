using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Overlays;
using Cuebot.Portal.Uploads;

namespace Cuebot.Portal.Tests;

/// <summary>Overlays and widgets through the portal API: what each role may change, and that values cannot carry code.</summary>
public sealed class PortalOverlaysTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-portal-overlays-").FullName;
    private readonly AppLog _log = new();
    private readonly OverlayStore _overlays;
    private readonly ElementStore _elements;
    private readonly WidgetCodeStore _code = new(null);
    private readonly AssetStore _assets = new(null);
    private readonly WidgetLibrary _library;
    private readonly PortalMediaStore _media;
    private readonly PortalDirectory _directory;
    private readonly PortalChanges _changes;
    private readonly PortalApi _api;
    private readonly Overlay _overlay;

    public PortalOverlaysTests()
    {
        _overlays = new OverlayStore(null, _log);
        _elements = new ElementStore(null, _log);
        _library = new WidgetLibrary(_overlays, _elements, _code);
        _media = new PortalMediaStore(new PortalMediaLibrary(), () => { }, Path.Combine(_dir, "media"));
        var profile = new ProfileService(null, _log);
        var commands = new CommandStore(null, _log);
        var registry = new Registry([], []);
        var engine = new ActionEngine(new EventBus(_log), profile, registry, new GlobalVariables(null, _log), _log);
        _directory = new PortalDirectory(new PortalSettings(), () => { });
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _directory,
            [new ActionArea(profile), new CommandArea(commands), new OverlayArea(_overlays, _elements, _code), new WidgetArea(_elements, _code)]);
        var overlays = new PortalOverlays(_overlays, _elements, _code, _assets, _changes, _media);
        _api = new PortalApi(_directory, _changes, profile, commands, engine, registry, overlays: overlays);

        _overlay = _overlays.Create("Main");
        var text = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Text, _overlay);
        _overlays.Update(_overlay.Id, o => o.Layers.Add(text));
        _overlay = _overlays.Get(_overlay.Id)!;
    }

    public void Dispose()
    {
        _code.Dispose();
        _assets.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _directory.RedeemInvite(_directory.CreateInvite(role).Secret,
            new ProviderIdentity(IdentityProvider.Google, Guid.NewGuid().ToString(), "m@x", "Sam"), PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private PortalMember Approver()
    {
        var role = _directory.SaveRole(new PortalRole { Name = "Overlay helper", Permissions = [PortalPermission.EditOverlays], Changes = ChangeMode.NeedsApproval });
        return Member(role.Id);
    }

    private Task<JsonObject> Call(PortalMember m, string op, JsonObject? body = null)
    {
        var request = body ?? new JsonObject();
        request["op"] = op;
        return _api.HandleAsync(m.Id, request);
    }

    private async Task<JsonObject> GetOverlay(PortalMember m, Guid? id = null) =>
        (await Call(m, "overlays.get", new JsonObject { ["id"] = (id ?? _overlay.Id).ToString() }))["overlay"]!.DeepClone().AsObject();

    private async Task<JsonObject> Widget(PortalMember m, Guid elementId)
    {
        var answer = await Call(m, "overlays.get", new JsonObject { ["id"] = _overlay.Id.ToString() });
        return ((JsonArray)answer["widgets"]!).Single(w => (string?)w!["id"] == elementId.ToString())!.DeepClone().AsObject();
    }

    private static JsonObject Field(JsonObject widget, string key) => (JsonObject)((JsonArray)widget["fields"]!).Single(f => (string?)f!["key"] == key)!;

    private static void Ok(JsonObject answer) => Assert.True((bool)answer["ok"]!, (string?)answer["error"]);

    private static string Error(JsonObject answer)
    {
        Assert.False((bool)answer["ok"]!);
        return (string)answer["error"]!;
    }

    private PortalMediaItem Upload(UploadKind kind, string extension, byte[] content)
    {
        var folder = Path.Combine(_media.Folder, PortalMediaStore.SubFolder(kind));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(PortalMediaStore.SubFolder(kind), Guid.NewGuid().ToString("N") + "." + extension);
        File.WriteAllBytes(Path.Combine(_media.Folder, file), content);
        var item = new PortalMediaItem { Name = "pic", Kind = kind, File = file, Bytes = content.Length };
        _media.Put(item);
        return item;
    }

    /// <summary>A custom widget with a text field placed in the given HTML and JS.</summary>
    private Guid CustomWidget(string html, string js, string fields = """{"title": {"type": "text", "label": "Title", "value": "Hi"}}""")
    {
        var layer = _library.AddCustomWidget(_overlay.Id, "Mine", new WidgetCode(html, "", js, fields, "{}"))!;
        return layer.ElementId!.Value;
    }

    [Fact]
    public async Task Roles_without_overlay_rights_see_nothing()
    {
        var standard = Member(DefaultRoles.StandardModId);
        Assert.Contains("overlays", Error(await Call(standard, "overlays.list")));
        Assert.Contains("overlays", Error(await Call(standard, "overlays.get", new JsonObject { ["id"] = _overlay.Id.ToString() })));
    }

    [Fact]
    public async Task A_lead_mod_moves_and_restyles_a_layer_and_the_link_never_reaches_the_portal()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var list = await Call(lead, "overlays.list");
        Ok(list);
        Assert.False((bool)list["canCreate"]!);
        var get = await Call(lead, "overlays.get", new JsonObject { ["id"] = _overlay.Id.ToString() });
        Assert.DoesNotContain(_overlay.Token, get.ToJsonString(), StringComparison.Ordinal);

        var overlay = await GetOverlay(lead);
        var layer = (JsonObject)((JsonArray)overlay["layers"]!)[0]!;
        layer["x"] = 12;
        layer["visible"] = false;
        layer["settings"]!["text"] = "Back in 5";
        layer["settings"]!["color"] = "#ff0000";
        Ok(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay }));

        var saved = _overlays.Get(_overlay.Id)!;
        Assert.Equal(_overlay.Token, saved.Token);
        Assert.Equal(12, saved.Layers[0].X);
        Assert.False(saved.Layers[0].Visible);
        Assert.Equal("Back in 5", (string?)saved.Layers[0].Settings!["text"]);
        var mine = await Call(lead, "changes.mine");
        Assert.DoesNotContain(_overlay.Token, mine.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_cannot_add_remove_or_resize_and_bad_values_are_refused()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var overlay = await GetOverlay(lead);
        ((JsonArray)overlay["layers"]!).Add(new JsonObject { ["type"] = "image" });
        Assert.Contains("add layers", Error(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay })));

        overlay = await GetOverlay(lead);
        ((JsonArray)overlay["layers"]!).Clear();
        Assert.Contains("remove layers", Error(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay })));

        overlay = await GetOverlay(lead);
        overlay["width"] = 1280;
        Assert.Contains("size", Error(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay })));

        foreach (var (key, value) in new (string, JsonNode)[] { ("background", "url(https://example.com/x.png)"), ("font", "x;}body{"), ("weight", "heavy"), ("evil", "1") })
        {
            overlay = await GetOverlay(lead);
            ((JsonArray)overlay["layers"]!)[0]!["settings"]![key] = value.DeepClone();
            Error(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay }));
        }
        overlay = await GetOverlay(lead);
        ((JsonArray)overlay["layers"]!)[0]!["enter"] = new JsonObject { ["name"] = "explode", ["duration"] = 1 };
        Assert.Contains("animation", Error(await Call(lead, "overlays.save", new JsonObject { ["overlay"] = overlay })));
        Assert.Equal("Your text here", (string?)_overlays.Get(_overlay.Id)!.Layers[0].Settings!["text"]);
        Assert.Contains("does not include", Error(await Call(lead, "overlays.create", new JsonObject { ["name"] = "New" })));
    }

    [Fact]
    public async Task Pictures_must_be_uploads_and_an_upload_becomes_an_overlay_file()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var png = Upload(UploadKind.Image, "png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        foreach (var address in new[] { "https://example.com/a.png", "C:/Users/kris/secret.png", "/assets/" + new string('a', 64) + ".png", "upload:" + Guid.NewGuid() })
        {
            var bad = await GetOverlay(tech);
            ((JsonArray)bad["layers"]!).Add(new JsonObject { ["type"] = "image", ["settings"] = new JsonObject { ["url"] = address } });
            Assert.Contains("uploads", Error(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = bad })));
        }

        var overlay = await GetOverlay(tech);
        ((JsonArray)overlay["layers"]!).Add(new JsonObject { ["type"] = "image", ["name"] = "Logo", ["settings"] = new JsonObject { ["url"] = "upload:" + png.Id } });
        Ok(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = overlay }));
        var image = _overlays.Get(_overlay.Id)!.Layers.Single(l => l.Type == OverlayLayerTypes.Image);
        var url = (string)image.Settings!["url"]!;
        Assert.StartsWith("/assets/", url, StringComparison.Ordinal);
        Assert.NotNull(_assets.PathOf(url["/assets/".Length..]));
        // The portal sees the upload, not the file.
        var shown = ((JsonArray)(await GetOverlay(tech))["layers"]!).Single(l => (string?)l!["type"] == "image")!;
        Assert.Equal("upload:" + png.Id, (string?)shown["settings"]!["url"]);
        // A sound is not a picture.
        var mp3 = Upload(UploadKind.Sound, "mp3", [1, 2, 3]);
        overlay = await GetOverlay(tech);
        ((JsonArray)overlay["layers"]!).Single(l => (string?)l!["type"] == "image")!["settings"]!["url"] = "upload:" + mp3.Id;
        Assert.Contains("right kind", Error(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = overlay })));
    }

    [Fact]
    public async Task A_tech_lead_adds_cuebots_widgets_and_the_code_is_made_when_it_goes_live()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var overlay = await GetOverlay(tech);
        ((JsonArray)overlay["layers"]!).Add(new JsonObject { ["type"] = "custom", ["widget"] = "goal", ["x"] = 40, ["y"] = 40 });
        Ok(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = overlay }));
        var layer = _overlays.Get(_overlay.Id)!.Layers.Single(l => l.Type == OverlayLayerTypes.Custom);
        var element = _elements.Get(layer.ElementId!.Value)!;
        Assert.Equal("goal", BuiltInWidgets.KindOf(element));
        Assert.Equal(BuiltInWidgets.For("goal")!.Js, _code.Read(element.Id).Js);

        // A widget cannot be made from code the mod sends, and an unknown kind is refused.
        overlay = await GetOverlay(tech);
        ((JsonArray)overlay["layers"]!).Add(new JsonObject { ["type"] = "custom", ["widget"] = "evil" });
        Error(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = overlay }));
        overlay = await GetOverlay(tech);
        ((JsonArray)overlay["layers"]!).Add(new JsonObject { ["type"] = "custom", ["elementId"] = Guid.NewGuid().ToString(), ["html"] = "<script>x()</script>" });
        Error(await Call(tech, "overlays.save", new JsonObject { ["overlay"] = overlay }));
    }

    [Fact]
    public async Task Widget_text_is_made_safe_to_paste_into_code()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var id = CustomWidget("<h1>{title}</h1>", "var title = \"{title}\";");
        var widget = await Widget(lead, id);
        Assert.True((bool)Field(widget, "title")["editable"]!);

        Ok(await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["title"] = "Kris's \"big\" goal & more" } }));
        Assert.Equal("Kris\u2019s \u201Cbig\u201D goal & more", (string?)WidgetCode.ParseObject(_code.Read(id).Data)["title"]);
        var substituted = WidgetFields.Substitute(_code.Read(id).Js, _code.Read(id).FieldData());
        Assert.Equal("var title = \"Kris\u2019s \u201Cbig\u201D goal & more\";", substituted);

        foreach (var bad in new[] { "<img src=x onerror=alert(1)>", "\"; alert(1); //\\", "&#60;b&#62;", "&lt;b&gt;", "javascript:alert(1)", " data:text/html,x", "{other}", "line\nbreak" })
            Error(await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["title"] = bad } }));
    }

    [Theory]
    [InlineData("<div>{title}</div>", "var t = {title};")]
    [InlineData("<img src={title}>", "")]
    [InlineData("<img src=\"x\" onerror=\"{title}\">", "")]
    [InlineData("<script>var t = {title};</script>", "")]
    [InlineData("", "var t = `${ {title} }`;")]
    [InlineData("", "var s = 'unclosed\n{title}';")]
    public async Task A_field_the_widget_pastes_where_no_text_is_safe_stays_the_streamers(string html, string js)
    {
        var lead = Member(DefaultRoles.LeadModId);
        var id = CustomWidget(html, js);
        var field = Field(await Widget(lead, id), "title");
        Assert.False((bool)field["editable"]!);
        Error(await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["title"] = "Hello" } }));
    }

    [Theory]
    [InlineData("<div title=\"{title}\">{{title}}</div><style>.a{content:'{title}'}</style>", "")]
    [InlineData("<script>var a = '{title}';</script>", "// {title}\nvar b = `x ${1 + 2} {title}`; /* {title} */ var c = { a: \"{title}\" };")]
    public async Task Fields_in_text_quoted_attributes_strings_and_comments_are_editable(string html, string js)
    {
        var lead = Member(DefaultRoles.LeadModId);
        var id = CustomWidget(html, js);
        Assert.True((bool)Field(await Widget(lead, id), "title")["editable"]!);
        Ok(await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["title"] = "Hello" } }));
    }

    [Fact]
    public async Task Only_settings_the_widget_has_and_mods_may_change_are_accepted()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var id = CustomWidget("<p>{title}</p>", "", """
            {"title": {"type": "text", "label": "Title", "value": "Hi"},
             "secret": {"type": "hidden", "value": "x"},
             "goal": {"type": "number", "label": "Goal", "value": 10, "min": 1, "max": 100},
             "look": {"type": "dropdown", "label": "Look", "value": "a", "options": {"a": "A", "b": "B"}},
             "color": {"type": "colorpicker", "label": "Colour", "value": "#fff"}}
            """);
        var fields = (JsonArray)(await Widget(lead, id))["fields"]!;
        Assert.DoesNotContain(fields, f => (string?)f!["key"] == "secret");
        foreach (var (key, value) in new (string, JsonNode)[] { ("secret", "y"), ("brandNew", "<script>"), ("goal", 1000), ("look", "c"), ("color", "red;background:url(x)") })
            Error(await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { [key] = value.DeepClone() } }));
        Ok(await Call(lead, "widgets.save", new JsonObject
        {
            ["id"] = id.ToString(), ["data"] = new JsonObject { ["goal"] = 50, ["look"] = "b", ["color"] = "rgba(0, 0, 0, 0.5)", ["secret"] = "x" },
        }));
        var data = WidgetCode.ParseObject(_code.Read(id).Data);
        Assert.Equal(50, (double)data["goal"]!);
        Assert.Equal("b", (string?)data["look"]);
        Assert.False(data.ContainsKey("secret"));
        Assert.Equal("<p>{title}</p>", _code.Read(id).Html);
    }

    [Fact]
    public async Task Alert_designs_can_be_edited_but_not_when_they_are_picked()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var layer = _library.AddAlertBox(_overlay.Id)!;
        var id = layer.ElementId!.Value;
        var sound = Upload(UploadKind.Sound, "mp3", [9, 9, 9]);
        var alerts = (await Widget(tech, id))["alerts"]!.DeepClone().AsObject();
        alerts["types"]!["follower"]!["design"]!["template"] = "{name} joined the crew!";
        alerts["types"]!["follower"]!["design"]!["sound"] = "upload:" + sound.Id;
        alerts["types"]!["tip"]!["enabled"] = false;
        Ok(await Call(tech, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["alerts"] = alerts } }));
        var saved = (JsonObject)_code.Read(id).FieldData()["alerts"]!;
        Assert.Equal("{name} joined the crew!", (string?)saved["types"]!["follower"]!["design"]!["template"]);
        Assert.StartsWith("/assets/", (string?)saved["types"]!["follower"]!["design"]!["sound"], StringComparison.Ordinal);
        Assert.False((bool)saved["types"]!["tip"]!["enabled"]!);
        Assert.Equal("upload:" + sound.Id, (string?)((JsonObject)(await Widget(tech, id))["alerts"]!)["types"]!["follower"]!["design"]!["sound"]);

        alerts = (await Widget(tech, id))["alerts"]!.DeepClone().AsObject();
        ((JsonArray)alerts["types"]!["follower"]!["variations"]!).Add(new JsonObject { ["name"] = "Big", ["enabled"] = true });
        Assert.Contains("variations", Error(await Call(tech, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["alerts"] = alerts } })));
        alerts = (await Widget(tech, id))["alerts"]!.DeepClone().AsObject();
        alerts["types"]!["follower"]!["design"]!["sound"] = "https://example.com/loud.mp3";
        Assert.Contains("uploads", Error(await Call(tech, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["alerts"] = alerts } })));
    }

    [Fact]
    public async Task Changes_wait_for_approval_and_undo_puts_them_back()
    {
        var helper = Approver();
        var overlay = await GetOverlay(helper);
        ((JsonArray)overlay["layers"]!)[0]!["settings"]!["text"] = "Pending text";
        var answer = await Call(helper, "overlays.save", new JsonObject { ["overlay"] = overlay });
        Ok(answer);
        Assert.Equal("pending", (string?)answer["status"]);
        Assert.Equal("Your text here", (string?)_overlays.Get(_overlay.Id)!.Layers[0].Settings!["text"]);
        var change = Assert.Single(_changes.Pending);
        Assert.Equal(ChangeResult.Applied, await _changes.ApproveAsync(change.Id));
        Assert.Equal("Pending text", (string?)_overlays.Get(_overlay.Id)!.Layers[0].Settings!["text"]);
        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(change.Id));
        Assert.Equal("Your text here", (string?)_overlays.Get(_overlay.Id)!.Layers[0].Settings!["text"]);
        Assert.Equal(_overlay.Token, _overlays.Get(_overlay.Id)!.Token);
    }

    [Fact]
    public async Task A_deleted_overlay_comes_back_with_the_same_link()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var created = await Call(tech, "overlays.create", new JsonObject { ["name"] = "Vertical", ["width"] = 1080, ["height"] = 1920 });
        Ok(created);
        var made = _overlays.Get(Guid.Parse((string)created["id"]!))!;
        Assert.Equal(1920, made.Height);

        var delete = await Call(tech, "overlays.delete", new JsonObject { ["id"] = _overlay.Id.ToString() });
        Ok(delete);
        Assert.Null(_overlays.Get(_overlay.Id));
        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(Guid.Parse((string)delete["change"]!)));
        var back = _overlays.Get(_overlay.Id)!;
        Assert.Equal(_overlay.Token, back.Token);
        Assert.Single(back.Layers);
    }

    [Fact]
    public async Task Saving_what_is_already_there_records_no_change()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var id = CustomWidget("<p>{title}</p>", "");
        var answer = await Call(lead, "overlays.save", new JsonObject { ["overlay"] = await GetOverlay(lead) });
        Assert.Equal("unchanged", (string?)answer["status"]);
        answer = await Call(lead, "widgets.save", new JsonObject { ["id"] = id.ToString(), ["data"] = new JsonObject { ["title"] = "Hi" } });
        Assert.Equal("unchanged", (string?)answer["status"]);
        Assert.Empty(_changes.All);
    }

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#a1b2c3d4", true)]
    [InlineData("rgba(10, 20, 30, 0.5)", true)]
    [InlineData("hsl(120deg 50% 50%)", true)]
    [InlineData("rebeccapurple", true)]
    [InlineData("red;background:url(x)", false)]
    [InlineData("url(https://x)", false)]
    [InlineData("expression(alert(1))", false)]
    public void Colours(string value, bool ok) => Assert.Equal(ok, OverlayValues.IsColor(value));
}
