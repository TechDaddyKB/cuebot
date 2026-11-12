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

    /// <summary>
    /// A source started or stopped showing on the live output (program), e.g. a camera in the scene that went live.
    /// High-volume: subscribed only while such a trigger is enabled. Args: inputName, videoActive.
    /// </summary>
    public const string SourceActiveChanged = "obs.sourceActiveChanged";

    /// <summary>A source started or stopped showing anywhere (program, preview, projector, properties). Args: inputName, videoShowing.</summary>
    public const string SourceShowChanged = "obs.sourceShowChanged";

    /// <summary>
    /// Talking detection from the volume meters: an input went above the trigger's level, or stayed below it for the
    /// trigger's quiet time. Args: inputName, talking, levelDb, thresholdDb, quietMs.
    /// </summary>
    public const string Talking = "obs.talking";

    /// <summary>A source in a scene was moved, resized, rotated or cropped. Args: sceneName, sourceName, sceneItemId, transform (JSON).</summary>
    public const string SourceTransformChanged = "obs.sourceTransformChanged";

    /// <summary>A stinger's video finished (before the transition itself ends). Args: transitionName.</summary>
    public const string TransitionVideoEnded = "obs.transitionVideoEnded";

    /// <summary>Args: inputName, monitorType (None, MonitorOnly, MonitorAndOutput).</summary>
    public const string AudioMonitorChanged = "obs.audioMonitorChanged";

    /// <summary>Args: inputName, inputAudioBalance (0 left, 0.5 center, 1 right).</summary>
    public const string AudioBalanceChanged = "obs.audioBalanceChanged";

    /// <summary>Args: inputName, inputAudioSyncOffset (ms).</summary>
    public const string AudioSyncOffsetChanged = "obs.audioSyncOffsetChanged";

    /// <summary>Args: inputName, inputAudioTracks (JSON).</summary>
    public const string AudioTracksChanged = "obs.audioTracksChanged";

    /// <summary>The sources of a scene were reordered. Args: sceneName.</summary>
    public const string SourceOrderChanged = "obs.sourceOrderChanged";

    /// <summary>Args: sceneName, sourceName, sceneItemId, locked.</summary>
    public const string SourceLockChanged = "obs.sourceLockChanged";

    /// <summary>Args: sourceName, filterName, filterKind.</summary>
    public const string FilterAdded = "obs.filterAdded";

    /// <summary>Args: sourceName, filterName.</summary>
    public const string FilterRemoved = "obs.filterRemoved";

    /// <summary>Args: sourceName, oldFilterName, filterName.</summary>
    public const string FilterRenamed = "obs.filterRenamed";

    /// <summary>Args: sourceName, filters (JSON).</summary>
    public const string FilterOrderChanged = "obs.filterOrderChanged";

    /// <summary>Args: sourceName, filterName, filterSettings (JSON). OBS 30.1+.</summary>
    public const string FilterSettingsChanged = "obs.filterSettingsChanged";

    /// <summary>Args: inputName, inputSettings (JSON). OBS 30.1+.</summary>
    public const string InputSettingsChanged = "obs.inputSettingsChanged";

    /// <summary>OBS is about to switch profile. Args: profileName (the current one).</summary>
    public const string ProfileChanging = "obs.profileChanging";

    /// <summary>OBS is about to switch scene collection. Args: sceneCollectionName (the current one).</summary>
    public const string SceneCollectionChanging = "obs.sceneCollectionChanging";

    /// <summary>OBS announced that it is shutting down (ExitStarted); the connection closes right after.</summary>
    public const string Exiting = "obs.exiting";

    /// <summary>
    /// A custom event another obs-websocket client (or Cuebot) broadcast through OBS. Args: eventData (raw JSON) and
    /// customEvent.&lt;field&gt; per top-level field.
    /// </summary>
    public const string CustomEvent = "obs.customEvent";

    /// <summary>Any obs-websocket event. Args: eventType, eventData (raw JSON).</summary>
    public const string Event = "obs.event";
}
