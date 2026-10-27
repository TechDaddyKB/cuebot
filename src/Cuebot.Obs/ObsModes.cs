namespace Cuebot.Obs;

/// <summary>What to do with a scene item's visibility.</summary>
public enum VisibilityMode { Visible, Hidden, Toggle }

/// <summary>What to do with an input's mute state.</summary>
public enum MuteMode { Mute, Unmute, Toggle }

/// <summary>What to do with the stream, recording or virtual camera output.</summary>
public enum OutputCommand { Start, Stop, Toggle }

/// <summary>What to do with the recording output.</summary>
public enum RecordCommand { Start, Stop, Toggle, Pause, Resume, TogglePause }

/// <summary>What to do with the replay buffer.</summary>
public enum ReplayBufferCommand { Start, Stop, Toggle, Save }

/// <summary>Enable, disable or toggle something on/off (filters, studio mode, audio tracks).</summary>
public enum EnableMode { Enable, Disable, Toggle }

/// <summary>Media source controls.</summary>
public enum MediaCommand { Play, Pause, Stop, Restart, Next, Previous, TogglePlayPause }

/// <summary>How a volume value is interpreted.</summary>
public enum VolumeMode { SetDb, SetMultiplier, AdjustDb }

/// <summary>Whether a media seek is absolute or relative to the current position.</summary>
public enum SeekMode { Absolute, Offset }

public enum FlipAxis { Horizontal, Vertical }

/// <summary>Which output <c>Get output state</c> reads.</summary>
public enum ObsOutputKind { Stream, Record, ReplayBuffer, VirtualCam, Other }
