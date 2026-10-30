using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Tests.Overlays;

public sealed class WidgetCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-widgets-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private const string Fields = """
        {"color":{"type":"colorpicker","label":"Color","value":"#ff0000"},
         "size":{"type":"number","value":18},
         "show":{"type":"checkbox","value":true},
         "go":{"type":"button","label":"Test"}}
        """;

    [Fact]
    public void Field_data_is_defaults_replaced_by_saved_values()
    {
        var code = new WidgetCode("", "", "", Fields, """{"size":24,"extra":"kept"}""");
        var data = code.FieldData();
        Assert.Equal("#ff0000", data["color"]!.GetValue<string>());
        Assert.Equal(24, data["size"]!.GetValue<int>());
        Assert.True(data["show"]!.GetValue<bool>());
        Assert.Equal("kept", data["extra"]!.GetValue<string>());
        Assert.False(data.ContainsKey("go"));   // buttons have no value
        Assert.Empty(new WidgetCode("", "", "", "not json", "[1]").FieldData());
    }

    [Fact]
    public void Placeholders_are_filled_for_known_fields_only()
    {
        var data = new WidgetCode("", "", "", Fields, "{}").FieldData();
        var text = "color: {color}; size: {{size}}px; show: { show }; {{ show }}; function f() { return {unknown}; }";
        Assert.Equal("color: #ff0000; size: 18px; show: { show }; true; function f() { return {unknown}; }", WidgetFields.Substitute(text, data));
    }

    [Theory]
    [InlineData("""{"widgetDuration":7}""", 7)]
    [InlineData("""{"widgetDuration":"2.5"}""", 2.5)]
    [InlineData("""{"widgetDuration":-3}""", 0)]
    [InlineData("{}", 0)]
    public void Widget_duration_comes_from_the_reserved_field(string data, double expected) =>
        Assert.Equal(expected, WidgetCode.WidgetDuration(new WidgetCode("", "", "", "{}", data).FieldData()));

    [Fact]
    public void Widget_io_zips_follow_their_ini()
    {
        var code = WidgetImport.FromZip(Zip(
            ("widget.ini", "[HTML]\npath = \"html.txt\"\n\n[CSS]\npath = \"css.txt\"\n[JS]\npath = \"js.txt\"\n[FIELDS]\npath = \"fields.txt\"\n[DATA]\npath = \"data.txt\"\n"),
            ("html.txt", "<div class=\"main\"></div>"), ("css.txt", ".main{}"), ("js.txt", "window.x=1;"), ("fields.txt", Fields), ("data.txt", "{\"size\":30}")));

        Assert.Equal("<div class=\"main\"></div>", code.Html);
        Assert.Equal(".main{}", code.Css);
        Assert.Equal("window.x=1;", code.Js);
        Assert.Equal(30, code.FieldData()["size"]!.GetValue<int>());
    }

    [Fact]
    public void Zips_without_an_ini_are_read_by_pane_names_one_folder_down()
    {
        var code = WidgetImport.FromZip(Zip(("My Widget/HTML.html", "<p>hi</p>"), ("My Widget/style.css", "x"), ("My Widget/js.js", "go()"),
            ("My Widget/fields.json", Fields)));
        Assert.Equal(("<p>hi</p>", "", "go()"), (code.Html, code.Css, code.Js));   // "style.css" is not a pane name
        Assert.Equal("{}", code.Data);
        Assert.Equal(18, code.FieldData()["size"]!.GetValue<int>());
    }

    [Fact]
    public void Zips_without_a_widget_or_too_big_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => WidgetImport.FromZip(Zip(("readme.txt", "hello"))));
        Assert.Throws<InvalidDataException>(() => WidgetImport.FromZip(Zip(("html.txt", new string('x', WidgetCode.MaxBytes + 1)))));
        Assert.Throws<InvalidDataException>(() => WidgetImport.FromZip(new MemoryStream(Encoding.UTF8.GetBytes("not a zip"))));
        Assert.Throws<InvalidDataException>(() => WidgetImport.FromPanes("<p></p>", "", "", "[1,2]", ""));
        Assert.Equal("{}", WidgetImport.FromPanes("<p></p>", "", "", " ", "").Fields);
    }

    [Fact]
    public void Code_is_stored_per_element_and_survives_a_restart()
    {
        var paths = new AppPaths(_dir);
        var id = Guid.NewGuid();
        var changed = new List<Guid>();
        using (var store = new WidgetCodeStore(paths))
        {
            store.Changed += changed.Add;
            Assert.Same(WidgetCode.Empty, store.Read(id));
            store.Write(id, new WidgetCode("<b>é</b>", "c", "j", Fields, "{}") { CssOverride = ".x{}" });
        }
        using var again = new WidgetCodeStore(paths);
        Assert.Equal("<b>é</b>", again.Read(id).Html);
        Assert.Equal(".x{}", again.Read(id).CssOverride);
        Assert.Equal([id], changed);
        again.Delete(id);
        Assert.False(again.Exists(id));
    }

    [Fact]
    public void The_key_value_store_persists_and_has_limits()
    {
        var paths = new AppPaths(_dir);
        using (var store = new WidgetKeyValueStore(paths, _log))
        {
            var seen = new List<string>();
            store.Changed += (key, _) => seen.Add(key);
            Assert.True(store.Set("counter", new JsonObject { ["n"] = 3 }));
            Assert.False(store.Set("", 1));
            Assert.False(store.Set("big", new string('x', WidgetKeyValueStore.MaxValueBytes + 1)));
            Assert.Equal(["counter"], seen);
        }
        using var again = new WidgetKeyValueStore(paths, _log);
        Assert.Equal(3, again.Get("counter")!["n"]!.GetValue<int>());
        Assert.Null(again.Get("missing"));
    }

    [Fact]
    public void The_chat_box_ships_complete_and_editable()
    {
        var box = BuiltInWidgets.ChatBox();
        Assert.Contains("id=\"chat\"", box.Html);
        Assert.Contains("onEventReceived", box.Js);
        Assert.Contains("yt-live-chat-text-message-renderer", box.Js);   // the YouTube popout layout
        Assert.Contains("--twitch", box.Css);
        var fields = JsonNode.Parse(box.Fields)!.AsObject();
        Assert.All(fields, f => Assert.NotNull(f.Value!["type"]));
        var data = box.FieldData();
        Assert.Equal(("cuebot", 20), (data["layout"]!.GetValue<string>(), data["maxMessages"]!.GetValue<int>()));
        Assert.Equal("youtube", BuiltInWidgets.ChatBox("youtube").FieldData()["layout"]!.GetValue<string>());
        // Every placeholder in the panes is a field (none would be left as text).
        var substituted = WidgetFields.Substitute(box.Html + box.Css, data);
        Assert.DoesNotMatch(@"\{\{[A-Za-z]", substituted);
    }

    [Fact]
    public void The_chat_box_sits_in_the_lower_left_or_fills_a_vertical_canvas()
    {
        using var overlays = new OverlayStore(null, _log);
        using var elements = new ElementStore(null, _log);
        using var code = new WidgetCodeStore(null);
        var library = new WidgetLibrary(overlays, elements, code);

        var landscape = overlays.Create("Main", 1920, 1080);
        var box = library.AddChatBox(landscape.Id)!;
        Assert.Equal((20.0, 460.0, 480.0, 600.0), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal("Chat box", box.Name);

        var vertical = overlays.Create("Phone", 1080, 1920);
        var tall = library.AddChatBox(vertical.Id, "youtube")!;
        Assert.Equal((20.0, 1040.0, 920.0), (tall.X, tall.Width, tall.Height));
        Assert.Equal("youtube", library.CodeFor(tall).FieldData()["layout"]!.GetValue<string>());
        Assert.Null(library.AddChatBox(Guid.NewGuid()));
    }

    [Fact]
    public void Library_templates_are_copied_and_widgets_shared()
    {
        using var overlays = new OverlayStore(null, _log);
        using var elements = new ElementStore(null, _log);
        using var code = new WidgetCodeStore(null);
        var library = new WidgetLibrary(overlays, elements, code);
        var main = overlays.Create("Main");
        var other = overlays.Create("Other", 1280, 720);

        var text = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Text, main);
        text.Name = "BRB";
        text.Width = 900;
        text.Settings!["text"] = "Be right back";
        overlays.Update(main.Id, o => o.Layers.Add(text));
        var template = library.SaveToLibrary(main.Id, text.Id)!;
        Assert.Equal(("BRB", "text"), (template.Name, template.Type));

        var copy = library.AddFromLibrary(other.Id, template.Id)!;
        Assert.Equal(("Be right back", 900.0, 190.0), (copy.Settings!["text"]!.GetValue<string>(), copy.Width, copy.X));
        Assert.Null(copy.ElementId);   // a template is copied: changing it later does not touch this layer

        var widget = library.AddChatBox(main.Id)!;
        Assert.Null(library.SaveToLibrary(main.Id, widget.Id));   // widgets are in the library already
        var shared = library.AddFromLibrary(other.Id, widget.ElementId!.Value)!;
        Assert.Equal(widget.ElementId, shared.ElementId);

        var entry = library.Library().Single(e => e.Element.Id == widget.ElementId);
        Assert.Equal(["Main", "Other"], entry.UsedBy.Select(o => o.Name));
        Assert.Null(library.AddFromLibrary(other.Id, Guid.NewGuid()));
    }

    [Fact]
    public void Library_elements_are_renamed_duplicated_and_deleted_safely()
    {
        using var overlays = new OverlayStore(null, _log);
        using var elements = new ElementStore(null, _log);
        using var code = new WidgetCodeStore(null);
        var library = new WidgetLibrary(overlays, elements, code);
        var main = overlays.Create("Main");
        var layer = library.AddCustomWidget(main.Id, "Chat", new WidgetCode("<p>original</p>", "", "", "{}", "{}"))!;
        var id = layer.ElementId!.Value;

        Assert.True(library.Rename(id, "Pink chat"));
        Assert.False(library.Rename(id, " "));
        Assert.Equal("Pink chat", overlays.Get(main.Id)!.Layers.Single().Name);   // layers follow the widget's name

        var duplicate = library.Duplicate(id)!;
        Assert.Equal("Pink chat copy", duplicate.Name);
        code.Write(duplicate.Id, new WidgetCode("<p>changed</p>", "", "", "{}", "{}"));
        Assert.Equal("<p>original</p>", code.Read(id).Html);   // a separate copy of the code

        Assert.Equal(["Main"], library.Delete(id).Select(o => o.Name));   // still used: kept
        Assert.NotNull(elements.Get(id));
        Assert.Empty(library.Delete(duplicate.Id));
        Assert.Null(elements.Get(duplicate.Id));
        Assert.False(code.Exists(duplicate.Id));
    }

    [Fact]
    public void The_library_places_moves_and_removes_widgets()
    {
        using var overlays = new OverlayStore(null, _log);
        using var elements = new ElementStore(null, _log);
        using var code = new WidgetCodeStore(null);
        var library = new WidgetLibrary(overlays, elements, code);
        var overlay = overlays.Create("Main", 1280, 720);

        var layer = library.AddCustomWidget(overlay.Id, "Chat", new WidgetCode("<p></p>", "", "", "{}", "{}"))!;
        Assert.Equal((1280.0, 720.0, "custom"), (layer.Width, layer.Height, layer.Type));
        Assert.Equal("<p></p>", library.CodeFor(overlays.Get(overlay.Id)!.Layers.Single()).Html);
        Assert.Equal("Chat", elements.Get(layer.ElementId!.Value)!.Name);

        Assert.True(library.MoveLayer(overlay.Id, layer.Id, 10, 20, 400, 0));
        var moved = overlays.Get(overlay.Id)!.Layers.Single();
        Assert.Equal((10.0, 20.0, 400.0, 1.0), (moved.X, moved.Y, moved.Width, moved.Height));

        Assert.True(library.RemoveLayer(overlay.Id, layer.Id));
        Assert.Empty(overlays.Get(overlay.Id)!.Layers);
        Assert.NotNull(elements.Get(layer.ElementId!.Value));   // stays in the library
        Assert.Null(library.AddCustomWidget(Guid.NewGuid(), "x", WidgetCode.Empty));
    }
}
