namespace Cuebot.Speech;

/// <summary>Plays an in-memory WAV file to the default output device.</summary>
public interface IAudioPlayer
{
    /// <summary>Completes when playback ends; cancelling stops playback and throws <see cref="OperationCanceledException"/>.</summary>
    Task PlayAsync(byte[] wav, CancellationToken ct);
}
