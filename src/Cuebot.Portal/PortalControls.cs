using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Speech;

namespace Cuebot.Portal;

/// <summary>
/// The portal's live controls (docs/MOD_PORTAL_PLAN.md, section 6): the dashboard, scenes, audio levels, queues,
/// text-to-speech and sounds. Using one is not a change to anything saved, so it never waits for approval; each is
/// gated by a role permission, limited per member and written to the log with the member's name. Every service is
/// optional (tests, and a Cuebot without that module).
/// </summary>
public sealed class PortalControls(
    ProfileService profile,
    ChatHub? chat = null,
    IEnumerable<IConnectionStatus>? connections = null,
    ObsService? obs = null,
    QueueService? queues = null,
    SpeechService? speech = null,
    AudioService? audio = null,
    AppLog? log = null,
    TimeProvider? time = null)
{
    public const int MaxFeedItems = 50;
    public const int MaxSpeakLength = 300;
    public const int ControlsPerMinute = 30;
    private const string LogSource = "Mod portal";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _uses = [];

    /// <summary>The ops this class answers, for PortalApi's dispatch.</summary>
    public static bool Handles(string op) => op is "dashboard" or "scenes.list" or "scenes.set" or "audio.list" or "audio.set"
        or "queues.list" or "queues.control" or "tts.state" or "tts.control" or "tts.speak" or "sounds.list" or "sounds.play";

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request, CancellationToken ct = default)
    {
        var (permission, isControl) = op switch
        {
            "dashboard" => (null, false),
            "scenes.list" => (PortalPermission.StreamControl, false),
            "scenes.set" => (PortalPermission.StreamControl, true),
            "audio.list" => (PortalPermission.AudioLevels, false),
            "audio.set" => (PortalPermission.AudioLevels, true),
            "queues.list" => (PortalPermission.StreamControl, false),
            "queues.control" => (PortalPermission.StreamControl, true),
            "tts.state" => (PortalPermission.RunActions, false),
            "tts.control" => (PortalPermission.StreamControl, true),
            "tts.speak" => (PortalPermission.RunActions, true),
            "sounds.list" => (PortalPermission.RunActions, false),
            "sounds.play" => (PortalPermission.RunActions, true),
            _ => ((PortalPermission?)null, false),
        };
        if (permission is { } p && !PortalPolicy.Has(role, p)) return Error("Your role does not include this.");
        if (isControl && !TakeUse(member.Id)) return Error("That is a lot of button presses. Wait a moment and try again.");
        try
        {
            return op switch
            {
                "dashboard" => Dashboard(role),
                "scenes.list" => Scenes(),
                "scenes.set" => await SetSceneAsync(member, request, ct).ConfigureAwait(false),
                "audio.list" => await AudioAsync(ct).ConfigureAwait(false),
                "audio.set" => await SetAudioAsync(member, request, ct).ConfigureAwait(false),
                "queues.list" => Queues(),
                "queues.control" => ControlQueue(member, request),
                "tts.state" => Speech(role),
                "tts.control" => ControlSpeech(member, request),
                "tts.speak" => await SpeakAsync(member, request, ct).ConfigureAwait(false),
                "sounds.list" => Ok(new JsonObject { ["sounds"] = new JsonArray([.. Sounds(role).Select(s => (JsonNode)s.Node)]) }),
                _ => await PlaySoundAsync(member, role, request, ct).ConfigureAwait(false),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or IOException or TimeoutException
            or System.Net.WebSockets.WebSocketException or ArgumentException)
        {
            return Error("That did not work: " + e.Message);
        }
    }

    private JsonObject Dashboard(PortalRole? role)
    {
        var body = new JsonObject();
        if (PortalPolicy.Has(role, PortalPermission.ViewActivity) && chat is not null)
            body["feed"] = new JsonArray([.. chat.RecentFeed().Where(f => !f.IsTest).TakeLast(MaxFeedItems).Reverse().Select(f => (JsonNode)new JsonObject
            {
                ["platform"] = f.Platform,
                ["kind"] = f.Kind.ToString(),
                ["user"] = f.User,
                ["title"] = f.Title,
                ["detail"] = f.Detail,
                ["amount"] = f.Amount,
                ["time"] = f.Time,
            })]);
        if (PortalPolicy.Has(role, PortalPermission.ViewLogs) && connections is not null)
            body["connections"] = new JsonArray([.. connections.Where(c => c is not Relay.PortalRelayClient).Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["state"] = c.State.ToString(),
                ["message"] = c.StatusMessage,
            })]);
        if (PortalPolicy.Has(role, PortalPermission.RunActions))
            body["quickActions"] = new JsonArray([.. profile.Snapshot.Actions
                .Where(a => a.Enabled && PortalPolicy.InScope(role, a.Group) && PortalPolicy.CheckRun(role, a).Count == 0)
                .OrderBy(a => a.Group).ThenBy(a => a.Name)
                .Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id.ToString(), ["name"] = a.Name, ["group"] = a.Group,
                    ["risk"] = PortalPolicy.Steps(a.SubActions).Select(s => (int)StepRiskCatalog.ForStep(s.Type)).DefaultIfEmpty(0).Max(),
                })]);
        return Ok(body);
    }

    // ---- Scenes and audio (OBS) ----

    private JsonObject Scenes() => obs is { IsConnected: true }
        ? Ok(new JsonObject { ["scenes"] = new JsonArray([.. obs.Scenes.Select(s => (JsonNode)s)]), ["current"] = obs.CurrentScene })
        : Error("OBS is not connected to Cuebot right now.");

    private async Task<JsonObject> SetSceneAsync(PortalMember member, JsonObject request, CancellationToken ct)
    {
        var scene = Str(request, "scene");
        if (obs is not { IsConnected: true }) return Error("OBS is not connected to Cuebot right now.");
        if (!obs.Scenes.Contains(scene)) return Error("There is no scene with that name.");
        await obs.SetCurrentSceneAsync(scene, ct).ConfigureAwait(false);
        Log(member, $"switched OBS to the scene \"{scene}\"");
        return Ok(new JsonObject { ["current"] = scene });
    }

    private async Task<JsonObject> AudioAsync(CancellationToken ct)
    {
        if (obs is not { IsConnected: true }) return Error("OBS is not connected to Cuebot right now.");
        var list = new JsonArray();
        foreach (var name in await AudioInputsAsync(ct).ConfigureAwait(false))
        {
            var volume = await obs.RequestAsync("GetInputVolume", new { inputName = name }, ct).ConfigureAwait(false);
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["db"] = Math.Round(volume.TryGetProperty("inputVolumeDb", out var db) && db.ValueKind == JsonValueKind.Number ? db.GetDouble() : 0, 1),
                ["muted"] = await obs.Default.GetInputMuteAsync(name, ct).ConfigureAwait(false),
            });
        }
        return Ok(new JsonObject { ["inputs"] = list });
    }

    /// <summary>OBS inputs that have audio: the special (desktop and mic) inputs and every capture or media input.</summary>
    private async Task<List<string>> AudioInputsAsync(CancellationToken ct)
    {
        var names = new List<string>();
        var inputs = await obs!.RequestAsync("GetInputList", null, ct).ConfigureAwait(false);
        if (!inputs.TryGetProperty("inputs", out var array) || array.ValueKind != JsonValueKind.Array) return names;
        foreach (var input in array.EnumerateArray())
        {
            var kind = input.TryGetProperty("inputKind", out var k) ? k.GetString() ?? "" : "";
            var name = input.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
            if (name.Length > 0 && AudioKinds.Any(a => kind.Contains(a, StringComparison.OrdinalIgnoreCase))) names.Add(name);
        }
        return names;
    }

    private static readonly string[] AudioKinds = ["audio", "wasapi", "pulse", "alsa", "jack", "coreaudio", "pipewire", "ffmpeg_source", "vlc_source", "browser_source"];

    private async Task<JsonObject> SetAudioAsync(PortalMember member, JsonObject request, CancellationToken ct)
    {
        var name = Str(request, "input");
        if (obs is not { IsConnected: true }) return Error("OBS is not connected to Cuebot right now.");
        if (!(await AudioInputsAsync(ct).ConfigureAwait(false)).Contains(name)) return Error("There is no audio source with that name.");
        if (request["muted"] is JsonValue m && m.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            var muted = m.GetValue<bool>();
            await obs.SetInputMuteAsync(name, muted ? MuteMode.Mute : MuteMode.Unmute, ct).ConfigureAwait(false);
            Log(member, $"{(muted ? "muted" : "unmuted")} \"{name}\"");
        }
        if (request["db"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number)
        {
            var db = Math.Clamp(d.GetValue<double>(), -100, 26);
            await obs.Default.SetInputVolumeAsync(name, VolumeMode.SetDb, db, ct).ConfigureAwait(false);
            Log(member, $"set \"{name}\" to {db:0.#} dB");
        }
        return Ok(new JsonObject());
    }

    // ---- Queues ----

    private JsonObject Queues() => queues is null ? Error("Queues are not available.") : Ok(new JsonObject
    {
        ["queues"] = new JsonArray([.. queues.Queues.Select(q => (JsonNode)new JsonObject
        {
            ["name"] = q.Name,
            ["default"] = q.IsDefault,
            ["paused"] = q.Paused,
            ["pending"] = q.Pending,
            ["running"] = q.Running,
        })]),
    });

    private JsonObject ControlQueue(PortalMember member, JsonObject request)
    {
        if (queues is null) return Error("Queues are not available.");
        var name = Str(request, "queue");
        if (queues.Queues.All(q => q.Name != name)) return Error("There is no queue with that name.");
        var cleared = 0;
        switch (Str(request, "command"))
        {
            case "pause": queues.Pause(name); break;
            case "resume": queues.Resume(name); break;
            case "clear": cleared = queues.Clear(name); break;
            default: return Error("Pause, resume or clear?");
        }
        Log(member, $"{Str(request, "command")}d the queue \"{QueueLabel(name)}\"");
        return Ok(new JsonObject { ["cleared"] = cleared });
    }

    private static string QueueLabel(string name) => name.Length == 0 ? "Default" : name;

    // ---- Text-to-speech ----

    private JsonObject Speech(PortalRole? role) => speech is null ? Error("Text-to-speech is not available.") : Ok(new JsonObject
    {
        ["enabled"] = speech.Enabled,
        ["paused"] = speech.Paused,
        ["queue"] = speech.QueueLength,
        ["current"] = speech.CurrentText,
        ["canControl"] = PortalPolicy.Has(role, PortalPermission.StreamControl),
    });

    private JsonObject ControlSpeech(PortalMember member, JsonObject request)
    {
        if (speech is null) return Error("Text-to-speech is not available.");
        var command = Str(request, "command");
        switch (command)
        {
            case "pause": speech.Paused = true; break;
            case "resume": speech.Paused = false; break;
            case "skip": speech.SkipCurrent(); break;
            case "clear": speech.ClearQueue(); break;
            case "enable": speech.Enabled = true; break;
            case "disable": speech.Enabled = false; break;
            default: return Error("That is not a text-to-speech control.");
        }
        Log(member, $"text-to-speech: {command}");
        return Ok(new JsonObject { ["enabled"] = speech.Enabled, ["paused"] = speech.Paused });
    }

    private async Task<JsonObject> SpeakAsync(PortalMember member, JsonObject request, CancellationToken ct)
    {
        if (speech is null) return Error("Text-to-speech is not available.");
        if (!speech.Enabled) return Error("Text-to-speech is turned off.");
        var text = Str(request, "text").Trim();
        if (text.Length == 0) return Error("Type something to say.");
        if (text.Length > MaxSpeakLength) return Error($"Keep it under {MaxSpeakLength} characters.");
        // Queued like any other speech, with the streamer's default voice; the streamer's word filters apply.
        await speech.SpeakAsync(text, new SpeakOptions { ApplyFilters = true, UserName = member.Name, Source = "mod-portal" }, wait: false, ct)
            .ConfigureAwait(false);
        Log(member, $"said with text-to-speech: \"{text}\"");
        return Ok(new JsonObject());
    }

    // ---- Sounds ----

    /// <summary>
    /// The sounds the streamer's own Play sound steps use, in actions the role can see. A mod plays one by its step:
    /// the file, device and volume are the streamer's, never a path the mod sent.
    /// </summary>
    private IEnumerable<(Guid Step, string File, SubActionDefinition Definition, JsonObject Node)> Sounds(PortalRole? role)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in profile.Snapshot.Actions.Where(a => PortalPolicy.InScope(role, a.Group)).OrderBy(a => a.Name))
            foreach (var step in PortalPolicy.Steps(action.SubActions).Where(s => s.Type == "audio.playSound"))
            {
                var file = step.Parameters.TryGetValue("file", out var f) ? f.Trim() : "";
                if (file.Length == 0 || file.Contains('%') || !seen.Add(file) || !File.Exists(file)) continue;
                yield return (step.Id, file, step, new JsonObject
                {
                    ["id"] = step.Id.ToString(),
                    ["name"] = Path.GetFileNameWithoutExtension(file),
                    ["action"] = action.Name,
                });
            }
    }

    private async Task<JsonObject> PlaySoundAsync(PortalMember member, PortalRole? role, JsonObject request, CancellationToken ct)
    {
        if (audio is null) return Error("Sounds are not available.");
        if (!Guid.TryParse(Str(request, "id"), out var id) || Sounds(role).FirstOrDefault(s => s.Step == id) is not { File.Length: > 0 } sound)
            return Error("That sound is not available.");
        var p = sound.Definition.Parameters;
        var device = p.TryGetValue("device", out var dv) && dv.Trim().Length > 0 ? dv.Trim() : null;
        var volume = p.TryGetValue("volume", out var vv) && int.TryParse(vv, out var v) ? Math.Clamp(v, 0, 100) : 100;
        await audio.StartFileAsync(sound.File, new PlaybackOptions(device, volume), "mod-portal", ct).ConfigureAwait(false);
        Log(member, $"played the sound \"{Path.GetFileName(sound.File)}\"");
        return Ok(new JsonObject());
    }

    // ---- Helpers ----

    private bool TakeUse(Guid member)
    {
        var now = _time.GetUtcNow();
        lock (_uses)
        {
            if (!_uses.TryGetValue(member, out var uses)) _uses[member] = uses = new();
            while (uses.Count > 0 && now - uses.Peek() > TimeSpan.FromMinutes(1)) uses.Dequeue();
            if (uses.Count >= ControlsPerMinute) return false;
            uses.Enqueue(now);
            return true;
        }
    }

    private void Log(PortalMember member, string what) => log?.Info(LogSource, $"{member.Name} {what}.");

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
}
