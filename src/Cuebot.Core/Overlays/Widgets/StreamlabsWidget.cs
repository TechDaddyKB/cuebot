using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>
/// Streamlabs custom widget code (the Custom HTML, CSS, JS and custom fields of a Streamlabs chat box, alert box,
/// event list, goal or other widget) run by Cuebot: <see cref="Adapt"/> recognises it and turns its custom fields
/// into Cuebot's (StreamElements') field types, adds the settings Streamlabs fills its built-in tokens from, and marks
/// it with the hidden <see cref="FormatField"/> and <see cref="TypeField"/> fields, so the widget host adds the
/// Streamlabs runtime (<c>sl-widget.js</c>) to its document.
/// </summary>
public static partial class StreamlabsWidget
{
    /// <summary>The hidden field that marks a widget's format; <see cref="Format"/> for Streamlabs code.</summary>
    public const string FormatField = "widgetFormat";

    public const string Format = "streamlabs";

    /// <summary>The hidden field naming which Streamlabs widget the code was written for (see <see cref="Types"/>).</summary>
    public const string TypeField = "widgetSlType";

    public static readonly IReadOnlyList<string> Types = ["chatbox", "alertbox", "eventlist", "goal", "generic"];

    /// <summary>True when the widget's fields mark it as Streamlabs code.</summary>
    public static bool IsStreamlabs(JsonObject fieldData) =>
        fieldData[FormatField] is JsonValue v && v.TryGetValue<string>(out var s) && s == Format;

    /// <summary>The Streamlabs widget type the code was written for, or null when it is not Streamlabs code.</summary>
    public static string? Detect(string html, string js)
    {
        if (js.Contains("onWidgetLoad", StringComparison.Ordinal)) return null;   // StreamElements code
        if (html.Contains("chatlist_item", StringComparison.Ordinal)) return "chatbox";
        if (html.Contains("eventlist_item", StringComparison.Ordinal)) return "eventlist";
        if (GoalEvents().IsMatch(js)) return "goal";
        if (html.Contains("alert-wrap", StringComparison.Ordinal)) return "alertbox";
        if (DocumentEvents().IsMatch(js)) return "generic";
        return null;
    }

    /// <summary>
    /// The widget made ready for Cuebot when it is Streamlabs code not yet adapted (otherwise unchanged): custom field
    /// types converted, Streamlabs' built-in settings added as fields, and the format fields set.
    /// </summary>
    public static WidgetCode Adapt(WidgetCode code)
    {
        var fields = WidgetCode.ParseObject(code.Fields);
        if (fields.ContainsKey(FormatField)) return code;
        var type = Detect(code.Html, code.Js);
        if (type is null) return code;

        foreach (var (_, field) in fields)
            if (field is JsonObject f) ConvertField(f);
        foreach (var (key, field) in BuiltInSettings(type))
            if (!fields.ContainsKey(key)) fields[key] = field;
        fields[FormatField] = new JsonObject { ["type"] = "hidden", ["value"] = Format };
        fields[TypeField] = new JsonObject { ["type"] = "hidden", ["value"] = type };
        return code with { Fields = fields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) };
    }

    /// <summary>A Streamlabs custom field as a StreamElements one (types and the slider's <c>steps</c>).</summary>
    internal static void ConvertField(JsonObject field)
    {
        var type = field["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
        field["type"] = type switch
        {
            "textfield" or "textinput" => "text",
            "fontpicker" => "googleFont",
            "checkbox" => "checkbox",
            "" => "text",
            _ => type,
        };
        if (type == "slider" && field["steps"] is { } steps && !field.ContainsKey("step"))
        {
            field.Remove("steps");
            field["step"] = steps;
        }
    }

    private static JsonObject Field(string type, string label, JsonNode value, string group = "Streamlabs settings") =>
        new() { ["type"] = type, ["label"] = label, ["value"] = value, ["group"] = group };

    /// <summary>The settings a Streamlabs widget of <paramref name="type"/> fills its built-in tokens from.</summary>
    internal static IEnumerable<(string Key, JsonObject Field)> BuiltInSettings(string type)
    {
        switch (type)
        {
            case "chatbox":
                yield return ("background_color", Field("colorpicker", "Background color", "rgba(0, 0, 0, 0)"));
                yield return ("text_color", Field("colorpicker", "Text color", "#ffffff"));
                yield return ("font_size", Field("text", "Font size", "20px"));
                yield return ("message_hide_delay", Field("text", "Hide messages after", "999999s"));
                break;
            case "eventlist":
                yield return ("background", Field("colorpicker", "Background", "rgba(0, 0, 0, 0)"));
                yield return ("text_color", Field("colorpicker", "Text color", "#ffffff"));
                yield return ("font_family", Field("googleFont", "Font", "Open Sans"));
                yield return ("font_size", Field("text", "Font size", "20px"));
                yield return ("max_events", Field("number", "Events shown", 10));
                yield return ("show_animation", Field("text", "Show animation", "fadeIn"));
                yield return ("hide_animation", Field("text", "Hide animation", "fadeOut"));
                yield return ("animation_speed", Field("text", "Animation speed", "1s"));
                yield return ("rotate_x", Field("text", "Rotate X", "0deg"));
                yield return ("rotate_y", Field("text", "Rotate Y", "0deg"));
                yield return ("hue", Field("text", "Hue", "0deg"));
                yield return ("saturation", Field("text", "Saturation", "1"));
                yield return ("brightness", Field("text", "Brightness", "1"));
                break;
            case "alertbox":
                yield return ("widgetDuration", Field("slider", "Alert duration (seconds)", 8, "Alerts"));
                foreach (var (alert, text) in new[]
                {
                    ("follow", "{name} just followed!"), ("subscription", "{name} just subscribed!"), ("donation", "{name} donated {amount}!"),
                    ("bits", "{name} cheered {amount} bits!"), ("raid", "{name} is raiding with {amount} viewers!"),
                    ("superchat", "{name} sent a Super Chat of {amount}!"), ("merch", "{name} bought {product}!"),
                })
                    yield return ($"messageTemplate_{alert}", Field("text", $"Message ({alert})", text, "Alerts"));
                break;
            case "goal":
                yield return ("goalType", new JsonObject
                {
                    ["type"] = "dropdown", ["label"] = "Goal", ["value"] = "tip", ["group"] = "Goal",
                    ["options"] = new JsonObject { ["tip"] = "Tips", ["follower"] = "Followers", ["sub"] = "Subscribers", ["bit"] = "Bits", ["superchat"] = "Super Chats" },
                });
                yield return ("goalTitle", Field("text", "Title", "My Goal", "Goal"));
                yield return ("goalTarget", Field("number", "Target", 100, "Goal"));
                yield return ("goalStart", Field("number", "Starting amount", 0, "Goal"));
                break;
        }
    }

    [GeneratedRegex(@"document\s*\.\s*addEventListener\s*\(\s*['""](onLoad|onEventReceived|goalLoad|goalEvent|bossLoad|bossDamaged|bossKilled)['""]")]
    private static partial Regex DocumentEvents();

    [GeneratedRegex(@"['""](goalLoad|goalEvent)['""]")]
    private static partial Regex GoalEvents();
}
