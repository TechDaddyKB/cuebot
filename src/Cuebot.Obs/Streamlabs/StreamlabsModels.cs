using System.Text.Json;

namespace Cuebot.Obs.Streamlabs;

/// <summary>Connection settings persisted in streamlabs-desktop.json.</summary>
public sealed class StreamlabsSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 59650;

    /// <summary>The Remote Control API token (Streamlabs Desktop: Settings → Remote Control → Show details).</summary>
    public string Token { get; set; } = "";

    public bool AutoConnect { get; set; }
}

/// <summary>A scene node: a source placed in the scene (or a folder, Streamlabs Desktop's groups).</summary>
/// <param name="ParentId">The id of the folder holding this node, or empty at the scene's top level.</param>
public sealed record SlobsSceneItem(string Id, string SourceId, string Name, bool Visible, string ResourceId, bool IsFolder,
    string ParentId = "");

/// <summary>A filter on a source or scene (SourceFiltersService.getFilters).</summary>
public sealed record SlobsFilter(string Name, bool Visible);

public sealed record SlobsScene(string Id, string Name, string ResourceId, IReadOnlyList<SlobsSceneItem> Items);

public sealed record SlobsSource(string Id, string Name, string Type, string ResourceId, bool Muted);

/// <summary>Bot event types published for Streamlabs Desktop. Args as noted.</summary>
public static class StreamlabsEvents
{
    // Every event also carries Streamer.bot's connection args sd.id, sd.name and sd.host.

    public const string Connected = "slobs.connected";

    /// <summary>Args: reason.</summary>
    public const string Disconnected = "slobs.disconnected";

    /// <summary>Args: sceneName, sceneId, previousScene, sd.sceneName.</summary>
    public const string SceneChanged = "slobs.sceneChanged";

    public const string StreamStarted = "slobs.streamStarted";
    public const string StreamStopped = "slobs.streamStopped";
    public const string RecordStarted = "slobs.recordStarted";
    public const string RecordStopped = "slobs.recordStopped";

    /// <summary>Args: savedReplayPath.</summary>
    public const string ReplayBufferSaved = "slobs.replayBufferSaved";
}

/// <summary>Parses Streamlabs Desktop models leniently.</summary>
internal static class StreamlabsParser
{
    public static SlobsScene[] ParseScenes(JsonElement scenes) =>
        scenes.ValueKind != JsonValueKind.Array
            ? []
            : scenes.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(ParseScene).OfType<SlobsScene>().ToArray();

    public static SlobsScene? ParseScene(JsonElement scene)
    {
        if (scene.GetStringOrNull("id") is not { } id) return null;
        var items = scene.GetArray("nodes").Where(n => n.ValueKind == JsonValueKind.Object).Select(n => new SlobsSceneItem(
            n.GetStringOrNull("sceneItemId") ?? n.GetStringOrNull("id") ?? "",
            n.GetStringOrNull("sourceId") ?? "",
            n.GetStringOrNull("name") ?? "",
            n.GetBoolOrNull("visible") ?? true,
            n.GetStringOrNull("resourceId") ?? "",
            n.GetStringOrNull("sceneNodeType") == "folder",
            n.GetStringOrNull("parentId") ?? "")).ToArray();
        return new SlobsScene(id, scene.GetStringOrNull("name") ?? id, scene.GetStringOrNull("resourceId") ?? $"Scene[\"{id}\"]", items);
    }

    public static SlobsSource[] ParseSources(JsonElement sources) =>
        sources.ValueKind != JsonValueKind.Array
            ? []
            : sources.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object && (s.GetStringOrNull("sourceId") ?? s.GetStringOrNull("id")) is not null)
                .Select(s =>
                {
                    var id = s.GetStringOrNull("sourceId") ?? s.GetStringOrNull("id")!;
                    return new SlobsSource(id, s.GetStringOrNull("name") ?? id, s.GetStringOrNull("type") ?? "",
                        s.GetStringOrNull("resourceId") ?? $"Source[\"{id}\"]", s.GetBoolOrNull("muted") ?? false);
                })
                .ToArray();

    public static SlobsFilter[] ParseFilters(JsonElement filters) =>
        filters.ValueKind != JsonValueKind.Array
            ? []
            : filters.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.Object && f.GetStringOrNull("name") is not null)
                .Select(f => new SlobsFilter(f.GetStringOrNull("name")!, f.GetBoolOrNull("visible") ?? f.GetBoolOrNull("enabled") ?? true))
                .ToArray();

    /// <summary>"live" → started, "offline" → stopped, transitional states → null.</summary>
    public static bool? StreamingState(string? status) => status switch
    {
        "live" => true,
        "offline" => false,
        _ => null,
    };

    public static bool? RecordingState(string? status) => status switch
    {
        "recording" => true,
        "offline" => false,
        _ => null,
    };
}
