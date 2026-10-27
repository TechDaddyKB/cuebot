namespace Cuebot.Speech;

/// <summary>
/// Fallback backend over the WAV-only players (winmm PlaySound on Windows; pw-play/paplay/aplay on Linux), used when
/// the native library can't load. Default device only, no capture; winmm cannot mix.
/// </summary>
internal sealed class LegacyAudioBackend(IAudioPlayer player, string name, bool canMix) : IAudioBackend
{
    public string Name => name;
    public bool CanMix => canMix;

    public IReadOnlyList<AudioDevice> GetOutputDevices() => player.GetOutputDevices();
    public IReadOnlyList<AudioDevice> GetInputDevices() => [];
    public AudioClip? TryDecode(byte[] data) => null;
    public IAudioCapture? StartCapture(string? deviceId, CaptureCallback onSamples) => null;

    public IAudioVoice Play(AudioClip clip, string? deviceId, float gain)
    {
        var wav = clip.ToWav16((int)MathF.Round(gain * 100));
        return new Voice(token => player.PlayAsync(wav, new PlaybackOptions(deviceId), token));
    }

    public void Dispose() => (player as IDisposable)?.Dispose();

    /// <summary>Creates the platform's fallback player and names it for the log.</summary>
    public static LegacyAudioBackend CreateForPlatform()
    {
        if (OperatingSystem.IsWindows()) return new LegacyAudioBackend(new WindowsAudioPlayer(), "winmm PlaySound (fallback)", canMix: false);
        if (CommandLineAudioPlayer.FindInstalled() is { } command)
            return new LegacyAudioBackend(command, $"{Path.GetFileName(command.Executable)} (fallback)", canMix: true);
        return new LegacyAudioBackend(new UnavailableAudioPlayer(AudioPlayerFactory.NoPlayerMessage), "none", canMix: false);
    }

    private sealed class Voice : IAudioVoice
    {
        private readonly CancellationTokenSource _stop = new();

        public Voice(Func<CancellationToken, Task> play) => Completion = RunAsync(play);

        public Task Completion { get; }

        public void Stop()
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task RunAsync(Func<CancellationToken, Task> play)
        {
            try
            {
                await play(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Stopped on request: a normal end for the voice.
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }
}
