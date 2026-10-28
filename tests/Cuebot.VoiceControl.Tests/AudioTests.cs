using Cuebot.VoiceControl.Audio;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Tests.Support;

namespace Cuebot.VoiceControl.Tests;

public sealed class ResamplerTests
{
    [Fact]
    public void Downsampling_48k_to_16k_keeps_length_level_and_low_frequencies()
    {
        var resampler = new Resampler(48000, 16000);
        var output = new List<float>();
        var input = Signals.Tone(48000, rate: 48000, hz: 440, amplitude: 0.5f);
        // Feed in odd-sized chunks like an audio callback would.
        for (var i = 0; i < input.Length; i += 1013) resampler.Process(input.AsSpan(i, Math.Min(1013, input.Length - i)), output);
        Assert.Equal(16000, output.Count);
        var rms = Math.Sqrt(output.Average(s => (double)s * s));
        Assert.InRange(rms, 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.01);
        var crossings = output.Zip(output.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        Assert.InRange(crossings, 438, 441);
    }

    [Fact]
    public void Downsampling_by_a_fractional_ratio_and_upsampling_produce_the_right_number_of_samples()
    {
        var down = new List<float>();
        new Resampler(44100, 16000).Process(new float[44100], down);
        Assert.InRange(down.Count, 15999, 16000);

        var up = new List<float>();
        var resampler = new Resampler(8000, 16000);
        resampler.Process(Enumerable.Repeat(0.25f, 8000).ToArray(), up);
        Assert.InRange(up.Count, 15998, 16002);
        Assert.All(up.Skip(2), s => Assert.Equal(0.25f, s, 3));
    }

    [Fact]
    public void Same_rate_passes_samples_through()
    {
        var output = new List<float>();
        new Resampler(16000, 16000).Process([0.1f, 0.2f], output);
        Assert.Equal([0.1f, 0.2f], output);
    }
}

public sealed class UtteranceSegmenterTests
{
    private readonly VoiceActivitySettings _vad = new() { SilenceMs = 400, MinSpeechMs = 200, MaxUtteranceSeconds = 3 };
    private readonly List<(long Id, float[] Samples)> _utterances = [];
    private readonly List<(long Id, float[] Samples)> _interims = [];
    private readonly UtteranceSegmenter _segmenter;

    public UtteranceSegmenterTests()
    {
        _segmenter = new UtteranceSegmenter(16000, () => _vad);
        _segmenter.UtteranceEnded += (id, s) => _utterances.Add((id, s));
        _segmenter.Interim += (id, s) => _interims.Add((id, s));
    }

    [Fact]
    public void Speech_between_pauses_becomes_one_utterance_with_pre_roll_and_a_short_tail()
    {
        _segmenter.Process(Signals.Concat(Signals.Silence(16000), Signals.Tone(16000), Signals.Silence(16000)));
        var (id, samples) = Assert.Single(_utterances);
        Assert.Equal(1, id);
        // 1 s of speech + up to 300 ms pre-roll + 200 ms tail.
        Assert.InRange(samples.Length, 16000 + 3000, 16000 + 4800 + 3200 + 320);
        Assert.False(_segmenter.InSpeech);
        Assert.InRange(_segmenter.NoiseFloorDb, -80, -60);
    }

    [Fact]
    public void Clicks_shorter_than_the_minimum_are_ignored()
    {
        _segmenter.Process(Signals.Concat(Signals.Silence(16000), Signals.Tone(1600), Signals.Silence(16000)));
        Assert.Empty(_utterances);
    }

    [Fact]
    public void Long_speech_is_cut_at_the_maximum_length_and_continues_as_a_new_utterance()
    {
        _segmenter.Process(Signals.Concat(Signals.Silence(8000), Signals.Tone(16000 * 5), Signals.Silence(16000)));
        Assert.Equal(2, _utterances.Count);
        Assert.Equal(16000 * 3, _utterances[0].Samples.Length);
        Assert.Equal([1L, 2L], _utterances.Select(u => u.Id));
    }

    [Fact]
    public void Interim_snapshots_arrive_while_speaking_with_the_utterance_id()
    {
        _segmenter.InterimIntervalMs = 500;
        _segmenter.Process(Signals.Concat(Signals.Silence(8000), Signals.Tone(16000 * 2), Signals.Silence(16000)));
        Assert.True(_interims.Count >= 3, $"{_interims.Count} interim snapshots");
        Assert.All(_interims, i => Assert.Equal(1, i.Id));
        Assert.True(_interims[1].Samples.Length > _interims[0].Samples.Length);
        Assert.Single(_utterances);
    }

