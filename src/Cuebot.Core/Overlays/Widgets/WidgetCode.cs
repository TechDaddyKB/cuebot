using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>
/// A StreamElements-style custom widget: the HTML, CSS and JS panes, the FIELDS schema (JSON object of field name →
/// {type, label, value, ...}) and the DATA pane (saved field values, a flat JSON object), plus Cuebot's
/// <see cref="CssOverride"/>: your own CSS, applied after the widget's, so a purchased widget can be restyled (for example
/// per platform) without editing its code.
/// </summary>
public sealed record WidgetCode(string Html, string Css, string Js, string Fields, string Data)
{
    public string CssOverride { get; init; } = "";

    public static readonly WidgetCode Empty = new("", "", "", "{}", "{}");

    /// <summary>The largest import accepted (all panes together).</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The widget's settings as StreamElements' <c>fieldData</c>: every field's default <c>value</c>, replaced by the
    /// saved DATA value when there is one (DATA keys without a field are kept too). Unreadable JSON counts as empty.
    /// </summary>
    public JsonObject FieldData()
    {
        var result = new JsonObject();
        foreach (var (key, field) in ParseObject(Fields))
        {
            if (field is JsonObject f && f.TryGetPropertyValue("value", out var value)) result[key] = value?.DeepClone();
        }
        foreach (var (key, value) in ParseObject(Data)) result[key] = value?.DeepClone();
        return result;
    }

    /// <summary>The widget's StreamElements queue hold in seconds (the reserved <c>widgetDuration</c> field), 0 for none.</summary>
    public static double WidgetDuration(JsonObject fieldData) =>
        fieldData["widgetDuration"] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? Math.Max(0, d)
            : 0;

    internal static JsonObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Fills StreamElements field placeholders (<c>{name}</c> and <c>{{name}}</c>) in a widget's HTML, CSS and JS.</summary>
public static partial class WidgetFields
{
    /// <summary>
    /// Replaces the placeholders of fields that exist in <paramref name="fieldData"/> with their values, as plain text
    /// (as StreamElements does; a JS string placeholder gets the raw value). Other braces are left alone.
    /// </summary>
    public static string Substitute(string text, JsonObject fieldData) =>
        Placeholder().Replace(text, m =>
        {
            var key = m.Groups["double"].Success ? m.Groups["double"].Value : m.Groups["single"].Value;
            return fieldData.TryGetPropertyValue(key, out var value) ? Format(value) : m.Value;
        });

    /// <summary>A field value as text: strings as they are, numbers and booleans in JSON form, lists as JSON.</summary>
    public static string Format(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => value.ToJsonString(),
    };

    [GeneratedRegex(@"\{\{\s*(?<double>[A-Za-z_][A-Za-z0-9_\-]*)\s*\}\}|\{(?<single>[A-Za-z_][A-Za-z0-9_\-]*)\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// Reads widget packages: the widget.io zip format (<c>widget.ini</c> naming <c>html.txt</c>, <c>css.txt</c>,
/// <c>js.txt</c>, <c>fields.txt</c>, <c>data.txt</c>) and zips that just contain those panes under common names
/// (<c>html/css/js/fields/data</c> with <c>.txt</c>, <c>.html</c>, <c>.css</c>, <c>.js</c> or <c>.json</c>), in the root or
/// one folder down. Missing panes are empty.
/// </summary>
public static class WidgetImport
{
    private static readonly string[] Panes = ["html", "css", "js", "fields", "data"];

    private static readonly Dictionary<string, string[]> Extensions = new(StringComparer.Ordinal)
    {
        ["html"] = [".txt", ".html", ".htm"],
        ["css"] = [".txt", ".css"],
        ["js"] = [".txt", ".js"],
        ["fields"] = [".txt", ".json"],
        ["data"] = [".txt", ".json"],
    };

    /// <summary>Reads a widget zip. Throws <see cref="InvalidDataException"/> when it is not a widget or too big.</summary>
    public static WidgetCode FromZip(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var files = archive.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')).ToArray();
        if (files.Sum(e => e.Length) > WidgetCode.MaxBytes) throw new InvalidDataException("The widget is larger than 5 MB.");

        var panes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ini = files.FirstOrDefault(e => e.Name.Equals("widget.ini", StringComparison.OrdinalIgnoreCase));
        var folder = ini is null ? null : ini.FullName[..^ini.Name.Length];
        if (ini is not null)
        {
            foreach (var (pane, path) in ParseIni(Read(ini)))
            {
                var entry = files.FirstOrDefault(e => e.FullName.Equals(folder + path, StringComparison.OrdinalIgnoreCase));
                if (entry is not null) panes[pane] = Read(entry);
            }
        }
        foreach (var pane in Panes.Where(p => !panes.ContainsKey(p)))
        {
            var entry = files
                .Where(e => e.FullName.Count(c => c == '/') <= 1)
                .FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.Name).Equals(pane, StringComparison.OrdinalIgnoreCase)
                                     && Extensions[pane].Contains(Path.GetExtension(e.Name).ToLowerInvariant()));
            if (entry is not null) panes[pane] = Read(entry);
        }
        if (!panes.ContainsKey("html") && !panes.ContainsKey("js"))
            throw new InvalidDataException("This zip has no widget in it (no HTML or JS pane, and no widget.ini).");
        return FromPanes(panes.GetValueOrDefault("html", ""), panes.GetValueOrDefault("css", ""), panes.GetValueOrDefault("js", ""),
            panes.GetValueOrDefault("fields", ""), panes.GetValueOrDefault("data", ""));
    }

    /// <summary>
    /// A widget from pasted panes; empty FIELDS and DATA become <c>{}</c>, and Streamlabs code is adapted
    /// (<see cref="StreamlabsWidget.Adapt"/>). Throws for invalid FIELDS JSON.
    /// </summary>
    public static WidgetCode FromPanes(string html, string css, string js, string fields, string data)
    {
        if (Encoding.UTF8.GetByteCount(html) + Encoding.UTF8.GetByteCount(css) + Encoding.UTF8.GetByteCount(js)
            + Encoding.UTF8.GetByteCount(fields) + Encoding.UTF8.GetByteCount(data) > WidgetCode.MaxBytes)
            throw new InvalidDataException("The widget is larger than 5 MB.");
        fields = string.IsNullOrWhiteSpace(fields) ? "{}" : fields.Trim();
        data = string.IsNullOrWhiteSpace(data) ? "{}" : data.Trim();
        if (fields != "{}" && WidgetCode.ParseObject(fields).Count == 0 && !IsEmptyObject(fields))
            throw new InvalidDataException("The FIELDS pane is not a JSON object.");
        return StreamlabsWidget.Adapt(new WidgetCode(html, css, js, fields, data));
    }

    private static bool IsEmptyObject(string json) => json.Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "") == "{}";

    /// <summary>The pane → path pairs of a widget.ini (<c>[HTML]</c> then <c>path = "html.txt"</c>).</summary>
    internal static IEnumerable<(string Pane, string Path)> ParseIni(string ini)
    {
        string? section = null;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim().ToLowerInvariant();
            else if (section is not null && Panes.Contains(section) && line.Split('=', 2) is [var key, var value]
                     && key.Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
                yield return (section, value.Trim().Trim('"', '\'').Replace('\\', '/'));
        }
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
