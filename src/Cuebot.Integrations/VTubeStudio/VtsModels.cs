using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>ArtMeshes of the current model (ArtMeshListResponse).</summary>
public sealed record VtsArtMeshList(IReadOnlyList<string> Names, IReadOnlyList<string> Tags, IReadOnlyList<VtsArtMeshGroup> Groups)
{
    public static VtsArtMeshList Empty { get; } = new([], [], []);
}

/// <summary>An ArtMesh/layer group the user set up in VTube Studio (group names need not be unique, ids are).</summary>
public sealed record VtsArtMeshGroup(string Id, string Name, IReadOnlyList<string> ArtMeshNames);

/// <summary>Physics of the current model (GetCurrentModelPhysicsResponse).</summary>
public sealed record VtsPhysics(bool HasPhysics, bool SwitchedOn, int BaseStrength, int BaseWind, IReadOnlyList<VtsPhysicsGroup> Groups)
{
    public static VtsPhysics None { get; } = new(false, false, 0, 0, []);
}

public sealed record VtsPhysicsGroup(string Id, string Name, double StrengthMultiplier, double WindMultiplier)
{
    public string DisplayName => Name.Length > 0 ? Name : Id;
}

/// <summary>The visual effects (post-processing) state (PostProcessingListResponse / PostProcessingUpdateResponse).</summary>
public sealed record VtsVfxState(bool Supported, bool Active, bool PresetActive, string ActivePreset, int ActiveEffectCount);

/// <summary>Where an ArtMesh was clicked, usable to pin an item at that exact spot (ModelClickedEvent artMeshHits).</summary>
public sealed record VtsArtMeshHit(string ModelId, string ArtMeshId, int Order, bool IsMasked, int VertexId1, int VertexId2, int VertexId3,
    double VertexWeight1, double VertexWeight2, double VertexWeight3);

/// <summary>One VTube Studio instance announced over UDP (VTubeStudioAPIStateBroadcast).</summary>
public sealed record VtsInstance(string InstanceId, string WindowTitle, int Port, bool Active, DateTimeOffset SeenAt);

/// <summary>A custom parameter Cuebot created (kept in the settings and created again on every connection).</summary>
public sealed class VtsCustomParameter
{
    public string Name { get; set; } = "";
    public string Explanation { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public double DefaultValue { get; set; }
}

internal static partial class VtsProtocol
{
    public static VtsArtMeshList ParseArtMeshes(JsonElement data) => new(
        data.Array("artMeshNames").Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToArray(),
        data.Array("artMeshTags").Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToArray(),
        data.Array("artMeshGroups").Select(g => new VtsArtMeshGroup(g.Str("groupID"), g.Str("groupName"),
            g.Array("artMeshNames").Select(n => n.GetString() ?? "").ToArray())).ToArray());

    public static VtsPhysics ParsePhysics(JsonElement data) =>
        data.Bool("modelLoaded") == false
            ? VtsPhysics.None
            : new VtsPhysics(data.Bool("modelHasPhysics") ?? false, data.Bool("physicsSwitchedOn") ?? false,
                (int)(data.Num("baseStrength") ?? 0), (int)(data.Num("baseWind") ?? 0),
                data.Array("physicsGroups").Select(g => new VtsPhysicsGroup(g.Str("groupID"), g.Str("groupName"),
                    g.Num("strengthMultiplier") ?? 1, g.Num("windMultiplier") ?? 1)).ToArray());

    public static VtsVfxState ParseVfx(JsonElement data) => new(data.Bool("postProcessingSupported") ?? true,
        data.Bool("postProcessingActive") ?? false, data.Bool("presetIsActive") ?? false, data.Str("activePreset"),
        (int)(data.Num("activeEffectCount") ?? 0));

    public static string[] ParsePresets(JsonElement data) =>
        data.Array("postProcessingPresets").Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToArray();

    public static VtsArtMeshHit[] ParseHits(JsonElement data) =>
        data.Array("artMeshHits").Select(h =>
        {
            var info = h.Obj("hitInfo");
            return new VtsArtMeshHit(info.Str("modelID"), info.Str("artMeshID"), (int)(h.Num("artMeshOrder") ?? 0), h.Bool("isMasked") ?? false,
                (int)(info.Num("vertexID1") ?? 0), (int)(info.Num("vertexID2") ?? 0), (int)(info.Num("vertexID3") ?? 0),
                info.Num("vertexWeight1") ?? 0, info.Num("vertexWeight2") ?? 0, info.Num("vertexWeight3") ?? 0);
        }).OrderBy(h => h.Order).ToArray();

    /// <summary>A VTubeStudioAPIStateBroadcast datagram, or null for anything else.</summary>
    public static VtsInstance? ParseBroadcast(string text, DateTimeOffset now)
    {
        if (Parse(text) is not { MessageType: "VTubeStudioAPIStateBroadcast" } message) return null;
        var port = (int)(message.Data.Num("port") ?? 0);
        return port is > 0 and < 65536
            ? new VtsInstance(message.Data.Str("instanceID"), message.Data.Str("windowTitle"), port, message.Data.Bool("active") ?? false, now)
            : null;
    }

    /// <summary>Custom parameter names: 4-32 letters and digits (no spaces).</summary>
    public static string CustomParameterName(string name)
    {
        name = Steps.Require(name, "Parameter name");
        if (name.Length is < 4 or > 32 || !name.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"'{name}' is not a valid parameter name: use 4 to 32 letters and digits, no spaces.");
        return name;
    }

    /// <summary>Checks injected values against the API limits (±1,000,000; weight 0-1).</summary>
    public static void ValidateInjection(IReadOnlyList<VtsParameterValue> values)
    {
        foreach (var v in values)
        {
            if (double.IsNaN(v.Value) || Math.Abs(v.Value) > 1_000_000)
                throw new ArgumentException($"The value of {v.Id} must be between -1000000 and 1000000.");
            if (v.Weight is { } w && (double.IsNaN(w) || w is < 0 or > 1))
                throw new ArgumentException($"The weight of {v.Id} must be between 0 and 1.");
        }
    }
}
