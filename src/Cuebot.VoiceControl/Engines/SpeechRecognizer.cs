namespace Cuebot.VoiceControl.Engines;

/// <summary>One candidate transcription and the engine's confidence in it (0-1).</summary>
public sealed record RecognitionAlternate(string Text, double Confidence);

/// <summary>What the engine heard in one utterance.</summary>
/// <param name="UtteranceId">Same id for the interim results and the final result of one utterance.</param>
/// <param name="Text">Best transcription.</param>
/// <param name="Confidence">0-1 (Whisper: mean token probability; System.Speech: its own confidence).</param>
/// <param name="IsFinal">False for interim results while the speaker is still talking.</param>
public sealed record RecognitionResult(long UtteranceId, string Text, double Confidence, bool IsFinal)
{
    /// <summary>Candidates best first; the first is the result itself.</summary>
    public IReadOnlyList<RecognitionAlternate> Alternates { get; init; } = [];

    /// <summary>Language the engine reported (e.g. "en"), when it says.</summary>
    public string? Language { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>How a session should recognise: language, the command phrases it may favour, interim results.</summary>
/// <param name="Locale">Culture name such as "en-US", or "auto".</param>
/// <param name="Phrases">Enabled command phrases (Exact ones become grammar on engines that support it).</param>
/// <param name="ExactPhrases">The subset of <paramref name="Phrases"/> used with the Exact location.</param>
/// <param name="InterimResults">Report interim results while speech is still going on.</param>
public sealed record RecognitionOptions(string Locale, IReadOnlyList<string> Phrases, IReadOnlyList<string> ExactPhrases, bool InterimResults);

/// <summary>A speech-to-text engine. Engines are thin wrappers; everything above them is tested with fakes.</summary>
public interface ISpeechRecognizerEngine
{
    /// <summary>Stable id stored in voicecontrol.json, e.g. "whisper".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Whether the engine can run on this system at all (platform, native libraries).</summary>
    bool IsAvailable { get; }

    /// <summary>Why the engine can't be used (null when available).</summary>
    string? UnavailableReason { get; }

    /// <summary>True when the engine needs a downloaded model (see <see cref="Models.ModelStore"/>).</summary>
    bool NeedsModel { get; }

    /// <summary>Locales the engine recognises ("auto" first when it detects the language itself).</summary>
    IReadOnlyList<string> GetLocales();

    /// <summary>Loads the engine and returns a session that accepts audio. Throws with a readable message on failure.</summary>
    Task<IRecognitionSession> StartAsync(RecognitionOptions options, CancellationToken ct);
}

/// <summary>A running recogniser fed with microphone audio.</summary>
public interface IRecognitionSession : IAsyncDisposable
{
    /// <summary>Mono sample rate <see cref="Accept"/> expects (16 kHz for every engine here).</summary>
    int SampleRate { get; }

    /// <summary>Description for the status line, e.g. "Whisper base (CPU, AVX2)".</summary>
    string Description { get; }

    /// <summary>Called on the audio thread with mono samples at <see cref="SampleRate"/>; must return quickly.</summary>
    void Accept(ReadOnlySpan<float> samples);

    /// <summary>The commands changed while listening.</summary>
    void UpdateOptions(RecognitionOptions options);

    /// <summary>Raised on a background thread for every interim and final result.</summary>
    event Action<RecognitionResult>? Recognized;

    /// <summary>Raised when the engine stops working (the session is then unusable).</summary>
    event Action<Exception>? Failed;
}
