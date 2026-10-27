using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>Translates obs-websocket events into bot events (pure; the connection adds its own name).</summary>
internal static class ObsEventMapper
{
    public const string ConnectionArg = "obsConnection";

    private const string OutputStarted = "OBS_WEBSOCKET_OUTPUT_STARTED";
    private const string OutputStopped = "OBS_WEBSOCKET_OUTPUT_STOPPED";
    private const string OutputPaused = "OBS_WEBSOCKET_OUTPUT_PAUSED";
    private const string OutputResumed = "OBS_WEBSOCKET_OUTPUT_RESUMED";
    private const string MediaActionPrefix = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_";

    /// <summary>Maps the output state events (stream, record, replay buffer, virtual camera); null for transitional states.</summary>
    public static BotEvent? MapOutputEvent(ObsEvent e) => (e.EventType, e.EventData.GetStringOrNull("outputState")) switch
    {
        ("StreamStateChanged", OutputStarted) => new BotEvent(ObsEvents.StreamStarted),
        ("StreamStateChanged", OutputStopped) => new BotEvent(ObsEvents.StreamStopped),
        ("RecordStateChanged", OutputStarted) => new BotEvent(ObsEvents.RecordStarted, RecordArgs(e.EventData)),
        ("RecordStateChanged", OutputStopped) => new BotEvent(ObsEvents.RecordStopped, RecordArgs(e.EventData)),
        ("RecordStateChanged", OutputPaused) => new BotEvent(ObsEvents.RecordPaused, RecordArgs(e.EventData)),
        ("RecordStateChanged", OutputResumed) => new BotEvent(ObsEvents.RecordResumed, RecordArgs(e.EventData)),
        ("ReplayBufferStateChanged", OutputStarted) => new BotEvent(ObsEvents.ReplayBufferStarted),
        ("ReplayBufferStateChanged", OutputStopped) => new BotEvent(ObsEvents.ReplayBufferStopped),
        ("VirtualcamStateChanged", OutputStarted) => new BotEvent(ObsEvents.VirtualCamStarted),
        ("VirtualcamStateChanged", OutputStopped) => new BotEvent(ObsEvents.VirtualCamStopped),
        _ => null,
    };

    /// <summary>
    /// Maps every event the module republishes except program scene changes and scene item visibility (which need
    /// the connection's state). Null when the event has no dedicated bot event (it still fires <see cref="ObsEvents.Event"/>).
    /// </summary>
    public static BotEvent? Map(ObsEvent e)
    {
        if (MapOutputEvent(e) is { } output) return output;
        var d = e.EventData;
        return e.EventType switch
        {
            "ReplayBufferSaved" => Event(ObsEvents.ReplayBufferSaved, ("savedReplayPath", Str(d, "savedReplayPath"))),
            "RecordFileChanged" => Event(ObsEvents.RecordFileChanged, ("newOutputPath", Str(d, "newOutputPath"))),
            "StudioModeStateChanged" => Event(ObsEvents.StudioModeChanged, ("studioModeEnabled", d.GetBoolOrNull("studioModeEnabled") ?? false)),
            "CurrentPreviewSceneChanged" => Event(ObsEvents.PreviewSceneChanged, ("sceneName", Str(d, "sceneName"))),
            "SceneTransitionStarted" => Event(ObsEvents.TransitionStarted, ("transitionName", Str(d, "transitionName"))),
            "SceneTransitionEnded" => Event(ObsEvents.TransitionEnded, ("transitionName", Str(d, "transitionName"))),
            "CurrentSceneTransitionChanged" => Event(ObsEvents.TransitionChanged, ("transitionName", Str(d, "transitionName"))),
            "CurrentSceneTransitionDurationChanged" => Event(ObsEvents.TransitionDurationChanged,
                ("transitionDuration", d.GetDoubleOrNull("transitionDuration") ?? 0)),
            "SourceFilterEnableStateChanged" => Event(ObsEvents.FilterStateChanged, ("sourceName", Str(d, "sourceName")),
                ("filterName", Str(d, "filterName")), ("filterEnabled", d.GetBoolOrNull("filterEnabled") ?? false)),
            "MediaInputPlaybackStarted" => Event(ObsEvents.MediaStarted, ("inputName", Str(d, "inputName"))),
            "MediaInputPlaybackEnded" => Event(ObsEvents.MediaEnded, ("inputName", Str(d, "inputName"))),
            "MediaInputActionTriggered" => Event(ObsEvents.MediaAction, ("inputName", Str(d, "inputName")),
                ("mediaAction", MediaActionName(d.GetStringOrNull("mediaAction")))),
            "InputMuteStateChanged" => Event(ObsEvents.InputMuteChanged, ("inputName", Str(d, "inputName")),
                ("inputMuted", d.GetBoolOrNull("inputMuted") ?? false)),
            "InputVolumeChanged" => Event(ObsEvents.InputVolumeChanged, ("inputName", Str(d, "inputName")),
                ("inputVolumeMul", d.GetDoubleOrNull("inputVolumeMul") ?? 0), ("inputVolumeDb", d.GetDoubleOrNull("inputVolumeDb") ?? 0)),
            "CurrentProfileChanged" => Event(ObsEvents.ProfileChanged, ("profileName", Str(d, "profileName"))),
            "CurrentSceneCollectionChanged" => Event(ObsEvents.SceneCollectionChanged, ("sceneCollectionName", Str(d, "sceneCollectionName"))),
            "ScreenshotSaved" => Event(ObsEvents.ScreenshotSaved, ("savedScreenshotPath", Str(d, "savedScreenshotPath"))),
            "VendorEvent" => Event(ObsEvents.VendorEvent, ("vendorName", Str(d, "vendorName")),
                ("vendorEventType", Str(d, "eventType")), ("eventData", RawJson(d, "eventData"))),
            _ => null,
        };
    }

    /// <summary>The catch-all <see cref="ObsEvents.Event"/> for any obs-websocket event.</summary>
    public static BotEvent Generic(ObsEvent e) => Event(ObsEvents.Event, ("eventType", e.EventType),
        ("eventData", e.EventData.ValueKind == JsonValueKind.Undefined ? "{}" : e.EventData.GetRawText()));

    public static BotEvent SceneChanged(string sceneName, string? previousScene) => new(ObsEvents.SceneChanged,
        new Dictionary<string, object?> { ["sceneName"] = sceneName, ["previousScene"] = previousScene ?? "" });

    public static BotEvent SourceVisibilityChanged(string sceneName, string sourceName, int sceneItemId, bool visible) =>
        Event(ObsEvents.SourceVisibilityChanged, ("sceneName", sceneName), ("sourceName", sourceName),
            ("sceneItemId", sceneItemId), ("visible", visible));

    /// <summary>A copy of <paramref name="e"/> whose args also name the connection it came from.</summary>
    public static BotEvent WithConnection(BotEvent e, string connection)
    {
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase) { [ConnectionArg] = connection };
        return new BotEvent(e.Type, args);
    }

    /// <summary>"OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY" -> "play".</summary>
    internal static string MediaActionName(string? action) => action is null
        ? ""
        : (action.StartsWith(MediaActionPrefix, StringComparison.Ordinal) ? action[MediaActionPrefix.Length..] : action).ToLowerInvariant();

    private static string Str(JsonElement d, string property) => d.GetStringOrNull(property) ?? "";

    private static BotEvent Event(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    private static string RawJson(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) ? value.GetRawText() : "{}";

    private static Dictionary<string, object?>? RecordArgs(JsonElement data) =>
        data.GetStringOrNull("outputPath") is { } path ? new() { ["outputPath"] = path } : null;
}
