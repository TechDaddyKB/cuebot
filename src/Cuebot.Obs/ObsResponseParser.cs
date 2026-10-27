using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>One source placed in a scene (or group), as listed by GetSceneItemList / GetGroupSceneItemList.</summary>
public sealed record ObsSceneItem(int Id, string SourceName, bool Enabled, bool IsGroup, string? InputKind);

/// <summary>One filter on a source, as listed by GetSourceFilterList.</summary>
public sealed record ObsFilter(string Name, bool Enabled, string Kind);

/// <summary>Extracts names and items from OBS list responses, leniently.</summary>
internal static class ObsResponseParser
{
    /// <summary>Scene names in the order the OBS UI lists them (OBS numbers scenes bottom-up).</summary>
    public static string[] ParseScenes(JsonElement data) =>
        Items(data, "scenes")
            .Select(s => (Name: s.GetStringOrNull("sceneName"), Index: s.GetIntOrNull("sceneIndex") ?? 0))
            .Where(s => s.Name is not null)
            .OrderByDescending(s => s.Index)
            .Select(s => s.Name!)
            .ToArray();

    public static string[] ParseInputs(JsonElement data) => ParseNames(data, "inputs", "inputName");

    /// <summary>The <paramref name="nameProperty"/> of every object in an array property.</summary>
    public static string[] ParseNames(JsonElement data, string arrayProperty, string nameProperty) =>
        Items(data, arrayProperty).Select(i => i.GetStringOrNull(nameProperty)).OfType<string>().ToArray();

    /// <summary>An array of plain strings, e.g. GetProfileList's <c>profiles</c>.</summary>
    public static string[] ParseStrings(JsonElement data, string arrayProperty) =>
        data.GetArray(arrayProperty).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();

    /// <summary>Scene items top to bottom as the OBS sources list shows them (OBS numbers them bottom-up).</summary>
    public static ObsSceneItem[] ParseSceneItems(JsonElement data) =>
        Items(data, "sceneItems")
            .Where(i => i.GetStringOrNull("sourceName") is not null && i.GetIntOrNull("sceneItemId") is not null)
            .OrderByDescending(i => i.GetIntOrNull("sceneItemIndex") ?? 0)
            .Select(i => new ObsSceneItem(
                i.GetIntOrNull("sceneItemId")!.Value,
                i.GetStringOrNull("sourceName")!,
                i.GetBoolOrNull("sceneItemEnabled") ?? true,
                i.GetBoolOrNull("isGroup") ?? false,
                i.GetStringOrNull("inputKind")))
            .ToArray();

    public static ObsFilter[] ParseFilters(JsonElement data) =>
        Items(data, "filters")
            .Where(f => f.GetStringOrNull("filterName") is not null)
            .OrderBy(f => f.GetIntOrNull("filterIndex") ?? 0)
            .Select(f => new ObsFilter(f.GetStringOrNull("filterName")!, f.GetBoolOrNull("filterEnabled") ?? true,
                f.GetStringOrNull("filterKind") ?? ""))
            .ToArray();

    private static IEnumerable<JsonElement> Items(JsonElement data, string arrayProperty) =>
        data.GetArray(arrayProperty).Where(item => item.ValueKind == JsonValueKind.Object);
}
