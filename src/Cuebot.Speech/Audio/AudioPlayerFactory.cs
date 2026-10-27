namespace Cuebot.Speech;

/// <summary>Chooses the audio player for the current operating system.</summary>
internal static class AudioPlayerFactory
{
    internal const string NoPlayerMessage =
        "No audio player found. Install PipeWire (pw-play), PulseAudio (paplay) or ALSA utilities (aplay).";

    public static IAudioPlayer Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsAudioPlayer();
        return CommandLineAudioPlayer.FindInstalled() ?? (IAudioPlayer)new UnavailableAudioPlayer(NoPlayerMessage);
    }
}
