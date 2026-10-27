namespace Cuebot.Speech;

/// <summary>A voice offered by a text-to-speech engine.</summary>
/// <param name="Id">Engine-specific identifier stored in <see cref="VoiceAlias.VoiceId"/>.</param>
public sealed record VoiceInfo(string Id, string Name, string? Language);

/// <summary>A text-to-speech backend that turns text into a WAV (PCM) file in memory.</summary>
public interface ITtsEngine
{
    /// <summary>Stable id stored in <see cref="VoiceAlias.EngineId"/>, e.g. "windows".</summary>
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    /// <summary>Why the engine cannot be used (null when available).</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct);

    /// <param name="rate">Speaking rate from -10 to 10; 0 is normal.</param>
    /// <returns>A complete WAV file.</returns>
    Task<byte[]> SynthesizeAsync(string text, string voiceId, int rate, CancellationToken ct);
}
