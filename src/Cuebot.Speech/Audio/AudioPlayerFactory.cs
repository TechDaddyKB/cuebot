using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>Chooses the audio backend: native miniaudio when it loads, otherwise the WAV-only fallback players.</summary>
internal static class AudioPlayerFactory
{
    internal const string NoPlayerMessage =
        "No audio player found. Install PipeWire (pw-play), PulseAudio (paplay) or ALSA utilities (aplay).";

    /// <summary>Set to "fallback" (or "legacy") to skip the native library, e.g. to work around a driver problem.</summary>
    internal const string BackendVariable = "CUEBOT_AUDIO_BACKEND";

    /// <summary>The application's audio output; the backend is picked on first use.</summary>
    public static AudioOutput Create(AppLog? log = null) =>
        new(() => SelectBackend(Environment.GetEnvironmentVariable(BackendVariable),
            () => SoundFlowBackend.Create(log), LegacyAudioBackend.CreateForPlatform, log), log);

    internal static IAudioBackend SelectBackend(string? preference, Func<IAudioBackend> native, Func<IAudioBackend> fallback,
        AppLog? log)
    {
        if (preference?.Trim().ToLowerInvariant() is "fallback" or "legacy")
        {
            var forced = fallback();
            log?.Info("Audio", $"Audio backend: {forced.Name} ({BackendVariable}={preference})");
            return forced;
        }
        try
        {
            var backend = native();
            log?.Info("Audio", $"Audio backend: {backend.Name}");
            return backend;
        }
        catch (Exception ex)
        {
            var backend = fallback();
            log?.Warn("Audio",
                $"The native audio library could not start ({Describe(ex)}); using {backend.Name}. Only WAV and Ogg Vorbis play, on the default device.");
            return backend;
        }
    }

    private static string Describe(Exception ex) =>
        ex is TypeInitializationException { InnerException: { } inner } ? Describe(inner) : $"{ex.GetType().Name}: {ex.Message}";
}
