using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Tests.Overlays;

public sealed class StreamElementsOverlayImportTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly OverlayStore _overlays;
    private readonly ElementStore _elements;
    private readonly WidgetCodeStore _code = new(null);
    private readonly WidgetLibrary _library;

    public StreamElementsOverlayImportTests()
    {
        _overlays = new OverlayStore(null, _log);
        _elements = new ElementStore(null, _log);
        _library = new WidgetLibrary(_overlays, _elements, _code);
    }

    public void Dispose()
    {
        _overlays.Dispose();
        _elements.Dispose();
        _code.Dispose();
    }

    // A StreamElements overlay in the shape the overlays API returns, written for this test.
    private const string Overlay = """
        {"_id":"ov1","name":"Main scene","settings":{"width":1280,"height":720,"name":"720p"},"widgets":[
          {"id":4,"type":"se-widget-alert-box","name":null,"visible":true,"locked":false,
           "css":{"z-index":5,"width":"600px","height":"400.5px","opacity":1,"top":"10px","left":"20px"},
           "variables":{"animation":{"in":"zoomIn","inDuration":1,"out":"spinOut","outDuration":1},
             "tip":{"enabled":true,"duration":7,"layout":"row","animation":{"in":"none","out":"none"},
               "text":{"message":"{name} tipped {currency}{amount}!","css":{"font-family":"Nunito","font-size":30,"color":"#ffffff",
                 "highlights":{"color":"rgb(255, 12, 117)"},"message":{"font-size":18}}},
               "graphics":{"src":"https://cdn.example/tip.webm","type":"video","volume":0.4},
               "audio":{"src":"https://cdn.example/tip.mp3","volume":0.8},"tts":{"enabled":true},
               "variations":[
                 {"name":"Small","type":"amount","condition":"ATLEAST","requirement":5,"enabled":true,"chance":100,
                  "settings":{"duration":5,"text":{"message":"{name} small tip"},"graphics":{"src":""},"audio":{"src":""}}},
                 {"name":"Big","type":"amount","condition":"ATLEAST","requirement":50,"enabled":true,"chance":100,
                  "settings":{"duration":12,"text":{"message":"{name} BIG tip"},"graphics":{"src":"https://cdn.example/big.gif","type":"image"},"audio":{"src":""}}}]},
             "subscriber":{"enabled":false,"duration":10,"text":{"message":"{name} subscribed"},
               "variations":[{"name":"Gift","type":"gift","condition":"EXACT","requirement":null,"enabled":true,"settings":{"text":{"message":"{sender} gifted {name}"}}},
                             {"name":"Odd","type":"something","condition":"ATLEAST","requirement":1}]}}},
          {"id":2,"type":"follower-latest","listener":"follower-latest","visible":false,
           "css":{"z-index":2,"width":"auto","height":"auto","opacity":0.5,"top":"690px","left":"4px"},
           "text":{"value":"Latest follower: {name}!","enableShadow":true,
             "css":{"font-family":"Orbitron","font-size":24,"color":"#ff0","font-weight":"bold","text-align":"left"}},
           "variables":{"noData":"Latest follower: nobody yet"}},
          {"id":3,"type":"se-widget-tip-session","listener":"tip-session",
           "css":{"z-index":3,"top":0,"left":0},"text":{"value":"Tips: {amount}","css":{"font-size":20}}},
          {"id":1,"type":"se-widget-custom-event-list","name":"My chat","version":1,"visible":true,
           "css":{"z-index":1,"width":"464.99px","height":"597.93px","opacity":1,"top":"27.18px","left":"27.6px","transform":"rotate(12.5deg)"},
           "variables":{"html":"<div class='c'>{title}</div>","css":".c{color:{color}}","js":"window.addEventListener('onWidgetLoad', function () {});",
             "fields":"{\"title\":{\"type\":\"text\",\"value\":\"Hi\"},\"color\":{\"type\":\"colorpicker\",\"value\":\"#000\"}}",
             "fieldData":{"title":"Hello","color":"#123456"}}},
          {"id":6,"type":"text","css":{"z-index":6,"width":"300px","height":"50px","top":"100px","left":"100px"},
           "text":{"value":"Be nice","enableShadow":false,"css":{"font-family":"Nunito","font-size":20,"color":"rgb(0, 0, 0)","font-weight":"400","text-align":"center"}}},
          {"id":7,"type":"image","css":{"z-index":7,"width":"64px","height":"64px","top":"0px","left":"0px"},"image":{"src":"https://cdn.example/logo.png"}},
          {"id":8,"type":"se-widget-follower-goal","listener":"follower-goal","css":{"z-index":8}},
          {"id":9,"type":"se-widget-kappagen","css":{"z-index":9}}
        ]}
        """;

    private (StreamElementsImportResult Result, Overlay Overlay) Run()
    {
        var result = StreamElementsOverlayImport.Import(JsonNode.Parse(Overlay)!.AsObject(), _overlays, _library);
        return (result, _overlays.Get(result.Overlay.Id)!);
    }

    [Fact]
    public void The_overlay_keeps_its_canvas_and_its_layers_in_stacking_order()
    {
        var (result, overlay) = Run();
        Assert.Equal(("Main scene", 1280, 720), (overlay.Name, overlay.Width, overlay.Height));
        Assert.Equal(6, result.Imported);
        Assert.Equal(["se-widget-follower-goal (goals are not imported yet; add a Cuebot goal)", "se-widget-kappagen (not supported)"], result.Skipped);
        Assert.Equal(["My chat", "Label", "Label", "Alert box", "Text", "Image"], overlay.Layers.Select(l => l.Name));
    }

    [Fact]
    public void A_custom_widget_keeps_its_code_settings_place_and_rotation()
    {
        var (_, overlay) = Run();
        var layer = overlay.Layers[0];
        Assert.Equal((27.6, 27.18, 464.99, 597.93, 12.5), (layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation));
        var code = _library.CodeFor(layer);
        Assert.Equal("<div class='c'>{title}</div>", code.Html);
        var data = code.FieldData();
        Assert.Equal(("Hello", "#123456"), (data["title"]!.GetValue<string>(), data["color"]!.GetValue<string>()));
    }

    [Fact]
    public void Labels_become_cuebot_labels_with_their_text_and_look()
    {
        var (_, overlay) = Run();
        var follower = overlay.Layers[1];
        Assert.Equal((4.0, 690.0, 0.5, false), (follower.X, follower.Y, follower.Opacity, follower.Visible));
        Assert.Equal(38, follower.Height);   // "auto": one line of 24px text
        Assert.Equal("label", BuiltInWidgets.KindOf(_elements.Get(follower.ElementId!.Value)));
        var f = _library.CodeFor(follower).FieldData();
        Assert.Equal(("follower", "latest", "Latest follower: ", "!", "nobody yet"),
            (S(f["type"]), S(f["show"]), S(f["prefix"]), S(f["suffix"]), S(f["emptyText"])));
        Assert.Equal(("Orbitron", 24, "700", "#ff0", true), (S(f["fontFamily"]), f["fontSize"]!.GetValue<int>(), S(f["fontWeight"]), S(f["textColor"]), f["shadow"]!.GetValue<bool>()));

        var tips = _library.CodeFor(overlay.Layers[2]).FieldData();
        Assert.Equal(("tip", "amount", "session", "Tips: "), (S(tips["type"]), S(tips["show"]), S(tips["period"]), S(tips["prefix"])));
    }

    [Fact]
    public void The_alert_box_gets_each_types_design_and_its_variations_biggest_first()
    {
        var (_, overlay) = Run();
        var layer = overlay.Layers[3];
        Assert.Equal((20.0, 10.0, 600.0, 400.5), (layer.X, layer.Y, layer.Width, layer.Height));
        Assert.True(BuiltInWidgets.IsAlertBox(_elements.Get(layer.ElementId!.Value)));
        var types = _library.CodeFor(layer).FieldData()["alerts"]!["types"]!;

        var tip = types["tip"]!;
        var design = tip["design"]!;
        Assert.Equal("{name} tipped {formattedAmount}!", S(design["template"]));
        Assert.Equal(("https://cdn.example/tip.webm", 40, "https://cdn.example/tip.mp3", 80), (S(design["media"]), I(design["mediaVolume"]), S(design["sound"]), I(design["volume"])));
        Assert.Equal(("side", "Nunito", 30, "rgb(255, 12, 117)", 18), (S(design["layout"]), S(design["font"]), I(design["size"]), S(design["highlight"]), I(design["messageSize"])));
        // The type's own animation is "none", so the alert box's is used; one Cuebot does not have falls back.
        Assert.Equal(("zoomIn", "fadeOut", true, 7.0), (S(design["enter"]), S(design["exit"]), design["tts"]!.GetValue<bool>(), design["duration"]!.GetValue<double>()));

        var variations = tip["variations"]!.AsArray();
        Assert.Equal(["Big", "Small"], variations.Select(v => S(v!["name"])));
        Assert.Equal(("amount", "atLeast", 50.0), (S(variations[0]!["param"]), S(variations[0]!["op"]), variations[0]!["value"]!.GetValue<double>()));
        Assert.Equal(("{name} BIG tip", "https://cdn.example/big.gif", "none"), (S(variations[0]!["design"]!["template"]), S(variations[0]!["design"]!["media"]), S(variations[0]!["design"]!["sound"])));

        var sub = types["subscriber"]!;
        Assert.False(sub["enabled"]!.GetValue<bool>());
        var gift = Assert.Single(sub["variations"]!.AsArray())!;   // the unknown kind is left out
        Assert.Equal(("gifted", "is", true, "{sender} gifted {name}"), (S(gift["param"]), S(gift["op"]), gift["value"]!.GetValue<bool>(), S(gift["design"]!["template"])));

        Assert.NotNull(types["follower"]);   // types StreamElements did not have keep Cuebot's defaults
    }

    [Fact]
    public void Text_and_image_layers_become_static_layers()
    {
        var (_, overlay) = Run();
        var text = overlay.Layers[4];
        Assert.Equal((OverlayLayerTypes.Text, 100.0, 100.0, 300.0, 50.0), (text.Type, text.X, text.Y, text.Width, text.Height));
        Assert.Equal(("Be nice", "Nunito", 20, "rgb(0, 0, 0)", "400", "center", false),
            (S(text.Settings!["text"]), S(text.Settings["font"]), I(text.Settings["size"]), S(text.Settings["color"]), S(text.Settings["weight"]),
             S(text.Settings["align"]), text.Settings["shadow"]!.GetValue<bool>()));
        var image = overlay.Layers[5];
        Assert.Equal((OverlayLayerTypes.Image, "https://cdn.example/logo.png", 64.0), (image.Type, S(image.Settings!["url"]), image.Width));
    }

    [Fact]
    public void The_overlay_list_is_read_from_docs_or_an_array()
    {
        var list = StreamElementsOverlayImport.ParseList(JsonNode.Parse("""
            {"docs":[{"_id":"a","name":"One","settings":{"width":1920,"height":1080}},{"_id":"b","name":"","settings":{}},{"name":"no id"}]}
            """));
        Assert.Equal([new("a", "One", 1920, 1080), new StreamElementsOverlaySummary("b", "Overlay", 1920, 1080)], list);
        Assert.Single(StreamElementsOverlayImport.ParseList(JsonNode.Parse("""[{"_id":"x","name":"X"}]""")));
    }

    private static string S(JsonNode? node) => node!.GetValue<string>();
    private static int I(JsonNode? node) => (int)OverlayAlerts.NumberOf(node);
}
