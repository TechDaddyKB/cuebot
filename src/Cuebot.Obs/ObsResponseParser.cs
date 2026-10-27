using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>Extracts names from GetSceneList / SceneListChanged / GetInputList payloads.</summary>
internal static class ObsResponseParser
{
    /// <summary>Scene names in the order the OBS UI lists them (OBS numbers scenes bottom-up).</summary>
    public static string[] ParseScenes(JsonElement data) =>
        Items(data, "scenes")
            .Select(s => (Name: s.GetStringOrNull("sceneName"), Index: s.TryGetProperty("sceneIndex", out var i) ? i.GetInt32() : 0))
            .Where(s => s.Name is not null)
            .OrderByDescending(s => s.Index)
            .Select(s => s.Name!)
            .ToArray();

    public static string[] ParseInputs(JsonElement data) =>
        Items(data, "inputs").Select(i => i.GetStringOrNull("inputName")).OfType<string>().ToArray();

    private static IEnumerable<JsonElement> Items(JsonElement data, string arrayProperty) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(arrayProperty, out var array) &&
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];
}
