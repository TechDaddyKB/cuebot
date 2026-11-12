using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>Where "Set source order" moves a source in its scene's list.</summary>
public enum ObsItemOrder { Top, Bottom, Up, Down, Position }

/// <summary>Blend modes of a scene item (OBS_BLEND_*).</summary>
public enum ObsBlendMode { Normal, Additive, Subtract, Screen, Multiply, Lighten, Darken }

/// <summary>Audio monitoring of an input (OBS_MONITORING_TYPE_*).</summary>
public enum ObsMonitorType { None, MonitorOnly, MonitorAndOutput }

/// <summary>Which projector to open.</summary>
public enum ObsProjector { Program, Preview, Multiview, Source }

/// <summary>Which window to open for an input.</summary>
public enum ObsInputDialog { Properties, Filters, Interact }

/// <summary>Where persistent data is kept in OBS: for every profile, or only the current one.</summary>
public enum ObsDataRealm { Global, Profile }

/// <summary>Deinterlacing modes (OBS_DEINTERLACE_MODE_*).</summary>
public enum ObsDeinterlaceMode { Disable, Discard, Retro, Blend, Blend2x, Linear, Linear2x, Yadif, Yadif2x }

/// <summary>Deinterlacing field order (OBS_DEINTERLACE_FIELD_ORDER_*).</summary>
public enum ObsFieldOrder { Top, Bottom }

/// <summary>
/// Commands for the newer obs-websocket features: recording files, audio monitoring and balance, scene item order,
/// lock and blending, creating and removing inputs, scene items, scenes and filters, transitions, screenshots to a
/// variable, captions, projectors, hotkeys, persistent data, profiles and stream settings, outputs, canvases, dialogs.
/// </summary>
public sealed partial class ObsConnection
{
    // ---- Canvases (OBS 32.1+) ----

    private static readonly AsyncLocal<string?> AmbientCanvas = new();

    /// <summary>Request types that accept <c>canvasUuid</c> next to a scene or source name (obs-websocket 5.7).</summary>
    internal static readonly HashSet<string> CanvasRequests = new(StringComparer.Ordinal)
    {
        "GetSourceFilterList", "CreateSourceFilter", "RemoveSourceFilter", "SetSourceFilterName", "GetSourceFilter",
        "SetSourceFilterIndex", "SetSourceFilterSettings", "SetSourceFilterEnabled", "CreateInput", "GetSceneItemList",
        "GetGroupSceneItemList", "GetSceneItemId", "GetSceneItemSource", "CreateSceneItem", "RemoveSceneItem",
        "DuplicateSceneItem", "GetSceneItemTransform", "SetSceneItemTransform", "GetSceneItemEnabled", "SetSceneItemEnabled",
        "GetSceneItemLocked", "SetSceneItemLocked", "GetSceneItemIndex", "SetSceneItemIndex", "GetSceneItemBlendMode",
        "SetSceneItemBlendMode", "GetSceneList", "CreateScene", "RemoveScene", "SetSceneName", "GetSceneSceneTransitionOverride",
        "SetSceneSceneTransitionOverride", "GetSourceActive", "GetSourceScreenshot", "SaveSourceScreenshot", "OpenSourceProjector",
    };

    /// <summary>
    /// Makes the requests of the current async flow address scenes and sources in another canvas (by name, from
    /// GetCanvasList). Empty does nothing. Dispose to go back.
    /// </summary>
    /// <exception cref="ArgumentException">OBS has no canvas with that name (or is older than 32.1).</exception>
    public IDisposable UseCanvas(string? canvasName)
    {
        if (string.IsNullOrWhiteSpace(canvasName)) return NoScope.Instance;
        var uuid = _canvases.FirstOrDefault(c => string.Equals(c.Name, canvasName.Trim(), StringComparison.OrdinalIgnoreCase))?.Uuid
                   ?? throw new ArgumentException(_canvases.Count == 0
                       ? $"OBS has no canvas named '{canvasName.Trim()}' (canvases need OBS 32.1 or newer and a plugin that adds one)."
                       : $"OBS has no canvas named '{canvasName.Trim()}'. Canvases: {string.Join(", ", _canvases.Select(c => c.Name))}.");
        var previous = AmbientCanvas.Value;
        AmbientCanvas.Value = uuid;
        return new CanvasScope(previous);
    }

