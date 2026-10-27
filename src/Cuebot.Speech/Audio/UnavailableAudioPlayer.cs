namespace Cuebot.Speech;

/// <summary>Stands in when no playback method exists, failing every request with an explanation.</summary>
internal sealed class UnavailableAudioPlayer(string reason) : IAudioPlayer
{
    public Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct) => Task.FromException(new InvalidOperationException(reason));

    public IReadOnlyList<AudioDevice> GetOutputDevices() => [];
}
