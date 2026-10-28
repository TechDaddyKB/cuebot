using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>
/// Builds Streamer.bot's <c>props.*</c> variables for Get Scene Item Properties from obs-websocket's scene item transform.
/// obs-websocket 5 does not expose the scale filter, so <c>props.scale.filter</c> is not produced.
/// </summary>
public static class ObsSceneItemProps
{
    /// <summary>The props for one item, keyed without the <c>props.</c> prefix (e.g. <c>position.x</c>).</summary>
    public static Dictionary<string, object?> Build(int itemId, string name, bool visible, bool locked, bool muted, JsonElement transform)
    {
        double? Number(string field) => transform.GetDoubleOrNull(field);
        string? Text(string field) => transform.ValueKind == JsonValueKind.Object && transform.TryGetProperty(field, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["itemId"] = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["name"] = name,
            ["visible"] = visible,
            ["locked"] = locked,
            ["muted"] = muted,
            ["status"] = "ok",
            ["position.x"] = Number("positionX"),
            ["position.y"] = Number("positionY"),
            ["position.alignment"] = Text("alignment"),
            ["rotation"] = Number("rotation"),
            ["scale.x"] = Number("scaleX"),
            ["scale.y"] = Number("scaleY"),
            ["width"] = Number("width"),
            ["height"] = Number("height"),
            ["sourceWidth"] = Number("sourceWidth"),
            ["sourceHeight"] = Number("sourceHeight"),
            ["bounds.type"] = Text("boundsType"),
            ["bounds.alignment"] = Text("boundsAlignment"),
            ["bounds.x"] = Number("boundsWidth"),
            ["bounds.y"] = Number("boundsHeight"),
            ["crop.top"] = Number("cropTop"),
            ["crop.bottom"] = Number("cropBottom"),
            ["crop.left"] = Number("cropLeft"),
            ["crop.right"] = Number("cropRight"),
        };
    }

    /// <summary>
    /// Writes <c>props.*</c> (and <c>props.groupChildren[#].*</c>) into args, plus <c>props._json</c> with everything as JSON.
    /// </summary>
    public static void Store(IDictionary<string, object?> args, Dictionary<string, object?> props,
        IReadOnlyList<Dictionary<string, object?>> groupChildren)
    {
        foreach (var (key, value) in props) args["props." + key] = value;
        for (var i = 0; i < groupChildren.Count; i++)
            foreach (var (key, value) in groupChildren[i])
                args[$"props.groupChildren[{i}].{key}"] = value;
        var json = new Dictionary<string, object?>(props);
        if (groupChildren.Count > 0) json["groupChildren"] = groupChildren;
        args["props._json"] = JsonSerializer.Serialize(json);
    }
}