    [Fact]
    public void Flush_ends_the_utterance_in_progress()
    {
        _segmenter.Process(Signals.Concat(Signals.Silence(8000), Signals.Tone(8000)));
        Assert.True(_segmenter.InSpeech);
        _segmenter.Flush();
        Assert.Single(_utterances);
    }
}

public sealed class UtteranceRecognitionSessionTests
{
    private static readonly RecognitionOptions Options = new("en-US", ["start"], ["start"], InterimResults: false);

    [Fact]
    public async Task Utterances_are_transcribed_in_order_and_raised_as_final_results()
    {
        var transcriber = new FakeTranscriber();
        var results = new List<RecognitionResult>();
        var session = new UtteranceRecognitionSession(transcriber, Options, () => new VoiceActivitySettings { SilenceMs = 300 });
        session.Recognized += r => { lock (results) results.Add(r); };
        session.Accept(Signals.Concat(Signals.Silence(8000), Signals.Tone(8000), Signals.Silence(8000), Signals.Tone(16000), Signals.Silence(8000)));
        await Wait.UntilAsync(() => { lock (results) return results.Count == 2; }, "two results");
        Assert.All(results, r => Assert.True(r.IsFinal));
        Assert.Equal([1L, 2L], results.Select(r => r.UtteranceId));
        Assert.All(transcriber.Calls, c => Assert.True(c.Samples.Length >= 16000 * 11 / 10, "short clips are padded to 1.1 s"));
        Assert.Equal(0.8, results[0].Confidence);
        Assert.Equal(results[0].Text, Assert.Single(results[0].Alternates).Text);
        await session.DisposeAsync();
        Assert.True(transcriber.Disposed);
    }

    [Fact]
    public async Task Empty_transcriptions_raise_nothing_and_failures_are_reported()
    {
        var transcriber = new FakeTranscriber { Answer = _ => null };
        var results = 0;
        Exception? failure = null;
        var session = new UtteranceRecognitionSession(transcriber, Options, () => new VoiceActivitySettings { SilenceMs = 300 });
        session.Recognized += _ => results++;
        session.Failed += ex => failure = ex;
        session.Accept(Signals.Concat(Signals.Silence(8000), Signals.Tone(8000), Signals.Silence(8000)));
        await Wait.UntilAsync(() => transcriber.Calls.Count == 1, "transcription");
        await session.DrainAsync();
        Assert.Equal(0, results);

        transcriber.Answer = _ => throw new InvalidOperationException("native crash");
        session.Accept(Signals.Concat(Signals.Tone(8000), Signals.Silence(8000)));
        await Wait.UntilAsync(() => failure is not null, "failure");
        Assert.Equal("native crash", failure!.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Interim_snapshots_are_only_transcribed_when_the_worker_is_idle()
    {
        var gate = new TaskCompletionSource();
        var transcriber = new FakeTranscriber { Gate = gate };
        var results = new List<RecognitionResult>();
        var session = new UtteranceRecognitionSession(transcriber, Options with { InterimResults = true },
            () => new VoiceActivitySettings { SilenceMs = 300, MaxUtteranceSeconds = 30 });
        session.Recognized += r => { lock (results) results.Add(r); };
        // 4 s of speech: interim snapshots every second, but the first one blocks the worker, so later ones are skipped.
        session.Accept(Signals.Concat(Signals.Silence(8000), Signals.Tone(16000 * 4), Signals.Silence(8000)));
        gate.SetResult();
        await Wait.UntilAsync(() => { lock (results) return results.Any(r => r.IsFinal); }, "final result");
        await session.DrainAsync();
        Assert.Equal(1, results.Count(r => !r.IsFinal));
        Assert.Equal(1, results.Count(r => r.IsFinal));
        Assert.All(results, r => Assert.Equal(1, r.UtteranceId));
        await session.DisposeAsync();
    }

    [Fact]
    public void Clips_are_padded_symmetrically_to_the_whisper_minimum()
    {
        var padded = UtteranceRecognitionSession.Pad([1f, 1f]);
        Assert.Equal(17600, padded.Length);
        Assert.Equal(1f, padded[8799]);
        var longClip = new float[20000];
        Assert.Same(longClip, UtteranceRecognitionSession.Pad(longClip));
    }
}

public sealed class BlockingAudioStreamTests
{
    [Fact]
    public async Task Reads_wait_for_samples_and_return_pcm16_until_completed()
    {
        using var stream = new BlockingAudioStream();
        var buffer = new byte[8];
        var read = Task.Run(() => stream.Read(buffer, 0, buffer.Length));
        await Task.Delay(50);
        Assert.False(read.IsCompleted);
        stream.Write([1f, -1f]);
        await Task.Delay(50);
        Assert.False(read.IsCompleted, "SAPI expects full reads");
        stream.Write([0f, 0f]);
        Assert.Equal(8, await read);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(buffer, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(buffer, 2));
        stream.Complete();
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }
}