    /// <summary>The request data with <c>canvasUuid</c> added when a canvas is in use and the request accepts one.</summary>
    internal static object? WithCanvas(string requestType, object? requestData)
    {
        if (AmbientCanvas.Value is not { } uuid || requestData is null || !CanvasRequests.Contains(requestType)) return requestData;
        var node = JsonSerializer.SerializeToNode(requestData) as JsonObject ?? new JsonObject();
        node["canvasUuid"] ??= uuid;
        return node;
    }

    /// <summary>A batch request object (<c>{requestType, requestData}</c>) with the canvas added the same way.</summary>
    internal static object WithCanvas(object request)
    {
        if (AmbientCanvas.Value is null) return request;
        var node = JsonSerializer.SerializeToNode(request) as JsonObject;
        if (node?["requestType"]?.GetValue<string>() is not { } type || node["requestData"] is not JsonObject data) return request;
        if (CanvasRequests.Contains(type)) data["canvasUuid"] ??= AmbientCanvas.Value;
        return node;
    }

    private sealed class CanvasScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientCanvas.Value = previous;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    // ---- Recording ----

    /// <summary>Starts a new recording file now (OBS 30.2+, needs "automatic file splitting" support in the output).</summary>
    public Task SplitRecordFileAsync(CancellationToken ct = default) => RequestAsync("SplitRecordFile", ct: ct);

    public async Task<string> GetRecordDirectoryAsync(CancellationToken ct = default) =>
        (await RequestAsync("GetRecordDirectory", ct: ct).ConfigureAwait(false)).GetStringOrNull("recordDirectory") ?? "";

    /// <summary>Changes the folder recordings are saved to (OBS 30+). It must be a full path on the OBS computer.</summary>
    public Task SetRecordDirectoryAsync(string directory, CancellationToken ct = default) =>
        ObsTransforms.IsFullPath(directory)
            ? RequestAsync("SetRecordDirectory", new { recordDirectory = directory.Trim() }, ct)
            : throw new ArgumentException($"'{directory}' is not a full path on the OBS computer.");

    // ---- Audio ----

    public Task SetAudioMonitorTypeAsync(string inputName, ObsMonitorType type, CancellationToken ct = default) =>
        RequestAsync("SetInputAudioMonitorType", new { inputName, monitorType = MonitorTypeName(type) }, ct);

    /// <summary>Sets the balance: 0 = left, 0.5 = center, 1 = right.</summary>
    public Task SetAudioBalanceAsync(string inputName, double balance, CancellationToken ct = default) =>
        RequestAsync("SetInputAudioBalance", new { inputName, inputAudioBalance = Math.Clamp(balance, 0, 1) }, ct);

    /// <summary>Sets the audio sync offset in milliseconds (-950 to 20000).</summary>
    public Task SetAudioSyncOffsetAsync(string inputName, int milliseconds, CancellationToken ct = default) =>
        RequestAsync("SetInputAudioSyncOffset", new { inputName, inputAudioSyncOffset = Math.Clamp(milliseconds, -950, 20000) }, ct);

