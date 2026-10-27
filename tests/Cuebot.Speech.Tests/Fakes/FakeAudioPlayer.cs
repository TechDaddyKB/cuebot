using System.Threading.Channels;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Player that records playbacks and, when <see cref="Block"/> is set, holds each one until released.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    private readonly Channel<Playback> _started = Channel.CreateUnbounded<Playback>();

    public bool Block { get; set; }

    public PlaybackOptions? LastOptions { get; private set; }

    public IReadOnlyList<AudioDevice> GetOutputDevices() => [new("fake-1", "Fake speakers", true), new("fake-2", "Fake headset", false)];

    public async Task PlayAsync(byte[] wav, PlaybackOptions options, CancellationToken ct)
    {
        LastOptions = options;
        var playback = new Playback(wav);
        _started.Writer.TryWrite(playback);
        if (Block) await playback.Released.Task.WaitAsync(ct);
    }

    /// <summary>Waits for the next playback to start.</summary>
    public async Task<Playback> NextAsync() =>
        await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed class Playback(byte[] wav)
{
    public byte[] Wav { get; } = wav;
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => Released.TrySetResult();
}
