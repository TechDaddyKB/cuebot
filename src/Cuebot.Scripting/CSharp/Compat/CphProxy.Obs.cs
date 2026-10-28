using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Streamer.bot.Plugin.Interface.Enums;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// The rest of Streamer.bot's OBS methods (media, replay buffer, recording pause, screenshots, colour sources, mute,
/// groups, batches) and its Streamlabs Desktop (Slobs*) methods, on the OBS module's obs.* sub-actions and requests
/// and the Streamlabs Desktop module's slobs.* sub-actions and <c>slobs.call</c>. As for the other OBS methods,
/// <c>scene</c> is accepted but OBS addresses inputs by name alone.
/// </summary>
public sealed partial class CphProxy
{
    // ---- OBS connections ------------------------------------------------------------------------------------------

    /// <summary>The index of the OBS connection named <paramref name="name"/>, -1 when there is none.</summary>
    public int ObsGetConnectionByName(string name) =>
        ObsConnectionNames(nameof(ObsGetConnectionByName)).FindIndex(n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Cuebot connects (and reconnects) OBS by itself; this only reports whether the connection is up.</summary>
    public bool ObsConnect(int connection = 0) => ObsIsConnected(connection);

    /// <summary>Not supported: Cuebot keeps its OBS connections open (disconnect them on the OBS page).</summary>
    public void ObsDisconnect(int connection = 0) => NotSupported(0, "Cuebot keeps its OBS connections open; disconnect them on the OBS page");

    // ---- Recording and replay buffer ------------------------------------------------------------------------------

    public void ObsPauseRecording(int connection = 0) => ObsRequest(connection, "PauseRecord", null);
    public void ObsResumeRecording(int connection = 0) => ObsRequest(connection, "ResumeRecord", null);

    public void ObsReplayBufferStart(int connection = 0) => ObsRequest(connection, "StartReplayBuffer", null);
    public void ObsReplayBufferStop(int connection = 0) => ObsRequest(connection, "StopReplayBuffer", null);
    public void ObsReplayBufferSave(int connection = 0) => ObsRequest(connection, "SaveReplayBuffer", null);

    /// <summary>State: 0 start, 1 stop, 2 save.</summary>
    public void ObsSetReplayBufferState(int state, int connection = 0) =>
        ObsRequest(connection, state switch
        {
            0 => "StartReplayBuffer",
            1 => "StopReplayBuffer",
            2 => "SaveReplayBuffer",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (start), 1 (stop) or 2 (save)"),
        }, null);

    // ---- Media sources --------------------------------------------------------------------------------------------

    public void ObsMediaPlay(string scene, string source, int connection = 0) => Media(connection, source, "PLAY");
    public void ObsMediaPause(string scene, string source, int connection = 0) => Media(connection, source, "PAUSE");
    public void ObsMediaRestart(string scene, string source, int connection = 0) => Media(connection, source, "RESTART");
    public void ObsMediaStop(string scene, string source, int connection = 0) => Media(connection, source, "STOP");
    public void ObsMediaNext(string scene, string source, int connection = 0) => Media(connection, source, "NEXT");
    public void ObsMediaPrevious(string scene, string source, int connection = 0) => Media(connection, source, "PREVIOUS");

    /// <summary>State: 0 play, 1 pause, 2 restart, 3 stop, 4 next, 5 previous.</summary>
    public void ObsSetMediaState(string scene, string source, int state, int connection = 0) =>
        Media(connection, source, state switch
        {
            0 => "PLAY",
            1 => "PAUSE",
            2 => "RESTART",
            3 => "STOP",
            4 => "NEXT",
            5 => "PREVIOUS",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (play) to 5 (previous)"),
        });

    private void Media(int connection, string source, string action, [CallerMemberName] string method = "") =>
        ObsRequest(connection, "TriggerMediaInputAction",
            new { inputName = source, mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_" + action }, method);

    // ---- Audio ----------------------------------------------------------------------------------------------------

    public void ObsSourceMute(string scene, string source, int connection = 0) => ObsSetSourceMuteState(scene, source, 0, connection);
    public void ObsSourceUnMute(string scene, string source, int connection = 0) => ObsSetSourceMuteState(scene, source, 1, connection);
    public void ObsSourceMuteToggle(string scene, string source, int connection = 0) => ObsSetSourceMuteState(scene, source, 2, connection);

    /// <summary>State: 0 muted, 1 not muted, 2 toggle.</summary>
    public void ObsSetSourceMuteState(string scene, string source, int state, int connection = 0)
    {
        if (state == 2)
        {
            ObsRequest(connection, "ToggleInputMute", new { inputName = source });
            return;
        }
        if (state is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (muted), 1 (not muted) or 2 (toggle)");
        ObsRequest(connection, "SetInputMute", new { inputName = source, inputMuted = state == 0 });
    }

    // ---- Filters (scene and source forms of ObsSetFilterState) ----------------------------------------------------

    /// <summary>State: 0 enabled, 1 disabled, 2 toggle.</summary>
    public void ObsSetSceneFilterState(string scene, string filterName, int state, int connection = 0) => SetFilterState(connection, scene, filterName, state);

    public void ObsSetSourceFilterState(string scene, string source, string filterName, int state, int connection = 0) =>
        SetFilterState(connection, source, filterName, state);

    // ---- Scenes, groups and colour sources ------------------------------------------------------------------------

    /// <summary>Hides every source of a group.</summary>
    public void ObsHideGroupsSources(string scene, string groupName, int connection = 0) =>
        RunSubAction("obs.hideGroupSources", new Dictionary<string, string> { ["connection"] = ObsConnectionName(connection), ["group"] = groupName ?? "" });

    /// <summary>Hides every source of a scene.</summary>
    public void ObsHideSceneSources(string scene, int connection = 0) =>
        RunSubAction("obs.hideSceneSources", new Dictionary<string, string> { ["connection"] = ObsConnectionName(connection), ["scene"] = scene ?? "" });

    /// <summary>Shows one random source of a group and hides the others; returns its name ("" when the group is empty).</summary>
    public string ObsSetRandomGroupSourceVisible(string scene, string groupName, int connection = 0) =>
        RandomSource("obs.randomGroupSource", "group", groupName, connection);

    /// <summary>Shows one random source of a scene and hides the others; returns its name ("" when the scene is empty).</summary>
    public string ObsSetRandomSceneSourceVisible(string scene, int connection = 0) => RandomSource("obs.randomSceneSource", "scene", scene, connection);

    /// <summary>The names of a group's sources, top first.</summary>
    public List<string> ObsGetGroupSources(string scene, string groupName, int connection = 0) =>
        ObsRequest(connection, "GetGroupSceneItemList", new { sceneName = groupName }) is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("sceneItems", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().OrderByDescending(i => IntProp(i, "sceneItemIndex")).Select(i => Prop(i, "sourceName")).ToList()
            : [];

    private string RandomSource(string typeId, string key, string name, int connection, [CallerMemberName] string method = "")
    {
        var args = RunSubActionForArgs(typeId,
            new Dictionary<string, string> { ["connection"] = ObsConnectionName(connection, method), [key] = name ?? "", ["resultArgument"] = "obsSourceName" },
            method);
        return Arg(args, "obsSourceName");
    }

    /// <summary>Sets a colour source's colour (each 0-255).</summary>
    public void ObsSetColorSourceColor(string scene, string source, int a, int r, int g, int b, int connection = 0) =>
        SetColorSource(connection, source, ObsConvertRgb(a, r, g, b));

    /// <summary>Sets a colour source's colour from "#RRGGBB" or "#AARRGGBB".</summary>
    public void ObsSetColorSourceColor(string scene, string source, string hexColor, int connection = 0) =>
        SetColorSource(connection, source, ObsConvertColorHex(hexColor));

    /// <summary>Gives a colour source a random opaque colour.</summary>
    public void ObsSetColorSourceRandomColor(string scene, string source, int connection = 0) =>
        SetColorSource(connection, source, ObsConvertRgb(255, Random.Shared.Next(256), Random.Shared.Next(256), Random.Shared.Next(256)));

    /// <summary>"#RRGGBB" or "#AARRGGBB" (the # is optional) as OBS's colour number (ABGR).</summary>
    public long ObsConvertColorHex(string colorHex)
    {
        var hex = (colorHex ?? "").Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            throw new ArgumentException($"'{colorHex}' is not a #RRGGBB or #AARRGGBB colour", nameof(colorHex));
        if (hex.Length == 6) argb |= 0xFF000000;
        return ObsConvertRgb((int)(argb >> 24), (int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF);
    }

    /// <summary>Alpha, red, green and blue (each 0-255) as OBS's colour number (ABGR).</summary>
    public long ObsConvertRgb(int a, int r, int g, int b) =>
        ((long)Math.Clamp(a, 0, 255) << 24) | ((long)Math.Clamp(b, 0, 255) << 16) | ((long)Math.Clamp(g, 0, 255) << 8) | (long)Math.Clamp(r, 0, 255);

    private void SetColorSource(int connection, string source, long color, [CallerMemberName] string method = "") =>
        ObsRequest(connection, "SetInputSettings", new { inputName = source, inputSettings = new { color } }, method);

    // ---- Screenshots, properties, batches -------------------------------------------------------------------------

    /// <summary>
    /// Saves a screenshot of a source (or scene; empty for the program scene) to <paramref name="path"/>; the format
    /// follows the file extension. <paramref name="quality"/> is 0-100, -1 for OBS's default. True when saved.
    /// </summary>
    public bool ObsTakeScreenshot(string source, string path, int quality = -1, int connection = 0) =>
        TrySubAction("obs.screenshot", new Dictionary<string, string>
        {
            ["connection"] = ObsConnectionName(connection),
            ["source"] = source ?? "",
            ["file"] = path ?? "",
            ["width"] = "",
            ["height"] = "",
            ["quality"] = Num(quality),
            ["resultArgument"] = "",
        });

    /// <summary>Older form: the scene is not needed (sources have unique names in OBS).</summary>
    public bool ObsTakeScreenshot(string scene, string source, string path, int quality = -1, int connection = 0) =>
        ObsTakeScreenshot(string.IsNullOrEmpty(source) ? scene : source, path, quality, connection);

    /// <summary>A scene item's properties as JSON, with the keys of the "Get Scene Item Properties" sub-action's props.* arguments.</summary>
    public string ObsGetSceneItemProperties(string scene, string source, int connection = 0)
    {
        var args = RunSubActionForArgs("obs.getSceneItemProperties",
            new Dictionary<string, string> { ["connection"] = ObsConnectionName(connection), ["scene"] = scene ?? "", ["source"] = source ?? "" });
        return Arg(args, "props._json") is { Length: > 0 } json ? json : "{}";
    }

    /// <summary>
    /// Sends an obs-websocket v5 request batch: <paramref name="data"/> is a JSON array of {requestType, requestData}.
    /// Cuebot runs batches in order, so <paramref name="executionType"/> is ignored. Returns the results array as JSON.
    /// </summary>
    public string ObsSendBatchRaw(string data, bool haltOnFailure = false, int executionType = 0, int connectionIdx = 0)
    {
        using var requests = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "[]" : data);
        var root = requests.RootElement.ValueKind == JsonValueKind.Object && requests.RootElement.TryGetProperty("requests", out var inner)
            ? inner
            : requests.RootElement;
        var result = connectionIdx == 0
            ? Call("obs.requestBatch", new { requests = root, haltOnFailure })
            : Call("obs.requestBatch", new { requests = root, haltOnFailure, connection = ObsConnectionName(connectionIdx) });
        return result.GetRawText();
    }

    // ---- Streamlabs Desktop (one connection: index 0) -------------------------------------------------------------
    // Cuebot drives a single Streamlabs Desktop canvas, so the output (horizontal/vertical) arguments are ignored.

    public bool SlobsIsConnected(int connection = 0)
    {
        if (!Slobs(connection)) return false;
        try
        {
            Call("slobs.call", new { resource = "StreamingService", method = "getModel" });
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return false;
        }
    }

    /// <summary>Cuebot connects (and reconnects) Streamlabs Desktop by itself; this only reports whether it is connected.</summary>
    public bool SlobsConnect(int connection = 0) => SlobsIsConnected(connection);

    /// <summary>Not supported: Cuebot keeps the Streamlabs Desktop connection open (disconnect it on its page).</summary>
    public void SlobsDisconnect(int connection = 0) => NotSupported(0, "Cuebot keeps the Streamlabs Desktop connection open; disconnect it on its page");

    public void SlobsSetScene(string sceneName, int connection = 0) => SlobsStep(connection, "slobs.setScene", ("scene", sceneName));

    public string SlobsGetCurrentScene(int connection = 0) =>
        Slobs(connection) ? Arg(RunSubActionForArgs("slobs.getCurrentScene", new Dictionary<string, string> { ["resultArgument"] = "slobsScene" }), "slobsScene") : "";

    public void SlobsStartStreaming(int connection = 0) => SlobsStep(connection, "slobs.streaming", ("mode", "Start"));
    public void SlobsStopStreaming(int connection = 0) => SlobsStep(connection, "slobs.streaming", ("mode", "Stop"));
    public void SlobsStartRecording(int connection = 0) => SlobsStep(connection, "slobs.recording", ("mode", "Start"));
    public void SlobsStopRecording(int connection = 0) => SlobsStep(connection, "slobs.recording", ("mode", "Stop"));

    /// <summary>Not supported: Streamlabs Desktop's API cannot pause a recording.</summary>
    public void SlobsPauseRecording(int connection = 0) => NotSupported(0, "Streamlabs Desktop's remote control API cannot pause a recording");

    /// <summary>Not supported: Streamlabs Desktop's API cannot pause a recording.</summary>
    public void SlobsResumeRecording(int connection = 0) => NotSupported(0, "Streamlabs Desktop's remote control API cannot pause a recording");

    public bool SlobsIsStreaming(int connection = 0) => SlobsStatus(connection, "streamingStatus") == "live";
    public bool SlobsIsRecording(int connection = 0) => SlobsStatus(connection, "recordingStatus") == "recording";

    // Source visibility.

    public void SlobsSetSourceVisibility(string scene, string source, bool visible, int connection = 0) =>
        SlobsSetSourceVisibilityState(scene, source, visible ? 0 : 1, StreamlabsDesktopOutputType.Both, connection);

    public void SlobsSetSourceVisibility(string scene, string source, bool visible, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsSetSourceVisibilityState(scene, source, visible ? 0 : 1, output, connection);

    /// <summary>State: 0 visible, 1 hidden, 2 toggle.</summary>
    public void SlobsSetSourceVisibilityState(string scene, string source, int state, StreamlabsDesktopOutputType output = StreamlabsDesktopOutputType.Both,
        int connection = 0) =>
        SlobsStep(connection, "slobs.setSourceVisibility", ("scene", scene), ("source", source), ("state", Visibility(state)));

    public void SlobsShowSource(string scene, string source, int connection = 0) => SlobsSetSourceVisibilityState(scene, source, 0, connection: connection);
    public void SlobsHideSource(string scene, string source, int connection = 0) => SlobsSetSourceVisibilityState(scene, source, 1, connection: connection);

    public void SlobsShowSource(string scene, string source, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsSetSourceVisibilityState(scene, source, 0, output, connection);

    public void SlobsHideSource(string scene, string source, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsSetSourceVisibilityState(scene, source, 1, output, connection);

    public bool SlobsIsSourceVisible(string scene, string source, int connection = 0) =>
        SlobsItems(connection, scene).FirstOrDefault(i => SameName(i, source)) is { ValueKind: JsonValueKind.Object } item
        && Prop(item, "visible") == "True";

    public bool SlobsIsSourceVisible(string scene, string source, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsIsSourceVisible(scene, source, connection);

    // Groups (Streamlabs Desktop folders).

    public List<string> SlobsGetGroupSources(string scene, string groupName, int connection = 0)
    {
        var items = SlobsItems(connection, scene);
        var folder = items.FirstOrDefault(i => SameName(i, groupName) && Prop(i, "sceneNodeType") == "folder");
        var id = Prop(folder, "id");
        return id.Length == 0 ? [] : items.Where(i => Prop(i, "parentId") == id && Prop(i, "sceneNodeType") != "folder").Select(i => Prop(i, "name")).ToList();
    }

    public void SlobsHideGroupsSources(string scene, string groupName, int connection = 0) =>
        SlobsStep(connection, "slobs.hideGroupSources", ("scene", scene), ("group", groupName));

    public void SlobsHideGroupsSources(string scene, string groupName, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsHideGroupsSources(scene, groupName, connection);

    /// <summary>Shows one random source of a folder and hides the others; returns its name ("" when there is none).</summary>
    public string SlobsSetRandomGroupSourceVisible(string scene, string groupName, int connection = 0) =>
        Slobs(connection)
            ? Arg(RunSubActionForArgs("slobs.randomGroupSource",
                new Dictionary<string, string> { ["scene"] = scene ?? "", ["group"] = groupName ?? "", ["resultArgument"] = "randomSource" }), "randomSource")
            : "";

    public string SlobsSetRandomGroupSourceVisible(string scene, string groupName, StreamlabsDesktopOutputType output, int connection = 0) =>
        SlobsSetRandomGroupSourceVisible(scene, groupName, connection);

    // Text, browser and audio.

    public void SlobsSetGdiText(string scene, string source, string text, int connection = 0) =>
        SlobsStep(connection, "slobs.setText", ("source", source), ("text", text));

    public void SlobsSetBrowserSource(string scene, string source, string url, int connection = 0) =>
        SlobsStep(connection, "slobs.setBrowserUrl", ("source", source), ("url", url));

    // Streamlabs Desktop's published signatures name the last text argument filterName; it is not used.
    public void SlobsSourceMute(string scene, string source, string filterName, int connection = 0) => SlobsSetSourceMuteState(scene, source, 0, connection);
    public void SlobsSourceUnMute(string scene, string source, string filterName, int connection = 0) => SlobsSetSourceMuteState(scene, source, 1, connection);
    public void SlobsSourceMuteToggle(string scene, string source, string filterName, int connection = 0) => SlobsSetSourceMuteState(scene, source, 2, connection);

    /// <summary>State: 0 muted, 1 not muted, 2 toggle.</summary>
    public void SlobsSetSourceMuteState(string scene, string source, int state, int connection = 0) =>
        SlobsStep(connection, "slobs.setSourceMute", ("source", source), ("state", state switch
        {
            0 => "Mute",
            1 => "Unmute",
            2 => "Toggle",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (muted), 1 (not muted) or 2 (toggle)"),
        }));

    // Filters: the scene forms change a scene's own filters, the source forms a source's.

    public void SlobsShowFilter(string scene, string filterName, int connection = 0) => SlobsSetFilterState(scene, filterName, 0, connection);
    public void SlobsHideFilter(string scene, string filterName, int connection = 0) => SlobsSetFilterState(scene, filterName, 1, connection);
    public void SlobsToggleFilter(string scene, string filterName, int connection = 0) => SlobsSetFilterState(scene, filterName, 2, connection);
    public void SlobsShowFilter(string scene, string source, string filterName, int connection = 0) => SlobsSetFilterState(scene, source, filterName, 0, connection);
    public void SlobsHideFilter(string scene, string source, string filterName, int connection = 0) => SlobsSetFilterState(scene, source, filterName, 1, connection);
    public void SlobsToggleFilter(string scene, string source, string filterName, int connection = 0) => SlobsSetFilterState(scene, source, filterName, 2, connection);

    /// <summary>State: 0 enabled, 1 disabled, 2 toggle.</summary>
    public void SlobsSetFilterState(string scene, string filterName, int state, int connection = 0) =>
        SlobsStep(connection, "slobs.setSceneFilterState", ("scene", scene), ("filter", filterName), ("state", Visibility(state)));

    public void SlobsSetFilterState(string scene, string source, string filterName, int state, int connection = 0)
    {
        var enabled = state switch
        {
            0 => true,
            1 => false,
            2 => !SlobsIsFilterEnabled(scene, source, filterName, connection),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (visible), 1 (hidden) or 2 (toggle)"),
        };
        SlobsStep(connection, "slobs.setFilterState", ("source", source), ("filter", filterName), ("state", enabled ? "Enable" : "Disable"));
    }

    public void SlobsSetRandomFilterState(string scene, int state, int connection = 0) =>
        SlobsStep(connection, "slobs.setRandomFilterState", ("scene", scene), ("source", ""), ("state", Visibility(state)), ("resultArgument", ""));

    public void SlobsSetRandomFilterState(string scene, string source, int state, int connection = 0) =>
        SlobsStep(connection, "slobs.setRandomFilterState", ("scene", scene), ("source", source), ("state", Visibility(state)), ("resultArgument", ""));

    public bool SlobsIsFilterEnabled(string scene, string filterName, int connection = 0) => SlobsFilterEnabled(connection, scene, filterName);
    public bool SlobsIsFilterEnabled(string scene, string source, string filterName, int connection = 0) => SlobsFilterEnabled(connection, source, filterName);

    private bool SlobsFilterEnabled(int connection, string source, string filter, [CallerMemberName] string method = "")
    {
        if (!Slobs(connection, method)) return false;
        var id = SlobsSourceId(source, method);
        if (id.Length == 0) return false;
        var filters = Call("slobs.call", new { resource = "SourceFiltersService", method = "getFilters", args = new[] { id } }, method);
        return filters.ValueKind == JsonValueKind.Array
               && filters.EnumerateArray().Any(f => SameName(f, filter) && Prop(f, "visible") == "True");
    }

    /// <summary>The Streamlabs Desktop source id of a source or scene by name; "" when there is none.</summary>
    private string SlobsSourceId(string name, string method)
    {
        var sources = Call("slobs.call", new { resource = "SourcesService", method = "getSourcesByName", args = new[] { name ?? "" } }, method);
        if (sources.ValueKind == JsonValueKind.Array && sources.EnumerateArray().Select(s => Prop(s, "sourceId")).FirstOrDefault(i => i.Length > 0) is { } id)
            return id;
        var scenes = Call("slobs.call", new { resource = "ScenesService", method = "getScenes" }, method);
        return scenes.ValueKind == JsonValueKind.Array ? Prop(scenes.EnumerateArray().FirstOrDefault(s => SameName(s, name)), "id") : "";
    }

    /// <summary>The items (nodes) of a scene by name; empty when it does not exist.</summary>
    private List<JsonElement> SlobsItems(int connection, string scene, [CallerMemberName] string method = "")
    {
        if (!Slobs(connection, method)) return [];
        var scenes = Call("slobs.call", new { resource = "ScenesService", method = "getScenes" }, method);
        if (scenes.ValueKind != JsonValueKind.Array) return [];
        var found = scenes.EnumerateArray().FirstOrDefault(s => SameName(s, scene));
        return Child(found, "nodes") is { ValueKind: JsonValueKind.Array } nodes ? nodes.EnumerateArray().ToList() : [];
    }

    private string SlobsStatus(int connection, string field, [CallerMemberName] string method = "") =>
        Slobs(connection, method) ? Prop(Call("slobs.call", new { resource = "StreamingService", method = "getModel" }, method), field) : "";

    private void SlobsStep(int connection, string typeId, params (string Key, string? Value)[] parameters)
    {
        if (!Slobs(connection)) return;
        RunSubAction(typeId, parameters.ToDictionary(p => p.Key, p => p.Value ?? ""));
    }

    private static string Visibility(int state) => state switch
    {
        0 => "Visible",
        1 => "Hidden",
        2 => "Toggle",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (visible), 1 (hidden) or 2 (toggle)"),
    };

    private static bool SameName(JsonElement element, string? name) =>
        string.Equals(Prop(element, "name"), name?.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool Slobs(int connection, [CallerMemberName] string method = "")
    {
        if (connection == 0) return true;
        LogWarn($"{method}: Cuebot has a single Streamlabs Desktop connection (use 0, not {connection})");
        return false;
    }
}
