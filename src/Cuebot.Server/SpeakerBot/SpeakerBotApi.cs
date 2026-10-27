using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.SpeakerBot;

/// <summary>
/// Speaker.bot's WebSocket requests (<c>{request, id, ...}</c>) and UDP commands (<c>{command, ...}</c>) mapped onto
/// an <see cref="ISpeakerBackend"/>. Pure request → response logic shared by both transports.
/// </summary>
public sealed class SpeakerBotApi(ISpeakerBackend speech, AppLog log)
{
    private const string LogSource = "Speaker.bot API";
    private const string Platform = "twitch";

    public static readonly IReadOnlyList<string> Requests =
    [
        "Speak", "Stop", "Enable", "On", "Disable", "Off", "Events", "Mode", "Pause", "Resume", "Clear",
        "SetEnabled", "SetPaused", "ActivateVoiceGate", "Set", "Assign", "GetVoices", "GetAliases", "GetInfo", "GetState",
    ];

    /// <summary>Handles one WebSocket request; returns the response body without status/id.</summary>
    public async Task<JsonObject> HandleRequestAsync(string request, JsonElement message, CancellationToken ct = default)
    {
        switch (request.ToLowerInvariant())
        {
            case "speak":
            {
                var text = Str(message, "message");
                if (text.Trim().Length == 0) throw new RequestException("message is required");
                var wait = Bool(message, "wait") || Bool(message, "delay");
                var silent = Bool(message, "silent");
                var result = await speech.SpeakAsync(text, Str(message, "voice"), Bool(message, "badWordFilter"), wait, silent, ct)
                    .ConfigureAwait(false);
                return new JsonObject
                {
                    ["success"] = result.Success,
                    ["duration"] = result.DurationMs,
                    ["speechFile"] = result.SpeechFile,
                };
            }
            case "stop": speech.Stop(); return [];
            case "enable" or "on": speech.Enabled = true; return State();
            case "disable" or "off": speech.Enabled = false; return State();
            case "pause": speech.Paused = true; return State();
            case "resume": speech.Paused = false; return State();
            case "clear": speech.Clear(); return State();
            case "events":
            {
                var name = EventName(message);
                if (name is null)
                {
                    speech.EventsEnabled = OnOff(Str(message, "state"), speech.EventsEnabled);
                    return State();
                }
                var enabled = ToggleEvent(name, Str(message, "state")) ?? throw new RequestException($"Unknown event '{name}'");
                var state = State();
                state["event"] = name;
                state["eventEnabled"] = enabled;
                return state;
            }
            case "set":
                switch (Str(message, "method").ToLowerInvariant())
                {
                    case "nickname":
                        speech.SetNickname(Platform, Str(message, "username"), Str(message, "nickname"));
                        return [];
                    case "sticky":
                        speech.StickyRandom = OnOff(StickyValue(message), speech.StickyRandom);
                        return new JsonObject { ["sticky"] = speech.StickyRandom };
                    default:
                        throw new RequestException("method must be 'nickname' or 'sticky'");
                }
            case "assign":
            {
                if (!Str(message, "method", "last").Equals("last", StringComparison.OrdinalIgnoreCase))
                    throw new RequestException("method must be 'last'");
                var user = UserName(message);
                if (user.Length == 0) throw new RequestException("username is required");
                var alias = speech.AssignLastVoice(Platform, user);
                return new JsonObject { ["success"] = alias is not null, ["alias"] = alias ?? "" };
            }
            case "mode":
                if (!speech.SetMode(Str(message, "mode"))) throw new RequestException("mode must be 'all' or 'command'");
                return [];
            case "setenabled":
                speech.Enabled = Toggle(Str(message, "state"), speech.Enabled, "enabled", "disabled");
                return State();
            case "setpaused":
                // Streamer.bot's sub-action sends Paused / Enabled (= resumed) / Toggle.
                speech.Paused = Toggle(Str(message, "state"), speech.Paused, "paused", "enabled", "resumed");
                return State();
            case "activatevoicegate":
            {
                var profile = Str(message, "profile");
                return new JsonObject { ["success"] = speech.ActivateVoiceGateProfile(profile) };
            }
            case "getaliases":
                return new JsonObject { ["aliases"] = new JsonArray(speech.Aliases.Select(a => (JsonNode)a).ToArray()) };
            case "getvoices":
            {
                var voices = await speech.GetVoicesAsync(ct).ConfigureAwait(false);
                return new JsonObject
                {
                    ["voices"] = new JsonArray(voices.Select(v => (JsonNode)new JsonObject
                    {
                        ["engine"] = v.Engine, ["id"] = v.Id, ["name"] = v.Name, ["language"] = v.Language,
                    }).ToArray()),
                };
            }
            case "getinfo": return new JsonObject { ["info"] = new JsonObject { ["name"] = "Cuebot", ["source"] = "cuebot" } };
            case "getstate": return State();
            default: throw new RequestException($"Unknown request '{request}'");
        }
    }

