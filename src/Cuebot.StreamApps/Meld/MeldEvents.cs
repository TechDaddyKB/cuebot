namespace Cuebot.StreamApps.Meld;

/// <summary>Bot event types and Meld "Event" names published by <see cref="MeldStudioService"/>.</summary>
public static class MeldEvents
{
    public const string Prefix = "meldstudio";

    public const string Connected = "meldstudio.connected";
    public const string Disconnected = "meldstudio.disconnected";
    public const string SceneChanged = "meldstudio.sceneChanged";
    public const string RecordingStarted = "meldstudio.recordingStarted";
    public const string RecordingStopped = "meldstudio.recordingStopped";
    public const string StreamingStarted = "meldstudio.streamingStarted";
    public const string StreamingStopped = "meldstudio.streamingStopped";

    /// <summary>Every session change; <c>meldStudioEvent.event</c> names it (one of <see cref="EventNames"/>).</summary>
    public const string Event = "meldstudio.event";

    // Values of meldStudioEvent.event (the first six are the ones Streamer.bot documents).
    public const string LayerVisibilityChanged = "LayerVisibilityChanged";
    public const string EffectEnabledStateChanged = "EffectEnabledStateChanged";
    public const string TrackMutedStateChanged = "TrackMutedStateChanged";
    public const string TrackMonitoringStateChanged = "TrackMonitoringStateChanged";
    public const string ObjectAdded = "ObjectAdded";
    public const string ObjectUpdated = "ObjectUpdated";
    public const string ObjectRemoved = "ObjectRemoved";
    public const string SceneStaged = "SceneStaged";
    public const string TrackGainChanged = "TrackGainChanged";

    public static readonly string[] EventNames =
    [
        LayerVisibilityChanged, EffectEnabledStateChanged, TrackMutedStateChanged, TrackMonitoringStateChanged,
        ObjectAdded, ObjectUpdated, ObjectRemoved, SceneStaged, TrackGainChanged,
    ];
}
