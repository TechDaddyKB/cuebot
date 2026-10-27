using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Meld;

/// <summary>One entry of Meld's <c>session.items</c>: a scene, layer, effect or audio track, keyed by a 32-hex id.</summary>
public sealed class MeldItem
{
    internal MeldItem(string id, JsonElement json)
    {
        Id = id;
        Json = json;
        Type = json.Str("type");
        Name = json.Str("name");
        Parent = json.Str("parent");
        Index = (int)(json.Num("index") ?? 0);
    }

    public string Id { get; }

    /// <summary>"scene", "layer", "effect" or "track".</summary>
    public string Type { get; }

    public string Name { get; }

    /// <summary>Scene id for layers, layer id for effects and layer tracks; empty for scenes and global tracks.</summary>
    public string Parent { get; }

    public int Index { get; }

    internal JsonElement Json { get; }

    public bool IsScene => Type == "scene";
    public bool IsLayer => Type == "layer";
    public bool IsEffect => Type == "effect";
    public bool IsTrack => Type == "track";

    public bool Current => Json.Bool("current") == true;
    public bool Staged => Json.Bool("staged") == true;
    public bool Visible => Json.Bool("visible") == true;
    public bool Enabled => Json.Bool("enabled") == true;
    public bool Muted => Json.Bool("muted") == true;
    public bool Monitoring => Json.Bool("monitoring") == true;

    /// <summary>Browser layers have a url.</summary>
    public string Url => Json.Str("url");

    public bool? Flag(string name) => Json.Bool(name);

    public override string ToString() => $"{Type} {Name} ({Id})";
}

/// <summary>An immutable snapshot of Meld's session with lookups by name or id.</summary>
public sealed class MeldSession
{
    public static readonly MeldSession Empty = new([]);

    private readonly Dictionary<string, MeldItem> _byId;

    private MeldSession(Dictionary<string, MeldItem> items)
    {
        _byId = items;
        Scenes = items.Values.Where(i => i.IsScene).OrderBy(i => i.Index).ToArray();
    }

