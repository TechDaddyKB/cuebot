using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Variables;
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
    public static BotEvent WithConnection(BotEvent e, string connection) => WithConnection(e, new ObsSource(connection, "", "", ""));

    /// <summary>
    /// A copy of <paramref name="e"/> whose args also name the connection it came from, plus Streamer.bot's names:
    /// <c>obs.id</c>, <c>obs.name</c>, <c>obs.host</c>, <c>obs.studioVersion</c>, <c>obs.websocketVersion</c> on every
    /// event, <c>obs.sceneName</c>/<c>obs.oldSceneName</c> (scene changed), <c>obsVendorEvent.*</c> (vendor event)
    /// and <c>obsEvent.event</c> plus <c>obsEvent.&lt;field&gt;</c> per top-level event data field (OBS event).
    /// </summary>
    public static BotEvent WithConnection(BotEvent e, ObsSource source)
    {
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase) { [ConnectionArg] = source.Name };
        // Streamer.bot identifies connections by a Guid; Cuebot's are named, so the id is derived from the name.
        StreamerBotArgs.Set(args, "obs.id", ConnectionId(source.Name));
        StreamerBotArgs.Set(args, "obs.name", source.Name);
        StreamerBotArgs.Set(args, "obs.host", source.Host);
        StreamerBotArgs.Set(args, "obs.studioVersion", source.StudioVersion);
        StreamerBotArgs.Set(args, "obs.websocketVersion", source.WebSocketVersion);
        switch (e.Type)
        {
            case ObsEvents.SceneChanged:
                StreamerBotArgs.Alias(args, "obs.sceneName", "sceneName");
                StreamerBotArgs.Alias(args, "obs.oldSceneName", "previousScene");
                break;
            case ObsEvents.VendorEvent:
                StreamerBotArgs.Alias(args, "obsVendorEvent.vendor", "vendorName");
                StreamerBotArgs.Alias(args, "obsVendorEvent.event", "vendorEventType");
                break;
            case ObsEvents.Event:
                StreamerBotArgs.Alias(args, "obsEvent.event", "eventType");
                if (args.GetValueOrDefault("eventData") is string { Length: > 1 } json) AddEventFields(args, json);
                break;
        }
        return new BotEvent(e.Type, args);
    }

    /// <summary>A stable Guid for a connection name (the same name gives the same id on every run).</summary>
    public static Guid ConnectionId(string name) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("obs:" + name)).AsSpan(0, 16));

    /// <summary><c>obsEvent.&lt;field&gt;</c> for each top-level field of the event data: values as text, objects and lists as JSON.</summary>
    private static void AddEventFields(Dictionary<string, object?> args, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                StreamerBotArgs.Set(args, "obsEvent." + p.Name, p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => "",
                    _ => p.Value.GetRawText(),
                });
            }
        }
        catch (JsonException)
        {
            // Not JSON: nothing to flatten.
        }
    }

    /// <summary>"OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY" -> "play".</summary>
    internal static string MediaActionName(string? action) => action is null
        ? ""
        : (action.StartsWith(MediaActionPrefix, StringComparison.Ordinal) ? action[MediaActionPrefix.Length..] : action).ToLowerInvariant();

    private static string Str(JsonElement d, string property) => d.GetStringOrNull(property) ?? "";

    /// <summary>The connection an event came from, for <see cref="WithConnection(BotEvent, ObsSource)"/>.</summary>
    public sealed record ObsSource(string Name, string Host, string StudioVersion, string WebSocketVersion);

    private static BotEvent Event(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    private static string RawJson(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) ? value.GetRawText() : "{}";

    private static Dictionary<string, object?>? RecordArgs(JsonElement data) =>
        data.GetStringOrNull("outputPath") is { } path ? new() { ["outputPath"] = path } : null;
}