    /// <summary>Handles one UDP datagram <c>{"command": ...}</c>. Unknown commands are logged and ignored.</summary>
    public async Task HandleCommandAsync(JsonElement message, CancellationToken ct = default)
    {
        var command = Str(message, "command").ToLowerInvariant();
        switch (command)
        {
            case "speak":
                await speech.SpeakAsync(Str(message, "message"), Str(message, "voice"),
                    Bool(message, "badWordFilter") || Bool(message, "badwordfilter"), wait: false, silent: false, ct).ConfigureAwait(false);
                break;
            case "stop": speech.Stop(); break;
            case "enable" or "on": speech.Enabled = true; break;
            case "disable" or "off": speech.Enabled = false; break;
            case "pause": speech.Paused = true; break;
            case "resume": speech.Paused = false; break;
            case "clear": speech.Clear(); break;
            case "events":
            {
                if (EventName(message) is not { } name)
                    speech.EventsEnabled = OnOff(Str(message, "state"), speech.EventsEnabled);
                else if (ToggleEvent(name, Str(message, "state")) is null)
                    log.Warn(LogSource, $"Unknown event '{name}'");
                break;
            }
            case "profile":
                if (!speech.ActivateIgnoreProfile(Str(message, "profile"))) log.Warn(LogSource, $"No voice profile '{Str(message, "profile")}'");
                break;
            case "reg":
                speech.SetRegular(Platform, Str(message, "id"), Str(message, "user"),
                    !Str(message, "mode").Equals("del", StringComparison.OrdinalIgnoreCase));
                break;
            case "set" when Str(message, "method").Equals("nickname", StringComparison.OrdinalIgnoreCase):
                speech.SetNickname(Platform, Str(message, "username"), Str(message, "nickname"));
                break;
            case "set" when Str(message, "method").Equals("sticky", StringComparison.OrdinalIgnoreCase):
                speech.StickyRandom = OnOff(StickyValue(message), speech.StickyRandom);
                break;
            case "assign" when Str(message, "method", "last").Equals("last", StringComparison.OrdinalIgnoreCase):
            {
                var user = UserName(message);
                if (speech.AssignLastVoice(Platform, user) is null) log.Warn(LogSource, $"'{user}' has not spoken yet; no voice assigned");
                break;
            }
            default:
                log.Warn(LogSource, $"Unsupported UDP command '{command}'");
                break;
        }
    }

    /// <summary>The Speaker.bot event for a speech bus event, or null for other events.</summary>
    public static (string Type, JsonObject Data)? MapEvent(BotEvent e) => e.Type switch
    {
        Speech.SpeechEvents.Started => ("SpeechStarted", StreamerBotEnvelope.Data(e.Args)),
        Speech.SpeechEvents.Finished => ("SpeechFinished", StreamerBotEnvelope.Data(e.Args)),
        Speech.SpeechEvents.StateChanged => ("StateChanged", StreamerBotEnvelope.Data(e.Args)),
        _ => null,
    };

    private JsonObject State() => new()
    {
        ["enabled"] = speech.Enabled,
        ["paused"] = speech.Paused,
        ["eventsEnabled"] = speech.EventsEnabled,
        ["queueLength"] = speech.QueueLength,
        ["current"] = speech.CurrentText,
    };

    private static bool OnOff(string state, bool current) => state.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "enabled" => true,
        "off" or "false" or "disabled" => false,
        "toggle" => !current,
        _ => throw new RequestException("state must be 'on', 'off' or 'toggle'"),
    };

    private static bool Toggle(string state, bool current, string onWord, params string[] offWords)
    {
        var s = state.Trim().ToLowerInvariant();
        if (s == "toggle") return !current;
        if (s == onWord || s == "true" || s == "on") return true;
        if (offWords.Contains(s) || s == "false" || s == "off") return false;
        throw new RequestException($"state must be '{onWord}', '{offWords[0]}' or 'toggle'");
    }

    /// <summary>The single event an "events" message names (<c>value</c>, or <c>event</c>); null for all events.</summary>
    private static string? EventName(JsonElement message)
    {
        var value = Str(message, "value");
        if (value.Length == 0) value = Str(message, "event");
        return value.Trim().Length == 0 || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();
    }

    /// <summary>Turns one event on, off or toggles it; the new state, or null when the event is unknown.</summary>
    private bool? ToggleEvent(string name, string state)
    {
        if (speech.IsEventEnabled(name) is not { } current) return null;
        var enabled = OnOff(state, current);
        return speech.SetEventEnabled(name, enabled) ? enabled : null;
    }

    /// <summary>"set sticky" sends <c>value</c> (bool); <c>state</c> is accepted too.</summary>
    private static string StickyValue(JsonElement message) =>
        Str(message, "value") is { Length: > 0 } value ? value : Str(message, "state");

    private static string UserName(JsonElement message) =>
        (Str(message, "username") is { Length: > 0 } u ? u : Str(message, "user")).Trim().TrimStart('@');

    private static string Str(JsonElement message, string name, string fallback = "") => StreamerBotRequests.Str(message, name, fallback);
    private static bool Bool(JsonElement message, string name) => StreamerBotRequests.Bool(message, name, false);
}
