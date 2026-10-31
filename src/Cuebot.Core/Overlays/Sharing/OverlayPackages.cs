using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Core.Overlays.Sharing;

/// <summary>What an export left out: custom widgets whose code was not included (the user did not confirm the rights).</summary>
public sealed record PackageExportResult(int Widgets, int Assets, IReadOnlyList<string> LeftOut);

/// <summary>
/// What an import made: the overlay it created or added to, how many layers came in, and what was left out (widgets
/// shared without their code, files that did not match their names).
/// </summary>
public sealed record PackageImportResult(Overlay Overlay, string Kind, int Layers, IReadOnlyList<string> LeftOut);

/// <summary>A package that cannot be read (corrupt, too big, not a Cuebot package). The message is safe to show.</summary>
public sealed class PackageFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One-file overlay and widget packages (<c>.cuebot-overlay</c>, a zip): <c>package.json</c> (the overlay's canvas and
/// layers, or one widget's layer, and which widgets they use), <c>widgets/{id}/</c> with each widget's panes, settings
/// and CSS override, and <c>assets/</c> with the overlay files they use (images, video, sound, fonts). Cuebot's own
/// widgets always travel; any other widget's code (bought, imported or written by someone else) only when the user
/// confirms they may share it, otherwise its layer is listed as left out. On import, Cuebot's alert box and media
/// player always get Cuebot's own code (only their settings come from the package), so a package cannot give other code
/// their abilities (alert TTS, moving the media queue).
/// </summary>
public sealed partial class OverlayPackages(OverlayStore overlays, ElementStore elements, WidgetCodeStore code, AssetStore assets, WidgetLibrary library)
{
    public const string Format = "cuebot-overlay";
    public const int Version = 1;
    public const string FileExtension = ".cuebot-overlay";

    /// <summary>The largest package read, and the most it may unpack to.</summary>
    public const long MaxBytes = 500L * 1024 * 1024;

    /// <summary>Built-in kinds with abilities other widgets do not have; they always run Cuebot's own code.</summary>
    private static readonly HashSet<string> Privileged = new(StringComparer.Ordinal) { BuiltInWidgets.AlertBoxKind, "media" };

    private static readonly (string File, Func<WidgetCode, string> Get)[] Panes =
    [
        ("widget.html", c => c.Html), ("widget.css", c => c.Css), ("widget.js", c => c.Js),
        ("fields.json", c => c.Fields), ("data.json", c => c.Data), ("override.css", c => c.CssOverride),
    ];

    /// <summary>The names of the overlay's widgets whose code needs the user's word before it is shared.</summary>
    public IReadOnlyList<string> CustomWidgets(Guid overlayId, Guid? layerId = null) =>
        (overlays.Get(overlayId)?.Layers ?? [])
        .Where(l => layerId is null || l.Id == layerId)
        .Where(l => l.ElementId is { } id && !IsCuebots(elements.Get(id), code.Read(id)))
        .Select(l => l.Name)
        .ToArray();

    /// <summary>True for Cuebot's own widgets: built-ins (marked with their kind) and the chat box.</summary>
    internal static bool IsCuebots(OverlayElement? element, WidgetCode widget)
    {
        if (BuiltInWidgets.KindOf(element) is not null) return true;
        var chat = BuiltInWidgets.ChatBox();
        return widget.Html == chat.Html && widget.Js == chat.Js && widget.Css == chat.Css;
    }

    /// <summary>Writes the overlay (canvas and every layer) as a package.</summary>
    public PackageExportResult ExportOverlay(Guid overlayId, Stream output, bool includeCustomCode) =>
        Export(overlays.Get(overlayId) ?? throw new ArgumentException("That overlay does not exist"), null, output, includeCustomCode);

    /// <summary>Writes one layer (for example an alert box with its designs) as a widget package.</summary>
    public PackageExportResult ExportWidget(Guid overlayId, Guid layerId, Stream output, bool includeCustomCode)
    {
        var overlay = overlays.Get(overlayId) ?? throw new ArgumentException("That overlay does not exist");
        var layer = overlay.Layers.FirstOrDefault(l => l.Id == layerId) ?? throw new ArgumentException("That widget is not on the overlay");
        return Export(overlay, layer, output, includeCustomCode);
    }

    private PackageExportResult Export(Overlay overlay, OverlayLayer? only, Stream output, bool includeCustomCode)
    {
        var layers = only is null ? overlay.Layers : [only];
        var widgets = new JsonObject();
        var layerArray = new JsonArray();
        var leftOut = new List<string>();
        var codes = new Dictionary<string, WidgetCode>(StringComparer.Ordinal);
        var assetNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var node = LayerNode(layer);
            Collect(layer.Settings?.ToJsonString(), assetNames);
            if (layer.ElementId is { } elementId)
            {
                var element = elements.Get(elementId);
                var widget = code.Read(elementId);
                var cuebots = IsCuebots(element, widget);
                if (!cuebots && !includeCustomCode)
                {
                    leftOut.Add(layer.Name);
                    node["widget"] = null;
                    node["leftOut"] = true;
                    layerArray.Add(node);
                    continue;
                }
                var id = "w" + (codes.Count + 1);
                codes[id] = widget;
                widgets[id] = new JsonObject
                {
                    ["name"] = element?.Name ?? layer.Name,
                    ["builtIn"] = BuiltInWidgets.KindOf(element),
                    ["cuebot"] = cuebots,
                };
                node["widget"] = id;
                foreach (var (_, get) in Panes) Collect(get(widget), assetNames);
            }
            layerArray.Add(node);
        }

        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["kind"] = only is null ? "overlay" : "widget",
            ["name"] = only?.Name ?? overlay.Name,
            ["created"] = DateTimeOffset.UtcNow.ToString("O"),
            ["width"] = overlay.Width,
            ["height"] = overlay.Height,
            ["layers"] = layerArray,
            ["widgets"] = widgets,
            ["assets"] = new JsonArray([.. assetNames.Where(n => assets.PathOf(n) is not null).Select(n => (JsonNode)n)]),
            ["leftOut"] = new JsonArray([.. leftOut.Select(n => (JsonNode)n)]),
        };

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteText(zip, "package.json", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (id, widget) in codes)
            foreach (var (file, get) in Panes)
                WriteText(zip, $"widgets/{id}/{file}", get(widget));
        var written = 0;
        foreach (var name in assetNames)
        {
            if (assets.PathOf(name) is not { } path) continue;
            zip.CreateEntryFromFile(path, "assets/" + name, CompressionLevel.Optimal);
            written++;
        }
        return new PackageExportResult(codes.Count, written, leftOut);
    }

    /// <summary>
    /// Reads a package: an overlay package becomes a new overlay (with a new link); a widget package is placed on
    /// <paramref name="targetOverlayId"/>. Throws <see cref="PackageFormatException"/> when it is not a readable package.
    /// </summary>
    public async Task<PackageImportResult> ImportAsync(Stream input, Guid? targetOverlayId = null, CancellationToken ct = default)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageFormatException("This file is not a Cuebot overlay package.", ex);
        }
        using (zip)
        {
            if (zip.Entries.Sum(e => e.Length) > MaxBytes) throw new PackageFormatException("The package is larger than 500 MB.");
            var manifest = ReadManifest(zip);
            var kind = Str(manifest["kind"]) == "widget" ? "widget" : "overlay";
            var leftOut = new List<string>();
            var count = 0;

            Overlay overlay;
            if (kind == "overlay")
            {
                // A second import of the same package gets "Name (2)", so the two can be told apart.
                var taken = overlays.All().Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                overlay = overlays.Create(Transfer.BundleImporter.UniqueName(Str(manifest["name"]) is { Length: > 0 } n ? n : "Imported overlay", taken),
                    Math.Clamp(Int(manifest["width"], 1920), 16, 7680), Math.Clamp(Int(manifest["height"], 1080), 16, 7680));
            }
            else
            {
                overlay = (targetOverlayId is { } target ? overlays.Get(target) : null)
                          ?? throw new ArgumentException("Pick the overlay to add this widget to.");
            }

            try
            {
                // Files first, so widgets that use them work at once; one whose content does not match its name is skipped.
                foreach (var name in (manifest["assets"] as JsonArray ?? []).Select(Str).Distinct(StringComparer.Ordinal))
                {
                    if (!AssetName().IsMatch(name) || AssetStore.ExtensionOf(name) is null || zip.GetEntry("assets/" + name) is not { } entry) continue;
                    await using var stream = entry.Open();
                    var stored = await assets.AddAsync(stream, name, ct).ConfigureAwait(false);
                    // Kept under its real name (files are named by content, so it may be one another overlay uses).
                    if (stored != name) leftOut.Add($"file {name} (its content does not match its name)");
                }

                var widgets = manifest["widgets"] as JsonObject ?? [];
                    foreach (var node in (manifest["layers"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = Str(node["name"]) is { Length: > 0 } ln ? ln : "Widget";
                    if (node["leftOut"] is JsonValue lo && lo.TryGetValue<bool>(out var wasLeftOut) && wasLeftOut)
                    {
                        leftOut.Add($"{name} (shared without its code; add the widget yourself)");
                        continue;
                    }
                    var type = Str(node["type"]);
                    OverlayLayer? layer;
                    if (OverlayLayerTypes.Static.Contains(type))
                    {
                        layer = OverlayLayerTypes.NewStatic(type, overlay);
                        layer.Settings = node["settings"]?.DeepClone() as JsonObject;
                        var added = layer;
                        overlays.Update(overlay.Id, o => o.Layers.Add(added));
                    }
                    else if (Str(node["widget"]) is { Length: > 0 } wid && widgets[wid] is JsonObject info && ValidId().IsMatch(wid))
                    {
                        layer = AddWidget(overlay, zip, wid, info, name);
                    }
                    else
                    {
                        leftOut.Add($"{name} (not readable)");
                        continue;
                    }
                    if (layer is null) continue;
                    Apply(overlay.Id, layer.Id, node, name);
                    count++;
                }
            }
            catch when (kind == "overlay")
            {
                overlays.Delete(overlay.Id);   // no half-made overlay is left behind
                throw;
            }
            return new PackageImportResult(overlays.Get(overlay.Id) ?? overlay, kind, count, leftOut);
        }
    }

    private OverlayLayer? AddWidget(Overlay overlay, ZipArchive zip, string id, JsonObject info, string layerName)
    {
        string Pane(string file) =>
            zip.GetEntry($"widgets/{id}/{file}") is { } entry ? ReadText(entry) : file.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "";
        var widget = new WidgetCode(Pane("widget.html"), Pane("widget.css"), Pane("widget.js"), Pane("fields.json"), Pane("data.json"))
        {
            CssOverride = Pane("override.css"),
        };
        var builtIn = Str(info["builtIn"]);
        JsonObject? settings = null;
        if (builtIn.Length > 0)
        {
            var shipped = builtIn == BuiltInWidgets.AlertBoxKind ? BuiltInWidgets.AlertBox() : BuiltInWidgets.For(builtIn);
            if (shipped is null) builtIn = "";   // a kind this Cuebot does not know: an ordinary widget
            else
            {
                settings = new JsonObject { ["builtIn"] = builtIn };
                // The alert box and media player keep Cuebot's own code; their settings and CSS come from the package.
                if (Privileged.Contains(builtIn)) widget = shipped with { Data = widget.Data, CssOverride = widget.CssOverride };
            }
        }
        var name = Str(info["name"]) is { Length: > 0 } n ? n : layerName;
        return library.AddCustomWidget(overlay.Id, name, widget, settings);
    }

    /// <summary>Copies a packaged layer's place, size and look onto the layer just made.</summary>
    private void Apply(Guid overlayId, Guid layerId, JsonObject node, string name) =>
        overlays.Update(overlayId, o =>
        {
            if (o.Layers.FirstOrDefault(l => l.Id == layerId) is not { } layer) return;
            layer.Name = name;
            layer.X = Number(node["x"], layer.X);
            layer.Y = Number(node["y"], layer.Y);
            layer.Width = Math.Max(1, Number(node["width"], layer.Width));
            layer.Height = Math.Max(1, Number(node["height"], layer.Height));
            layer.Opacity = Math.Clamp(Number(node["opacity"], 1), 0, 1);
            layer.Rotation = Number(node["rotation"], 0);
            layer.Visible = node["visible"] is not JsonValue v || !v.TryGetValue<bool>(out var visible) || visible;
            layer.Locked = node["locked"] is JsonValue l && l.TryGetValue<bool>(out var locked) && locked;
            layer.Enter = Animation(node["enter"], OverlayLayerTypes.EnterAnimations);
            layer.Exit = Animation(node["exit"], OverlayLayerTypes.ExitAnimations);
        });

    private static LayerAnimation Animation(JsonNode? node, IReadOnlyList<string> allowed) => new()
    {
        Name = allowed.Contains(Str(node?["name"])) ? Str(node?["name"]) : "none",
        Duration = Math.Clamp(Number(node?["duration"], 1), 0, 30),
    };

    private static JsonObject LayerNode(OverlayLayer layer) => new()
    {
        ["name"] = layer.Name,
        ["type"] = layer.Type,
        ["x"] = layer.X,
        ["y"] = layer.Y,
        ["width"] = layer.Width,
        ["height"] = layer.Height,
        ["opacity"] = layer.Opacity,
        ["rotation"] = layer.Rotation,
        ["visible"] = layer.Visible,
        ["locked"] = layer.Locked,
        ["enter"] = new JsonObject { ["name"] = layer.Enter.Name, ["duration"] = layer.Enter.Duration },
        ["exit"] = new JsonObject { ["name"] = layer.Exit.Name, ["duration"] = layer.Exit.Duration },
        ["settings"] = OverlayLayerTypes.Static.Contains(layer.Type) ? layer.Settings?.DeepClone() : null,
    };

    private static JsonObject ReadManifest(ZipArchive zip)
    {
        if (zip.GetEntry("package.json") is not { } entry) throw new PackageFormatException("This file is not a Cuebot overlay package (no package.json).");
        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(ReadText(entry)) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new PackageFormatException("The package's package.json is not readable.", ex);
        }
        if (manifest is null || Str(manifest["format"]) != Format) throw new PackageFormatException("This file is not a Cuebot overlay package.");
        if (Int(manifest["version"], 0) > Version)
            throw new PackageFormatException("This package was made by a newer Cuebot. Update Cuebot to import it.");
        return manifest;
    }

    /// <summary>Asset names (<c>/assets/{sha256}.{ext}</c>) mentioned in <paramref name="text"/>.</summary>
    private static void Collect(string? text, ISet<string> names)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in AssetReference().Matches(text)) names.Add(m.Groups[1].Value);
    }

    private static void WriteText(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > WidgetCode.MaxBytes) throw new PackageFormatException($"'{entry.FullName}' is larger than 5 MB.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static double Number(JsonNode? node, double fallback) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.GetValue<double>()) ? v.GetValue<double>() : fallback;

    private static int Int(JsonNode? node, int fallback) => (int)Math.Round(Number(node, fallback));

    [GeneratedRegex(@"/assets/([0-9a-f]{64}\.[a-z0-9]{1,8})")]
    private static partial Regex AssetReference();

    [GeneratedRegex("^[0-9a-f]{64}\\.[a-z0-9]{1,8}$")]
    private static partial Regex AssetName();

    [GeneratedRegex("^w[0-9]{1,5}$")]
    private static partial Regex ValidId();
}
