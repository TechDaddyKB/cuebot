using System.Threading.Channels;
using Cuebot.VoiceControl.Audio;

namespace Cuebot.VoiceControl.Engines;

/// <summary>Text and confidence for one clip, or null when nothing intelligible was said.</summary>
internal sealed record Transcription(string Text, double Confidence, string? Language = null);

/// <summary>Transcribes whole clips (Whisper). The thin native layer; faked in tests.</summary>
internal interface IUtteranceTranscriber : IAsyncDisposable
{
    string Description { get; }

    /// <param name="samples">16 kHz mono.</param>
    Task<Transcription?> TranscribeAsync(float[] samples, RecognitionOptions options, CancellationToken ct);
}

/// <summary>
/// A session for clip-based engines: the <see cref="UtteranceSegmenter"/> cuts the stream at pauses and a single
/// worker transcribes each utterance in order. Interim snapshots are only transcribed when the worker is idle, so
/// they never delay final results; final results are never dropped.
/// </summary>
internal sealed class UtteranceRecognitionSession : IRecognitionSession
{
    public const int Rate = 16000;
    private const int InterimIntervalMs = 1000;

    /// <summary>Whisper refuses clips shorter than a second; shorter utterances are padded with silence.</summary>
    private const int MinimumClipSamples = Rate * 11 / 10;

    private readonly IUtteranceTranscriber _transcriber;
    private readonly UtteranceSegmenter _segmenter;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private RecognitionOptions _options;
    private int _pendingJobs;

    public UtteranceRecognitionSession(IUtteranceTranscriber transcriber, RecognitionOptions options, Func<VoiceActivitySettings> vad)
    {
        _transcriber = transcriber;
        _options = options;
        _segmenter = new UtteranceSegmenter(Rate, vad) { InterimIntervalMs = options.InterimResults ? InterimIntervalMs : 0 };
        _segmenter.UtteranceEnded += (id, samples) => Enqueue(new Job(id, samples, IsFinal: true));
        _segmenter.Interim += (id, samples) =>
        {
            if (Volatile.Read(ref _pendingJobs) == 0) Enqueue(new Job(id, samples, IsFinal: false));
        };
        _worker = Task.Run(WorkAsync);
    }

    public int SampleRate => Rate;
    public string Description => _transcriber.Description;

    /// <summary>The segmenter's live levels, for the UI meter.</summary>
    internal UtteranceSegmenter Segmenter => _segmenter;

    public event Action<RecognitionResult>? Recognized;
    public event Action<Exception>? Failed;

    public void Accept(ReadOnlySpan<float> samples)
    {
        lock (_gate) _segmenter.Process(samples);
    }

    public void UpdateOptions(RecognitionOptions options)
    {
        lock (_gate)
        {
            _options = options;
            _segmenter.InterimIntervalMs = options.InterimResults ? InterimIntervalMs : 0;
        }
    }

    /// <summary>Completes when every queued utterance has been transcribed (tests).</summary>
    internal async Task DrainAsync()
    {
        while (Volatile.Read(ref _pendingJobs) > 0 && !_worker.IsCompleted) await Task.Delay(5).ConfigureAwait(false);
    }

    private void Enqueue(Job job)
    {
        Interlocked.Increment(ref _pendingJobs);
        if (!_jobs.Writer.TryWrite(job)) Interlocked.Decrement(ref _pendingJobs);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    RecognitionOptions options;
                    lock (_gate) options = _options;
                    var clip = Pad(job.Samples);
                    var text = await _transcriber.TranscribeAsync(clip, options, _stop.Token).ConfigureAwait(false);
                    if (text is not null && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        Recognized?.Invoke(new RecognitionResult(job.Id, text.Text.Trim(), Math.Clamp(text.Confidence, 0, 1), job.IsFinal)
                        {
                            Alternates = [new RecognitionAlternate(text.Text.Trim(), Math.Clamp(text.Confidence, 0, 1))],
                            Language = text.Language,
                            Duration = TimeSpan.FromSeconds((double)job.Samples.Length / Rate),
                        });
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingJobs);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    internal static float[] Pad(float[] samples)
    {
        if (samples.Length >= MinimumClipSamples) return samples;
        var padded = new float[MinimumClipSamples];
        samples.CopyTo(padded, (MinimumClipSamples - samples.Length) / 2);
        return padded;
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); }
        catch (Exception) { /* already reported through Failed */ }
        _stop.Dispose();
        await _transcriber.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record Job(long Id, float[] Samples, bool IsFinal);
}
