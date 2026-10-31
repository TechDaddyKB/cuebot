using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.GoXlr.Model;
using Cuebot.GoXlr.Protocol;
using static Cuebot.GoXlr.Protocol.GoXlrRequests;

namespace Cuebot.GoXlr;

/// <summary>What a mute sub-action does: mute per the fader's mute function, mute to everything, unmute, or toggle.</summary>
public enum GoXlrMuteAction { Toggle, Mute, MuteToAll, Unmute }

/// <summary>
/// The GoXLR (and GoXLR Mini) through the open-source GoXLR Utility's local WebSocket API
/// (<c>ws://localhost:14564/api/websocket</c>; Linux, Windows and macOS). Loads the full status, keeps it current from
/// the utility's JSON patches, turns the differences into Cuebot events and runs commands. Reconnects with exponential
/// backoff while auto-reconnect is on.
/// </summary>
public sealed class GoXlrService : IConnectable, IAsyncDisposable
{
    private readonly JsonFile<GoXlrSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IGoXlrTransport> _transports;
    private readonly GoXlrTimings _timings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Lock _statusGate = new();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile GoXlrPeer? _peer;
    private JsonNode? _statusNode;
    private volatile GoXlrStatus _status = GoXlrStatus.Empty;
    private int _resyncing;

