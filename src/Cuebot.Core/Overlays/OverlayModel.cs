using System.Text.Json.Nodes;

namespace Cuebot.Core.Overlays;

/// <summary>overlays.json: every overlay and its layers.</summary>
public sealed class OverlayDocument
{
    public List<Overlay> Overlays { get; set; } = [];
}

/// <summary>
/// One overlay: a page of <see cref="Width"/> x <see cref="Height"/> pixels that OBS loads as a browser source at
/// <c>/o/{Id}?token={Token}</c> on the overlay server. The token is the only thing that grants access to it.
/// </summary>
public sealed class Overlay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Overlay";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string Token { get; set; } = "";
    public List<OverlayLayer> Layers { get; set; } = [];
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A widget placed on an overlay. It either uses a library element (<see cref="ElementId"/>) or carries its own
/// <see cref="Settings"/>. Position and size are in overlay pixels; rotation in degrees; opacity 0 to 1.
/// </summary>
public sealed class OverlayLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The widget kind, e.g. "custom", "chat", "alertbox", "text", "image".</summary>
    public string Type { get; set; } = "";

    public Guid? ElementId { get; set; }
    public Guid? GroupId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 300;
    public double Opacity { get; set; } = 1;
    public double Rotation { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public LayerAnimation Enter { get; set; } = new();
    public LayerAnimation Exit { get; set; } = new();
    public JsonObject? Settings { get; set; }
}

/// <summary>An enter or exit animation (animate.css names, "none" for none).</summary>
public sealed class LayerAnimation
{
    public string Name { get; set; } = "none";
    public double Duration { get; set; } = 1;
}

/// <summary>elements.json: the reusable library of widgets and alert designs shared across overlays.</summary>
public sealed class ElementDocument
{
    public List<OverlayElement> Elements { get; set; } = [];
}

/// <summary>A saved widget or alert design that layers on any overlay can use.</summary>
public sealed class OverlayElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Element";

    /// <summary>The widget kind, as in <see cref="OverlayLayer.Type"/>.</summary>
    public string Type { get; set; } = "";

    public JsonObject Settings { get; set; } = [];
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
}
