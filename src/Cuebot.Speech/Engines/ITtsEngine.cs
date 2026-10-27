namespace Cuebot.Speech;

/// <summary>A voice offered by a text-to-speech engine.</summary>
/// <param name="Id">Engine-specific identifier stored in <see cref="VoiceAlias.VoiceId"/>.</param>
public sealed record VoiceInfo(string Id, string Name, string? Language)
{
    /// <summary>"Female", "Male" or null when unknown.</summary>
    public string? Gender { get; init; }

    /// <summary>Quality tier label such as "Standard", "Neural" or "WaveNet"; null when the engine has one tier.</summary>
    public string? Tier { get; init; }

    /// <summary>Speaking styles the voice supports (Azure); empty otherwise.</summary>
    public IReadOnlyList<string> Styles { get; init; } = [];
}

/// <summary>Container format of synthesized audio. Players decode WAV everywhere; MP3/OGG where the backend can.</summary>
public enum AudioFormat { Wav, Mp3, Ogg }

/// <summary>What to synthesize and how. Rate and pitch are -10..10 (0 normal); volume is 0-100.</summary>
public sealed record SynthesisRequest(string Text, string VoiceId)
{
    public int Rate { get; init; }
    public int Pitch { get; init; }

    /// <summary>Engine-side volume where supported; playback volume is applied separately.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Speaking style (Azure); empty for none.</summary>
    public string Style { get; init; } = "";
}

/// <summary>Audio bytes and their container format.</summary>
public sealed record SynthesizedAudio(byte[] Data, AudioFormat Format)
{
    public static SynthesizedAudio Wav(byte[] data) => new(data, AudioFormat.Wav);

    public string FileExtension => Format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Ogg => ".ogg",
        _ => ".wav",
    };
}

/// <summary>A text-to-speech backend that turns text into audio in memory.</summary>
public interface ITtsEngine
{
    /// <summary>Stable id stored in <see cref="VoiceAlias.EngineId"/>, e.g. "windows".</summary>
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    /// <summary>Why the engine cannot be used (null when available).</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct);

    /// <returns>Complete audio, usually WAV; cloud engines may return MP3 or OGG.</returns>
    Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct);
}

public static class TtsEngineExtensions
{
    /// <summary>Synthesizes with default pitch and volume and returns the audio bytes.</summary>
    public static async Task<byte[]> SynthesizeAsync(this ITtsEngine engine, string text, string voiceId, int rate,
        CancellationToken ct) =>
        (await engine.SynthesizeAsync(new SynthesisRequest(text, voiceId) { Rate = rate }, ct).ConfigureAwait(false)).Data;
}
