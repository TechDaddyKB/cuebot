using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>A sound started by <see cref="AudioService"/>.</summary>
public sealed class SoundPlayback
{
    private readonly CancellationTokenSource _stop;

    internal SoundPlayback(string file, string? tag, TimeSpan duration, CancellationTokenSource stop)
    {
        File = file;
        Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        Duration = duration;
        _stop = stop;
    }

    public string File { get; }

    /// <summary>Name given when playing, used by "Stop sounds" to stop a group of sounds.</summary>
    public string? Tag { get; }

    public TimeSpan Duration { get; }

    /// <summary>Completes when the sound ends or is stopped; faults when playback fails.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    public bool IsStopped => _stop.IsCancellationRequested;

    public void Stop()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>Plays sound files from actions and keeps track of them so they can be stopped.</summary>
public sealed class AudioService(IAudioPlayer player, AppLog? log = null)
{
    private readonly object _gate = new();
    private readonly List<SoundPlayback> _active = [];

    public IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        try
        {
            return player.GetOutputDevices();
        }
        catch (Exception ex)
        {
            log?.Error("Audio", "Could not list output devices", ex);
            return [];
        }
    }

    /// <summary>Sounds currently playing.</summary>
    public IReadOnlyList<SoundPlayback> Active
    {
        get { lock (_gate) return [.. _active]; }
    }

    /// <summary>Plays a sound file at <paramref name="volume"/> percent (0-100) on the default device and waits for it.</summary>
    /// <exception cref="NotSupportedException">The file is not a supported audio format.</exception>
    public Task PlayFileAsync(string path, int volume, CancellationToken ct) =>
        PlayFileAsync(path, new PlaybackOptions(Volume: volume), tag: null, ct);

    /// <summary>Plays a sound file and waits for it to end or be stopped; cancelling <paramref name="ct"/> stops it.</summary>
    public async Task<SoundPlayback> PlayFileAsync(string path, PlaybackOptions options, string? tag, CancellationToken ct)
    {
        var playback = await StartFileAsync(path, options, tag, ct).ConfigureAwait(false);
        using (ct.Register(playback.Stop))
        {
            await playback.Completion.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return playback;
    }

    /// <summary>
    /// Loads, decodes and starts a sound, returning once it plays. The sound is independent of <paramref name="ct"/>,
    /// which only cancels loading.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file is not a supported audio format.</exception>
    public async Task<SoundPlayback> StartFileAsync(string path, PlaybackOptions options, string? tag, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Sound file not found: {path}", path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        Func<CancellationToken, Task> play;
        TimeSpan duration;
        if (player is IAudioClipPlayer clips)
        {
            var clip = clips.Decode(bytes);
            duration = clip.Duration;
            play = token => clips.PlayClipAsync(clip, options, token);
        }
        else
        {
            // Plain players take WAV only: decode other formats in managed code and apply the volume here.
            var wav = ToWav(bytes, options.Volume, path);
            var info = Wav.Parse(wav);
            var bytesPerSecond = (double)info.Format.SampleRate * info.Format.Channels * Math.Max(1, info.Format.BitsPerSample / 8);
            duration = TimeSpan.FromSeconds(info.DataLength / bytesPerSecond);
            play = token => player.PlayAsync(wav, options with { Volume = 100 }, token);
        }

        var stop = new CancellationTokenSource();
        var playback = new SoundPlayback(path, tag, duration, stop);
        lock (_gate) _active.Add(playback);
        playback.Completion = RunAsync(playback, play, stop);
        return playback;
    }

    private async Task RunAsync(SoundPlayback playback, Func<CancellationToken, Task> play, CancellationTokenSource stop)
    {
        try
        {
            await play(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped on request.
        }
        finally
        {
            lock (_gate) _active.Remove(playback);
            stop.Dispose();
        }
    }

    private static byte[] ToWav(byte[] bytes, int volume, string path) => AudioFormatDetector.Detect(bytes) switch
    {
        AudioFileFormat.Wav => Wav.ApplyVolume(bytes, volume),
        AudioFileFormat.OggVorbis => OggVorbisDecoder.Decode(bytes).ToWav16(volume),
        _ => throw new NotSupportedException($"This audio player can only play WAV and Ogg Vorbis files ({Path.GetFileName(path)})."),
    };

    /// <summary>Stops every playing sound; returns how many were stopped.</summary>
    public int StopAll() => StopWhere(_ => true);

    /// <summary>Stops the sounds started with <paramref name="tag"/> (case-insensitive); returns how many were stopped.</summary>
    public int Stop(string tag) =>
        StopWhere(p => string.Equals(p.Tag, tag.Trim(), StringComparison.OrdinalIgnoreCase));

    private int StopWhere(Func<SoundPlayback, bool> predicate)
    {
        SoundPlayback[] matches;
        lock (_gate) matches = _active.Where(p => !p.IsStopped && predicate(p)).ToArray();
        foreach (var playback in matches) playback.Stop();
        return matches.Length;
    }

    /// <summary>
    /// Picks a random sound file in <paramref name="folder"/>. <paramref name="extensions"/> is a list such as
    /// "mp3, wav" or "*.ogg;*.flac"; empty means every supported format. Returns null when nothing matches.
    /// </summary>
    public static string? PickRandomFile(string folder, string? extensions, bool includeSubfolders, Random? random = null)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Sound folder not found: {folder}");
        var allowed = ParseExtensions(extensions);
        var files = Directory
            .EnumerateFiles(folder, "*", includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => allowed.Contains(Path.GetExtension(f)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return files.Length == 0 ? null : files[(random ?? Random.Shared).Next(files.Length)];
    }

    internal static HashSet<string> ParseExtensions(string? extensions)
    {
        var parsed = (extensions ?? "")
            .Split([',', ';', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => "." + e.TrimStart('*').TrimStart('.'))
            .Where(e => e.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parsed.Count > 0 ? parsed : AudioFormatDetector.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
