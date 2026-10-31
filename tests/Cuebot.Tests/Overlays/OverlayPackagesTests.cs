using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Sharing;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Tests.Overlays;

public sealed class OverlayPackagesTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly OverlayStore _overlays;
    private readonly ElementStore _elements;
    private readonly WidgetCodeStore _code = new(null);
    private readonly AssetStore _assets = new(null);
    private readonly WidgetLibrary _library;
    private readonly OverlayPackages _packages;

    public OverlayPackagesTests()
    {
        _overlays = new OverlayStore(null, _log);
        _elements = new ElementStore(null, _log);
        _library = new WidgetLibrary(_overlays, _elements, _code);
        _packages = new OverlayPackages(_overlays, _elements, _code, _assets, _library);
    }

    public void Dispose()
    {
        _overlays.Dispose();
        _elements.Dispose();
        _code.Dispose();
        _assets.Dispose();
    }

    /// <summary>An overlay with an alert box (designs using an uploaded sound), a label, a bought widget and an image layer.</summary>
    private async Task<(Overlay Overlay, string Sound, string Image)> SampleAsync()
    {
        var sound = await _assets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("pretend mp3")), "ding.mp3");
        var image = await _assets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("pretend png")), "logo.png");
        var overlay = _overlays.Create("Main scene", 1280, 720);
        var alert = _library.AddAlertBox(overlay.Id, new JsonObject { ["alerts"] = new JsonObject { ["sound"] = "/assets/" + sound } })!;
        _library.AddBuiltIn(overlay.Id, "label", new JsonObject { ["prefix"] = "Latest: " });
        var bought = _library.AddCustomWidget(overlay.Id, "Bought chat", new WidgetCode("<div id=bought></div>", "", "/* paid */", "{}", "{}"))!;
        _library.MoveLayer(overlay.Id, bought.Id, 10, 20, 300, 400);
        var picture = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Image, overlay);
        picture.Settings = new JsonObject { ["url"] = "/assets/" + image, ["fit"] = "cover" };
        picture.Rotation = 15;
        picture.Enter = new LayerAnimation { Name = "fadeIn", Duration = 2 };
        _overlays.Update(overlay.Id, o => o.Layers.Add(picture));
        _overlays.Update(overlay.Id, o => o.Layers.Single(l => l.Id == alert.Id).Opacity = 0.5);
        return (_overlays.Get(overlay.Id)!, sound, image);
    }

    private static async Task<MemoryStream> Bytes(Action<Stream> write)
    {
        var stream = new MemoryStream();
        write(stream);
        await stream.FlushAsync();
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task An_overlay_round_trips_with_its_widgets_settings_and_files_but_not_bought_code_unless_confirmed()
    {
        var (overlay, sound, image) = await SampleAsync();
        Assert.Equal(["Bought chat"], _packages.CustomWidgets(overlay.Id));

        PackageExportResult exported = null!;
        using var package = await Bytes(s => exported = _packages.ExportOverlay(overlay.Id, s, includeCustomCode: false));
        Assert.Equal((2, 2), (exported.Widgets, exported.Assets));
        Assert.Equal(["Bought chat"], exported.LeftOut);
        using (var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "assets/" + sound);
            Assert.Contains(zip.Entries, e => e.FullName == "assets/" + image);
            Assert.DoesNotContain(zip.Entries, e => new StreamReader(e.Open()).ReadToEnd().Contains("/* paid */"));
        }
        package.Position = 0;

        var result = await _packages.ImportAsync(package);
        Assert.Equal(("overlay", 3), (result.Kind, result.Layers));
        Assert.Equal(["Bought chat (shared without its code; add the widget yourself)"], result.LeftOut);
        var copy = result.Overlay;
        Assert.NotEqual(overlay.Id, copy.Id);
        Assert.NotEqual(overlay.Token, copy.Token);
        Assert.Equal(("Main scene (2)", 1280, 720), (copy.Name, copy.Width, copy.Height));   // the original is still there
        Assert.Equal(["Alert box", "Label", "Image"], copy.Layers.Select(l => l.Name));

        var alert = copy.Layers[0];
        Assert.Equal(0.5, alert.Opacity);
        Assert.True(BuiltInWidgets.IsAlertBox(_elements.Get(alert.ElementId!.Value)));
        Assert.Equal("/assets/" + sound, _library.CodeFor(alert).FieldData()["alerts"]!["sound"]!.GetValue<string>());
        Assert.Equal("Latest: ", _library.CodeFor(copy.Layers[1]).FieldData()["prefix"]!.GetValue<string>());
        var picture = copy.Layers[2];
        Assert.Equal(("/assets/" + image, 15.0, "fadeIn", 2.0), (picture.Settings!["url"]!.GetValue<string>(), picture.Rotation, picture.Enter.Name, picture.Enter.Duration));
    }

    [Fact]
    public async Task Confirmed_custom_code_travels_and_a_widget_package_is_placed_on_another_overlay()
    {
        var (overlay, _, _) = await SampleAsync();
        var bought = overlay.Layers.Single(l => l.Name == "Bought chat");
        using var package = await Bytes(s => _packages.ExportWidget(overlay.Id, bought.Id, s, includeCustomCode: true));

        var target = _overlays.Create("Other", 1920, 1080);
        await Assert.ThrowsAsync<ArgumentException>(() => _packages.ImportAsync(package));
        package.Position = 0;
        var result = await _packages.ImportAsync(package, target.Id);
        Assert.Equal(("widget", 1, 0), (result.Kind, result.Layers, result.LeftOut.Count));
        var layer = Assert.Single(_overlays.Get(target.Id)!.Layers);
        Assert.Equal(("Bought chat", 10.0, 20.0, 300.0, 400.0), (layer.Name, layer.X, layer.Y, layer.Width, layer.Height));
        Assert.Equal("/* paid */", _library.CodeFor(layer).Js);
    }

    [Fact]
    public async Task A_package_cannot_give_its_own_code_the_alert_box_or_media_player_abilities()
    {
        var target = _overlays.Create("Target", 1920, 1080);
        using var package = await Bytes(s =>
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true);
            Write(zip, "package.json", """
                {"format":"cuebot-overlay","version":1,"kind":"overlay","name":"Sneaky","width":800,"height":600,
                 "layers":[{"name":"Alerts","type":"custom","widget":"w1","x":0,"y":0,"width":800,"height":600},
                           {"name":"Player","type":"custom","widget":"w2"},
                           {"name":"Odd","type":"custom","widget":"../w3"}],
                 "widgets":{"w1":{"builtIn":"alertbox"},"w2":{"builtIn":"media"},"../w3":{}},
                 "assets":["../../etc/passwd","0000000000000000000000000000000000000000000000000000000000000000.png"]}
                """);
            Write(zip, "widgets/w1/widget.js", "steal()");
            Write(zip, "widgets/w1/data.json", """{"alerts":{"randomPick":true}}""");
            Write(zip, "widgets/w2/widget.js", "steal()");
            Write(zip, "assets/0000000000000000000000000000000000000000000000000000000000000000.png", "not that hash");
        });
        var result = await _packages.ImportAsync(package, target.Id);
        Assert.Equal(2, result.Layers);
        Assert.Contains("Odd (not readable)", result.LeftOut);
        Assert.Contains(result.LeftOut, l => l.StartsWith("file 0000", StringComparison.Ordinal));
        foreach (var layer in result.Overlay.Layers)
        {
            var code = _library.CodeFor(layer);
            Assert.DoesNotContain("steal()", code.Js);
        }
        Assert.Equal(BuiltInWidgets.AlertBox().Js, _library.CodeFor(result.Overlay.Layers[0]).Js);
        Assert.True(_library.CodeFor(result.Overlay.Layers[0]).FieldData()["alerts"]!["randomPick"]!.GetValue<bool>());   // settings kept
    }

    [Fact]
    public async Task Unreadable_packages_are_refused_and_leave_nothing_behind()
    {
        using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        await Assert.ThrowsAsync<PackageFormatException>(() => _packages.ImportAsync(notZip));

        using var other = await Bytes(s => { using var zip = new ZipArchive(s, ZipArchiveMode.Create, true); Write(zip, "package.json", """{"format":"something"}"""); });
        await Assert.ThrowsAsync<PackageFormatException>(() => _packages.ImportAsync(other));

        using var newer = await Bytes(s => { using var zip = new ZipArchive(s, ZipArchiveMode.Create, true); Write(zip, "package.json", """{"format":"cuebot-overlay","version":99}"""); });
        var error = await Assert.ThrowsAsync<PackageFormatException>(() => _packages.ImportAsync(newer));
        Assert.Contains("newer Cuebot", error.Message);

        using var huge = await Bytes(s =>
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Create, true);
            Write(zip, "package.json", """{"format":"cuebot-overlay","version":1,"layers":[{"name":"Big","type":"custom","widget":"w1"}],"widgets":{"w1":{}}}""");
            Write(zip, "widgets/w1/widget.js", new string('x', (int)WidgetCode.MaxBytes + 1));
        });
        await Assert.ThrowsAsync<PackageFormatException>(() => _packages.ImportAsync(huge));
        Assert.Empty(_overlays.All());
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(text);
    }
}
