namespace Cuebot.VoiceControl.Audio;

/// <summary>
/// Energy-based voice activity detection: cuts a 16 kHz mono stream into utterances at pauses so Whisper (which
/// transcribes whole clips) gets one phrase at a time. The background noise level is tracked while nobody speaks;
/// speech is anything a margin above it. A short pre-roll keeps the first syllable, and long speech is cut at
/// <see cref="VoiceActivitySettings.MaxUtteranceSeconds"/>. Optionally hands out snapshots of the utterance so far
/// (interim results) at a fixed interval.
/// </summary>
internal sealed class UtteranceSegmenter
{
    public const int FrameMs = 20;
    private const int PreRollMs = 300;
    private const int TailMs = 200;
    private const double HysteresisDb = 3;

    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly Func<VoiceActivitySettings> _settings;
    private readonly float[] _frame;
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = [];
    private int _frameFill;
    private int _silentFrames;
    private int _voicedFrames;
    private int _lastInterimSamples;
    private long _nextId = 1;

    public UtteranceSegmenter(int sampleRate, Func<VoiceActivitySettings> settings)
    {
        _sampleRate = sampleRate;
        _settings = settings;
        _frameSamples = sampleRate * FrameMs / 1000;
        _frame = new float[_frameSamples];
    }

    /// <summary>A finished utterance: id and its samples.</summary>
    public event Action<long, float[]>? UtteranceEnded;

    /// <summary>The utterance so far, every <see cref="InterimIntervalMs"/> of speech.</summary>
    public event Action<long, float[]>? Interim;

    /// <summary>0 = no interim snapshots.</summary>
    public int InterimIntervalMs { get; set; }

    public bool InSpeech { get; private set; }

    /// <summary>Id of the utterance in progress (or the next one).</summary>
    public long CurrentId => _nextId;

    /// <summary>Level of the last 20 ms frame, dBFS.</summary>
    public double LevelDb { get; private set; } = -96;

    /// <summary>Estimated background noise, dBFS.</summary>
    public double NoiseFloorDb { get; private set; } = -60;

    /// <summary>Level a frame must reach to count as speech.</summary>
    public double ThresholdDb
    {
        get
        {
            var s = _settings();
            return Math.Max(s.MinimumSpeechDb, NoiseFloorDb + s.SpeechAboveNoiseDb);
        }
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            var take = Math.Min(samples.Length, _frameSamples - _frameFill);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill < _frameSamples) continue;
            _frameFill = 0;
            ProcessFrame(_frame);
        }
    }

    /// <summary>Ends the utterance in progress now (e.g. when listening stops).</summary>
    public void Flush()
    {
        if (InSpeech) EndUtterance();
    }

    private void ProcessFrame(float[] frame)
    {
        var s = _settings();
        double sum = 0;
        foreach (var x in frame) sum += (double)x * x;
        var db = Math.Sqrt(sum / frame.Length) is var rms && rms > 0 ? Math.Max(-96, 20 * Math.Log10(rms)) : -96;
        LevelDb = db;
        var threshold = ThresholdDb;

        if (!InSpeech)
        {
            if (db >= threshold)
            {
                InSpeech = true;
                _utterance.Clear();
                foreach (var f in _preRoll) _utterance.AddRange(f);
                _preRoll.Clear();
                _utterance.AddRange(frame);
                _voicedFrames = 1;
                _silentFrames = 0;
                _lastInterimSamples = 0;
                return;
            }
            // Track the noise: fall quickly, rise slowly (so speech that just failed the threshold doesn't lift it).
            NoiseFloorDb += (db - NoiseFloorDb) * (db < NoiseFloorDb ? 0.3 : 0.02);
            NoiseFloorDb = Math.Clamp(NoiseFloorDb, -90, -10);
            _preRoll.Enqueue((float[])frame.Clone());
            while (_preRoll.Count > PreRollMs / FrameMs) _preRoll.Dequeue();
            return;
        }

        _utterance.AddRange(frame);
        if (db >= threshold - HysteresisDb)
        {
            _voicedFrames++;
            _silentFrames = 0;
        }
        else
        {
            _silentFrames++;
        }

        if (_silentFrames * FrameMs >= s.SilenceMs)
        {
            EndUtterance();
            return;
        }
        if (_utterance.Count >= Math.Max(1, s.MaxUtteranceSeconds) * _sampleRate)
        {
            EndUtterance(stillSpeaking: true);
            return;
        }
        if (InterimIntervalMs > 0 && _voicedFrames * FrameMs >= s.MinSpeechMs
            && _utterance.Count - _lastInterimSamples >= InterimIntervalMs * _sampleRate / 1000)
        {
            _lastInterimSamples = _utterance.Count;
            Interim?.Invoke(_nextId, [.. _utterance]);
        }
    }

    private void EndUtterance(bool stillSpeaking = false)
    {
        var s = _settings();
        var id = _nextId++;
        // Drop most of the trailing silence; keep a short tail so the last word isn't clipped.
        var trailing = Math.Max(0, _silentFrames * _frameSamples - TailMs * _sampleRate / 1000);
        var length = Math.Max(0, _utterance.Count - trailing);
        var voicedMs = _voicedFrames * FrameMs;
        var samples = _utterance.GetRange(0, length).ToArray();
        _utterance.Clear();
        _silentFrames = 0;
        _voicedFrames = 0;
        _lastInterimSamples = 0;
        InSpeech = stillSpeaking;
        if (stillSpeaking) _voicedFrames = 1;
        if (voicedMs >= s.MinSpeechMs) UtteranceEnded?.Invoke(id, samples);
    }
}