    public GoXlrService(JsonFile<GoXlrSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), GoXlrTimings.Default)
    {
    }

    internal GoXlrService(JsonFile<GoXlrSettings> settings, EventBus bus, AppLog log, Func<IGoXlrTransport> transports, GoXlrTimings timings)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transports = transports;
        _timings = timings;
    }

    public string Name => "GoXLR";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when the status snapshot changed.</summary>
    public event Action? DataChanged;

    public bool IsConnected => State == ConnectionState.Connected && _peer is not null;

    /// <summary>True while the background loop is looking for, connected to, or waiting to reconnect to the utility.</summary>
    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    public GoXlrSettings Settings => _settings.Value;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The latest status (empty while disconnected).</summary>
    public GoXlrStatus Status => _status;

    /// <summary>The raw status JSON as the utility last described it (after patches), or "" while disconnected.</summary>
    public string RawStatus
    {
        get
        {
            lock (_statusGate) return _statusNode?.ToJsonString() ?? "";
        }
    }

    public Uri Url => BuildUrl(Settings);

    internal static Uri BuildUrl(GoXlrSettings s)
    {
        var host = s.Host.Trim() is { Length: > 0 } h ? h : "localhost";
        var port = s.Port is > 0 and < 65536 ? s.Port : GoXlrDefaults.Port;
        return new Uri($"ws://{(host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}{GoXlrDefaults.WebSocketPath}");
    }

    /// <summary>The GoXLR that commands without a serial go to (the configured one, else the first).</summary>
    public GoXlrMixer Mixer(string serial = "") => Live.Find(serial, Settings.Serial);

    private GoXlrStatus Live => IsConnected ? _status : throw new GoXlrNotConnectedException();

    // ---- Connection ----

    /// <summary>Starts connecting in the background (replacing a running loop).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, $"Connecting to the GoXLR Utility at {Url}...");
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>At startup: connects when auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(Name, $"Automatic connection failed: {ex.Message}");
        }
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (reason, hadConnected) = await RunSessionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (hadConnected) delay = _timings.ReconnectDelay;
                if (!Settings.AutoReconnect)
                {
                    SetStatus(ConnectionState.Disconnected, reason);
                    return;
                }
                if (hadConnected) _log.Warn(Name, $"Connection lost: {reason}");
                SetStatus(ConnectionState.Connecting, $"{reason}; retrying in {Seconds(delay)}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Connection loop stopped: {ex.Message}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    private static string Seconds(TimeSpan delay) =>
        delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";

    private async Task<(string Reason, bool HadConnected)> RunSessionAsync(CancellationToken ct)
    {
        var url = Url;
        var transport = _transports();
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var peer = new GoXlrPeer(transport, _timings.RequestTimeout);
        var pump = Task.CompletedTask;
        var connected = false;
        string reason;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_timings.ConnectTimeout);
                try
                {
                    await transport.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
                    || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return ($"The GoXLR Utility is not reachable at {url.Host}:{url.Port} (is it running with its HTTP server on?)", false);
                }
            }
            peer.Patch = patch => OnPatch(patch);
            pump = peer.RunAsync(life.Token);
            var status = await peer.CallAsync(GetStatus, ct).ConfigureAwait(false);
            if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("Status", out var body))
                throw new GoXlrRequestException("the server did not answer GetStatus like the GoXLR Utility");
            lock (_statusGate)
            {
                _statusNode = JsonNode.Parse(body.GetRawText());
                _status = GoXlrStatusParser.Parse(_statusNode);
            }
            _peer = peer;
            var s = _status;
            var message = $"Connected to GoXLR Utility {s.DaemonVersion} at {url.Host}:{url.Port} ({Devices(s)})";
            SetStatus(ConnectionState.Connected, message);
            connected = true;
            _log.Info(Name, message);
            var args = GoXlrArgs.New();
            args["goXlrDaemonVersion"] = s.DaemonVersion;
            args["goXlrDeviceCount"] = s.Mixers.Count;
            Publish(GoXlrEvents.Connected, args);
            DataChanged?.Invoke();
            await pump.ConfigureAwait(false);
            reason = transport.CloseReason ?? "the GoXLR Utility closed the connection";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "disconnected";
        }
        catch (Exception ex)
        {
            reason = transport.CloseReason ?? ex.Message;
        }
        finally
        {
            _peer = null;
            peer.Patch = null;
            await life.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // Closing.
            }
            await transport.DisposeAsync().ConfigureAwait(false);
            lock (_statusGate)
            {
                _statusNode = null;
                _status = GoXlrStatus.Empty;
            }
        }
        if (connected)
        {
            var args = GoXlrArgs.New();
            args["reason"] = reason;
            Publish(GoXlrEvents.Disconnected, args);
            DataChanged?.Invoke();
        }
        return (reason, connected);
    }

    private static string Devices(GoXlrStatus s) => s.Mixers.Count switch
    {
        0 => "no GoXLR plugged in",
        1 => $"{(s.Mixers[0].IsMini ? "GoXLR Mini" : "GoXLR")} {s.Mixers[0].Serial}",
        var n => $"{n} devices",
    };

    private async Task StopAsync()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Error while disconnecting: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    // ---- Status updates ----

    private void OnPatch(JsonElement patch)
    {
        List<(string, Dictionary<string, object?>)> events;
        lock (_statusGate)
        {
            if (_statusNode is null) return;   // Still loading: the full status answer follows.
            JsonNode? next;
            try
            {
                next = JsonPatch.Apply(_statusNode, patch);
            }
            catch (JsonPatchException ex)
            {
                _log.Debug(Name, $"A status patch did not apply ({ex.Message}); reloading the status");
                _ = ResyncAsync();
                return;
            }
            events = Replace(next);
        }
        Raise(events);
    }

    /// <summary>Swaps in a new status document and returns the events for what changed. Call under <see cref="_statusGate"/>.</summary>
    private List<(string, Dictionary<string, object?>)> Replace(JsonNode? next)
    {
        var before = _status;
        _statusNode = next;
        var after = _status = GoXlrStatusParser.Parse(next);
        var events = new List<(string, Dictionary<string, object?>)>();
        foreach (var serial in before.Mixers.Select(m => m.Serial).Union(after.Mixers.Select(m => m.Serial)))
            events.AddRange(GoXlrDiff.Compare(before.Mixers.FirstOrDefault(m => m.Serial == serial), after.Mixers.FirstOrDefault(m => m.Serial == serial)));
        return events;
    }

    private void Raise(List<(string Type, Dictionary<string, object?> Args)> events)
    {
        foreach (var (type, args) in events) Publish(type, args);
        DataChanged?.Invoke();
    }

    /// <summary>Reloads the whole status (after a patch that did not apply), firing events for the differences.</summary>
    private async Task ResyncAsync()
    {
        if (Interlocked.Exchange(ref _resyncing, 1) == 1) return;
        try
        {
            if (_peer is not { } peer) return;
            var status = await peer.CallAsync(GetStatus, CancellationToken.None).ConfigureAwait(false);
            if (!status.TryGetProperty("Status", out var body)) return;
            List<(string, Dictionary<string, object?>)> events;
            lock (_statusGate)
            {
                if (_statusNode is null) return;
                events = Replace(JsonNode.Parse(body.GetRawText()));
            }
            Raise(events);
        }
        catch (Exception ex) when (ex is GoXlrRequestException or GoXlrNotConnectedException or TimeoutException or WebSocketException or ObjectDisposedException)
        {
            _log.Debug(Name, $"Could not reload the status: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _resyncing, 0);
        }
    }

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, args));
    }

    // ---- Requests ----

    private GoXlrPeer Peer => IsConnected && _peer is { } p ? p : throw new GoXlrNotConnectedException();

    /// <summary>Sends any DaemonRequest and returns the answer's data ("Ok", {"Status":...}, ...).</summary>
    public Task<JsonElement> RequestAsync(JsonNode request, CancellationToken ct = default) => Peer.CallAsync(request, ct);

    /// <summary>Sends a GoXLRCommand to one GoXLR (empty serial: the default one).</summary>
    public async Task CommandAsync(JsonNode command, string serial = "", CancellationToken ct = default)
    {
        var target = Mixer(serial).Serial;
        await Peer.CallAsync(Command(target, command), ct).ConfigureAwait(false);
    }

    /// <summary>Pings the utility and returns the round-trip time.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await Peer.CallAsync(Ping, ct).ConfigureAwait(false);
        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    // ---- Commands ----

    /// <summary>Sets (or with <paramref name="relative"/> adjusts) a channel's volume in percent. Returns the new percent.</summary>
    public async Task<int> SetVolumeAsync(string channel, double percent, bool relative = false, CancellationToken ct = default)
    {
        var name = GoXlrNames.Channel(channel);
        var m = Mixer();
        var target = relative ? GoXlrNames.Percent(m.Volume(name)) + percent : percent;
        var raw = GoXlrNames.Raw(target);
        await CommandAsync(Cmd("SetVolume", V(name), V(raw)), m.Serial, ct).ConfigureAwait(false);
        return GoXlrNames.Percent(raw);
    }

    /// <summary>The fader for "A".."D" / "Fader 2" or for the channel assigned to one.</summary>
    public GoXlrFader ResolveFader(string faderOrChannel)
    {
        var m = Mixer();
        if (GoXlrNames.FindFader(faderOrChannel) is { } fader && m.Faders.TryGetValue(fader, out var f)) return f;
        var channel = GoXlrNames.Channel(faderOrChannel);
        return m.FaderFor(channel) ?? throw new GoXlrRequestException($"{GoXlrNames.Label(channel)} is not on a fader; assign it to one first.");
    }

    internal static string NextMuteState(GoXlrMuteAction action, string current) => action switch
    {
        GoXlrMuteAction.Mute => "MutedToX",
        GoXlrMuteAction.MuteToAll => "MutedToAll",
        GoXlrMuteAction.Unmute => "Unmuted",
        _ => GoXlrNames.IsMuted(current) ? "Unmuted" : "MutedToX",
    };

    public static GoXlrMuteAction ParseMuteAction(string value) => value.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "toggle" or "" => GoXlrMuteAction.Toggle,
        "mute" or "muted" or "on" or "true" or "1" or "mutedtox" => GoXlrMuteAction.Mute,
        "mutetoall" or "mutedtoall" or "muteall" => GoXlrMuteAction.MuteToAll,
        "unmute" or "unmuted" or "off" or "false" or "0" => GoXlrMuteAction.Unmute,
        _ => throw new ArgumentException($"Unknown mute state '{value}' (expected Mute, Mute to all, Unmute or Toggle)."),
    };

    /// <summary>Mutes / unmutes a fader (by letter or by the channel on it). Returns the new mute state.</summary>
    public async Task<string> SetFaderMuteAsync(string faderOrChannel, GoXlrMuteAction action, CancellationToken ct = default)
    {
        var f = ResolveFader(faderOrChannel);
        var next = NextMuteState(action, f.MuteState);
        await CommandAsync(Cmd("SetFaderMuteState", V(f.Name), V(next)), "", ct).ConfigureAwait(false);
        return next;
    }

    public Task SetFaderChannelAsync(string fader, string channel, CancellationToken ct = default) =>
        CommandAsync(Cmd("SetFader", V(GoXlrNames.Fader(fader)), V(GoXlrNames.Channel(channel))), "", ct);

    public Task SetFaderMuteFunctionAsync(string fader, string function, CancellationToken ct = default) =>
        CommandAsync(Cmd("SetFaderMuteFunction", V(GoXlrNames.Fader(fader)), V(GoXlrNames.Parse(GoXlrNames.MuteFunctions, function, "mute function"))), "", ct);

    /// <summary>The cough (Mini: mic mute) button. Returns the new mute state.</summary>
    public async Task<string> SetCoughMuteAsync(GoXlrMuteAction action, CancellationToken ct = default)
    {
        var next = NextMuteState(action, Mixer().Cough.MuteState);
        await CommandAsync(Cmd("SetCoughMuteState", V(next)), "", ct).ConfigureAwait(false);
        return next;
    }

    /// <summary>Loads a profile; <paramref name="remember"/> makes the utility load it again next time.</summary>
    public Task LoadProfileAsync(string name, bool remember = true, CancellationToken ct = default) =>
        CommandAsync(Cmd("LoadProfile", V(ProfileName(name, Status.Profiles, "profile")), V(remember)), "", ct);

    public Task LoadMicProfileAsync(string name, bool remember = true, CancellationToken ct = default) =>
        CommandAsync(Cmd("LoadMicProfile", V(ProfileName(name, Status.MicProfiles, "mic profile")), V(remember)), "", ct);

    /// <summary>The listed name with that spelling (case-insensitive), or the text as typed when the list does not have it.</summary>
    private static string ProfileName(string name, IReadOnlyList<string> known, string what)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException($"A {what} name is required.");
        return known.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }

    private GoXlrEffects RequireEffects(GoXlrMixer m) =>
        m.Effects ?? throw new GoXlrRequestException("This GoXLR has no effects (the GoXLR Mini has no effects section).");

    /// <summary>Turns FX, Megaphone, Robot or Hard Tune on/off. Returns the new state.</summary>
    public async Task<bool> SetEffectAsync(string effect, SwitchMode mode, CancellationToken ct = default)
    {
        var name = GoXlrNames.Effect(effect);
        var e = RequireEffects(Mixer());
        var on = mode switch { SwitchMode.On => true, SwitchMode.Off => false, _ => !e.IsEnabled(name) };
        var command = name switch
        {
            "FX" => "SetFXEnabled",
            "Megaphone" => "SetMegaphoneEnabled",
            "Robot" => "SetRobotEnabled",
            _ => "SetHardTuneEnabled",
        };
        await CommandAsync(Cmd(command, V(on)), "", ct).ConfigureAwait(false);
        return on;
    }

    /// <summary>Selects effect preset 1-6 (by number, "Preset3" or its name). Returns the slot (Preset1..Preset6).</summary>
    public async Task<string> SetEffectPresetAsync(string preset, CancellationToken ct = default)
    {
        var e = RequireEffects(Mixer());
        var slot = GoXlrNames.FindPreset(preset)
            ?? e.PresetNames.FirstOrDefault(p => string.Equals(p.Value, preset.Trim(), StringComparison.OrdinalIgnoreCase)).Key
            ?? throw new ArgumentException($"Unknown effect preset '{preset.Trim()}' (use 1-6 or a preset name: {string.Join(", ", e.PresetNames.OrderBy(p => p.Key).Select(p => p.Value))}).");
        await CommandAsync(Cmd("SetActiveEffectPreset", V(slot)), "", ct).ConfigureAwait(false);
        return slot;
    }

    /// <summary>Loads a preset from the utility's preset library into the active effect slot.</summary>
    public Task LoadEffectPresetAsync(string name, CancellationToken ct = default)
    {
        RequireEffects(Mixer());
        return CommandAsync(Cmd("LoadEffectPreset", V(ProfileName(name, Status.Presets, "preset"))), "", ct);
    }

    private GoXlrSampler RequireSampler(GoXlrMixer m) =>
        m.Sampler ?? throw new GoXlrRequestException("This GoXLR has no sampler (the GoXLR Mini has no sampler).");

    public Task SetSamplerBankAsync(string bank, CancellationToken ct = default)
    {
        RequireSampler(Mixer());
        return CommandAsync(Cmd("SetActiveSamplerBank", V(GoXlrNames.Parse(GoXlrNames.SampleBanks, bank, "sampler bank"))), "", ct);
    }

    /// <summary>Plays a sampler button's next sample, or the sample at <paramref name="index"/> (0-based). Empty bank = the active one.</summary>
    public async Task<(string Bank, string Button)> PlaySampleAsync(string bank, string button, int? index = null, CancellationToken ct = default)
    {
        var (b, btn) = SampleTarget(bank, button);
        var command = index is { } i ? Cmd("PlaySampleByIndex", V(b), V(btn), V(i)) : Cmd("PlayNextSample", V(b), V(btn));
        await CommandAsync(command, "", ct).ConfigureAwait(false);
        return (b, btn);
    }

    public async Task<(string Bank, string Button)> StopSampleAsync(string bank, string button, CancellationToken ct = default)
    {
        var (b, btn) = SampleTarget(bank, button);
        await CommandAsync(Cmd("StopSamplePlayback", V(b), V(btn)), "", ct).ConfigureAwait(false);
        return (b, btn);
    }

    private (string Bank, string Button) SampleTarget(string bank, string button)
    {
        var s = RequireSampler(Mixer());
        var b = bank.Trim().Length == 0 ? s.ActiveBank : GoXlrNames.Parse(GoXlrNames.SampleBanks, bank, "sampler bank");
        return (b, GoXlrNames.Parse(GoXlrNames.SampleButtons, button, "sampler button"));
    }

    /// <summary>Routes (or unroutes) an input to an output in the routing table. Returns the new state.</summary>
    public async Task<bool> SetRoutingAsync(string input, string output, SwitchMode mode, CancellationToken ct = default)
    {
        var i = GoXlrNames.Parse(GoXlrNames.Inputs, input, "input");
        var o = GoXlrNames.Parse(GoXlrNames.Outputs, output, "output");
        var on = mode switch { SwitchMode.On => true, SwitchMode.Off => false, _ => !Mixer().Routed(i, o) };
        await CommandAsync(Cmd("SetRouter", V(i), V(o), V(on)), "", ct).ConfigureAwait(false);
        return on;
    }

    public Task SetGlobalColourAsync(string colour, CancellationToken ct = default) =>
        CommandAsync(Cmd("SetGlobalColour", V(GoXlrNames.Colour(colour))), "", ct);

    public Task SetFaderColoursAsync(string fader, string top, string bottom, CancellationToken ct = default) =>
        CommandAsync(Cmd("SetFaderColours", V(GoXlrNames.Fader(fader)), V(GoXlrNames.Colour(top, "Top colour")), V(GoXlrNames.Colour(bottom, "Bottom colour"))), "", ct);

    /// <summary>A button's colour when on and (optionally) when off.</summary>
    public Task SetButtonColoursAsync(string button, string on, string off = "", CancellationToken ct = default) =>
        CommandAsync(Cmd("SetButtonColours", V(GoXlrNames.Parse(GoXlrNames.Buttons, button, "button")), V(GoXlrNames.Colour(on, "On colour")),
            off.Trim().Length == 0 ? null : V(GoXlrNames.Colour(off, "Off colour"))), "", ct);

    public Task SetAnimationModeAsync(string mode, CancellationToken ct = default) =>
        CommandAsync(Cmd("SetAnimationMode", V(GoXlrNames.Parse(GoXlrNames.AnimationModes, mode, "animation mode"))), "", ct);

    /// <summary>The text at the bottom of a fader's scribble strip (full-size GoXLR).</summary>
    public Task SetScribbleTextAsync(string fader, string text, CancellationToken ct = default)
    {
        if (Mixer().IsMini) throw new GoXlrRequestException("The GoXLR Mini has no scribble strips.");
        return CommandAsync(Cmd("SetScribbleText", V(GoXlrNames.Fader(fader)), V(text)), "", ct);
    }
}
