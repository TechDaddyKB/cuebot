using System.Threading.Channels;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Player that records playbacks and, when <see cref="Block"/> is set, holds each one until released.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    private readonly Channel<Playback> _started = Channel.CreateUnbounded<Playback>();

    public bool Block { get; set; }

    public async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
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
