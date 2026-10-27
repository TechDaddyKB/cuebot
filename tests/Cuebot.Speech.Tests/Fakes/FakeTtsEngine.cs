using System.Threading.Channels;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Engine that records what it was asked to say and returns a WAV of constant samples.</summary>
internal sealed class FakeTtsEngine(string id = "fake", bool available = true, params string[] voices) : ITtsEngine
{
    public const short SampleValue = 1000;

    private readonly Channel<SynthesisCall> _calls = Channel.CreateUnbounded<SynthesisCall>();
    private readonly List<SynthesisCall> _history = [];

    public string Id => id;
    public string DisplayName => id;
    public bool IsAvailable => available;
    public string? UnavailableReason => available ? null : "fake engine disabled";

    /// <summary>When set, synthesis throws this exception.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<SynthesisCall> History
    {
        get
        {
            lock (_history) return _history.ToArray();
        }
    }

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VoiceInfo>>(voices.Select(v => new VoiceInfo(v, v, null)).ToArray());

    public Task<byte[]> SynthesizeAsync(string text, string voiceId, int rate, CancellationToken ct)
    {
        var call = new SynthesisCall(text, voiceId, rate);
        lock (_history) _history.Add(call);
        _calls.Writer.TryWrite(call);
        if (Failure is not null) throw Failure;
        var pcm = new byte[8];
        for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), SampleValue);
        return Task.FromResult(Wav.FromPcm16(pcm, 16000, 1));
    }

    /// <summary>Waits for the next synthesis request.</summary>
    public async Task<SynthesisCall> NextAsync() =>
        await _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed record SynthesisCall(string Text, string VoiceId, int Rate);
