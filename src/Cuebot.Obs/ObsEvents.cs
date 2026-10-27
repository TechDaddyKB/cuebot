namespace Cuebot.Obs;

/// <summary>Bot event types published by the OBS module.</summary>
public static class ObsEvents
{
    public const string Connected = "obs.connected";
    public const string Disconnected = "obs.disconnected";
    public const string SceneChanged = "obs.sceneChanged";
    public const string StreamStarted = "obs.streamStarted";
    public const string StreamStopped = "obs.streamStopped";
    public const string RecordStarted = "obs.recordStarted";
    public const string RecordStopped = "obs.recordStopped";
}
