using Cuebot.Core.Actions;

namespace Cuebot.Speech;

/// <summary>Shared parameters and playback for the "Play sound" sub-actions.</summary>
internal static class SoundPlaybackSteps
{
    public static ParameterDescriptor Device(AudioService audio) => new("device", "Output device")
    {
        Help = "Empty plays on the system default device",
        Suggestions = () => audio.GetOutputDevices().Select(d => d.Id),
    };

    public static readonly ParameterDescriptor Volume = new("volume", "Volume (0-100)", ParameterKind.Number) { DefaultValue = "100" };
    public static readonly ParameterDescriptor Wait = new("wait", "Wait until finished", ParameterKind.Boolean) { DefaultValue = "true" };

    public static readonly ParameterDescriptor UseFileName = new("useFileName", "Use the file name as the tag", ParameterKind.Boolean) { DefaultValue = "false" };

    public static readonly ParameterDescriptor Tag = new("tag", "Sound tag")
    {
        Help = "Optional name; \"Stop sounds\" can stop every sound with this tag",
    };

    /// <summary>Plays <paramref name="file"/>, sets soundFile/soundDuration arguments and optionally waits.</summary>
    public static async Task PlayAsync(AudioService audio, ActionRunContext context, ParameterValues p, string file,
        CancellationToken ct)
    {
        var options = new PlaybackOptions(NullIfEmpty(p["device"]), Math.Clamp(p.GetInt("volume", 100), 0, 100));
        // The sound outlives the action's token unless the action waits for it.
        // Streamer.bot "Use Filename As Name": the sound is tagged with its file name, for Stop Sound Playback.
        var tag = p.GetBool("useFileName") ? Path.GetFileNameWithoutExtension(file) : NullIfEmpty(p["tag"]);
        var playback = await audio.StartFileAsync(file, options, tag, ct).ConfigureAwait(false);
        context.Args["soundFile"] = file;
        context.Args["soundDuration"] = Math.Round(playback.Duration.TotalSeconds, 3);
        context.Args["soundDurationMs"] = (long)Math.Round(playback.Duration.TotalMilliseconds);
        // Streamer.bot names.
        context.Args["soundLength"] = context.Args["soundDuration"];
        context.Args["soundLengthMs"] = context.Args["soundDurationMs"];
        if (p.GetBool("wait", true))
        {
            using (ct.Register(playback.Stop))
            {
                await playback.Completion.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            _ = LogFailureAsync(context, file, playback.Completion);
        }
    }

    private static async Task LogFailureAsync(ActionRunContext context, string file, Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Error(context.Action.Name, $"Could not play {Path.GetFileName(file)}", ex);
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Plays a sound file (WAV, MP3, Ogg Vorbis, FLAC).</summary>
internal sealed class PlaySoundSubAction(AudioService audio) : ISubActionHandler
{
    public string TypeId => "audio.playSound";
    public string Category => SpeechCategories.Audio;
    public string DisplayName => "Play sound";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("file", "Sound file") { Help = "Path to a .wav, .mp3, .ogg or .flac file" },
        SoundPlaybackSteps.Device(audio),
        SoundPlaybackSteps.Volume,
        SoundPlaybackSteps.Wait,
        SoundPlaybackSteps.Tag,
        SoundPlaybackSteps.UseFileName,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await SoundPlaybackSteps.PlayAsync(audio, context, parameters, parameters["file"].Trim(), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Plays a random sound file from a folder.</summary>
internal sealed class PlaySoundFromFolderSubAction(AudioService audio, Random? random = null) : ISubActionHandler
{
    public string TypeId => "audio.playSoundFromFolder";
    public string Category => SpeechCategories.Audio;
    public string DisplayName => "Play sound from folder";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("folder", "Folder"),
        new("extensions", "File types") { DefaultValue = "wav, mp3, ogg, flac", Help = "Comma-separated extensions; empty = all supported" },
        new("subfolders", "Include subfolders", ParameterKind.Boolean) { DefaultValue = "false" },
        SoundPlaybackSteps.Device(audio),
        SoundPlaybackSteps.Volume,
        SoundPlaybackSteps.Wait,
        SoundPlaybackSteps.Tag,
        SoundPlaybackSteps.UseFileName,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var folder = parameters["folder"].Trim();
        var file = AudioService.PickRandomFile(folder, parameters["extensions"], parameters.GetBool("subfolders"), random);
        if (file is null)
        {
            context.Log.Warn(context.Action.Name, $"No sound files found in {folder}");
            return SubActionResult.Next;
        }
        context.Args["randomSoundFile"] = file;
        context.Args["randomSoundFileName"] = Path.GetFileName(file);
        await SoundPlaybackSteps.PlayAsync(audio, context, parameters, file, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Stops all sounds, or those played with a tag. Text-to-speech is not affected.</summary>
internal sealed class StopSoundsSubAction(AudioService audio) : ISubActionHandler
{
    public string TypeId => "audio.stopSounds";
    public string Category => SpeechCategories.Audio;
    public string DisplayName => "Stop sounds";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("tag", "Sound tag") { Help = "Empty stops every sound" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var tag = parameters["tag"].Trim();
        context.Args["soundsStopped"] = tag.Length == 0 ? audio.StopAll() : audio.Stop(tag);
        return Task.FromResult(SubActionResult.Next);
    }
}
