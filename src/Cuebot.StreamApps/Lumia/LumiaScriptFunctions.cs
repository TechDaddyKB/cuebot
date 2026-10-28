using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Lumia;

/// <summary>
/// Lumia Stream functions for user scripts. Streamer.bot's <c>LumiaSendCommand(command)</c> and <c>LumiaSetToDefault()</c>
/// map to <c>lumia.sendCommand</c> and <c>lumia.setToDefault</c>.
/// </summary>
internal sealed class LumiaScriptFunctions(LumiaService lumia) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("lumia.isConnected", "True while Lumia Stream is connected", [], (_, _, _) => Task.FromResult<object?>(lumia.IsConnected)),
        new("lumia.connect", "Connect to Lumia Stream (returns immediately; it keeps retrying in the background)", [], async (_, _, ct) =>
        {
            await lumia.ConnectAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("lumia.disconnect", "Disconnect from Lumia Stream", [], async (_, _, _) =>
        {
            await lumia.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("lumia.sendCommand", "Run a Lumia command: {command, type (chat-command default, chatbot-command, twitch-points...), hold, extraSettings:{}}",
            ["command", "type", "hold", "extraSettings"], async (_, a, ct) =>
            {
                await lumia.SendCommandAsync(a.GetString("command"), a.GetString("type", "chat-command"), a.GetBool("hold"), Pairs(a, "extraSettings"), ct)
                    .ConfigureAwait(false);
                return true;
            }),
        new("lumia.setToDefault", "Return Lumia's lights to their default state", [], async (_, _, ct) =>
        {
            await lumia.SetToDefaultAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("lumia.setColor", "Set a colour: {color:'#FF4076' or 'r,g,b', brightness, transition, duration (ms), lights:'Hue go, hue:19', hold}",
            ["color", "brightness", "transition", "duration", "lights", "hold"], async (_, a, ct) =>
            {
                await lumia.SetColorAsync(a.GetString("color"), Number(a, "brightness"), Number(a, "transition"), Number(a, "duration"),
                    a.GetString("lights"), a.GetBool("hold"), ct).ConfigureAwait(false);
                return true;
            }),
        new("lumia.sendAlert", "Simulate a Lumia alert: {alert:'twitch-follower', extraSettings:{username:'x'}}", ["alert", "extraSettings"], async (_, a, ct) =>
        {
            await lumia.SendAlertAsync(a.GetString("alert"), Pairs(a, "extraSettings"), ct).ConfigureAwait(false);
            return true;
        }),
        new("lumia.chatbotMessage", "Send a chat message through Lumia's chat bot: {message, platform (twitch), asSelf}", ["message", "platform", "asSelf"],
            async (_, a, ct) =>
            {
                await lumia.SendChatbotMessageAsync(a.GetString("message"), a.GetString("platform", "twitch"), a.GetBool("asSelf"), ct).ConfigureAwait(false);
                return true;
            }),
        new("lumia.tts", "Speak with Lumia's TTS: {message, voice, volume}", ["message", "voice", "volume"], async (_, a, ct) =>
        {
            await lumia.SpeakAsync(a.GetString("message"), a.GetString("voice"), Number(a, "volume"), ct).ConfigureAwait(false);
            return true;
        }),
        new("lumia.getVariable", "Read a Lumia variable", ["name"], async (_, a, ct) => await lumia.GetVariableAsync(a.GetString("name"), ct).ConfigureAwait(false)),
        new("lumia.setVariable", "Update a Lumia variable", ["name", "value"], async (_, a, ct) =>
        {
            await lumia.SetVariableAsync(a.GetString("name"), a.GetString("value"), ct).ConfigureAwait(false);
            return true;
        }),
        new("lumia.send", "Any Send API request: {type:'studio-scene', params:{value:'Sunset'}}; returns Lumia's reply message", ["type", "params"],
            async (_, a, ct) => await lumia.SendApiAsync(a.GetString("type"), a.GetElement("params") is { ValueKind: JsonValueKind.Object } p ? p : null, ct)
                .ConfigureAwait(false)),
        new("lumia.getInfo", "What Lumia offers (read on connect): {commands, alerts, lights:[{id,name,type}], voices}", [], (_, _, _) =>
            Task.FromResult<object?>(new
            {
                commands = LumiaProtocol.CommandTypes.ToDictionary(t => t, t => lumia.Info.Values(t)),
                alerts = lumia.Info.Values("alert"),
                lights = lumia.Info.Lights.Select(l => new { id = l.Id, name = l.Name, type = l.Type }).ToArray(),
                voices = lumia.Info.Voices,
                states = lumia.States is { } s ? new { on = s.On, streamMode = s.StreamMode, fuze = s.Fuze } : null,
            })),
    ];

    private static double? Number(JsonElement args, string name) => args.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static Dictionary<string, string>? Pairs(JsonElement args, string name)
    {
        if (args.GetElement(name) is not { ValueKind: JsonValueKind.Object } obj) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return result;
    }
}