    /// <summary>Every audio setting of an input in one batch: mute, volume, balance, sync offset, monitoring and tracks.</summary>
    public async Task<Dictionary<string, object?>> GetAudioSettingsAsync(string inputName, CancellationToken ct = default)
    {
        string[] requests = ["GetInputMute", "GetInputVolume", "GetInputAudioBalance", "GetInputAudioSyncOffset", "GetInputAudioMonitorType", "GetInputAudioTracks"];
        var results = await RequestBatchAsync(requests.Select(r => (object)new { requestType = r, requestData = new { inputName } }).ToArray(), ct: ct)
            .ConfigureAwait(false);
        var answers = Answers(results, failOn: "GetInputMute");
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["obsAudioMuted"] = answers.GetValueOrDefault("GetInputMute").GetBoolOrNull("inputMuted") ?? false,
            ["obsAudioVolumeDb"] = answers.GetValueOrDefault("GetInputVolume").GetDoubleOrNull("inputVolumeDb"),
            ["obsAudioVolumeMul"] = answers.GetValueOrDefault("GetInputVolume").GetDoubleOrNull("inputVolumeMul"),
            ["obsAudioBalance"] = answers.GetValueOrDefault("GetInputAudioBalance").GetDoubleOrNull("inputAudioBalance"),
            ["obsAudioSyncOffset"] = answers.GetValueOrDefault("GetInputAudioSyncOffset").GetDoubleOrNull("inputAudioSyncOffset"),
            ["obsAudioMonitorType"] = MonitorTypeFromName(answers.GetValueOrDefault("GetInputAudioMonitorType").GetStringOrNull("monitorType")),
            ["obsAudioTracks"] = answers.GetValueOrDefault("GetInputAudioTracks").TryGetProperty("inputAudioTracks", out var tracks)
                ? tracks.GetRawText()
                : "{}",
        };
    }

    internal static string MonitorTypeName(ObsMonitorType type) => type switch
    {
        ObsMonitorType.MonitorOnly => "OBS_MONITORING_TYPE_MONITOR_ONLY",
        ObsMonitorType.MonitorAndOutput => "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT",
        _ => "OBS_MONITORING_TYPE_NONE",
    };

    /// <summary>"OBS_MONITORING_TYPE_MONITOR_ONLY" → "MonitorOnly" (the choice names); unknown values unchanged.</summary>
    internal static string MonitorTypeFromName(string? name)
    {
        foreach (var type in Enum.GetValues<ObsMonitorType>())
            if (MonitorTypeName(type) == name) return type.ToString();
        return name ?? "";
    }

    // ---- Scene items: order, lock, blend, create, remove, duplicate ----

    /// <summary>Moves a source in its scene's list (top = in front). Returns the new index (0 = bottom).</summary>
    public async Task<int> SetSceneItemOrderAsync(string sceneName, string sourceName, ObsItemOrder order, int position = 0,
        CancellationToken ct = default)
    {
        var items = await GetItemsOfSceneOrGroupAsync(sceneName, ct).ConfigureAwait(false);   // top first
        var id = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var count = items.Count;
        var current = (await RequestAsync("GetSceneItemIndex", new { sceneName, sceneItemId = id }, ct).ConfigureAwait(false))
            .GetIntOrNull("sceneItemIndex") ?? 0;
        var index = order switch
        {
            ObsItemOrder.Top => count - 1,
            ObsItemOrder.Bottom => 0,
            ObsItemOrder.Up => current + 1,
            ObsItemOrder.Down => current - 1,
            _ => position,
        };
        index = Math.Clamp(index, 0, Math.Max(0, count - 1));
        await RequestAsync("SetSceneItemIndex", new { sceneName, sceneItemId = id, sceneItemIndex = index }, ct).ConfigureAwait(false);
        return index;
    }

    /// <summary>Locks, unlocks or toggles the lock of a source in a scene. Returns whether it is now locked.</summary>
    public async Task<bool> SetSceneItemLockedAsync(string sceneName, string sourceName, EnableMode mode, CancellationToken ct = default)
    {
        var id = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var locked = mode switch
        {
            EnableMode.Enable => true,
            EnableMode.Disable => false,
            _ => !((await RequestAsync("GetSceneItemLocked", new { sceneName, sceneItemId = id }, ct).ConfigureAwait(false))
                .GetBoolOrNull("sceneItemLocked") ?? false),
        };
        await RequestAsync("SetSceneItemLocked", new { sceneName, sceneItemId = id, sceneItemLocked = locked }, ct).ConfigureAwait(false);
        return locked;
    }

    public async Task SetSceneItemBlendModeAsync(string sceneName, string sourceName, ObsBlendMode mode, CancellationToken ct = default)
    {
        var id = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        await RequestAsync("SetSceneItemBlendMode",
            new { sceneName, sceneItemId = id, sceneItemBlendMode = "OBS_BLEND_" + mode.ToString().ToUpperInvariant() }, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a new source (input) of a kind, like browser_source or image_source, in a scene. Returns its scene item id.</summary>
    public async Task<int> CreateInputAsync(string sceneName, string inputName, string inputKind, object? inputSettings, bool visible,
        CancellationToken ct = default)
    {
        var data = new Dictionary<string, object> { ["sceneName"] = sceneName, ["inputName"] = inputName, ["inputKind"] = inputKind, ["sceneItemEnabled"] = visible };
        if (inputSettings is not null) data["inputSettings"] = inputSettings;
        return (await RequestAsync("CreateInput", data, ct).ConfigureAwait(false)).GetIntOrNull("sceneItemId") ?? 0;
    }

    /// <summary>Deletes a source (input) from OBS and every scene it is in.</summary>
    public Task RemoveInputAsync(string inputName, CancellationToken ct = default) => RequestAsync("RemoveInput", new { inputName }, ct);

    public Task RenameInputAsync(string inputName, string newInputName, CancellationToken ct = default) =>
        RequestAsync("SetInputName", new { inputName, newInputName }, ct);

    /// <summary>Adds an existing source (or scene) to a scene. Returns the new scene item id.</summary>
    public async Task<int> AddSceneItemAsync(string sceneName, string sourceName, bool visible, CancellationToken ct = default) =>
        (await RequestAsync("CreateSceneItem", new { sceneName, sourceName, sceneItemEnabled = visible }, ct).ConfigureAwait(false))
        .GetIntOrNull("sceneItemId") ?? 0;

    /// <summary>Removes a source from one scene (the source itself stays in OBS).</summary>
    public async Task RemoveSceneItemAsync(string sceneName, string sourceName, CancellationToken ct = default)
    {
        var id = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        await RequestAsync("RemoveSceneItem", new { sceneName, sceneItemId = id }, ct).ConfigureAwait(false);
    }

    /// <summary>Copies a source in a scene (with its position and crop), into the same or another scene. Returns the new item id.</summary>
    public async Task<int> DuplicateSceneItemAsync(string sceneName, string sourceName, string? destinationScene, CancellationToken ct = default)
    {
        var id = await GetSceneItemIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var data = await RequestAsync("DuplicateSceneItem", string.IsNullOrWhiteSpace(destinationScene)
            ? new { sceneName, sceneItemId = id }
            : new { sceneName, sceneItemId = id, destinationSceneName = destinationScene.Trim() }, ct).ConfigureAwait(false);
        return data.GetIntOrNull("sceneItemId") ?? 0;
    }

    /// <summary>Whether a source (or scene) is on the live output (videoActive) and showing anywhere (videoShowing).</summary>
    public async Task<(bool Active, bool Showing)> GetSourceActiveAsync(string sourceName, CancellationToken ct = default)
    {
        var data = await RequestAsync("GetSourceActive", new { sourceName }, ct).ConfigureAwait(false);
        return (data.GetBoolOrNull("videoActive") ?? false, data.GetBoolOrNull("videoShowing") ?? false);
    }

    // ---- Scenes ----

    public Task CreateSceneAsync(string sceneName, CancellationToken ct = default) => RequestAsync("CreateScene", new { sceneName }, ct);
    public Task RemoveSceneAsync(string sceneName, CancellationToken ct = default) => RequestAsync("RemoveScene", new { sceneName }, ct);

    public Task RenameSceneAsync(string sceneName, string newSceneName, CancellationToken ct = default) =>
        RequestAsync("SetSceneName", new { sceneName, newSceneName }, ct);

    /// <summary>Sets (or, with an empty transition and no duration, removes) the transition a scene always uses.</summary>
    public Task SetSceneTransitionOverrideAsync(string sceneName, string? transitionName, int? durationMs, CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["sceneName"] = sceneName,
            // null removes the override (obs-websocket: "Specify null to remove").
            ["transitionName"] = string.IsNullOrWhiteSpace(transitionName) ? null : transitionName.Trim(),
            ["transitionDuration"] = durationMs is { } ms ? Math.Clamp(ms, 50, 20000) : null,
        };
        return RequestAsync("SetSceneSceneTransitionOverride", data, ct);
    }

    // ---- Filters ----

    public Task CreateFilterAsync(string sourceName, string filterName, string filterKind, object? filterSettings, CancellationToken ct = default) =>
        RequestAsync("CreateSourceFilter", filterSettings is null
            ? new { sourceName, filterName, filterKind }
            : new { sourceName, filterName, filterKind, filterSettings }, ct);

    public Task RemoveFilterAsync(string sourceName, string filterName, CancellationToken ct = default) =>
        RequestAsync("RemoveSourceFilter", new { sourceName, filterName }, ct);

    public Task RenameFilterAsync(string sourceName, string filterName, string newFilterName, CancellationToken ct = default) =>
        RequestAsync("SetSourceFilterName", new { sourceName, filterName, newFilterName }, ct);

    /// <summary>Moves a filter in the source's filter list (0 = first, applied first).</summary>
    public Task SetFilterIndexAsync(string sourceName, string filterName, int index, CancellationToken ct = default) =>
        RequestAsync("SetSourceFilterIndex", new { sourceName, filterName, filterIndex = Math.Max(0, index) }, ct);

    // ---- Transitions ----

    public Task SetTransitionSettingsAsync(object transitionSettings, bool overlay = true, CancellationToken ct = default) =>
        RequestAsync("SetCurrentSceneTransitionSettings", new { transitionSettings, overlay }, ct);

    /// <summary>The current transition (name, kind, duration, fixed, configurable, settings) and the transition cursor.</summary>
    public async Task<JsonElement> GetCurrentTransitionAsync(CancellationToken ct = default) =>
        await RequestAsync("GetCurrentSceneTransition", ct: ct).ConfigureAwait(false);

    /// <summary>Moves the studio mode T-bar (0-1). Release lets OBS finish or cancel the transition.</summary>
    public Task SetTBarPositionAsync(double position, bool release, CancellationToken ct = default) =>
        RequestAsync("SetTBarPosition", new { position = Math.Clamp(position, 0, 1), release }, ct);

    // ---- Screenshots, captions, projectors, dialogs ----

    /// <summary>A screenshot of a source or scene (the program scene when empty) as a data: URL, without saving a file.</summary>
    public async Task<string> GetScreenshotAsync(string? sourceName, string format = "png", int? width = null, int? height = null,
        int quality = -1, CancellationToken ct = default)
    {
        var source = string.IsNullOrWhiteSpace(sourceName) ? await GetCurrentSceneAsync(ct).ConfigureAwait(false) : sourceName;
        var imageFormat = string.IsNullOrWhiteSpace(format) ? "png" : format.Trim().TrimStart('.').ToLowerInvariant();
        if (imageFormat == "jpeg") imageFormat = "jpg";
        var request = new Dictionary<string, object>
        {
            ["sourceName"] = source,
            ["imageFormat"] = imageFormat,
            ["imageCompressionQuality"] = Math.Clamp(quality, -1, 100),
        };
        if (width is > 0) request["imageWidth"] = Math.Clamp(width.Value, 8, 4096);
        if (height is > 0) request["imageHeight"] = Math.Clamp(height.Value, 8, 4096);
        var data = (await RequestAsync("GetSourceScreenshot", request, ct).ConfigureAwait(false)).GetStringOrNull("imageData") ?? "";
        // obs-websocket already answers with a data: URL; keep it, or add the prefix for one that does not.
        return data.StartsWith("data:", StringComparison.Ordinal) ? data : $"data:image/{(imageFormat == "jpg" ? "jpeg" : imageFormat)};base64,{data}";
    }

    /// <summary>Sends CEA-608 caption text with the stream (only while streaming).</summary>
    public Task SendStreamCaptionAsync(string text, CancellationToken ct = default) => RequestAsync("SendStreamCaption", new { captionText = text }, ct);

    /// <summary>Opens a fullscreen projector on a monitor (index from 0), or a window when <paramref name="monitorIndex"/> is negative.</summary>
    public Task OpenProjectorAsync(ObsProjector kind, string? sourceName, int monitorIndex, CancellationToken ct = default)
    {
        if (kind == ObsProjector.Source)
        {
            var source = string.IsNullOrWhiteSpace(sourceName) ? throw new ArgumentException("A source projector needs a source.") : sourceName.Trim();
            return monitorIndex >= 0
                ? RequestAsync("OpenSourceProjector", new { sourceName = source, monitorIndex }, ct)
                : RequestAsync("OpenSourceProjector", new { sourceName = source }, ct);
        }
        var videoMixType = "OBS_WEBSOCKET_VIDEO_MIX_TYPE_" + kind.ToString().ToUpperInvariant();
        return monitorIndex >= 0
            ? RequestAsync("OpenVideoMixProjector", new { videoMixType, monitorIndex }, ct)
            : RequestAsync("OpenVideoMixProjector", new { videoMixType }, ct);
    }

    /// <summary>Opens the properties, filters or interact window of an input on the OBS computer.</summary>
    public Task OpenInputDialogAsync(string inputName, ObsInputDialog dialog, CancellationToken ct = default) =>
        RequestAsync($"OpenInput{dialog}Dialog", new { inputName }, ct);

    // ---- Hotkeys ----

    /// <summary>Presses a key combination in OBS (key ids like OBS_KEY_F1; see libobs obs-hotkeys.h).</summary>
    public Task TriggerHotkeyByKeysAsync(string keyId, bool shift, bool control, bool alt, bool command, CancellationToken ct = default) =>
        RequestAsync("TriggerHotkeyByKeySequence",
            new { keyId = keyId.Trim(), keyModifiers = new { shift, control, alt, command } }, ct);

    // ---- Persistent data ----

    private static string Realm(ObsDataRealm realm) => realm == ObsDataRealm.Profile ? "OBS_WEBSOCKET_DATA_REALM_PROFILE" : "OBS_WEBSOCKET_DATA_REALM_GLOBAL";

    /// <summary>Stores a value in OBS (kept across restarts, readable by other obs-websocket tools).</summary>
    public Task SetPersistentDataAsync(ObsDataRealm realm, string slotName, JsonElement value, CancellationToken ct = default) =>
        RequestAsync("SetPersistentData", new { realm = Realm(realm), slotName, slotValue = value }, ct);

    /// <summary>A value stored in OBS; an undefined element when the slot is empty.</summary>
    public async Task<JsonElement> GetPersistentDataAsync(ObsDataRealm realm, string slotName, CancellationToken ct = default)
    {
        var data = await RequestAsync("GetPersistentData", new { realm = Realm(realm), slotName }, ct).ConfigureAwait(false);
        return data.TryGetProperty("slotValue", out var value) ? value : default;
    }

    // ---- Profiles, scene collections, video and stream settings ----

    public Task CreateProfileAsync(string profileName, CancellationToken ct = default) => RequestAsync("CreateProfile", new { profileName }, ct);
    public Task RemoveProfileAsync(string profileName, CancellationToken ct = default) => RequestAsync("RemoveProfile", new { profileName }, ct);

    public Task CreateSceneCollectionAsync(string sceneCollectionName, CancellationToken ct = default) =>
        RequestAsync("CreateSceneCollection", new { sceneCollectionName }, ct);

    /// <summary>A value from the current profile's basic.ini, like Output / Mode. Null when it is not set.</summary>
    public async Task<string?> GetProfileParameterAsync(string category, string name, CancellationToken ct = default) =>
        (await RequestAsync("GetProfileParameter", new { parameterCategory = category, parameterName = name }, ct).ConfigureAwait(false))
        .GetStringOrNull("parameterValue");

    /// <summary>Sets (or, with null, deletes) a value in the current profile's basic.ini.</summary>
    public Task SetProfileParameterAsync(string category, string name, string? value, CancellationToken ct = default) =>
        RequestAsync("SetProfileParameter", new JsonObject { ["parameterCategory"] = category, ["parameterName"] = name, ["parameterValue"] = value }, ct);

    public Task<JsonElement> GetVideoSettingsAsync(CancellationToken ct = default) => RequestAsync("GetVideoSettings", ct: ct);

    /// <summary>Changes canvas (base) and output resolution and/or the frame rate; null values stay as they are. OBS refuses while an output is active.</summary>
    public Task SetVideoSettingsAsync(int? baseWidth, int? baseHeight, int? outputWidth, int? outputHeight, int? fpsNumerator,
        int? fpsDenominator, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>();
        if (baseWidth is > 0 && baseHeight is > 0) (data["baseWidth"], data["baseHeight"]) = (baseWidth.Value, baseHeight.Value);
        if (outputWidth is > 0 && outputHeight is > 0) (data["outputWidth"], data["outputHeight"]) = (outputWidth.Value, outputHeight.Value);
        if (fpsNumerator is > 0) (data["fpsNumerator"], data["fpsDenominator"]) = (fpsNumerator.Value, fpsDenominator is > 0 ? fpsDenominator.Value : 1);
        if (data.Count == 0) throw new ArgumentException("Give a width and height, an output width and height, or a frame rate.");
        return RequestAsync("SetVideoSettings", data, ct);
    }

    /// <summary>
    /// The stream destination (service type and settings). The stream key (and any password) is replaced by "(hidden)"
    /// so it never ends up in a variable or the action history.
    /// </summary>
    public async Task<(string Type, JsonElement Settings)> GetStreamServiceAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetStreamServiceSettings", ct: ct).ConfigureAwait(false);
        var settings = data.TryGetProperty("streamServiceSettings", out var s) ? s : default;
        return (data.GetStringOrNull("streamServiceType") ?? "", RedactSecrets(settings));
    }

    public Task SetStreamServiceAsync(string serviceType, object settings, CancellationToken ct = default) =>
        RequestAsync("SetStreamServiceSettings", new { streamServiceType = serviceType, streamServiceSettings = settings }, ct);

    private static readonly string[] SecretKeys = ["key", "password", "bearer_token", "token"];

    internal static JsonElement RedactSecrets(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) return settings;
        var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
        foreach (var key in node.Select(p => p.Key).Where(k => SecretKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray())
            if (node[key] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) node[key] = "(hidden)";
        return JsonSerializer.SerializeToElement(node);
    }

    // ---- Outputs ----

    /// <summary>Starts, stops or toggles an output by name (from GetOutputList), e.g. a plugin's vertical stream.</summary>
    public Task ControlOutputAsync(string outputName, OutputCommand command, CancellationToken ct = default) =>
        RequestAsync($"{command}Output", new { outputName }, ct);

    public async Task<JsonElement> GetOutputSettingsAsync(string outputName, CancellationToken ct = default)
    {
        var data = await RequestAsync("GetOutputSettings", new { outputName }, ct).ConfigureAwait(false);
        return RedactSecrets(data.TryGetProperty("outputSettings", out var s) ? s : default);
    }

    public Task SetOutputSettingsAsync(string outputName, object outputSettings, CancellationToken ct = default) =>
        RequestAsync("SetOutputSettings", new { outputName, outputSettings }, ct);

    // ---- Inputs: special inputs, list items, deinterlacing ----

    /// <summary>The names of OBS's global audio sources (desktop1/2, mic1-4); empty when not set.</summary>
    public async Task<Dictionary<string, string>> GetSpecialInputsAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetSpecialInputs", ct: ct).ConfigureAwait(false);
        return new[] { "desktop1", "desktop2", "mic1", "mic2", "mic3", "mic4" }.ToDictionary(k => k, k => data.GetStringOrNull(k) ?? "");
    }

    /// <summary>The items of a list in an input's properties (like the displays of a display capture).</summary>
    public async Task<JsonElement> GetInputListItemsAsync(string inputName, string propertyName, CancellationToken ct = default)
    {
        var data = await RequestAsync("GetInputPropertiesListPropertyItems", new { inputName, propertyName }, ct).ConfigureAwait(false);
        return data.TryGetProperty("propertyItems", out var items) ? items : default;
    }

    /// <summary>Sets deinterlacing of an async input (OBS 31.1+); null leaves that part unchanged.</summary>
    public async Task SetDeinterlaceAsync(string inputName, ObsDeinterlaceMode? mode, ObsFieldOrder? order, CancellationToken ct = default)
    {
        if (mode is { } m)
            await RequestAsync("SetInputDeinterlaceMode", new { inputName, inputDeinterlaceMode = DeinterlaceModeName(m) }, ct).ConfigureAwait(false);
        if (order is { } o)
            await RequestAsync("SetInputDeinterlaceFieldOrder",
                new { inputName, inputDeinterlaceFieldOrder = "OBS_DEINTERLACE_FIELD_ORDER_" + o.ToString().ToUpperInvariant() }, ct).ConfigureAwait(false);
    }

    internal static string DeinterlaceModeName(ObsDeinterlaceMode mode) =>
        "OBS_DEINTERLACE_MODE_" + mode.ToString().ToUpperInvariant().Replace("2X", "_2X", StringComparison.Ordinal);

    // ---- Helpers ----

    /// <summary>The successful answers of a batch by request type; throws the failure of <paramref name="failOn"/>.</summary>
    private static Dictionary<string, JsonElement> Answers(JsonElement results, string? failOn = null)
    {
        var answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var result in results.GetArrayItems())
        {
            var type = result.GetStringOrNull("requestType") ?? "";
            var status = result.TryGetProperty("requestStatus", out var s) ? s : default;
            if (status.GetBoolOrNull("result") == true)
                answers[type] = result.TryGetProperty("responseData", out var data) ? data : default;
            else if (type == failOn)
                throw new ObsRequestException(type, status.GetIntOrNull("code") ?? 0, status.GetStringOrNull("comment"));
        }
        return answers;
    }
}
