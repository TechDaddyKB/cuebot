namespace Cuebot.Obs;

/// <summary>High-level OBS commands used by sub-actions; all throw when OBS is not connected.</summary>
public sealed partial class ObsService
{
    public Task SetCurrentSceneAsync(string sceneName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentProgramScene", new { sceneName }, ct);

    /// <summary>Shows, hides or toggles a source in a scene. Returns whether the source is now visible.</summary>
    public async Task<bool> SetSourceVisibilityAsync(string sceneName, string sourceName, VisibilityMode mode,
        CancellationToken ct = default)
    {
        var item = await RequestAsync("GetSceneItemId", new { sceneName, sourceName }, ct).ConfigureAwait(false);
        var sceneItemId = item.GetProperty("sceneItemId").GetInt32();
        var visible = mode switch
        {
            VisibilityMode.Visible => true,
            VisibilityMode.Hidden => false,
            VisibilityMode.Toggle => !await IsSceneItemEnabledAsync(sceneName, sceneItemId, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        await RequestAsync("SetSceneItemEnabled", new { sceneName, sceneItemId, sceneItemEnabled = visible }, ct)
            .ConfigureAwait(false);
        return visible;
    }

    /// <summary>Mutes, unmutes or toggles an audio input. Returns whether the input is now muted.</summary>
    public async Task<bool> SetInputMuteAsync(string inputName, MuteMode mode, CancellationToken ct = default)
    {
        if (mode == MuteMode.Toggle)
        {
            var toggled = await RequestAsync("ToggleInputMute", new { inputName }, ct).ConfigureAwait(false);
            return toggled.GetProperty("inputMuted").GetBoolean();
        }
        var muted = mode == MuteMode.Mute;
        await RequestAsync("SetInputMute", new { inputName, inputMuted = muted }, ct).ConfigureAwait(false);
        return muted;
    }

    /// <summary>Sets the text of a text source (GDI+ / FreeType 2), leaving its other settings untouched.</summary>
    public Task SetInputTextAsync(string inputName, string text, CancellationToken ct = default) =>
        RequestAsync("SetInputSettings", new { inputName, inputSettings = new { text } }, ct);

    public Task ControlStreamAsync(OutputCommand command, CancellationToken ct = default) =>
        RequestAsync(OutputRequest(command, "Stream"), ct: ct);

    public Task ControlRecordAsync(OutputCommand command, CancellationToken ct = default) =>
        RequestAsync(OutputRequest(command, "Record"), ct: ct);

    private async Task<bool> IsSceneItemEnabledAsync(string sceneName, int sceneItemId, CancellationToken ct)
    {
        var state = await RequestAsync("GetSceneItemEnabled", new { sceneName, sceneItemId }, ct).ConfigureAwait(false);
        return state.GetProperty("sceneItemEnabled").GetBoolean();
    }

    /// <summary>"StartStream", "StopRecord", "ToggleStream"...</summary>
    private static string OutputRequest(OutputCommand command, string output) => $"{command}{output}";
}
