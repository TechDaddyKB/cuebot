using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>Translates OBS events into bot events.</summary>
internal static class ObsEventMapper
{
    private const string OutputStarted = "OBS_WEBSOCKET_OUTPUT_STARTED";
    private const string OutputStopped = "OBS_WEBSOCKET_OUTPUT_STOPPED";

    /// <summary>Maps StreamStateChanged / RecordStateChanged; null for every other event or transitional state.</summary>
    public static BotEvent? MapOutputEvent(ObsEvent e) => (e.EventType, e.EventData.GetStringOrNull("outputState")) switch
    {
        ("StreamStateChanged", OutputStarted) => new BotEvent(ObsEvents.StreamStarted),
        ("StreamStateChanged", OutputStopped) => new BotEvent(ObsEvents.StreamStopped),
        ("RecordStateChanged", OutputStarted) => new BotEvent(ObsEvents.RecordStarted, RecordArgs(e.EventData)),
        ("RecordStateChanged", OutputStopped) => new BotEvent(ObsEvents.RecordStopped, RecordArgs(e.EventData)),
        _ => null,
    };

    public static BotEvent SceneChanged(string sceneName, string? previousScene) => new(ObsEvents.SceneChanged,
        new Dictionary<string, object?> { ["sceneName"] = sceneName, ["previousScene"] = previousScene ?? "" });

    private static Dictionary<string, object?>? RecordArgs(JsonElement data) =>
        data.GetStringOrNull("outputPath") is { } path ? new() { ["outputPath"] = path } : null;
}
