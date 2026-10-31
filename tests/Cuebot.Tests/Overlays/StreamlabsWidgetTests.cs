using System.Text.Json.Nodes;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Tests.Overlays;

public sealed class StreamlabsWidgetTests
{
    private const string ChatHtml = """
        <div id="log" class="sl__chat__layout"></div>
        <script type="text/template" id="chatlist_item"><div data-id="{messageId}"><span class="name">{from}</span>{message}</div></script>
        """;

    private const string ChatJs = "document.addEventListener('onEventReceived', function (obj) { });";

    [Theory]
    [InlineData(ChatHtml, ChatJs, "chatbox")]
    [InlineData("<script type=\"text/template\" id=\"eventlist_item\"><li>{from}</li></script>", "", "eventlist")]
    [InlineData("<div id=\"alert-wrap\"><div id=\"inner-wrap\">{messageTemplate}</div></div>", "$(document).ready(() => {});", "alertbox")]
    [InlineData("<div id=\"title\"></div>", "document.addEventListener('goalLoad', function (obj) {});", "goal")]
    [InlineData("<div></div>", "document.addEventListener(\"bossLoad\", function (obj) {});", "generic")]
    [InlineData("<div></div>", "window.addEventListener('onWidgetLoad', function (obj) {});", null)]
    [InlineData(ChatHtml, "window.addEventListener('onWidgetLoad', function () {});", null)]   // StreamElements code using the same ids
    [InlineData("<div></div>", "", null)]
    public void Streamlabs_code_is_recognised_by_its_template_ids_and_document_events(string html, string js, string? expected) =>
        Assert.Equal(expected, StreamlabsWidget.Detect(html, js));

    [Fact]
    public void Adapting_converts_custom_fields_adds_the_built_in_settings_and_marks_the_format()
    {
        const string fields = """
            {"customColor":{"label":"Color","type":"colorpicker","value":"#ff0000"},
             "customText":{"label":"Text","type":"textfield","value":"hi"},
             "customFont":{"label":"Font","type":"fontpicker","value":"Roboto"},
             "customSize":{"label":"Size","type":"slider","min":10,"max":40,"steps":2,"value":20},
             "font_size":{"label":"Mine","type":"textfield","value":"30px"}}
            """;
        var code = WidgetImport.FromPanes(ChatHtml, "body { font-size: {font_size}; }", ChatJs, fields, "");
        var f = JsonNode.Parse(code.Fields)!.AsObject();

        Assert.Equal("colorpicker", f["customColor"]!["type"]!.GetValue<string>());
        Assert.Equal("text", f["customText"]!["type"]!.GetValue<string>());
        Assert.Equal("googleFont", f["customFont"]!["type"]!.GetValue<string>());
        Assert.Equal(2, f["customSize"]!["step"]!.GetValue<int>());
        Assert.Null(f["customSize"]!["steps"]);
        Assert.Equal("30px", f["font_size"]!["value"]!.GetValue<string>());   // the widget's own field wins
        Assert.Equal("#ffffff", f["text_color"]!["value"]!.GetValue<string>());
        Assert.Equal(("hidden", "streamlabs"), (f["widgetFormat"]!["type"]!.GetValue<string>(), f["widgetFormat"]!["value"]!.GetValue<string>()));
        Assert.Equal("chatbox", f["widgetSlType"]!["value"]!.GetValue<string>());

        var data = code.FieldData();
        Assert.True(StreamlabsWidget.IsStreamlabs(data));
        Assert.Equal("body { font-size: 30px; }", WidgetFields.Substitute(code.Css, data));

        // Saving again leaves it as it is, even when a field was changed or removed since.
        var again = WidgetImport.FromPanes(code.Html, code.Css, code.Js, code.Fields.Replace("\"text_color\"", "\"old_color\""), "");
        Assert.Null(JsonNode.Parse(again.Fields)!["text_color"]);
    }

    [Fact]
    public void An_alert_box_gets_a_duration_and_message_templates_and_streamelements_code_is_untouched()
    {
        var alert = WidgetImport.FromPanes("<div id=\"alert-wrap\">{messageTemplate}</div>", "", "", "", "");
        var data = alert.FieldData();
        Assert.Equal(8, data["widgetDuration"]!.GetValue<int>());
        Assert.Equal("{name} donated {amount}!", data["messageTemplate_donation"]!.GetValue<string>());
        Assert.Equal("alertbox", data["widgetSlType"]!.GetValue<string>());
        // Streamlabs' alert tokens are not fields, so they reach the runtime as they are.
        Assert.Equal("<div id=\"alert-wrap\">{messageTemplate}</div>", WidgetFields.Substitute(alert.Html, data));

        var se = WidgetImport.FromPanes("<div></div>", "", "window.addEventListener('onWidgetLoad', () => {});", "{\"a\":{\"type\":\"text\",\"value\":1}}", "");
        Assert.Equal("{\"a\":{\"type\":\"text\",\"value\":1}}", se.Fields);
        Assert.False(StreamlabsWidget.IsStreamlabs(se.FieldData()));
    }
}
