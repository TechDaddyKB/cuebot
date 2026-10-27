using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuebot.Speech;

/// <summary>Plays WAV data with the winmm PlaySound API on a dedicated thread.</summary>
/// <remarks>PlaySound is process-wide: starting or stopping a sound affects any other sound it is playing.</remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsAudioPlayer : IAudioPlayer
{
    private const uint SndSync = 0x0000;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    public IReadOnlyList<AudioDevice> GetOutputDevices() => [];

    /// <summary>Plays on the default device; the volume is applied to 16-bit PCM samples.</summary>
    public Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct)
    {
        var wav = Wav.ApplyVolume(audio, options.Volume);
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => PlaySound(null, IntPtr.Zero, 0));
        var thread = new Thread(() =>
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    completion.TrySetCanceled(ct);
                    return;
                }
                var played = PlaySound(wav, IntPtr.Zero, SndMemory | SndSync | SndNoDefault);
                if (ct.IsCancellationRequested) completion.TrySetCanceled(ct);
                else if (!played) completion.TrySetException(new InvalidOperationException("Windows could not play the sound."));
                else completion.TrySetResult();
            }
            finally
            {
                registration.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Cuebot audio",
        };
        thread.Start();
        return completion.Task;
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(byte[]? sound, IntPtr module, uint flags);
}
