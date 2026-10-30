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
