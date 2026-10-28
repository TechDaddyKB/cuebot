using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.StreamDeck;

/// <summary>
/// Stream Deck functions for user scripts (the counterparts of Streamer.bot's StreamDeckSetTitle, StreamDeckSetState, ...).
/// Each returns how many visible keys have the button id.
/// </summary>
internal sealed class StreamDeckScriptFunctions(StreamDeckHub hub) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("streamdeck.isConnected", "True while at least one Stream Deck plugin is connected", [],
            (_, _, _) => Task.FromResult<object?>(hub.Instances.Count > 0)),
        new("streamdeck.getInstances", "Connected Stream Deck apps with their devices and visible Cuebot keys", [],
            (_, _, _) => Task.FromResult<object?>(hub.Instances.Select(i => new
            {
                id = i.Id, remote = i.Remote, pluginVersion = i.PluginVersion, streamDeckVersion = i.AppVersion, platform = i.Platform,
                connectedAt = i.ConnectedAt.ToString("o"),
                devices = i.Devices.Select(d => new { id = d.Id, name = d.Name, type = d.TypeName, columns = d.Columns, rows = d.Rows, connected = d.Connected }),
                buttons = i.Buttons.Select(b => new
                {
                    buttonId = b.ButtonId, kind = b.Kind, controller = b.Controller, deviceId = b.DeviceId, row = b.Row, column = b.Column, state = b.State,
                }),
            }).ToArray())),
        Command("streamdeck.setTitle", "Set a key's title (state: 0/1, omit for the current one)", ["buttonId", "title", "state"],
            (id, a) => new StreamDeckCommand("setTitle", id, Title: a.GetString("title"), State: StreamDeckSubAction.OptionalState(a.GetString("state")))),
        Command("streamdeck.setState", "Set a key's state (0 or 1)", ["buttonId", "state"],
            (id, a) => new StreamDeckCommand("setState", id, State: StreamDeckSubAction.OptionalState(a.GetString("state", "0")) ?? 0)),
        Command("streamdeck.setBackground", "Set a key's image (file, http(s) URL or data: URI; empty restores it)", ["buttonId", "image", "state"],
            (id, a) => new StreamDeckCommand("setImage", id, Image: StreamDeckProtocol.ResolveImage(a.GetString("image")),
                State: StreamDeckSubAction.OptionalState(a.GetString("state")))),
        Command("streamdeck.setValue", "Set a key's or dial's value (indicator 0-100 for dials)", ["buttonId", "value", "indicator"],
            (id, a) => new StreamDeckCommand("setValue", id, Value: a.GetString("value"), Indicator: StreamDeckSubAction.OptionalIndicator(a.GetString("indicator")))),
        Command("streamdeck.showAlert", "Flash the alert (warning) icon on a key", ["buttonId"], (id, _) => new StreamDeckCommand("showAlert", id)),
        Command("streamdeck.showOk", "Flash the OK (check mark) icon on a key", ["buttonId"], (id, _) => new StreamDeckCommand("showOk", id)),
        Command("streamdeck.toggleState", "Switch a two-state key to its other state", ["buttonId"], (id, _) => new StreamDeckCommand("toggleState", id)),
    ];

    private ScriptFunction Command(string name, string description, string[] parameters, Func<string, JsonElement, StreamDeckCommand> build) =>
        new(name, description, parameters, async (_, a, ct) =>
        {
            var id = a.GetString("buttonId").Trim();
            if (id.Length == 0) throw new ScriptException("buttonId is required");
            return await hub.SendCommandAsync(build(id, a), ct).ConfigureAwait(false);
        });
}
