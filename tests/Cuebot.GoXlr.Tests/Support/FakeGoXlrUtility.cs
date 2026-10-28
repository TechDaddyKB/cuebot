using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.GoXlr.Tests.Support;

/// <summary>
/// An in-process GoXLR Utility on http://localhost:&lt;free port&gt;/api/websocket, modelled on the utility's documented
/// protocol (goxlr-ipc's WebsocketRequest/WebsocketResponse, DaemonStatus and GoXLRCommand): requests are
/// <c>{"id":n,"data":...}</c>, answers echo the id, and every status change is broadcast as a JSON patch (RFC 6902, the
/// json-patch crate's diff output) with id u64::MAX. Commands it knows change its status the way the utility does.
/// </summary>
internal sealed class FakeGoXlrUtility : IAsyncDisposable
{
    public const string Serial = "S210500000ABC";
    public const ulong PatchId = ulong.MaxValue;

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<WebSocket, byte> _sessions = new();
    private readonly Lock _statusLock = new();
    private JsonNode _status;

    private FakeGoXlrUtility(JsonNode status)
    {
        _status = status;
        for (var attempt = 1; ; attempt++)
        {
            Port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 5) { _listener.Close(); }
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Every request's <c>data</c>, as JSON text, in arrival order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Paths the clients asked for (the websocket upgrade path).</summary>
    public ConcurrentQueue<string> Paths { get; } = new();

    public int SessionCount => _sessions.Count;

    public int SessionsOpened { get; private set; }

    /// <summary>When false, commands are answered "Ok" but the status does not change (so no patch is sent).</summary>
    public bool ApplyCommands { get; set; } = true;

    public static FakeGoXlrUtility Start(bool mini = false) => new(Status(mini));

    public JsonNode Snapshot()
    {
        lock (_statusLock) return _status.DeepClone();
    }

    /// <summary>Changes the status (as the hardware or the utility's own UI would) and broadcasts the patch.</summary>
    public Task MutateAsync(Action<JsonNode> change)
    {
        JsonArray patch;
        lock (_statusLock)
        {
            var before = _status.DeepClone();
            change(_status);
            patch = Diff(before, _status);
        }
        return patch.Count == 0 ? Task.CompletedTask : BroadcastAsync(new JsonObject { ["id"] = PatchId, ["data"] = new JsonObject { ["Patch"] = patch } }.ToJsonString());
    }

    public JsonNode Mixer(JsonNode status) => status["mixers"]![Serial]!;

    public Task BroadcastAsync(string text) => Task.WhenAll(_sessions.Keys.Select(s => SendAsync(s, text)));

    /// <summary>Closes every session from the server side, as the utility does when it quits.</summary>
    public async Task CloseSessionsAsync()
    {
        foreach (var s in _sessions.Keys)
        {
            try { await s.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None); }
            catch (Exception) { /* already gone */ }
        }
    }

