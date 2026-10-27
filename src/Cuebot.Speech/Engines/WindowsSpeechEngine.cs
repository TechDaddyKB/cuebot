using System.Globalization;
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

    public Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(UnavailableReason);
        return SynthesizeInBackground(request, ct);
    }

    /// <summary>SSML used when the pitch differs from normal (SAPI has no numeric pitch property).</summary>
    internal static string BuildSsml(string text, int pitch, string? culture) =>
        "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\""
        + Ssml.Escape(string.IsNullOrEmpty(culture) ? "en-US" : culture) + "\">"
        + Prosody.ProsodyElement(text, rate: 0, pitch) + "</speak>";

    [SupportedOSPlatform("windows")]
    private static Task<SynthesizedAudio> SynthesizeInBackground(SynthesisRequest request, CancellationToken ct) =>
        Task.Run(() => SynthesizedAudio.Wav(Synthesize(request, ct)), ct);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VoiceInfo> ListVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => new VoiceInfo(v.VoiceInfo.Name, v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name)
            {
                Gender = v.VoiceInfo.Gender switch
                {
                    VoiceGender.Female => "Female",
                    VoiceGender.Male => "Male",
                    _ => null,
                },
            })
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Synthesize(SynthesisRequest request, CancellationToken ct)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (!string.IsNullOrWhiteSpace(request.VoiceId)) synthesizer.SelectVoice(request.VoiceId);
        synthesizer.Rate = Math.Clamp(request.Rate, -10, 10);
        synthesizer.Volume = Math.Clamp(request.Volume, 0, 100);
        using var stream = new MemoryStream();
        synthesizer.SetOutputToWaveStream(stream);
        ct.ThrowIfCancellationRequested(); // rendering to memory is fast, so it is not interrupted midway
        if (request.Pitch == 0) synthesizer.Speak(request.Text);
        else synthesizer.SpeakSsml(BuildSsml(request.Text, request.Pitch,
            synthesizer.Voice?.Culture?.Name ?? CultureInfo.CurrentCulture.Name));
        synthesizer.SetOutputToNull(); // finalizes the WAV header
        ct.ThrowIfCancellationRequested();
        return stream.ToArray();
    }
}
