using System.Text.Json.Nodes;

namespace Cuebot.Core.Overlays;

/// <summary>
/// Layer kinds. <see cref="Custom"/> layers run widget code from the library; the static kinds are drawn by the overlay
/// page itself from the layer's settings.
/// </summary>
public static class OverlayLayerTypes
{
    public const string Custom = "custom";
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string Slideshow = "slideshow";

    public static readonly IReadOnlySet<string> Static = new HashSet<string>(StringComparer.Ordinal) { Text, Image, Video, Slideshow };

    /// <summary>Enter and exit animations layers can use (names as in animate.css; "none" for none).</summary>
    public static readonly IReadOnlyList<string> EnterAnimations =
    [
        "none", "fadeIn", "fadeInUp", "fadeInDown", "fadeInLeft", "fadeInRight", "slideInUp", "slideInDown", "slideInLeft",
        "slideInRight", "zoomIn", "bounceIn",
    ];

    public static readonly IReadOnlyList<string> ExitAnimations =
    [
        "none", "fadeOut", "fadeOutUp", "fadeOutDown", "fadeOutLeft", "fadeOutRight", "slideOutUp", "slideOutDown",
        "slideOutLeft", "slideOutRight", "zoomOut", "bounceOut",
    ];

    /// <summary>A new static layer of <paramref name="type"/>, centred on the canvas, with default settings.</summary>
    public static OverlayLayer NewStatic(string type, Overlay overlay)
    {
        var (name, width, height, settings) = type switch
        {
            Text => ("Text", 600.0, 120.0, new JsonObject
            {
                ["text"] = "Your text here", ["font"] = "Inter", ["size"] = 48, ["color"] = "#ffffff", ["weight"] = "700",
                ["align"] = "center", ["shadow"] = true, ["background"] = "", ["padding"] = 0,
            }),
            Image => ("Image", 400.0, 300.0, new JsonObject { ["url"] = "", ["fit"] = "contain" }),
            Video => ("Video", 640.0, 360.0, new JsonObject { ["url"] = "", ["fit"] = "contain", ["loop"] = true, ["muted"] = true, ["volume"] = 100 }),
            Slideshow => ("Slideshow", 640.0, 360.0, new JsonObject
            {
                ["urls"] = new JsonArray(), ["seconds"] = 8, ["fit"] = "cover", ["transition"] = "fade",
            }),
            _ => throw new ArgumentException($"'{type}' is not a static layer type"),
        };
        width = Math.Min(width, overlay.Width);
        height = Math.Min(height, overlay.Height);
        return new OverlayLayer
        {
            Name = name,
            Type = type,
            X = Math.Round((overlay.Width - width) / 2),
            Y = Math.Round((overlay.Height - height) / 2),
            Width = width,
            Height = height,
            Settings = settings,
        };
    }
}
