using System.Globalization;
using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>One message of the VTube Studio public API.</summary>
internal sealed record VtsMessage(string MessageType, string RequestId, JsonElement Data)
{
    public bool IsError => MessageType == VtsProtocol.ApiError;
    public bool IsEvent => MessageType.EndsWith("Event", StringComparison.Ordinal);
}

/// <summary>Envelope building and parsing for the VTube Studio public API (see PROTOCOL.md).</summary>
internal static class VtsProtocol
{
    public const string ApiName = "VTubeStudioPublicAPI";
    public const string ApiVersion = "1.0";
    public const string ApiError = "APIError";

    /// <summary>errorID when the user clicked "Deny" on the plugin popup.</summary>
    public const int ErrorTokenRequestDenied = 50;

    /// <summary>Events Cuebot subscribes to after authenticating.</summary>
    public static readonly string[] SubscribedEvents =
    [
        "ModelLoadedEvent", "TrackingStatusChangedEvent", "BackgroundChangedEvent", "ModelConfigChangedEvent",
        "ModelMovedEvent", "HotkeyTriggeredEvent", "ModelAnimationEvent", "ItemEvent", "ModelClickedEvent",
        "ExpressionToggledEvent", "PostProcessingEvent",
    ];

    public static string Request(string messageType, string requestId, object? data) => JsonUtil.Serialize(new
    {
        apiName = ApiName,
        apiVersion = ApiVersion,
        requestID = requestId,
        messageType,
        data,
    });