    public async Task<string> NextRequestAsync(Func<string, bool> match, int skip = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = Requests.Where(match).Skip(skip).FirstOrDefault();
            if (hit is not null) return hit;
            await Task.Delay(10);
        }
        throw new TimeoutException("no matching request");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Close();
        try { await _acceptLoop; } catch (Exception) { /* listener closed */ }
        _stop.Dispose();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            Paths.Enqueue(context.Request.Url!.AbsolutePath);
            if (!context.Request.IsWebSocketRequest || context.Request.Url.AbsolutePath != "/api/websocket")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _sessions[socket] = 0;
            SessionsOpened++;
            _ = Task.Run(() => RunSessionAsync(socket));
        }
    }

    private async Task RunSessionAsync(WebSocket socket)
    {
        try
        {
            while (await ReceiveAsync(socket) is { } text)
            {
                JsonNode? request;
                try { request = JsonNode.Parse(text); }
                catch (JsonException) { await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "bad json", CancellationToken.None); return; }
                var id = request?["id"]?.GetValue<ulong>();
                if (id is null) { await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Missing or invalid Request ID", CancellationToken.None); return; }
                var data = request!["data"];
                Requests.Enqueue(data?.ToJsonString() ?? "null");
                var (answer, patch) = Handle(data);
                await SendAsync(socket, new JsonObject { ["id"] = id, ["data"] = answer }.ToJsonString());
                if (patch is { Count: > 0 })
                    await BroadcastAsync(new JsonObject { ["id"] = PatchId, ["data"] = new JsonObject { ["Patch"] = patch } }.ToJsonString());
            }
        }
        catch (Exception)
        {
            // Client went away.
        }
        finally
        {
            _sessions.TryRemove(socket, out _);
        }
    }

    private (JsonNode Answer, JsonArray? Patch) Handle(JsonNode? data)
    {
        if (data is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            return v.GetValue<string>() switch
            {
                "Ping" => (JsonValue.Create("Ok")!, null),
                "GetStatus" => (new JsonObject { ["Status"] = Snapshot() }, null),
                var other => (Error($"unknown variant `{other}`"), null),
            };
        }
        if (data is JsonObject o && o["Command"] is JsonArray { Count: 2 } args)
        {
            var serial = args[0]!.GetValue<string>();
            lock (_statusLock)
            {
                if (_status["mixers"]![serial] is null) return (Error($"Device {serial} is not connected"), null);
                var before = _status.DeepClone();
                var error = ApplyCommands ? Apply(_status["mixers"]![serial]!, args[1]!) : null;
                if (error is not null) return (Error(error), null);
                return (JsonValue.Create("Ok")!, Diff(before, _status));
            }
        }
        return (Error("Error Deserialising Request"), null);
    }

    private static JsonObject Error(string message) => new() { ["Error"] = message };

    /// <summary>The subset of GoXLRCommand the tests use, applied like the daemon does. Returns an error message or null.</summary>
    private static string? Apply(JsonNode mixer, JsonNode command)
    {
        if (command is not JsonObject { Count: 1 } c) return "invalid command";
        var (name, value) = c.First();
        JsonNode? Arg(int i) => value is JsonArray arr ? arr[i] : i == 0 ? value : null;
        string S(int i) => Arg(i)!.GetValue<string>();
        switch (name)
        {
            case "SetVolume":
                mixer["levels"]!["volumes"]![S(0)] = Arg(1)!.GetValue<int>();
                return null;
            case "SetFader":
                mixer["fader_status"]![S(0)]!["channel"] = S(1);
                return null;
            case "SetFaderMuteState":
                mixer["fader_status"]![S(0)]!["mute_state"] = S(1);
                return null;
            case "SetFaderMuteFunction":
                mixer["fader_status"]![S(0)]!["mute_type"] = S(1);
                return null;
            case "SetCoughMuteState":
                mixer["cough_button"]!["state"] = S(0);
                return null;
            case "LoadProfile":
                if (!Profiles.Contains(S(0))) return $"Profile {S(0)} does not exist";
                mixer["profile_name"] = S(0);
                // Loading a profile replaces whole sections, as the daemon does.
                mixer["fader_status"]!["B"] = new JsonObject { ["channel"] = "Game", ["mute_type"] = "All", ["scribble"] = null, ["mute_state"] = "Unmuted" };
                return null;
            case "LoadMicProfile":
                mixer["mic_profile_name"] = S(0);
                return null;
            case "SetFXEnabled":
                mixer["effects"]!["is_enabled"] = Arg(0)!.GetValue<bool>();
                return null;
            case "SetMegaphoneEnabled":
            case "SetRobotEnabled":
            case "SetHardTuneEnabled":
                var key = name switch { "SetMegaphoneEnabled" => "megaphone", "SetRobotEnabled" => "robot", _ => "hard_tune" };
                mixer["effects"]!["current"]![key]!["is_enabled"] = Arg(0)!.GetValue<bool>();
                return null;
            case "SetActiveEffectPreset":
                mixer["effects"]!["active_preset"] = S(0);
                return null;
            case "SetActiveSamplerBank":
                mixer["sampler"]!["active_bank"] = S(0);
                return null;
            case "PlayNextSample":
            case "PlaySampleByIndex":
                mixer["sampler"]!["banks"]![S(0)]![S(1)]!["is_playing"] = true;
                return null;
            case "StopSamplePlayback":
                mixer["sampler"]!["banks"]![S(0)]![S(1)]!["is_playing"] = false;
                return null;
            case "SetRouter":
                mixer["router"]![S(0)]![S(1)] = Arg(2)!.GetValue<bool>();
                return null;
            case "SetGlobalColour":
                mixer["lighting"]!["simple"]!["Global"]!["colour_one"] = S(0);
                return null;
            case "SetFaderColours":
            case "SetButtonColours":
            case "SetAnimationMode":
            case "SetScribbleText":
                return null;
            default:
                return $"unknown variant `{name}`";
        }
    }

    public static readonly string[] Profiles = ["Default", "Gaming", "Just Chatting"];

    /// <summary>A DaemonStatus like the utility's for one full-size GoXLR (or a Mini).</summary>
    public static JsonNode Status(bool mini = false)
    {
        string[] channels = ["Mic", "LineIn", "Console", "System", "Game", "Chat", "Sample", "Music", "Headphones", "MicMonitor", "LineOut"];
        string[] inputs = ["Microphone", "Chat", "Music", "Game", "Console", "LineIn", "System", "Samples"];
        string[] outputs = ["Headphones", "BroadcastMix", "ChatMic", "Sampler", "LineOut", "StreamMix2"];
        string[] buttons =
        [
            "Fader1Mute", "Fader2Mute", "Fader3Mute", "Fader4Mute", "Bleep", "Cough", "EffectSelect1", "EffectSelect2", "EffectSelect3",
            "EffectSelect4", "EffectSelect5", "EffectSelect6", "EffectFx", "EffectMegaphone", "EffectRobot", "EffectHardTune",
            "SamplerSelectA", "SamplerSelectB", "SamplerSelectC", "SamplerTopLeft", "SamplerTopRight", "SamplerBottomLeft", "SamplerBottomRight", "SamplerClear",
        ];
        JsonObject Fader(string channel) => new()
        {
            ["channel"] = channel, ["mute_type"] = "All", ["mute_state"] = "Unmuted",
            ["scribble"] = new JsonObject { ["file_name"] = null, ["bottom_text"] = channel, ["left_text"] = null, ["inverted"] = false },
        };
        var volumes = new JsonObject();
        foreach (var c in channels) volumes[c] = c == "Music" ? 191 : 255;
        var router = new JsonObject();
        foreach (var i in inputs)
        {
            var row = new JsonObject();
            foreach (var o in outputs) row[o] = o is "Headphones" or "BroadcastMix";
            router[i] = row;
        }
        var down = new JsonObject();
        foreach (var b in buttons) down[b] = false;
        JsonObject Button(int samples) => new()
        {
            ["function"] = "PlayNext", ["order"] = "Sequential", ["is_playing"] = false, ["is_recording"] = false,
            ["samples"] = new JsonArray([.. Enumerable.Range(1, samples).Select(n => (JsonNode)new JsonObject { ["name"] = $"clip{n}.wav", ["start_pct"] = 0.0, ["stop_pct"] = 100.0 })]),
        };
        JsonObject Bank() => new() { ["TopLeft"] = Button(2), ["TopRight"] = Button(1), ["BottomLeft"] = Button(0), ["BottomRight"] = Button(0) };
        JsonObject Effect(string extra) => new() { ["is_enabled"] = false, ["style"] = extra, ["amount"] = 0 };

        var mixer = new JsonObject
        {
            ["hardware"] = new JsonObject
            {
                ["versions"] = new JsonObject { ["firmware"] = new JsonArray(1, 4, 2, 107) },
                ["serial_number"] = Serial, ["manufactured_date"] = "2021-05-01", ["device_type"] = mini ? "Mini" : "Full", ["colour_way"] = "Black",
                ["usb_device"] = new JsonObject { ["manufacturer_name"] = "TC-Helicon", ["product_name"] = mini ? "GoXLRMini" : "GoXLR", ["version"] = new JsonArray(1, 0, 0), ["bus_number"] = 1, ["address"] = 4, ["identifier"] = null },
            },
            ["shutdown_commands"] = new JsonArray(), ["sleep_commands"] = new JsonArray(), ["wake_commands"] = new JsonArray(),
            ["fader_status"] = new JsonObject { ["A"] = Fader("Mic"), ["B"] = Fader("Music"), ["C"] = Fader("Chat"), ["D"] = Fader("System") },
            ["mic_status"] = new JsonObject { ["mic_type"] = "Dynamic", ["mic_gains"] = new JsonObject { ["Dynamic"] = 40, ["Condenser"] = 30, ["Jack"] = 20 } },
            ["levels"] = new JsonObject { ["submix_supported"] = false, ["output_monitor"] = "Headphones", ["volumes"] = volumes, ["submix"] = null, ["bleep"] = -20, ["deess"] = 0 },
            ["router"] = router,
            ["cough_button"] = new JsonObject { ["is_toggle"] = false, ["mute_type"] = "All", ["state"] = "Unmuted" },
            ["lighting"] = new JsonObject { ["simple"] = new JsonObject { ["Global"] = new JsonObject { ["colour_one"] = "00FFFF" } } },
            ["effects"] = mini ? null : new JsonObject
            {
                ["is_enabled"] = false, ["active_preset"] = "Preset1",
                ["preset_names"] = new JsonObject { ["Preset1"] = "Default", ["Preset2"] = "Spooky", ["Preset3"] = "Chipmunk", ["Preset4"] = "Radio", ["Preset5"] = "Robot", ["Preset6"] = "Echoes" },
                ["current"] = new JsonObject
                {
                    ["reverb"] = Effect("Library"), ["echo"] = Effect("Quarter"), ["pitch"] = Effect("Narrow"), ["gender"] = Effect("Medium"),
                    ["megaphone"] = Effect("Megaphone"), ["robot"] = Effect("Robot1"), ["hard_tune"] = Effect("Natural"),
                },
            },
            ["sampler"] = mini ? null : new JsonObject
            {
                ["processing_state"] = new JsonObject { ["progress"] = null, ["last_error"] = null },
                ["active_bank"] = "A", ["clear_active"] = false, ["record_buffer"] = 0,
                ["banks"] = new JsonObject { ["A"] = Bank(), ["B"] = Bank(), ["C"] = Bank() },
            },
            ["settings"] = new JsonObject { ["mute_hold_duration"] = 500, ["vc_mute_also_mute_cm"] = true, ["lock_faders"] = false },
            ["button_down"] = down,
            ["profile_name"] = "Default",
            ["mic_profile_name"] = "DEFAULT",
        };
        return new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["http_settings"] = new JsonObject { ["enabled"] = true, ["bind_address"] = "localhost", ["cors_enabled"] = false, ["port"] = 14564 },
                ["daemon_version"] = "1.2.3", ["platform"] = "linux", ["allow_network_access"] = false,
            },
            ["firmware"] = new JsonObject(),
            ["mixers"] = new JsonObject { [Serial] = mixer },
            ["paths"] = new JsonObject { ["profile_directory"] = "/home/user/.local/share/goxlr-utility/profiles" },
            ["files"] = new JsonObject
            {
                ["profiles"] = new JsonArray([.. Profiles.Select(p => (JsonNode)JsonValue.Create(p)!)]),
                ["mic_profiles"] = new JsonArray("DEFAULT", "Podcast"),
                ["presets"] = new JsonArray("Cave", "Robot Voice"),
                ["samples"] = new JsonObject { ["clip1.wav"] = new JsonObject { ["name"] = "clip1.wav", ["gain_pct"] = 100 } },
                ["icons"] = new JsonArray(),
            },
        };
    }

    /// <summary>A JSON patch from <paramref name="a"/> to <paramref name="b"/>, like the json-patch crate's diff (add/remove/replace).</summary>
    public static JsonArray Diff(JsonNode? a, JsonNode? b, string path = "")
    {
        var ops = new JsonArray();
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var (k, va) in oa)
            {
                var p = path + "/" + k.Replace("~", "~0").Replace("/", "~1");
                if (!ob.ContainsKey(k)) ops.Add(new JsonObject { ["op"] = "remove", ["path"] = p });
                else foreach (var op in Diff(va, ob[k], p)) ops.Add(op!.DeepClone());
            }
            foreach (var (k, vb) in ob)
                if (!oa.ContainsKey(k))
                    ops.Add(new JsonObject { ["op"] = "add", ["path"] = path + "/" + k.Replace("~", "~0").Replace("/", "~1"), ["value"] = vb?.DeepClone() });
        }
        else if (!JsonNode.DeepEquals(a, b))
        {
            ops.Add(new JsonObject { ["op"] = "replace", ["path"] = path, ["value"] = b?.DeepClone() });
        }
        return ops;
    }

    private async Task SendAsync(WebSocket socket, string text)
    {
        await _sendLock.WaitAsync();
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception) { /* closed */ }
        finally { _sendLock.Release(); }
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); } catch (Exception) { /* gone */ }
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}
