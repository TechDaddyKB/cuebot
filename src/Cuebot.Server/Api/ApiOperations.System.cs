using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Decks;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Speech;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    // ---- Connections -----------------------------------------------------------------------------------------------

    /// <summary>Every connection (platforms, OBS, donation services, devices) with its state and whether it can be (dis)connected here.</summary>
    public JsonObject Connections() => new()
    {
        ["connections"] = new JsonArray([.. services.Connections.Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name, ["state"] = c.State.ToString(), ["message"] = c.StatusMessage, ["controllable"] = c is IConnectable,
        })]),
    };

    /// <summary>
    /// Connects or disconnects one connection by name. Connecting uses the sign-in and settings saved on its page; it
    /// cannot sign in for the streamer.
    /// </summary>
    public async Task<JsonObject> SetConnectionAsync(string name, bool connect, CancellationToken ct)
    {
        var matches = services.Connections.Where(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var connection = matches.FirstOrDefault() ?? throw ApiException.NotFound($"No connection '{name}' (list_connections shows them)");
        if (connection is not IConnectable control) throw ApiException.BadRequest($"{connection.Name} is not started or stopped from here; use its page in Cuebot");
        try
        {
            if (connect) await control.ConnectAsync(ct).ConfigureAwait(false);
            else await control.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TimeoutException)
        {
            throw ApiException.Conflict($"{connection.Name}: {ex.Message}");
        }
        services.Log.Info(LogSource, $"API {(connect ? "connected" : "disconnected")} {connection.Name}");
        return new JsonObject { ["name"] = connection.Name, ["state"] = connection.State.ToString(), ["message"] = connection.StatusMessage };
    }

    // ---- Speech ----------------------------------------------------------------------------------------------------

    private SpeechService SpeechService => services.Speech ?? throw ApiException.Unavailable("Speech is not available");

    /// <summary>The speech keys the API may read and change (never engine credentials).</summary>
    private static readonly string[] SpeechKeys = ["enabled", "defaultAlias", "speakChat", "replacements", "masterVolume", "filters"];

    /// <summary>Speech settings without credentials: voice aliases, the default alias, chat reading, replacements, volume, filters.</summary>
    public JsonObject SpeechSettings()
    {
        var all = ToObject(SpeechService.Settings);
        var result = new JsonObject();
        foreach (var key in SpeechKeys) result[key] = all[key]?.DeepClone();
        result["aliases"] = all["aliases"]?.DeepClone();
        return result;
    }

    /// <summary>Changes speech settings (only <see cref="SpeechKeys"/>; objects are merged).</summary>
    public JsonObject UpdateSpeechSettings(JsonObject changes)
    {
        if (changes.Select(c => c.Key).FirstOrDefault(k => !SpeechKeys.Contains(k)) is { } bad)
            throw ApiException.BadRequest($"'{bad}' cannot be changed here (allowed: {string.Join(", ", SpeechKeys)}; aliases with save_voice_alias)");
        ApplySettings(SpeechService.Settings, changes);
        SpeechService.SaveSettings();
        return SpeechSettings();
    }

    /// <summary>The speech engines and their voices (for voice aliases).</summary>
    public async Task<JsonObject> SpeechVoicesAsync(string? engine, CancellationToken ct)
    {
        var engines = new JsonArray();
        foreach (var e in SpeechService.Engines.Where(e => string.IsNullOrWhiteSpace(engine) || string.Equals(e.Id, engine, StringComparison.OrdinalIgnoreCase)))
        {
            var entry = new JsonObject { ["id"] = e.Id, ["name"] = e.DisplayName, ["available"] = e.IsAvailable, ["reason"] = e.UnavailableReason };
            if (e.IsAvailable)
            {
                try
                {
                    var voices = await e.GetVoicesAsync(ct).ConfigureAwait(false);
                    entry["voices"] = JsonSerializer.SerializeToNode(voices.Take(300), JsonOptions);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
                {
                    entry["reason"] = ex.Message;
                }
            }
            engines.Add(entry);
        }
        return new JsonObject { ["engines"] = engines };
    }

    /// <summary>Creates or replaces a voice alias (by name): <c>{name, engineId, voiceId, rate, pitch, volume, style, voices, selection}</c>.</summary>
    public JsonObject SaveVoiceAlias(JsonObject alias)
    {
        var value = FromNode<VoiceAlias>(alias, "alias");
        if (string.IsNullOrWhiteSpace(value.Name)) throw ApiException.BadRequest("The alias needs a name");
        if (value.EngineId.Length > 0 && SpeechService.Engines.All(e => !string.Equals(e.Id, value.EngineId, StringComparison.OrdinalIgnoreCase)))
            throw ApiException.BadRequest($"Unknown engine '{value.EngineId}' (list_speech_voices)");
        var aliases = SpeechService.Settings.Aliases;
        var index = aliases.FindIndex(a => string.Equals(a.Name, value.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) aliases[index] = value;
        else aliases.Add(value);
        SpeechService.SaveSettings();
        return SpeechSettings();
    }

    public JsonObject DeleteVoiceAlias(string name)
    {
        if (SpeechService.Settings.Aliases.RemoveAll(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
            throw ApiException.NotFound($"No voice alias '{name}'");
        SpeechService.SaveSettings();
        return SpeechSettings();
    }

    /// <summary>Speaks text now with Cuebot's speech (filters on), as an alias or the default voice.</summary>
    public async Task<JsonObject> SpeakAsync(string text, string? alias, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw ApiException.BadRequest("Nothing to say");
        var speaker = services.Speaker ?? throw ApiException.Unavailable("Speech is not available");
        var result = await speaker.SpeakAsync(text, alias ?? "", badWordFilter: true, wait: false, silent: false, ct).ConfigureAwait(false);
        return new JsonObject { ["queued"] = result.Success, ["message"] = result.Success ? null : "Speech did not take it (off, or filtered)" };
    }

    /// <summary>Gives a viewer their own voice alias (as !voice does).</summary>
    public JsonObject SetViewerVoice(string platform, string user, string alias)
    {
        if (SpeechService.Settings.Aliases.All(a => !string.Equals(a.Name, alias.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw ApiException.NotFound($"No voice alias '{alias}'");
        var (id, name) = ResolveUser(platform, user);
        SpeechService.SetUserVoice(platform, id, name ?? user, alias.Trim());
        return new JsonObject { ["platform"] = platform, ["user"] = name ?? user, ["alias"] = alias.Trim() };
    }

    // ---- Feature settings ------------------------------------------------------------------------------------------

    /// <summary>The settings of a feature page that has no secrets: points, media, polls, viewers.</summary>
    public JsonObject FeatureSettings(string feature) => feature.Trim().ToLowerInvariant() switch
    {
        "points" => ToObject((services.Points ?? throw ApiException.Unavailable("Points are not available")).Settings),
        "media" => ToObject((services.Media ?? throw ApiException.Unavailable("Media requests are not available")).Settings),
        "polls" => new JsonObject
        {
            ["voteCommand"] = (services.ChatPolls ?? throw ApiException.Unavailable("Polls are not available")).VoteCommand,
            ["betCommand"] = services.ChatPolls.BetCommand,
            ["numbersVote"] = services.ChatPolls.NumbersVote,
        },
        "viewers" => ToObject((services.Users ?? throw ApiException.Unavailable("Viewers are not available")).Settings),
        _ => throw ApiException.BadRequest($"Unknown feature '{feature}' (points, media, polls or viewers)"),
    };

    /// <summary>Changes a feature's settings (fields merged); see <see cref="FeatureSettings"/>.</summary>
    public JsonObject UpdateFeatureSettings(string feature, JsonObject changes)
    {
        switch (feature.Trim().ToLowerInvariant())
        {
            case "points":
                var points = services.Points ?? throw ApiException.Unavailable("Points are not available");
                if (changes.ContainsKey("currencies")) throw ApiException.BadRequest("Extra currencies are changed on the Points page");
                points.UpdateSettings(s => ApplySettings(s, changes));
                break;
            case "media":
                var media = services.Media ?? throw ApiException.Unavailable("Media requests are not available");
                media.UpdateSettings(s => ApplySettings(s, changes));
                break;
            case "polls":
                var polls = services.ChatPolls ?? throw ApiException.Unavailable("Polls are not available");
                polls.SetCommands(Text(changes["voteCommand"]), Text(changes["betCommand"]),
                    changes["numbersVote"] is JsonValue n && n.TryGetValue<bool>(out var b) ? b : null);
                break;
            case "viewers":
                var users = services.Users ?? throw ApiException.Unavailable("Viewers are not available");
                users.UpdateSettings(s => ApplySettings(s, changes));
                break;
            default:
                throw ApiException.BadRequest($"Unknown feature '{feature}' (points, media, polls or viewers)");
        }
        return FeatureSettings(feature);

        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>
    /// Merges <paramref name="changes"/> into <paramref name="target"/>: serialized, merged, read back and copied
    /// property by property, so unknown keys and wrong types are refused.
    /// </summary>
    private static void ApplySettings<T>(T target, JsonObject changes) where T : class
    {
        var merged = Merge(ToObject(target), changes);
        T updated;
        try
        {
            updated = merged.Deserialize<T>(JsonOptions) ?? throw ApiException.BadRequest("The settings could not be read");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("The settings could not be read: " + ex.Message);
        }
        var known = ToObject(target).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changes.Select(c => c.Key).FirstOrDefault(k => !known.Contains(k)) is { } unknown)
            throw ApiException.BadRequest($"Unknown setting '{unknown}'");
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
            property.SetValue(target, property.GetValue(updated));
    }

    // ---- Stream totals ---------------------------------------------------------------------------------------------

    /// <summary>This stream's totals (follows, subs, tips... counts and amounts, goals) used by labels and goal widgets.</summary>
    public JsonObject StreamTotals() => new()
    {
        ["session"] = (services.SessionData ?? throw ApiException.Unavailable("Stream totals are not available")).Snapshot(),
    };

    /// <summary><c>reset</c> this stream's totals, or <c>setGoal</c> for a type (follower, subscriber, tip, cheer...).</summary>
    public JsonObject StreamTotalsCommand(string command, string? type, double? amount)
    {
        var session = services.SessionData ?? throw ApiException.Unavailable("Stream totals are not available");
        switch (command)
        {
            case "reset": session.ResetSession(); break;
            case "setGoal":
                session.SetGoal(string.IsNullOrWhiteSpace(type) ? throw ApiException.BadRequest("type is required") : type.Trim(),
                    amount ?? throw ApiException.BadRequest("amount is required"));
                break;
            default: throw ApiException.BadRequest($"Unknown command '{command}' (reset or setGoal)");
        }
        return StreamTotals();
    }

    // ---- Hotkeys ---------------------------------------------------------------------------------------------------

    private HotkeyService HotkeyService => services.Hotkeys ?? throw ApiException.Unavailable("Hotkeys are not available");

    public JsonObject Hotkeys() => new()
    {
        ["hotkeys"] = JsonSerializer.SerializeToNode(HotkeyService.Hotkeys, JsonOptions),
        ["groups"] = JsonSerializer.SerializeToNode(HotkeyService.Groups, JsonOptions),
    };

    /// <summary>Creates or replaces a hotkey (<c>{id?, name, gesture, action, enabled, group}</c>); gesture like "Ctrl+Shift+F1".</summary>
    public async Task<JsonObject> SaveHotkeyAsync(JsonObject hotkey)
    {
        var value = FromNode<HotkeyDefinition>(hotkey, "hotkey");
        if (string.IsNullOrWhiteSpace(value.Gesture)) throw ApiException.BadRequest("The hotkey needs a gesture, e.g. Ctrl+Shift+F1");
        if (value.Action.Length > 0) value.Action = FindAction(value.Action).Name;
        if (value.Id == Guid.Empty) value.Id = Guid.NewGuid();
        await HotkeyService.SaveAsync(value).ConfigureAwait(false);
        return Hotkeys();
    }

    public async Task<JsonObject> DeleteHotkeyAsync(string hotkey)
    {
        var text = hotkey.Trim();
        var found = HotkeyService.Hotkeys.FirstOrDefault(h => h.Id.ToString() == text || string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw ApiException.NotFound($"No hotkey '{hotkey}'");
        await HotkeyService.RemoveAsync(found.Id).ConfigureAwait(false);
        return Hotkeys();
    }

    // ---- Decks -----------------------------------------------------------------------------------------------------

    private DeckService DeckService => services.Decks ?? throw ApiException.Unavailable("Decks are not available");

    public JsonObject Decks() => new()
    {
        ["decks"] = new JsonArray([.. DeckService.Decks.Select(d => (JsonNode)new JsonObject { ["id"] = d.Id.ToString(), ["name"] = d.Name, ["enabled"] = d.Enabled })]),
    };

    public JsonObject GetDeck(string deck) => ToObject(DeckService.Find(deck) ?? throw ApiException.NotFound($"No deck '{deck}'"));

    /// <summary>Creates or replaces a deck (the full definition, as get_deck returns it).</summary>
    public JsonObject SaveDeck(JsonObject deck)
    {
        var value = FromNode<DeckDefinition>(deck, "deck");
        if (string.IsNullOrWhiteSpace(value.Name)) throw ApiException.BadRequest("The deck needs a name");
        if (value.Id == Guid.Empty) value.Id = Guid.NewGuid();
        DeckService.Save(value);
        return GetDeck(value.Id.ToString());
    }

    public JsonObject DeleteDeck(string deck)
    {
        var found = DeckService.Find(deck) ?? throw ApiException.NotFound($"No deck '{deck}'");
        DeckService.Remove(found.Id);
        return Decks();
    }
}