    public static VtsMessage? Parse(string text)
    {
        if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } root) return null;
        var type = root.Str("messageType");
        if (type.Length == 0) return null;
        return new VtsMessage(type, root.Str("requestID"), root.Obj("data"));
    }

    public static IntegrationRequestException Error(VtsMessage message)
    {
        var id = (int)(message.Data.Num("errorID") ?? 0);
        var text = message.Data.Str("message");
        return new IntegrationRequestException($"VTube Studio error {id}: {(text.Length > 0 ? text : "request failed")}", id);
    }

    public static VtsCurrentModel ParseCurrentModel(JsonElement data)
    {
        var position = data.Obj("modelPosition");
        return new VtsCurrentModel(data.Bool("modelLoaded") ?? false, data.Str("modelName"), data.Str("modelID"),
            new VtsModelPosition(position.Num("positionX") ?? 0, position.Num("positionY") ?? 0,
                position.Num("rotation") ?? 0, position.Num("size") ?? 0))
        {
            Raw = data.ValueKind == JsonValueKind.Undefined ? default : data.Clone(),
        };
    }

    /// <summary>
    /// Streamer.bot's <c>model.*</c> variables of Get Current Model, from a CurrentModelResponse's data:
    /// id, name, vtsModelName, vtsModelIconName, live2DModelName, loadTime, timeSinceLoaded, numberOfLive2DParameters,
    /// numberOfLive2DArtmeshes, hasPhysicsFile, numberOfTextures, textureResolution, posX, posY, rotation, size.
    /// </summary>
    public static Dictionary<string, object?> StreamerBotModelArgs(VtsCurrentModel model)
    {
        var d = model.Raw;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model.id"] = model.Id,
            ["model.name"] = model.Name,
            ["model.vtsModelName"] = d.Str("vtsModelName"),
            ["model.vtsModelIconName"] = d.Str("vtsModelIconName"),
            ["model.live2DModelName"] = d.Str("live2DModelName"),
            ["model.loadTime"] = (long)(d.Num("modelLoadTime") ?? 0),
            ["model.timeSinceLoaded"] = (long)(d.Num("timeSinceModelLoaded") ?? 0),
            ["model.numberOfLive2DParameters"] = (long)(d.Num("numberOfLive2DParameters") ?? 0),
            ["model.numberOfLive2DArtmeshes"] = (long)(d.Num("numberOfLive2DArtmeshes") ?? 0),
            ["model.hasPhysicsFile"] = d.Bool("hasPhysicsFile") ?? false,
            ["model.numberOfTextures"] = (long)(d.Num("numberOfTextures") ?? 0),
            ["model.textureResolution"] = (long)(d.Num("textureResolution") ?? 0),
            ["model.posX"] = model.Position.PositionX,
            ["model.posY"] = model.Position.PositionY,
            ["model.rotation"] = model.Position.Rotation,
            ["model.size"] = model.Position.Size,
        };
    }

    /// <summary>
    /// Flattens a raw response as Streamer.bot's Send Raw Request does: <c>prefix.data.modelName</c>, with arrays as
    /// <c>prefix.data.list[0].name</c>.
    /// </summary>
    public static void StoreRawResponse(IDictionary<string, object?> args, string prefix, JsonElement data)
    {
        prefix = prefix.Trim();
        if (prefix.Length == 0) return;
        Flatten(args, prefix + ".data", data, 0);

        static void Flatten(IDictionary<string, object?> into, string path, JsonElement element, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object when depth < 8:
                    foreach (var property in element.EnumerateObject()) Flatten(into, $"{path}.{property.Name}", property.Value, depth + 1);
                    break;
                case JsonValueKind.Array when depth < 8:
                    var i = 0;
                    foreach (var item in element.EnumerateArray()) Flatten(into, $"{path}[{i++}]", item, depth + 1);
                    break;
                case JsonValueKind.String: into[path] = element.GetString(); break;
                case JsonValueKind.Number: into[path] = element.GetDouble(); break;
                case JsonValueKind.True or JsonValueKind.False: into[path] = element.GetBoolean(); break;
                case JsonValueKind.Undefined or JsonValueKind.Null: break;
                default: into[path] = element.GetRawText(); break;
            }
        }
    }

    public static VtsModel[] ParseModels(JsonElement data) =>
        data.Array("availableModels").Select(m => new VtsModel(m.Str("modelID"), m.Str("modelName"), m.Bool("modelLoaded") ?? false)).ToArray();

    public static VtsHotkey[] ParseHotkeys(JsonElement data) =>
        data.Array("availableHotkeys")
            .Select(h => new VtsHotkey(h.Str("hotkeyID"), h.Str("name"), h.Str("type"), h.Str("file"), h.Str("description")))
            .ToArray();

    public static VtsExpression[] ParseExpressions(JsonElement data) =>
        data.Array("expressions").Select(e => new VtsExpression(e.Str("file"), e.Str("name"), e.Bool("active") ?? false)).ToArray();

    public static VtsItemInstance[] ParseItemInstances(JsonElement data) =>
        data.Array("itemInstancesInScene")
            .Select(i => new VtsItemInstance(i.Str("instanceID"), i.Str("fileName"), (int)(i.Num("order") ?? 0), i.Str("type")))
            .ToArray();

    public static VtsItemFile[] ParseItemFiles(JsonElement data) =>
        data.Array("availableItemFiles")
            .Select(i => new VtsItemFile(i.Str("fileName"), i.Str("type"), (int)(i.Num("loadedCount") ?? 0)))
            .ToArray();

    public static string[] ParseParameterNames(JsonElement data) =>
        data.Array("defaultParameters").Concat(data.Array("customParameters")).Select(p => p.Str("name"))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Parses parameter values for InjectParameterDataRequest: "FaceAngleX=10, MouthOpen=1" (also ";" or new lines,
    /// "name=value@weight"), a JSON object {"FaceAngleX":10}, or a JSON array [{"id":"FaceAngleX","value":10,"weight":1}].
    /// </summary>
    public static IReadOnlyList<VtsParameterValue> ParseParameterValues(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Enter at least one parameter, e.g. FaceAngleX=10.");
        if (text[0] is '{' or '[')
        {
            var json = JsonUtil.TryParse(text) ?? throw new ArgumentException("Parameters are not valid JSON.");
            var list = json.ValueKind == JsonValueKind.Object
                ? json.EnumerateObject().Select(p => new VtsParameterValue(p.Name, p.Value.ValueKind == JsonValueKind.Number
                    ? p.Value.GetDouble() : Parse(p.Value.ToString(), p.Name), null)).ToList()
                : json.EnumerateArray().Select(e => new VtsParameterValue(Steps.Require(e.Str("id", "name"), "Parameter id"),
                    e.Num("value") ?? throw new ArgumentException($"Parameter '{e.Str("id", "name")}' needs a value."), e.Num("weight"))).ToList();
            return list.Count > 0 ? list : throw new ArgumentException("Enter at least one parameter.");
        }
        var values = new List<VtsParameterValue>();
        foreach (var entry in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOfAny(['=', ':']);
            if (eq <= 0) throw new ArgumentException($"'{entry}' is not name=value.");
            var name = entry[..eq].Trim();
            var rest = entry[(eq + 1)..].Trim();
            double? weight = null;
            var at = rest.IndexOf('@');
            if (at >= 0)
            {
                weight = Parse(rest[(at + 1)..], name + " weight");
                rest = rest[..at];
            }
            values.Add(new VtsParameterValue(name, Parse(rest, name), weight));
        }
        return values;

        static double Parse(string value, string label) =>
            double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : throw new ArgumentException($"'{label}' must be a number (got '{value.Trim()}').");
    }

    /// <summary>"#RRGGBB", "#RRGGBBAA" or "r,g,b[,a]" (0-255) → RGBA bytes.</summary>
    public static (int R, int G, int B, int A) ParseColor(string text)
    {
        text = text.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Contains(','))
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 255
                    ? v : throw new ArgumentException($"Color component '{p}' must be 0-255.")).ToArray();
            if (parts.Length is 3 or 4) return (parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : 255);
        }
        else if (text.Length is 6 or 8 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            int Hex(int i) => int.Parse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (Hex(0), Hex(2), Hex(4), text.Length == 8 ? Hex(6) : 255);
        }
        throw new ArgumentException($"'{text}' is not a color; use #RRGGBB, #RRGGBBAA or r,g,b[,a].");
    }

    public static string[] SplitList(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record VtsModelPosition(double PositionX, double PositionY, double Rotation, double Size);

public sealed record VtsCurrentModel(bool Loaded, string Name, string Id, VtsModelPosition Position)
{
    /// <summary>The whole CurrentModelResponse data (for Streamer.bot's model.* variables).</summary>
    public System.Text.Json.JsonElement Raw { get; init; }
}

public sealed record VtsModel(string Id, string Name, bool Loaded);

public sealed record VtsHotkey(string Id, string Name, string Type, string File, string Description);

public sealed record VtsExpression(string File, string Name, bool Active)
{
    /// <summary>The name without ".exp3.json" when VTube Studio sends no name.</summary>
    public string DisplayName => Name.Length > 0 ? Name : File.Replace(".exp3.json", "", StringComparison.OrdinalIgnoreCase);
}

public sealed record VtsItemInstance(string InstanceId, string FileName, int Order, string Type);

public sealed record VtsItemFile(string FileName, string Type, int LoadedCount);

public sealed record VtsParameterValue(string Id, double Value, double? Weight);
