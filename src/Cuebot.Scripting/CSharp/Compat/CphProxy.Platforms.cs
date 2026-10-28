using System.Net.Http.Json;
using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Chat -----------------------------------------------------------------------------------------------------

    /// <summary>Sends to Twitch chat. Cuebot sends with its connected account; <paramref name="bot"/> is ignored.</summary>
    public void SendMessage(string message, bool bot = true, bool fallback = true) => SendChat("Twitch", "twitch.sendMessage", message);

    public void SendYouTubeMessage(string message, bool bot = true, bool fallback = true) => SendChat("YouTube", "youtube.sendMessage", message);

    public void SendKickMessage(string message, bool bot = true, bool fallback = true) => SendChat("Kick", "kick.sendMessage", message);

    /// <summary>Cuebot extension: sends to every connected chat platform.</summary>
    public void SendMessageToAll(string message) => SendChat(Core.Chat.ChatHub.AllPlatforms, "", message);

    private void SendChat(string platform, string function, string message, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_services.Chat is { } chat && (platform == Core.Chat.ChatHub.AllPlatforms
                || chat.SendablePlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase) || !HasFunction(function)))
        {
            Sync(chat.SendAsync(platform, message, _ct));
            return;
        }
        Call(function, new { message }, method);
    }

    // ---- OBS (one connection: index 0) ----------------------------------------------------------------------------

    public bool ObsIsConnected(int connection = 0)
    {
        Obs(connection);
        try
        {
            Call("obs.request", new { requestType = "GetVersion" });
            return true;
        }
        catch (Exception ex) when (ex is not NotSupportedException and not OperationCanceledException)
        {
            return false;
        }
    }

    public void ObsSetScene(string sceneName, int connection = 0) =>
        ObsRequest(connection, "SetCurrentProgramScene", new { sceneName });

    public string ObsGetCurrentScene(int connection = 0) =>
        ObsRequest(connection, "GetCurrentProgramScene", null) is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("currentProgramSceneName", out var name) ? name.GetString() ?? "" : "";

    public void ObsSetSourceVisibility(string scene, string source, bool visible, int connection = 0)
    {
        var id = SceneItemId(connection, scene, source);
        ObsRequest(connection, "SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = visible });
    }

    public void ObsShowSource(string scene, string source, int connection = 0) => ObsSetSourceVisibility(scene, source, true, connection);
    public void ObsHideSource(string scene, string source, int connection = 0) => ObsSetSourceVisibility(scene, source, false, connection);

    public bool ObsIsSourceVisible(string scene, string source, int connection = 0)
    {
        var id = SceneItemId(connection, scene, source);
        var data = ObsRequest(connection, "GetSceneItemEnabled", new { sceneName = scene, sceneItemId = id });
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sceneItemEnabled", out var on) && on.ValueKind == JsonValueKind.True;
    }

    public void ObsToggleSource(string scene, string source, int connection = 0) =>
        ObsSetSourceVisibility(scene, source, !ObsIsSourceVisible(scene, source, connection), connection);

    /// <summary>Enables a filter on a scene (or source) named <paramref name="sourceName"/>.</summary>
    public void ObsShowFilter(string sourceName, string filterName, int connection = 0) => SetFilter(connection, sourceName, filterName, true);
    public void ObsHideFilter(string sourceName, string filterName, int connection = 0) => SetFilter(connection, sourceName, filterName, false);
    public void ObsShowFilter(string scene, string source, string filterName, int connection = 0) => SetFilter(connection, source, filterName, true);
    public void ObsHideFilter(string scene, string source, string filterName, int connection = 0) => SetFilter(connection, source, filterName, false);

    public void ObsSetGdiText(string scene, string source, string text, int connection = 0) =>
        ObsRequest(connection, "SetInputSettings", new { inputName = source, inputSettings = new { text } });

    public void ObsSetBrowserSource(string scene, string source, string url, int connection = 0) =>
        ObsRequest(connection, "SetInputSettings", new { inputName = source, inputSettings = new { url } });

    public void ObsSetImageSourceFile(string scene, string source, string file, int connection = 0) =>
        ObsRequest(connection, "SetInputSettings", new { inputName = source, inputSettings = new { file } });

    public void ObsSetMediaSourceFile(string scene, string source, string file, int connection = 0) =>
        ObsRequest(connection, "SetInputSettings", new { inputName = source, inputSettings = new { local_file = file } });

    public void ObsStartStreaming(int connection = 0) => ObsRequest(connection, "StartStream", null);
    public void ObsStopStreaming(int connection = 0) => ObsRequest(connection, "StopStream", null);
    public void ObsStartRecording(int connection = 0) => ObsRequest(connection, "StartRecord", null);
    public void ObsStopRecording(int connection = 0) => ObsRequest(connection, "StopRecord", null);

    public bool ObsIsStreaming(int connection = 0) => OutputActive(ObsRequest(connection, "GetStreamStatus", null));
    public bool ObsIsRecording(int connection = 0) => OutputActive(ObsRequest(connection, "GetRecordStatus", null));

    /// <summary>Sends any obs-websocket v5 request; <paramref name="data"/> is its JSON requestData. Returns the response JSON.</summary>
    public string ObsSendRaw(string requestType, string data, int connection = 0)
    {
        object? requestData = string.IsNullOrWhiteSpace(data) ? null : JsonDocument.Parse(data).RootElement.Clone();
        return ObsRequest(connection, requestType, requestData).GetRawText();
    }

    private void SetFilter(int connection, string source, string filter, bool enabled) =>
        ObsRequest(connection, "SetSourceFilterEnabled", new { sourceName = source, filterName = filter, filterEnabled = enabled });

    private int SceneItemId(int connection, string scene, string source)
    {
        var data = ObsRequest(connection, "GetSceneItemId", new { sceneName = scene, sourceName = source });
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sceneItemId", out var id) ? id.GetInt32()
            : throw new InvalidOperationException($"OBS has no source '{source}' in scene '{scene}'");
    }

    private static bool OutputActive(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("outputActive", out var on) && on.ValueKind == JsonValueKind.True;

    private JsonElement ObsRequest(int connection, string requestType, object? requestData,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        Obs(connection, method);
        return Call("obs.request", new { requestType, requestData }, method);
    }

    private static void Obs(int connection, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (connection != 0) throw new NotSupportedException($"CPH.{method}: Cuebot has a single OBS connection (use connection 0)");
    }

    // ---- Text-to-speech and sounds --------------------------------------------------------------------------------

    /// <summary>Speaks <paramref name="message"/> with a voice alias through Cuebot's speech module. Returns 0.</summary>
    public int TtsSpeak(string voiceAlias, string message, bool badWordFilter = false)
    {
        Call("speech.speak", new { text = message, alias = voiceAlias, badWordFilter });
        return 0;
    }

    /// <summary>
    /// Plays a sound file. <paramref name="volume"/> is 0-1. With <paramref name="finishBeforeContinuing"/> the call
    /// waits until it ends. Sounds are tagged with <paramref name="soundName"/> (or the file name) for StopSoundPlayback.
    /// </summary>
    public double PlaySound(string fileName, float volume = 1.0f, bool finishBeforeContinuing = false, string? soundName = null,
        bool useFileName = true)
    {
        var tag = !string.IsNullOrWhiteSpace(soundName) ? soundName : useFileName ? Path.GetFileNameWithoutExtension(fileName) : "";
        RunSubAction("audio.playSound", new Dictionary<string, string>
        {
            ["file"] = fileName,
            ["device"] = "",
            ["volume"] = Math.Clamp((int)Math.Round(volume * 100), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["wait"] = finishBeforeContinuing ? "true" : "false",
            ["tag"] = tag,
        });
        return 0;
    }

    public void StopSoundPlayback(string soundName) => RunSubAction("audio.stopSounds", new Dictionary<string, string> { ["tag"] = soundName ?? "" });

    public void StopAllSoundPlayback() => RunSubAction("audio.stopSounds", new Dictionary<string, string> { ["tag"] = "" });

    /// <summary>Runs a registered sub-action handler with already-resolved parameters inside this run.</summary>
    private void RunSubAction(string typeId, Dictionary<string, string> parameters, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var handler = Need(_services.Registry, method).GetSubAction(typeId)
                      ?? throw new NotSupportedException($"CPH.{method} needs the '{typeId}' sub-action, which no loaded Cuebot module provides");
        var context = RequireContext(method);
        Sync(handler.ExecuteAsync(context, new ParameterValues(parameters), _ct));
    }

    // ---- Discord --------------------------------------------------------------------------------------------------

    /// <summary>Posts to a Discord webhook. Returns the new message's id ("" when Discord did not say).</summary>
    public string DiscordPostTextToWebhook(string webhookUrl, string content, string? username = null, string? avatarUrl = null,
        bool textToSpeech = false)
    {
        var http = Need(_services.Http);
        var payload = new Dictionary<string, object?> { ["content"] = content, ["tts"] = textToSpeech };
        if (!string.IsNullOrWhiteSpace(username)) payload["username"] = username;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) payload["avatar_url"] = avatarUrl;
        // wait=true makes Discord answer with the created message, which carries its id.
        var url = webhookUrl.Contains("wait=", StringComparison.OrdinalIgnoreCase) ? webhookUrl
            : webhookUrl + (webhookUrl.Contains('?') ? "&" : "?") + "wait=true";
        using var response = Sync(http.PostAsJsonAsync(url, payload, _ct));
        response.EnsureSuccessStatusCode();
        var body = Sync(response.Content.ReadAsStringAsync(_ct));
        try
        {
            return string.IsNullOrWhiteSpace(body) ? "" : Prop(JsonDocument.Parse(body).RootElement, "id");
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