    /// <summary>Parses the <c>session</c> property value (<c>{"items": {...}}</c>, or the items object itself).</summary>
    internal static MeldSession Parse(JsonElement? session)
    {
        if (session is not { ValueKind: JsonValueKind.Object } value) return Empty;
        var items = value.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : value;
        var result = new Dictionary<string, MeldItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in items.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object) result[p.Name] = new MeldItem(p.Name, p.Value.Clone());
        return new MeldSession(result);
    }

    public IReadOnlyCollection<MeldItem> Items => _byId.Values;

    public IReadOnlyList<MeldItem> Scenes { get; }

    public MeldItem? CurrentScene => Scenes.FirstOrDefault(s => s.Current);

    public MeldItem? StagedScene => Scenes.FirstOrDefault(s => s.Staged);

    public MeldItem? Get(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<MeldItem> LayersOf(string sceneId) =>
        _byId.Values.Where(i => i.IsLayer && string.Equals(i.Parent, sceneId, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Index);

    public IEnumerable<MeldItem> EffectsOf(string layerId) =>
        _byId.Values.Where(i => i.IsEffect && string.Equals(i.Parent, layerId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MeldItem> Layers => _byId.Values.Where(i => i.IsLayer);
    public IEnumerable<MeldItem> Effects => _byId.Values.Where(i => i.IsEffect);
    public IEnumerable<MeldItem> Tracks => _byId.Values.Where(i => i.IsTrack);

    /// <summary>The scene with this id or (case-insensitive) name.</summary>
    public MeldItem FindScene(string nameOrId) =>
        Find(Scenes, nameOrId) ?? throw new StreamAppRequestException($"Meld Studio has no scene '{nameOrId}'.");

    /// <summary>A layer by id or name, inside <paramref name="scene"/> when given (empty = any scene, current scene first).</summary>
    public MeldItem FindLayer(string scene, string nameOrId)
    {
        if (scene.Trim().Length > 0)
        {
            var s = FindScene(scene);
            return Find(LayersOf(s.Id), nameOrId)
                   ?? throw new StreamAppRequestException($"Meld Studio scene '{s.Name}' has no layer '{nameOrId}'.");
        }
        var current = CurrentScene;
        return (current is null ? null : Find(LayersOf(current.Id), nameOrId))
               ?? Find(Layers.OrderBy(l => l.Parent).ThenBy(l => l.Index), nameOrId)
               ?? throw new StreamAppRequestException($"Meld Studio has no layer '{nameOrId}'.");
    }

    public MeldItem FindEffect(MeldItem layer, string nameOrId) =>
        Find(EffectsOf(layer.Id), nameOrId) ?? throw new StreamAppRequestException($"Meld Studio layer '{layer.Name}' has no effect '{nameOrId}'.");

    /// <summary>A track by id or name: global tracks by their name, layer tracks by their layer's name.</summary>
    public MeldItem FindTrack(string nameOrId) =>
        Find(Tracks.OrderBy(t => t.Parent.Length > 0 ? 1 : 0), nameOrId) ?? throw new StreamAppRequestException($"Meld Studio has no audio track '{nameOrId}'.");

    /// <summary>The audio track of a layer (Meld gives layers with audio a track whose parent is the layer).</summary>
    public MeldItem TrackOfLayer(MeldItem layer) =>
        Tracks.FirstOrDefault(t => string.Equals(t.Parent, layer.Id, StringComparison.OrdinalIgnoreCase))
        ?? throw new StreamAppRequestException($"Meld Studio layer '{layer.Name}' has no audio track.");

    /// <summary>Name for event arguments: the item's name, or its parent layer's for layer tracks.</summary>
    public string DisplayName(MeldItem item) => item.Name.Length > 0 ? item.Name : Get(item.Parent)?.Name ?? item.Id;

    private static MeldItem? Find(IEnumerable<MeldItem> items, string nameOrId)
    {
        var key = nameOrId.Trim();
        if (key.Length == 0) return null;
        var list = items as IReadOnlyCollection<MeldItem> ?? items.ToArray();
        return list.FirstOrDefault(i => string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.Ordinal))
               ?? list.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A change found by comparing two session snapshots.</summary>
internal sealed record MeldChange(string Event, Dictionary<string, object?> Args);

/// <summary>Turns two session snapshots into Meld Studio events (scene switches, visibility, effects, tracks, objects).</summary>
internal static class MeldSessionDiff
{
    private static readonly HashSet<string> SpecificProperties = ["current", "staged", "visible", "enabled", "muted", "monitoring"];

    public static IReadOnlyList<MeldChange> Compare(MeldSession before, MeldSession after)
    {
        var changes = new List<MeldChange>();
        foreach (var item in after.Items)
        {
            if (before.Get(item.Id) is not { } old)
            {
                changes.Add(Object(MeldEvents.ObjectAdded, after, item));
                continue;
            }
            if (item.IsLayer && old.Visible != item.Visible)
                changes.Add(new(MeldEvents.LayerVisibilityChanged, new Dictionary<string, object?>
                {
                    ["layerId"] = item.Id, ["layerName"] = item.Name, ["visible"] = item.Visible,
                    ["sceneId"] = item.Parent, ["sceneName"] = after.Get(item.Parent)?.Name ?? "",
                }));
            if (item.IsEffect && old.Enabled != item.Enabled)
                changes.Add(new(MeldEvents.EffectEnabledStateChanged, new Dictionary<string, object?>
                {
                    ["effectId"] = item.Id, ["effectName"] = item.Name, ["enabled"] = item.Enabled,
                    ["layerId"] = item.Parent, ["layerName"] = after.Get(item.Parent)?.Name ?? "",
                }));
            if (item.IsTrack && old.Muted != item.Muted)
                changes.Add(new(MeldEvents.TrackMutedStateChanged, new Dictionary<string, object?>
                {
                    ["trackId"] = item.Id, ["trackName"] = after.DisplayName(item), ["muted"] = item.Muted,
                }));
            if (item.IsTrack && old.Monitoring != item.Monitoring)
                changes.Add(new(MeldEvents.TrackMonitoringStateChanged, new Dictionary<string, object?>
                {
                    ["trackId"] = item.Id, ["trackName"] = after.DisplayName(item), ["monitoring"] = item.Monitoring,
                }));
            if (item.IsScene && old.Staged != item.Staged && item.Staged)
                changes.Add(new(MeldEvents.SceneStaged, new Dictionary<string, object?> { ["sceneId"] = item.Id, ["sceneName"] = item.Name }));
            if (OtherPropertiesChanged(old.Json, item.Json)) changes.Add(Object(MeldEvents.ObjectUpdated, after, item));
        }
        foreach (var gone in before.Items.Where(i => after.Get(i.Id) is null))
            changes.Add(Object(MeldEvents.ObjectRemoved, before, gone));
        return changes;
    }

    private static MeldChange Object(string name, MeldSession session, MeldItem item)
    {
        var obj = new Dictionary<string, object?> { ["id"] = item.Id };
        foreach (var p in item.Json.EnumerateObject())
            if (p.Name != "type") obj[p.Name] = JsonUtil.ToArg(p.Value);
        obj["name"] = session.DisplayName(item);
        return new(name, new Dictionary<string, object?> { ["id"] = item.Id, ["name"] = session.DisplayName(item), ["objectType"] = item.Type, ["object"] = obj });
    }

    private static bool OtherPropertiesChanged(JsonElement before, JsonElement after)
    {
        var a = before.EnumerateObject().Where(p => !SpecificProperties.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.GetRawText());
        var b = after.EnumerateObject().Where(p => !SpecificProperties.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.GetRawText());
        return a.Count != b.Count || a.Any(kv => !b.TryGetValue(kv.Key, out var v) || v != kv.Value);
    }
}
