using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.PolyPop;

namespace Cuebot.StreamApps;

/// <summary>Meld Studio and PolyPop functions for user scripts (Streamer.bot's MeldStudio* C# methods map here).</summary>
internal sealed class StreamAppsScriptFunctions(MeldStudioService meld, PolyPopService polyPop) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("meldstudio.isConnected", "True while Meld Studio is connected", [], (_, _, _) => Task.FromResult<object?>(meld.IsConnected)),
        new("meldstudio.connect", "Connect to Meld Studio (returns immediately; it keeps retrying in the background)", [], async (_, _, ct) =>
        {
            await meld.ConnectAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("meldstudio.disconnect", "Disconnect from Meld Studio", [], async (_, _, _) =>
        {
            await meld.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("meldstudio.getStatus", "Connection, recording and streaming state plus the current scene", [], (_, _, _) =>
            Task.FromResult<object?>(new
            {
                isConnected = meld.IsConnected, isRecording = meld.IsRecording, isStreaming = meld.IsStreaming,
                currentScene = meld.Session.CurrentScene?.Name, apiVersion = meld.ApiVersion,
            })),
        new("meldstudio.showScene", "Show a scene by id or name; returns its name", ["scene"],
            async (_, a, ct) => (await meld.ShowSceneAsync(a.GetString("scene"), ct).ConfigureAwait(false)).Name),
        new("meldstudio.showSceneByName", "Show a scene by name; returns its name", ["sceneName"],
            async (_, a, ct) => (await meld.ShowSceneAsync(a.GetString("sceneName"), ct).ConfigureAwait(false)).Name),
        new("meldstudio.showLayer", "Show a layer (scene optional)", ["layer", "scene"],
            async (_, a, ct) => await meld.SetLayerVisibilityAsync(a.GetString("scene"), a.GetString("layer"), SwitchMode.On, ct).ConfigureAwait(false)),
        new("meldstudio.hideLayer", "Hide a layer (scene optional)", ["layer", "scene"],
            async (_, a, ct) => await meld.SetLayerVisibilityAsync(a.GetString("scene"), a.GetString("layer"), SwitchMode.Off, ct).ConfigureAwait(false)),
        new("meldstudio.setLayerVisibility", "Layer state Visible, Hidden or Toggle; returns the new visibility", ["layer", "state", "scene"],
            async (_, a, ct) => await meld.SetLayerVisibilityAsync(a.GetString("scene"), a.GetString("layer"),
                Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("meldstudio.setEffectEnabled", "Effect state Enabled, Disabled or Toggle; returns the new state", ["layer", "effect", "state", "scene"],
            async (_, a, ct) => await meld.SetEffectEnabledAsync(a.GetString("scene"), a.GetString("layer"), a.GetString("effect"),
                Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("meldstudio.setTrackMuted", "Track mute Muted, NotMuted or Toggle; returns the new state", ["track", "state"],
            async (_, a, ct) => await meld.SetTrackMutedAsync(a.GetString("track"), Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("meldstudio.setTrackGain", "Set a track's gain (0-1)", ["track", "gain"], async (_, a, ct) =>
        {
            var gain = a.GetElement("gain") is { ValueKind: JsonValueKind.Number } g ? g.GetDouble() : throw new ArgumentException("gain must be a number");
            await meld.SetTrackGainAsync(a.GetString("track"), gain, ct).ConfigureAwait(false);
            return true;
        }),
        new("meldstudio.recording", "Start, Stop or Toggle recording; returns the new state", ["state"],
            async (_, a, ct) => await meld.SetRecordingAsync(Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("meldstudio.streaming", "Start, Stop or Toggle streaming; returns the new state", ["state"],
            async (_, a, ct) => await meld.SetStreamingAsync(Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("meldstudio.screenshot", "Take a screenshot (vertical: true for the vertical canvas)", ["vertical"], async (_, a, ct) =>
        {
            await meld.TakeScreenshotAsync(a.GetBool("vertical"), ct).ConfigureAwait(false);
            return true;
        }),
        new("meldstudio.sendCommand", "Run a Meld command such as meld.recordClip or meld.replay.show", ["command"], async (_, a, ct) =>
        {
            await meld.SendCommandAsync(a.GetString("command"), ct).ConfigureAwait(false);
            return true;
        }),
        new("meldstudio.getScenes", "Scenes in order: id, name, current, staged", [], (_, _, _) =>
            Task.FromResult<object?>(meld.Session.Scenes.Select(s => new { id = s.Id, name = s.Name, current = s.Current, staged = s.Staged }).ToArray())),
        new("meldstudio.getSession", "Every session item (scenes, layers, effects, tracks) keyed by id", [], (_, _, _) =>
            Task.FromResult<object?>(meld.Session.Items.ToDictionary(i => i.Id, i => ScriptArgs.ToClr(i.Json)))),
        new("meldstudio.invoke", "Call any method of Meld's WebChannel object, e.g. {method:'setStagedScene', args:['<id>']}", ["method", "args"], async (_, a, ct) =>
        {
            var args = a.GetElement("args") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(e => (object?)e.Clone()).ToArray() : [];
            var result = await meld.InvokeAsync(Steps.Require(a.GetString("method"), "method"), args, ct).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Undefined ? null : ScriptArgs.ToClr(result);
        }),

        new("polypop.isConnected", "True while at least one PolyPop plugin is connected", [], (_, _, _) => Task.FromResult<object?>(polyPop.IsConnected)),
        new("polypop.triggerAlert", "Trigger a PolyPop WebSocket Alert with text variables, e.g. {alertName:'Follow', variables:{user:'Kris'}}",
            ["alertName", "variables"], async (_, a, ct) =>
            {
                var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                if (a.GetElement("variables") is { ValueKind: JsonValueKind.Object } obj)
                    foreach (var p in obj.EnumerateObject())
                        variables[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                return await polyPop.TriggerAlertAsync(a.GetString("alertName"), variables, ct).ConfigureAwait(false);
            }),
    ];
}
