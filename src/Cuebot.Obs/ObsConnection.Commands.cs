using System.Text.Json;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>High-level OBS commands used by sub-actions and the UI; all throw when OBS is not connected.</summary>
public sealed partial class ObsConnection
{
    // ---- Scenes ----

    public Task SetCurrentSceneAsync(string sceneName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentProgramScene", new { sceneName }, ct);

    /// <summary>Sets the studio mode preview scene (OBS fails the request when studio mode is off).</summary>
    public Task SetPreviewSceneAsync(string sceneName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentPreviewScene", new { sceneName }, ct);

    /// <summary>The program scene as OBS reports it now.</summary>
    public async Task<string> GetCurrentSceneAsync(CancellationToken ct = default) =>
        (await RequestAsync("GetCurrentProgramScene", ct: ct).ConfigureAwait(false)).GetStringOrNull("currentProgramSceneName")
        ?? _currentScene ?? "";

    // ---- Scene items ----

    /// <summary>The items of a scene, or of a group when <paramref name="group"/> is true, top to bottom.</summary>
    public async Task<IReadOnlyList<ObsSceneItem>> GetSceneItemsAsync(string sceneName, CancellationToken ct = default,
        bool group = false)
    {
        var data = await RequestAsync(group ? "GetGroupSceneItemList" : "GetSceneItemList", new { sceneName }, ct)
            .ConfigureAwait(false);
        var items = ObsResponseParser.ParseSceneItems(data);
        foreach (var item in items) _sceneItemNames[(sceneName, item.Id)] = item.SourceName;
        return items;
    }

    public async Task<int> GetSceneItemIdAsync(string sceneName, string sourceName, CancellationToken ct = default)
    {
        var item = await RequestAsync("GetSceneItemId", new { sceneName, sourceName }, ct).ConfigureAwait(false);
        return item.GetProperty("sceneItemId").GetInt32();
    }

    /// <summary>Shows, hides or toggles a source in a scene. Returns whether the source is now visible.</summary>
    public async Task<bool> SetSourceVisibilityAsync(string sceneName, string sourceName, VisibilityMode mode,
        CancellationToken ct = default)
    {
        var sceneItemId = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var visible = mode switch
        {
            VisibilityMode.Visible => true,
            VisibilityMode.Hidden => false,
            VisibilityMode.Toggle => !await IsSceneItemEnabledAsync(sceneName, sceneItemId, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        await SetSceneItemEnabledAsync(sceneName, sceneItemId, visible, ct).ConfigureAwait(false);
        return visible;
    }

    public Task SetSceneItemEnabledAsync(string sceneName, int sceneItemId, bool enabled, CancellationToken ct = default) =>
        RequestAsync("SetSceneItemEnabled", new { sceneName, sceneItemId, sceneItemEnabled = enabled }, ct);

    /// <summary>Hides every item of a scene (or group). Returns how many items were hidden.</summary>
    public async Task<int> HideAllSourcesAsync(string sceneName, bool group = false, CancellationToken ct = default)
    {
        var items = await GetSceneItemsAsync(sceneName, ct, group).ConfigureAwait(false);
        foreach (var item in items.Where(i => i.Enabled))
            await SetSceneItemEnabledAsync(sceneName, item.Id, false, ct).ConfigureAwait(false);
        return items.Count(i => i.Enabled);
    }

    /// <summary>Shows one randomly chosen item of a scene (or group) and hides the others. Returns its source name.</summary>
    public async Task<string?> ShowRandomSourceAsync(string sceneName, bool group = false, CancellationToken ct = default)
    {
        var items = await GetSceneItemsAsync(sceneName, ct, group).ConfigureAwait(false);
        if (items.Count == 0) return null;
        var chosen = items[Random.Shared.Next(items.Count)];
        foreach (var item in items)
        {
            var show = item.Id == chosen.Id;
            if (item.Enabled != show) await SetSceneItemEnabledAsync(sceneName, item.Id, show, ct).ConfigureAwait(false);
        }
        return chosen.SourceName;
    }

    /// <summary>The scene item's transform object (position, scale, rotation, crop, bounds, size...).</summary>
    public async Task<JsonElement> GetSceneItemTransformAsync(string sceneName, string sourceName, CancellationToken ct = default)
    {
        var sceneItemId = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var data = await RequestAsync("GetSceneItemTransform", new { sceneName, sceneItemId }, ct).ConfigureAwait(false);
        return data.TryGetProperty("sceneItemTransform", out var transform) ? transform : default;
    }

    /// <summary>Applies a partial transform (only the given fields change).</summary>
    public async Task SetSceneItemTransformAsync(string sceneName, string sourceName, IReadOnlyDictionary<string, object> transform,
        CancellationToken ct = default)
    {
        if (transform.Count == 0) return;
        var sceneItemId = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        await RequestAsync("SetSceneItemTransform", new { sceneName, sceneItemId, sceneItemTransform = transform }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Mirrors a source in place; see <see cref="ObsTransforms.Flip"/>.</summary>
    public async Task FlipSourceAsync(string sceneName, string sourceName, FlipAxis axis, CancellationToken ct = default)
    {
        var transform = await GetSceneItemTransformAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        await SetSceneItemTransformAsync(sceneName, sourceName, ObsTransforms.Flip(transform, axis), ct).ConfigureAwait(false);
    }

    /// <summary>Sets (or, when <paramref name="relative"/>, adds to) a source's rotation. Returns the new rotation.</summary>
    public async Task<double> RotateSourceAsync(string sceneName, string sourceName, double degrees, bool relative,
        CancellationToken ct = default)
    {
        var rotation = degrees;
        if (relative)
        {
            var transform = await GetSceneItemTransformAsync(sceneName, sourceName, ct).ConfigureAwait(false);
            rotation += transform.GetDoubleOrNull("rotation") ?? 0;
        }
        rotation = ((rotation % 360) + 360) % 360;
        await SetSceneItemTransformAsync(sceneName, sourceName, new Dictionary<string, object> { ["rotation"] = rotation }, ct)
            .ConfigureAwait(false);
        return rotation;
    }

    // ---- Inputs ----

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

    public async Task<bool> GetInputMuteAsync(string inputName, CancellationToken ct = default) =>
        (await RequestAsync("GetInputMute", new { inputName }, ct).ConfigureAwait(false)).GetBoolOrNull("inputMuted") ?? false;

    /// <summary>Sets an input's volume. Returns the new volume in dB.</summary>
    public async Task<double> SetInputVolumeAsync(string inputName, VolumeMode mode, double value, CancellationToken ct = default)
    {
        switch (mode)
        {
            case VolumeMode.SetMultiplier:
                var mul = Math.Clamp(value, 0, 20);
                await RequestAsync("SetInputVolume", new { inputName, inputVolumeMul = mul }, ct).ConfigureAwait(false);
                return ObsTransforms.MulToDb(mul);
            case VolumeMode.AdjustDb:
                var current = await RequestAsync("GetInputVolume", new { inputName }, ct).ConfigureAwait(false);
                value += current.GetDoubleOrNull("inputVolumeDb") ?? 0;
                goto default;
            default:
                var db = ObsTransforms.ClampDb(value);
                await RequestAsync("SetInputVolume", new { inputName, inputVolumeDb = db }, ct).ConfigureAwait(false);
                return db;
        }
    }

    /// <summary>Enables, disables or toggles one of an input's audio tracks (1-6). Returns whether it is now enabled.</summary>
    public async Task<bool> SetAudioTrackAsync(string inputName, int track, EnableMode mode, CancellationToken ct = default)
    {
        if (track is < 1 or > 6) throw new ArgumentException($"Audio track must be 1-6 (got {track}).");
        var key = track.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var enabled = mode switch
        {
            EnableMode.Enable => true,
            EnableMode.Disable => false,
            _ => !((await RequestAsync("GetInputAudioTracks", new { inputName }, ct).ConfigureAwait(false))
                .TryGetProperty("inputAudioTracks", out var tracks) && tracks.GetBoolOrNull(key) == true),
        };
        await RequestAsync("SetInputAudioTracks", new { inputName, inputAudioTracks = new Dictionary<string, bool> { [key] = enabled } }, ct)
            .ConfigureAwait(false);
        return enabled;
    }

    /// <summary>Sets the text of a text source (GDI+ / FreeType 2), leaving its other settings untouched.</summary>
    public Task SetInputTextAsync(string inputName, string text, CancellationToken ct = default) =>
        SetInputSettingsAsync(inputName, new { text }, overlay: true, ct);

    public Task SetBrowserUrlAsync(string inputName, string url, CancellationToken ct = default) =>
        SetInputSettingsAsync(inputName, new { url }, overlay: true, ct);

    /// <summary>Reloads a browser source without cache, like its "Refresh cache of current page" button.</summary>
    public Task RefreshBrowserAsync(string inputName, CancellationToken ct = default) =>
        RequestAsync("PressInputPropertiesButton", new { inputName, propertyName = "refreshnocache" }, ct);

    public Task SetImageFileAsync(string inputName, string file, CancellationToken ct = default) =>
        SetInputSettingsAsync(inputName, new { file }, overlay: true, ct);

    /// <summary>Points a media source at a local file.</summary>
    public Task SetMediaFileAsync(string inputName, string file, CancellationToken ct = default) =>
        SetInputSettingsAsync(inputName, new { local_file = file, is_local_file = true }, overlay: true, ct);

    /// <summary>Sets a color source's color; see <see cref="ObsTransforms.ParseColor"/>.</summary>
    public Task SetColorSourceColorAsync(string inputName, string color, CancellationToken ct = default) =>
        SetInputSettingsAsync(inputName, new { color = ObsTransforms.ParseColor(color) }, overlay: true, ct);

    /// <param name="overlay">True merges into the existing settings; false replaces them (unset values reset to defaults).</param>
    public Task SetInputSettingsAsync(string inputName, object inputSettings, bool overlay = true, CancellationToken ct = default) =>
        RequestAsync("SetInputSettings", new { inputName, inputSettings, overlay }, ct);

    /// <summary>The input's settings object (only values that differ from the defaults).</summary>
    public async Task<JsonElement> GetInputSettingsAsync(string inputName, CancellationToken ct = default)
    {
        var data = await RequestAsync("GetInputSettings", new { inputName }, ct).ConfigureAwait(false);
        return data.TryGetProperty("inputSettings", out var settings) ? settings : default;
    }

    // ---- Media ----

    public async Task MediaControlAsync(string inputName, MediaCommand command, CancellationToken ct = default)
    {
        if (command == MediaCommand.TogglePlayPause)
        {
            var status = await RequestAsync("GetMediaInputStatus", new { inputName }, ct).ConfigureAwait(false);
            command = status.GetStringOrNull("mediaState") == "OBS_MEDIA_STATE_PLAYING" ? MediaCommand.Pause : MediaCommand.Play;
        }
        var mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_" + command.ToString().ToUpperInvariant();
        await RequestAsync("TriggerMediaInputAction", new { inputName, mediaAction }, ct).ConfigureAwait(false);
    }

    public Task SeekMediaAsync(string inputName, SeekMode mode, double milliseconds, CancellationToken ct = default) =>
        mode == SeekMode.Offset
            ? RequestAsync("OffsetMediaInputCursor", new { inputName, mediaCursorOffset = milliseconds }, ct)
            : RequestAsync("SetMediaInputCursor", new { inputName, mediaCursor = Math.Max(0, milliseconds) }, ct);

    // ---- Filters ----

    public async Task<IReadOnlyList<ObsFilter>> GetFiltersAsync(string sourceName, CancellationToken ct = default) =>
        ObsResponseParser.ParseFilters(await RequestAsync("GetSourceFilterList", new { sourceName }, ct).ConfigureAwait(false));

    /// <summary>Enables, disables or toggles a filter on a source or scene. Returns whether it is now enabled.</summary>
    public async Task<bool> SetFilterStateAsync(string sourceName, string filterName, EnableMode mode, CancellationToken ct = default)
    {
        var enabled = mode switch
        {
            EnableMode.Enable => true,
            EnableMode.Disable => false,
            _ => !((await RequestAsync("GetSourceFilter", new { sourceName, filterName }, ct).ConfigureAwait(false))
                .GetBoolOrNull("filterEnabled") ?? false),
        };
        await RequestAsync("SetSourceFilterEnabled", new { sourceName, filterName, filterEnabled = enabled }, ct).ConfigureAwait(false);
        return enabled;
    }

    /// <summary>Applies <paramref name="mode"/> to one randomly chosen filter of the source. Returns its name, or null when it has none.</summary>
    public async Task<string?> SetRandomFilterStateAsync(string sourceName, EnableMode mode, CancellationToken ct = default)
    {
        var filters = await GetFiltersAsync(sourceName, ct).ConfigureAwait(false);
        if (filters.Count == 0) return null;
        var chosen = filters[Random.Shared.Next(filters.Count)];
        var enabled = mode switch { EnableMode.Enable => true, EnableMode.Disable => false, _ => !chosen.Enabled };
        await RequestAsync("SetSourceFilterEnabled", new { sourceName, filterName = chosen.Name, filterEnabled = enabled }, ct)
            .ConfigureAwait(false);
        return chosen.Name;
    }

    /// <summary>Enables or disables every filter of a source or scene. Returns how many filters changed.</summary>
    public async Task<int> SetAllFiltersEnabledAsync(string sourceName, bool enabled, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var filter in await GetFiltersAsync(sourceName, ct).ConfigureAwait(false))
        {
            if (filter.Enabled == enabled) continue;
            await RequestAsync("SetSourceFilterEnabled", new { sourceName, filterName = filter.Name, filterEnabled = enabled }, ct)
                .ConfigureAwait(false);
            changed++;
        }
        return changed;
    }

    public Task SetFilterSettingsAsync(string sourceName, string filterName, object filterSettings, bool overlay = true,
        CancellationToken ct = default) =>
        RequestAsync("SetSourceFilterSettings", new { sourceName, filterName, filterSettings, overlay }, ct);

    // ---- Transitions and studio mode ----

    public Task SetTransitionAsync(string transitionName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentSceneTransition", new { transitionName }, ct);

    public Task SetTransitionDurationAsync(int milliseconds, CancellationToken ct = default) =>
        RequestAsync("SetCurrentSceneTransitionDuration", new { transitionDuration = Math.Clamp(milliseconds, 50, 20000) }, ct);

    /// <summary>Studio mode: transitions the preview scene to program.</summary>
    public Task TriggerStudioTransitionAsync(CancellationToken ct = default) => RequestAsync("TriggerStudioModeTransition", ct: ct);

    /// <summary>Enables, disables or toggles studio mode. Returns whether it is now enabled.</summary>
    public async Task<bool> SetStudioModeAsync(EnableMode mode, CancellationToken ct = default)
    {
        var enabled = mode switch
        {
            EnableMode.Enable => true,
            EnableMode.Disable => false,
            _ => !((await RequestAsync("GetStudioModeEnabled", ct: ct).ConfigureAwait(false)).GetBoolOrNull("studioModeEnabled") ?? false),
        };
        await RequestAsync("SetStudioModeEnabled", new { studioModeEnabled = enabled }, ct).ConfigureAwait(false);
        return enabled;
    }

    // ---- Outputs ----

    public Task ControlStreamAsync(OutputCommand command, CancellationToken ct = default) =>
        RequestAsync($"{command}Stream", ct: ct);

    public Task ControlRecordAsync(OutputCommand command, CancellationToken ct = default) =>
        RequestAsync($"{command}Record", ct: ct);

    public Task ControlRecordAsync(RecordCommand command, CancellationToken ct = default) =>
        RequestAsync(command switch
        {
            RecordCommand.Pause => "PauseRecord",
            RecordCommand.Resume => "ResumeRecord",
            RecordCommand.TogglePause => "ToggleRecordPause",
            _ => $"{command}Record",
        }, ct: ct);

    public Task ControlVirtualCamAsync(OutputCommand command, CancellationToken ct = default) =>
        RequestAsync($"{command}VirtualCam", ct: ct);

    /// <summary>Starts, stops, toggles or saves the replay buffer. Returns the saved file for <see cref="ReplayBufferCommand.Save"/>.</summary>
    public async Task<string?> ControlReplayBufferAsync(ReplayBufferCommand command, CancellationToken ct = default)
    {
        if (command == ReplayBufferCommand.Save) return await SaveReplayBufferAsync(ct).ConfigureAwait(false);
        await RequestAsync($"{command}ReplayBuffer", ct: ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Saves the replay buffer and waits for OBS to finish writing it (the ReplayBufferSaved event), falling back to
    /// GetLastReplayBufferReplay. Returns the saved file's path.
    /// </summary>
    public async Task<string> SaveReplayBufferAsync(CancellationToken ct = default)
    {
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _replaySaved, saved)?.TrySetCanceled(CancellationToken.None);
        try
        {
            await RequestAsync("SaveReplayBuffer", ct: ct).ConfigureAwait(false);
            try
            {
                return await saved.Task.WaitAsync(ReplaySaveTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return (await RequestAsync("GetLastReplayBufferReplay", ct: ct).ConfigureAwait(false))
                    .GetStringOrNull("savedReplayPath") ?? "";
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _replaySaved, null, saved);
        }
    }

    public Task CreateRecordChapterAsync(string? chapterName, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(chapterName)
            ? RequestAsync("CreateRecordChapter", ct: ct)
            : RequestAsync("CreateRecordChapter", new { chapterName }, ct);

    /// <summary>Status of an output: GetStreamStatus, GetRecordStatus... or GetOutputStatus for <see cref="ObsOutputKind.Other"/>.</summary>
    public Task<JsonElement> GetOutputStatusAsync(ObsOutputKind kind, string? outputName = null, CancellationToken ct = default) =>
        kind switch
        {
            ObsOutputKind.Stream => RequestAsync("GetStreamStatus", ct: ct),
            ObsOutputKind.Record => RequestAsync("GetRecordStatus", ct: ct),
            ObsOutputKind.ReplayBuffer => RequestAsync("GetReplayBufferStatus", ct: ct),
            ObsOutputKind.VirtualCam => RequestAsync("GetVirtualCamStatus", ct: ct),
            _ => RequestAsync("GetOutputStatus",
                new { outputName = string.IsNullOrWhiteSpace(outputName) ? throw new ArgumentException("Output name is required.") : outputName }, ct),
        };

    // ---- Misc ----

    /// <summary>Saves a screenshot of a source or scene (the current program scene when empty). Returns the file path.</summary>
    public async Task<string> TakeScreenshotAsync(string? sourceName, string filePath, int? width = null, int? height = null,
        int quality = -1, CancellationToken ct = default)
    {
        var source = string.IsNullOrWhiteSpace(sourceName) ? await GetCurrentSceneAsync(ct).ConfigureAwait(false) : sourceName;
        var request = new Dictionary<string, object>
        {
            ["sourceName"] = source,
            ["imageFormat"] = ObsTransforms.ImageFormatFor(filePath),
            ["imageFilePath"] = filePath,
            ["imageCompressionQuality"] = Math.Clamp(quality, -1, 100),
        };
        if (width is > 0) request["imageWidth"] = Math.Clamp(width.Value, 8, 4096);
        if (height is > 0) request["imageHeight"] = Math.Clamp(height.Value, 8, 4096);
        await RequestAsync("SaveSourceScreenshot", request, ct).ConfigureAwait(false);
        return filePath;
    }

    public Task SetProfileAsync(string profileName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentProfile", new { profileName }, ct);

    public Task SetSceneCollectionAsync(string sceneCollectionName, CancellationToken ct = default) =>
        RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName }, ct);

    public Task TriggerHotkeyAsync(string hotkeyName, CancellationToken ct = default) =>
        RequestAsync("TriggerHotkeyByName", new { hotkeyName }, ct);

    public Task<JsonElement> GetStatsAsync(CancellationToken ct = default) => RequestAsync("GetStats", ct: ct);

    /// <summary>
    /// A snapshot of the connection for action arguments: connected, streaming, recording (paused), replay buffer,
    /// virtual camera, studio mode and current scene. Outputs that this OBS does not offer read as false.
    /// </summary>
    public async Task<Dictionary<string, object?>> GetStatusAsync(CancellationToken ct = default)
    {
        var status = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["obsConnected"] = IsConnected };
        if (!IsConnected) return status;
        status["obsStreaming"] = await ActiveAsync("GetStreamStatus").ConfigureAwait(false);
        var record = await TryRequestAsync("GetRecordStatus").ConfigureAwait(false);
        status["obsRecording"] = record.GetBoolOrNull("outputActive") ?? false;
        status["obsRecordingPaused"] = record.GetBoolOrNull("outputPaused") ?? false;
        status["obsReplayBuffer"] = await ActiveAsync("GetReplayBufferStatus").ConfigureAwait(false);
        status["obsVirtualCam"] = await ActiveAsync("GetVirtualCamStatus").ConfigureAwait(false);
        status["obsStudioMode"] = (await TryRequestAsync("GetStudioModeEnabled").ConfigureAwait(false)).GetBoolOrNull("studioModeEnabled") ?? false;
        status["obsScene"] = CurrentScene ?? "";
        status["obsVersion"] = Version ?? "";
        return status;

        async Task<bool> ActiveAsync(string request) =>
            (await TryRequestAsync(request).ConfigureAwait(false)).GetBoolOrNull("outputActive") ?? false;

        async Task<JsonElement> TryRequestAsync(string request)
        {
            try { return await RequestAsync(request, ct: ct).ConfigureAwait(false); }
            catch (ObsRequestException) { return default; }
        }
    }

    private async Task<bool> IsSceneItemEnabledAsync(string sceneName, int sceneItemId, CancellationToken ct)
    {
        var state = await RequestAsync("GetSceneItemEnabled", new { sceneName, sceneItemId }, ct).ConfigureAwait(false);
        return state.GetProperty("sceneItemEnabled").GetBoolean();
    }
}
