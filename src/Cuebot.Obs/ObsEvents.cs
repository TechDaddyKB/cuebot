namespace Cuebot.Obs;

/// <summary>
/// Bot event types published by the OBS module. Every event carries <c>obsConnection</c> (the connection's name)
/// besides the arguments listed here.
/// </summary>
public static class ObsEvents
{
    public const string Connected = "obs.connected";
    public const string Disconnected = "obs.disconnected";

    /// <summary>Args: sceneName, previousScene.</summary>
    public const string SceneChanged = "obs.sceneChanged";

    /// <summary>Studio mode preview changed. Args: sceneName.</summary>
    public const string PreviewSceneChanged = "obs.previewSceneChanged";

    public const string StreamStarted = "obs.streamStarted";
    public const string StreamStopped = "obs.streamStopped";

    /// <summary>Args: outputPath (when OBS reports it).</summary>
    public const string RecordStarted = "obs.recordStarted";
    public const string RecordStopped = "obs.recordStopped";
    public const string RecordPaused = "obs.recordPaused";
    public const string RecordResumed = "obs.recordResumed";

    /// <summary>Args: newOutputPath (the file the recording continues in after a split).</summary>
    public const string RecordFileChanged = "obs.recordFileChanged";

    public const string ReplayBufferStarted = "obs.replayBufferStarted";
    public const string ReplayBufferStopped = "obs.replayBufferStopped";

    /// <summary>Args: savedReplayPath.</summary>
    public const string ReplayBufferSaved = "obs.replayBufferSaved";

    public const string VirtualCamStarted = "obs.virtualCamStarted";
    public const string VirtualCamStopped = "obs.virtualCamStopped";

    /// <summary>Args: studioModeEnabled.</summary>
    public const string StudioModeChanged = "obs.studioModeChanged";

    /// <summary>Args: transitionName.</summary>
    public const string TransitionStarted = "obs.transitionStarted";
    public const string TransitionEnded = "obs.transitionEnded";

    /// <summary>The current transition was changed. Args: transitionName.</summary>
    public const string TransitionChanged = "obs.transitionChanged";

    /// <summary>Args: transitionDuration (ms).</summary>
    public const string TransitionDurationChanged = "obs.transitionDurationChanged";

    /// <summary>Args: sceneName, sourceName, sceneItemId, visible.</summary>
    public const string SourceVisibilityChanged = "obs.sourceVisibilityChanged";

    /// <summary>Args: sourceName, filterName, filterEnabled.</summary>
    public const string FilterStateChanged = "obs.filterStateChanged";

    /// <summary>Args: inputName.</summary>
    public const string MediaStarted = "obs.mediaStarted";
    public const string MediaEnded = "obs.mediaEnded";

    /// <summary>Args: inputName, mediaAction (play, pause, stop, restart, next, previous).</summary>
    public const string MediaAction = "obs.mediaAction";

    /// <summary>Args: inputName, inputMuted.</summary>
    public const string InputMuteChanged = "obs.inputMuteChanged";

    /// <summary>Args: inputName, inputVolumeMul, inputVolumeDb.</summary>
    public const string InputVolumeChanged = "obs.inputVolumeChanged";

    /// <summary>Args: profileName.</summary>
    public const string ProfileChanged = "obs.profileChanged";

    /// <summary>Args: sceneCollectionName.</summary>
    public const string SceneCollectionChanged = "obs.sceneCollectionChanged";

    /// <summary>Args: savedScreenshotPath.</summary>
    public const string ScreenshotSaved = "obs.screenshotSaved";

    /// <summary>A third-party plugin event. Args: vendorName, vendorEventType, eventData (raw JSON).</summary>
    public const string VendorEvent = "obs.vendorEvent";

    /// <summary>Any obs-websocket event. Args: eventType, eventData (raw JSON).</summary>
    public const string Event = "obs.event";
}
