using System.Runtime.Versioning;
using System.Speech.Synthesis;

namespace Cuebot.Speech;

/// <summary>Speaks with the voices installed in Windows (SAPI). Listed on every OS, available only on Windows.</summary>
internal sealed class WindowsSpeechEngine : ITtsEngine
{
    public string Id => "windows";
    public string DisplayName => "Windows voices";
    public bool IsAvailable => OperatingSystem.IsWindows();
    public string? UnavailableReason => IsAvailable ? null : "Windows voices are only available on Windows.";

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(UnavailableReason);
        return Task.Run(ListVoices, ct);
    }

    public Task<byte[]> SynthesizeAsync(string text, string voiceId, int rate, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(UnavailableReason);
        return SynthesizeInBackground(text, voiceId, rate, ct);
    }

    [SupportedOSPlatform("windows")]
    private static Task<byte[]> SynthesizeInBackground(string text, string voiceId, int rate, CancellationToken ct) =>
        Task.Run(() => Synthesize(text, voiceId, rate, ct), ct);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VoiceInfo> ListVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => new VoiceInfo(v.VoiceInfo.Name, v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name))
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Synthesize(string text, string voiceId, int rate, CancellationToken ct)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (!string.IsNullOrWhiteSpace(voiceId)) synthesizer.SelectVoice(voiceId);
        synthesizer.Rate = Math.Clamp(rate, -10, 10);
        using var stream = new MemoryStream();
        synthesizer.SetOutputToWaveStream(stream);
        ct.ThrowIfCancellationRequested(); // rendering to memory is fast, so it is not interrupted midway
        synthesizer.Speak(text);
        synthesizer.SetOutputToNull(); // finalizes the WAV header
        ct.ThrowIfCancellationRequested();
        return stream.ToArray();
    }
}
