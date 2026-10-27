using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>Bot event types published by the VTube Studio integration.</summary>
public static class VTubeStudioEvents
{
    public const string Prefix = "vtubestudio";
    public const string Connected = Prefix + ".connected";
    public const string Disconnected = Prefix + ".disconnected";
    public const string ModelLoaded = Prefix + ".modelLoaded";
    public const string ModelUnloaded = Prefix + ".modelUnloaded";
    public const string HotkeyTriggered = Prefix + ".hotkeyTriggered";
    public const string TrackingStatusChanged = Prefix + ".trackingStatusChanged";
    public const string TrackingLost = Prefix + ".trackingLost";
    public const string TrackingFound = Prefix + ".trackingFound";
    public const string BackgroundChanged = Prefix + ".backgroundChanged";
    public const string ModelConfigChanged = Prefix + ".modelConfigChanged";
    public const string ModelMoved = Prefix + ".modelMoved";
    public const string ModelAnimation = Prefix + ".modelAnimation";
    public const string ItemEvent = Prefix + ".itemEvent";
    public const string ModelClicked = Prefix + ".modelClicked";
    public const string ExpressionToggled = Prefix + ".expressionToggled";
    public const string PostProcessingChanged = Prefix + ".postProcessingChanged";

    /// <summary>Every VTube Studio API event, raw (eventName + data JSON).</summary>
    public const string Any = Prefix + ".event";
}

/// <summary>Tracking state remembered between TrackingStatusChangedEvents to detect "lost"/"found".</summary>
internal sealed class VtsTrackingState
{
    public bool? FaceFound { get; set; }
}

/// <summary>Maps VTube Studio API events to bot events (pure; see PROTOCOL.md for the field list).</summary>
internal static class VtsEventMapper
{
    public static IReadOnlyList<(string Type, Dictionary<string, object?> Args)> Map(string eventName, JsonElement data, VtsTrackingState tracking)
    {
        var result = new List<(string, Dictionary<string, object?>)>();
        switch (eventName)
        {
            case "ModelLoadedEvent":
                result.Add((data.Bool("modelLoaded") == false ? VTubeStudioEvents.ModelUnloaded : VTubeStudioEvents.ModelLoaded,
                    Args(("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")), ("modelLoaded", data.Bool("modelLoaded") ?? false))));
                break;
            case "HotkeyTriggeredEvent":
                result.Add((VTubeStudioEvents.HotkeyTriggered, Args(
                    ("hotkeyId", data.Str("hotkeyID")), ("hotkeyName", data.Str("hotkeyName")), ("hotkeyAction", data.Str("hotkeyAction")),
                    ("hotkeyFile", data.Str("hotkeyFile")), ("triggeredByApi", data.Bool("hotkeyTriggeredByAPI") ?? false),
                    ("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")), ("isLive2DItem", data.Bool("isLive2DItem") ?? false))));
                break;
            case "TrackingStatusChangedEvent":
            {
                var face = data.Bool("faceFound") ?? false;
                var args = Args(("faceFound", face), ("leftHandFound", data.Bool("leftHandFound") ?? false),
                    ("rightHandFound", data.Bool("rightHandFound") ?? false));
                result.Add((VTubeStudioEvents.TrackingStatusChanged, args));
                if (tracking.FaceFound != face)
                {
                    // The first status after connecting only reports a loss (a model starting untracked is worth a trigger).
                    if (tracking.FaceFound is not null || !face)
                        result.Add((face ? VTubeStudioEvents.TrackingFound : VTubeStudioEvents.TrackingLost, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
                    tracking.FaceFound = face;
                }
                break;
            }
            case "BackgroundChangedEvent":
                result.Add((VTubeStudioEvents.BackgroundChanged, Args(("backgroundName", data.Str("backgroundName")))));
                break;
            case "ModelConfigChangedEvent":
                result.Add((VTubeStudioEvents.ModelConfigChanged, Args(("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")),
                    ("hotkeyConfigChanged", data.Bool("hotkeyConfigChanged") ?? false))));
                break;
            case "ModelMovedEvent":
            {
                var position = data.Obj("modelPosition");
                result.Add((VTubeStudioEvents.ModelMoved, Args(("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")),
                    ("positionX", position.Num("positionX") ?? 0), ("positionY", position.Num("positionY") ?? 0),
                    ("rotation", position.Num("rotation") ?? 0), ("size", position.Num("size") ?? 0))));
                break;
            }
            case "ModelAnimationEvent":
                result.Add((VTubeStudioEvents.ModelAnimation, Args(
                    ("animationEventType", data.Str("animationEventType")), ("animationEventTime", data.Num("animationEventTime") ?? 0),
                    ("animationEventData", data.Str("animationEventData")), ("animationName", data.Str("animationName")),
                    ("animationLength", data.Num("animationLength") ?? 0), ("isIdleAnimation", data.Bool("isIdleAnimation") ?? false),
                    ("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")), ("isLive2DItem", data.Bool("isLive2DItem") ?? false))));
                break;
            case "ItemEvent":
            {
                var position = data.Obj("itemPosition");
                result.Add((VTubeStudioEvents.ItemEvent, Args(("itemEventType", data.Str("itemEventType")),
                    ("itemInstanceId", data.Str("itemInstanceID")), ("itemFileName", data.Str("itemFileName")),
                    ("itemX", position.Num("x") ?? 0), ("itemY", position.Num("y") ?? 0))));
                break;
            }
            case "ModelClickedEvent":
            {
                var click = data.Obj("clickPosition");
                var button = (int)(data.Num("mouseButtonID") ?? 0);
                result.Add((VTubeStudioEvents.ModelClicked, Args(("modelLoaded", data.Bool("modelLoaded") ?? false),
                    ("modelName", data.Str("loadedModelName")), ("modelId", data.Str("loadedModelID")),
                    ("modelWasClicked", data.Bool("modelWasClicked") ?? false), ("mouseButtonId", button),
                    ("mouseButton", button switch { 0 => "Left", 1 => "Right", 2 => "Middle", _ => button.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                    ("clickX", click.Num("x") ?? 0), ("clickY", click.Num("y") ?? 0),
                    ("clickedArtMeshCount", (int)(data.Num("clickedArtMeshCount") ?? 0)),
                    ("artMeshHits", data.Prop("artMeshHits")?.GetRawText() ?? "[]"))));
                break;
            }
            case "ExpressionToggledEvent":
                result.Add((VTubeStudioEvents.ExpressionToggled, Args(("expressionName", data.Str("expressionName")),
                    ("expressionFile", data.Str("expressionFile")), ("active", data.Bool("active") ?? false),
                    ("modelName", data.Str("modelName")), ("modelId", data.Str("modelID")))));
                break;
            case "PostProcessingEvent":
                result.Add((VTubeStudioEvents.PostProcessingChanged, Args(("postProcessingOn", data.Bool("currentOnState") ?? false),
                    ("postProcessingPreset", data.Str("currentPreset")))));
                break;
        }
        result.Add((VTubeStudioEvents.Any, Args(("eventName", eventName), ("data", data.ValueKind == JsonValueKind.Undefined ? "{}" : data.GetRawText()))));
        return result;
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] values)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values) args[key] = value;
        return args;
    }
}
