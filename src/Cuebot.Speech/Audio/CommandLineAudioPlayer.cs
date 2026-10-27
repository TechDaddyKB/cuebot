namespace Cuebot.Speech;

/// <summary>Plays WAV data by writing a temporary file and running a command-line player (pw-play, paplay, aplay).</summary>
internal sealed class CommandLineAudioPlayer(string executable, IReadOnlyList<string> arguments) : IAudioPlayer
{
    /// <summary>Supported players in order of preference, with the arguments placed before the file name.</summary>
    internal static readonly (string Name, string[] Arguments)[] KnownPlayers =
    [
        ("pw-play", []),
        ("paplay", []),
        ("aplay", ["-q"]),
    ];

    public string Executable => executable;

    /// <summary>The first known player installed on this machine, or null.</summary>
    public static CommandLineAudioPlayer? FindInstalled()
    {
        foreach (var (name, arguments) in KnownPlayers)
        {
            if (ExecutableLocator.Find([name]) is { } path) return new CommandLineAudioPlayer(path, arguments);
        }
        return null;
    }

    public IReadOnlyList<AudioDevice> GetOutputDevices() => [];

    /// <summary>Plays on the default device; the volume is applied to 16-bit PCM samples.</summary>
    public async Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct)
    {
        var wav = Wav.ApplyVolume(audio, options.Volume);
        var file = Path.Combine(Path.GetTempPath(), $"cuebot-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(file, wav, ct).ConfigureAwait(false);
            var result = await ProcessRunner.RunAsync(executable, [.. arguments, file], input: null, ct).ConfigureAwait(false);
            result.EnsureSuccess(Path.GetFileName(executable));
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { /* still locked by a dying player; the OS temp cleaner removes it */ }
        catch (UnauthorizedAccessException) { }
    }
}
