namespace Cuebot.Speech;

/// <summary>An audio device as the operating system reports it.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>How to play one clip: which output device (null = system default) and at what volume (0-100).</summary>
public sealed record PlaybackOptions(string? DeviceId = null, int Volume = 100)
{
    public static readonly PlaybackOptions Default = new();
}

/// <summary>Plays in-memory audio (WAV; other formats where the backend supports them) on an output device.</summary>
public interface IAudioPlayer
{
    /// <summary>Completes when playback ends; cancelling stops playback and throws <see cref="OperationCanceledException"/>.</summary>
    Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct);

    /// <summary>Output devices that <see cref="PlaybackOptions.DeviceId"/> can name. Empty when the backend can't choose.</summary>
    IReadOnlyList<AudioDevice> GetOutputDevices();
}

/// <summary>Microphone level monitoring (used by VoiceGate to hold speech while the streamer talks).</summary>
public interface IAudioInput
{
    IReadOnlyList<AudioDevice> GetInputDevices();

    /// <summary>Reports the input level in dBFS (about -96..0) roughly 20 times a second until disposed.</summary>
    IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb);
}

public static class AudioPlayerExtensions
{
    /// <summary>Plays on the default device at full volume.</summary>
    public static Task PlayAsync(this IAudioPlayer player, byte[] audio, CancellationToken ct) =>
        player.PlayAsync(audio, PlaybackOptions.Default, ct);
}
