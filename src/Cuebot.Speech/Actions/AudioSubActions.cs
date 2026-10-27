using Cuebot.Core.Actions;

namespace Cuebot.Speech;

/// <summary>Plays a WAV file.</summary>
internal sealed class PlaySoundSubAction(AudioService audio) : ISubActionHandler
{
    public string TypeId => "audio.playSound";
    public string Category => SpeechCategories.Audio;
    public string DisplayName => "Play sound";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("file", "Sound file") { Help = "Path to a .wav file" },
        new("volume", "Volume (0-100)", ParameterKind.Number) { DefaultValue = "100" },
        new("wait", "Wait until finished", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var file = parameters["file"];
        var volume = parameters.GetInt("volume", 100);
        if (parameters.GetBool("wait", true))
        {
            await audio.PlayFileAsync(file, volume, ct).ConfigureAwait(false);
        }
        else
        {
            // The action's token ends with the action, so background playback must not use it.
            _ = PlayInBackgroundAsync(context, file, volume);
        }
        return SubActionResult.Next;
    }

    private async Task PlayInBackgroundAsync(ActionRunContext context, string file, int volume)
    {
        try
        {
            await audio.PlayFileAsync(file, volume, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Error(context.Action.Name, $"Could not play {Path.GetFileName(file)}", ex);
        }
    }
}
