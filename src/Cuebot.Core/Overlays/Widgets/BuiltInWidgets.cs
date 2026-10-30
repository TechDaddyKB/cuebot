using System.Text.Json.Nodes;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>Widgets Cuebot ships, written for Cuebot on the StreamElements widget API (editable like any other widget).</summary>
public static class BuiltInWidgets
{
    /// <summary>
    /// The chat box: chat from every platform with platform tags, badges, emotes, pronouns and in-chat alerts. Its
    /// "youtube" layout builds YouTube popout chat's elements, so YouTube chat CSS themes (pasted into My CSS) apply.
    /// </summary>
    public static WidgetCode ChatBox(string layout = "cuebot") =>
        new(Read("ChatBox", "widget.html"), Read("ChatBox", "widget.css"), Read("ChatBox", "widget.js"), Read("ChatBox", "fields.json"),
            layout == "cuebot" ? "{}" : $$"""{"layout":"{{layout}}"}""");

    /// <summary>
    /// The alert box: a design per alert type (text template, image or video, sound, animations) with variations
    /// picked by amount, tier, platform, name, gifts or chance. Its designs are edited in the alert designer.
    /// </summary>
    public static WidgetCode AlertBox() =>
        new(Read("AlertBox", "widget.html"), Read("AlertBox", "widget.css"), Read("AlertBox", "widget.js"), Read("AlertBox", "fields.json"), "{}");

    /// <summary>A built-in widget placed with <see cref="WidgetLibrary.AddBuiltIn"/>: its kind, name and resource folder.</summary>
    public sealed record Kind(string Id, string Name, string Folder);

    /// <summary>Labels, goals and lists (phase 4), each edited with the widget editor's settings form.</summary>
    public static readonly IReadOnlyList<Kind> Kinds =
    [
        new("label", "Label", "Label"),
        new("goal", "Goal", "Goal"),
        new("eventlist", "Event list", "EventList"),
        new("credits", "Credits", "Credits"),
        new("countdown", "Countdown", "Countdown"),
    ];

    /// <summary>A built-in widget's code by kind id (see <see cref="Kinds"/>), or null for an unknown kind.</summary>
    public static WidgetCode? For(string kind) =>
        Kinds.FirstOrDefault(k => k.Id == kind) is { } k
            ? new(Read(k.Folder, "widget.html"), Read(k.Folder, "widget.css"), Read(k.Folder, "widget.js"), Read(k.Folder, "fields.json"), "{}")
            : null;

    /// <summary>Marks an element as Cuebot's alert box (in its settings), so its editor link opens the alert designer.</summary>
    public const string AlertBoxKind = "alertbox";

    public static bool IsAlertBox(OverlayElement? element) =>
        element?.Settings["builtIn"] is JsonValue v && v.TryGetValue<string>(out var kind) && kind == AlertBoxKind;

    private static string Read(string widget, string file)
    {
        var name = $"Cuebot.Core.Widgets.{widget}/{file}";
        using var stream = typeof(BuiltInWidgets).Assembly.GetManifestResourceStream(name)
                           ?? typeof(BuiltInWidgets).Assembly.GetManifestResourceStream(name.Replace('/', '\\'))
                           ?? throw new InvalidOperationException($"Missing built-in widget file '{name}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
