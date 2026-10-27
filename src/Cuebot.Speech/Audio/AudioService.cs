namespace Cuebot.Speech;

/// <summary>Plays sound files from actions.</summary>
public sealed class AudioService(IAudioPlayer player)
{
    /// <summary>Plays a WAV file at <paramref name="volume"/> percent (0-100).</summary>
    /// <exception cref="NotSupportedException">The file is not a WAV file.</exception>
    public async Task PlayFileAsync(string path, int volume, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Sound file not found: {path}", path);
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Only WAV files can be played ({Path.GetFileName(path)}).");
        var wav = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        await player.PlayAsync(Wav.ApplyVolume(wav, volume), ct).ConfigureAwait(false);
    }
}
