using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;
using Cuebot.WaveLink.Protocol;

namespace Cuebot.WaveLink;

/// <summary>
/// Elgato Wave Link: finds the app on 127.0.0.1 (Wave Link 3 on 1884-1893 via ws-info.json, Wave Link 1.x/2.x on
/// 1824-1834), tells the API generation from getApplicationInfo, loads its channels/mixes/devices, turns its
/// notifications into Cuebot events and runs commands. Reconnects with exponential backoff while auto-reconnect is on.
/// Wave Link only exists for Windows and macOS: elsewhere the service reports that and never connects.
/// </summary>
public sealed class WaveLinkService : IConnectionStatus, IAsyncDisposable, IWaveLinkHost
{
    private readonly JsonFile<WaveLinkSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IWaveLinkTransport> _transports;
    private readonly WaveLinkTimings _timings;
    private readonly Func<string?> _unsupportedReason;
    private readonly Func<IEnumerable<string>> _wsInfoPaths;
    private readonly LevelMeterThrottle _levels;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile WaveLinkProtocol? _protocol;
    private volatile bool _sessionConnected;

    public WaveLinkService(JsonFile<WaveLinkSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), WaveLinkTimings.Default, TimeProvider.System,
            DefaultUnsupportedReason, WaveLinkDiscovery.WsInfoPaths)
    {
    }

    internal WaveLinkService(JsonFile<WaveLinkSettings> settings, EventBus bus, AppLog log, Func<IWaveLinkTransport> transports,
        WaveLinkTimings timings, TimeProvider time, Func<string?> unsupportedReason, Func<IEnumerable<string>> wsInfoPaths)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transports = transports;
        _timings = timings;
        _unsupportedReason = unsupportedReason;
        _wsInfoPaths = wsInfoPaths;
        _levels = new LevelMeterThrottle(time, () => Settings.LevelMeterRate, PublishLevels);
    }

    /// <summary>Why Wave Link cannot run here (null on Windows and macOS).</summary>
    public static string? DefaultUnsupportedReason() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? null
        : OperatingSystem.IsLinux() ? "Wave Link is not available on Linux"
        : "Wave Link is only available on Windows and macOS";

    public string Name => "Wave Link";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when the snapshot or connection details changed.</summary>
    public event Action? DataChanged;

    public bool IsConnected => State == ConnectionState.Connected && _protocol is not null;

    /// <summary>True while the background loop is looking for, connected to, or waiting to reconnect to Wave Link.</summary>
    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    /// <summary>False on Linux (and anything but Windows/macOS).</summary>
    public bool IsAvailable => UnsupportedReason is null;

    public string? UnsupportedReason => _unsupportedReason();

    public WaveLinkSettings Settings => _settings.Value;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The detected API (None while disconnected).</summary>
    public WaveLinkApi Api { get; private set; }

    /// <summary>The port Wave Link answered on (0 while disconnected).</summary>
    public int Port { get; private set; }

    public int InterfaceRevision { get; private set; }

    public string AppName { get; private set; } = "";

    /// <summary>"Wave Link 3" / "Wave Link 1.x/2.x" / "".</summary>
    public string ApiDisplay => Api switch { WaveLinkApi.V3 => "Wave Link 3", WaveLinkApi.Legacy => "Wave Link 1.x/2.x", _ => "" };

    public WaveLinkSnapshot Snapshot => _protocol?.Snapshot ?? WaveLinkSnapshot.Empty;

    /// <summary>Level meter flushes so far (diagnostics).</summary>
    public int LevelMeterFlushes => _levels.FlushCount;

    // ---- Connection ----

    /// <summary>Starts looking for Wave Link in the background (replacing a running loop). Never connects on Linux.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (UnsupportedReason is { } reason)
            {
                SetStatus(ConnectionState.Disconnected, reason);
                return;
            }
            SetStatus(ConnectionState.Connecting, $"Looking for Wave Link on {WaveLinkDiscovery.Describe(Settings)}...");
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
            SetStatus(ConnectionState.Disconnected, UnsupportedReason ?? "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>At startup: reports "not available" on Linux, connects when auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (UnsupportedReason is { } reason)
        {
            SetStatus(ConnectionState.Disconnected, reason);
            return;
        }
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
                string reason;
                var hadConnected = false;
                var probe = await DiscoverAsync(ct).ConfigureAwait(false);
                if (probe is null) reason = $"Wave Link is not running (nothing answered on {WaveLinkDiscovery.Describe(Settings)})";
                else (reason, hadConnected) = await RunSessionAsync(probe, ct).ConfigureAwait(false);
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

    /// <summary>A websocket that answered getApplicationInfo like Wave Link, with its read loop already running.</summary>
    private sealed record Probe(IWaveLinkTransport Transport, JsonRpcPeer Peer, Task Pump, CancellationTokenSource Life,
        int Port, WaveLinkApi Api, JsonElement Info);

    /// <summary>Tries every candidate port at once and keeps the best one that answered (in candidate order).</summary>
    private async Task<Probe?> DiscoverAsync(CancellationToken ct)
    {
        var wsInfo = Settings.Port > 0 || Settings.Api == WaveLinkApiPreference.Legacy ? null : WaveLinkDiscovery.ReadWsInfoPort(_wsInfoPaths());
        var candidates = WaveLinkDiscovery.Candidates(Settings, wsInfo);
        var probes = await Task.WhenAll(candidates.Select(c => ProbeAsync(c, ct))).ConfigureAwait(false);
        var best = probes.FirstOrDefault(p => p is not null);
        foreach (var other in probes)
            if (other is not null && other != best) await CloseAsync(other).ConfigureAwait(false);
        return best;
    }

    private async Task<Probe?> ProbeAsync(WaveLinkDiscovery.Candidate candidate, CancellationToken ct)
    {
        var transport = _transports();
        var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timings.ConnectTimeout);
            await transport.ConnectAsync(new Uri($"ws://127.0.0.1:{candidate.Port}"), timeout.Token).ConfigureAwait(false);
            var peer = new JsonRpcPeer(transport, _timings.RequestTimeout);
            var pump = peer.RunAsync(life.Token);
            // The probe's answer must come within the connect timeout (the token), later requests get the normal one.
            var info = await peer.CallAsync("getApplicationInfo", null, timeout.Token).ConfigureAwait(false);
            if (WaveLinkDiscovery.Classify(info, candidate.Port, Settings.Api) is not { } api)
                throw new WaveLinkRequestException("not Wave Link");
            return new Probe(transport, peer, pump, life, candidate.Port, api, info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await life.CancelAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            life.Dispose();
            return null;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            life.Dispose();
            throw;
        }
    }

    private static async Task CloseAsync(Probe probe)
    {
        await probe.Life.CancelAsync().ConfigureAwait(false);
        try
        {
            await probe.Pump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // Closing.
        }
        await probe.Transport.DisposeAsync().ConfigureAwait(false);
        probe.Life.Dispose();
    }

    private async Task<(string Reason, bool HadConnected)> RunSessionAsync(Probe probe, CancellationToken ct)
    {
        WaveLinkProtocol protocol = probe.Api == WaveLinkApi.V3 ? new WaveLinkV3Protocol(probe.Peer, this) : new WaveLinkLegacyProtocol(probe.Peer, this);
        probe.Peer.Notification = (method, parameters) =>
        {
            try
            {
                protocol.OnNotification(method, parameters);
            }
            catch (Exception ex)
            {
                _log.Error(Name, $"Could not process Wave Link's {method}", ex);
            }
        };
        string reason;
        try
        {
            await protocol.InitializeAsync(ct).ConfigureAwait(false);
            Api = probe.Api;
            Port = probe.Port;
            InterfaceRevision = probe.Info.Num("interfaceRevision") is { } rev ? (int)rev : 0;
            AppName = probe.Info.Str("appName") is { Length: > 0 } n ? n : probe.Info.Str("name");
            _protocol = protocol;
            var s = protocol.Snapshot;
            var message = $"Connected to {ApiDisplay} on port {Port} (interface revision {InterfaceRevision}; " +
                $"{s.Channels.Count} {(Api == WaveLinkApi.V3 ? "channels" : "inputs")}, {s.Mixes.Count} mixes)";
            SetStatus(ConnectionState.Connected, message);
            _sessionConnected = true;
            _log.Info(Name, message);
            Publish(WaveLinkEvents.Connected, WaveLinkArgs.New());
            DataChanged?.Invoke();
            await probe.Pump.ConfigureAwait(false);
            reason = probe.Transport.CloseReason ?? "Wave Link closed the connection";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "disconnected";
        }
        catch (Exception ex)
        {
            reason = probe.Transport.CloseReason ?? ex.Message;
        }
        finally
        {
            _protocol = null;
            probe.Peer.Notification = null;
            await CloseAsync(probe).ConfigureAwait(false);
            _levels.Reset();
        }
        var hadConnected = _sessionConnected;
        if (hadConnected)
        {
            _sessionConnected = false;
            var args = WaveLinkArgs.New();
            args["reason"] = reason;
            Publish(WaveLinkEvents.Disconnected, args);
        }
        Api = WaveLinkApi.None;
        Port = 0;
        InterfaceRevision = 0;
        DataChanged?.Invoke();
        return (reason, hadConnected);
    }

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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _levels.Dispose();
    }

    // ---- IWaveLinkHost ----

    void IWaveLinkHost.Publish(string type, Dictionary<string, object?> args) => Publish(type, args);

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("waveLinkApi", Api switch { WaveLinkApi.V3 => "3", WaveLinkApi.Legacy => "legacy", _ => "" });
        args.TryAdd("waveLinkPort", Port);
        args.TryAdd("waveLinkInterfaceRevision", InterfaceRevision);
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, args));
    }

    void IWaveLinkHost.OfferLevels(IReadOnlyList<WaveLinkLevel> levels)
    {
        var s = Settings;
        if (!s.LevelMeters || levels.Count == 0) return;
        var type = s.LevelMeterType.Trim().ToLowerInvariant();
        _levels.Offer(type is "" or "all" or "any" ? levels : levels.Where(l => l.Type == type).ToArray());
    }

    void IWaveLinkHost.Log(string message) => _log.Debug(Name, message);

    void IWaveLinkHost.DataChanged() => DataChanged?.Invoke();

    /// <summary>One event per flush, carrying every meter that moved; the loudest one is also flattened.</summary>
    private void PublishLevels(IReadOnlyList<WaveLinkLevel> levels)
    {
        if (!IsConnected) return;
        var args = WaveLinkArgs.New();
        args["waveLinkEvent"] = Api == WaveLinkApi.V3 ? "levelMeterChanged" : "realTimeChanges";
        args["waveLinkLevelsCount"] = levels.Count;
        args["waveLinkLevels"] = JsonSerializer.Serialize(levels.Select(l => new { type = l.Type, id = l.Id, subId = l.SubId, name = l.Name, left = l.Left, right = l.Right }));
        LevelArgs(args, levels.MaxBy(l => l.Peak)!);
        Publish(WaveLinkEvents.LevelMeter, args);
    }

    internal static void LevelArgs(IDictionary<string, object?> args, WaveLinkLevel level)
    {
        args["waveLinkLevelType"] = level.Type;
        args["waveLinkLevelId"] = level.Id;
        args["waveLinkLevelSubId"] = level.SubId;
        args["waveLinkLevelName"] = level.Name;
        args["waveLinkLevelLeft"] = level.Left;
        args["waveLinkLevelRight"] = level.Right;
        args["waveLinkLevelPeak"] = level.Peak;
    }

    /// <summary>Re-sends Wave Link 3's notification subscription after the level meter / focused-app settings changed.</summary>
    public Task ApplySubscriptionsAsync(CancellationToken ct = default)
    {
        SaveSettings();
        return _protocol is { } p ? p.UpdateSubscriptionsAsync(ct) : Task.CompletedTask;
    }

    // ---- Commands ----

    /// <summary>Asks Wave Link for its application info and returns the round-trip time.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await Live.PingAsync(ct).ConfigureAwait(false);
        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    private WaveLinkProtocol Live =>IsConnected && _protocol is { } p ? p : throw new WaveLinkNotConnectedException();

    /// <summary>Mutes a channel (mix empty: the whole channel on Wave Link 3, both mixes on 1.x/2.x). Returns the new state.</summary>
    public Task<bool> SetChannelMuteAsync(string channel, string mix, SwitchMode mode, CancellationToken ct = default) =>
        Live.SetChannelMuteAsync(channel, mix, mode, ct);

    /// <summary>Sets (or with <paramref name="relative"/> adjusts) a channel's volume in percent. Returns the new volume.</summary>
    public Task<double> SetChannelVolumeAsync(string channel, string mix, double volume, bool relative = false, CancellationToken ct = default) =>
        Live.SetChannelVolumeAsync(channel, mix, volume, relative, ct);

    public Task<bool> SetChannelEffectAsync(string channel, string effect, SwitchMode mode, CancellationToken ct = default) =>
        Live.SetChannelEffectAsync(channel, effect, mode, ct);

    /// <summary>Wave Link 1.x/2.x: bypass an input's filters in one mix (or both).</summary>
    public Task<bool> SetFilterBypassAsync(string channel, string mix, SwitchMode mode, CancellationToken ct = default) =>
        Live.SetFilterBypassAsync(channel, mix, mode, ct);

    /// <summary>Wave Link 3: route an application (empty = the focused one) to a channel.</summary>
    public Task<WaveLinkApp> AddAppToChannelAsync(string channel, string app = "", CancellationToken ct = default) =>
        Live.AddAppToChannelAsync(channel, app, ct);

    public Task<bool> SetMixMuteAsync(string mix, SwitchMode mode, CancellationToken ct = default) => Live.SetMixMuteAsync(mix, mode, ct);

    public Task<double> SetMixVolumeAsync(string mix, double volume, bool relative = false, CancellationToken ct = default) =>
        Live.SetMixVolumeAsync(mix, volume, relative, ct);

    public Task<bool> SetInputMuteAsync(string device, SwitchMode mode, CancellationToken ct = default) => Live.SetInputMuteAsync(device, mode, ct);

    /// <summary>Wave Link 3: gain in percent of the device's range. Wave Link 1.x/2.x: the microphone's own gain value.</summary>
    public Task<double> SetInputGainAsync(string device, double gain, bool relative = false, CancellationToken ct = default) =>
        Live.SetInputGainAsync(device, gain, relative, ct);

    public Task<bool> SetInputGainLockAsync(string device, SwitchMode mode, CancellationToken ct = default) => Live.SetInputGainLockAsync(device, mode, ct);

    public Task<bool> SetInputLowCutAsync(string device, SwitchMode mode, CancellationToken ct = default) => Live.SetInputLowCutAsync(device, mode, ct);

    public Task<bool> SetInputClipGuardAsync(string device, SwitchMode mode, CancellationToken ct = default) => Live.SetInputClipGuardAsync(device, mode, ct);

    /// <summary>Mic/PC balance (Wave Link 3) or microphone/monitor balance (1.x/2.x), 0-100.</summary>
    public Task<double> SetMicPcMixAsync(string device, double balance, bool relative = false, CancellationToken ct = default) =>
        Live.SetMicPcMixAsync(device, balance, relative, ct);

    /// <summary>Wave Link 1.x/2.x: the microphone's headphone output volume (its own value scale).</summary>
    public Task<double> SetMicOutputVolumeAsync(string device, double volume, bool relative = false, CancellationToken ct = default) =>
        Live.SetMicOutputVolumeAsync(device, volume, relative, ct);

    /// <summary>Selects the main (monitoring) output device.</summary>
    public Task<WaveLinkOutputDevice> SetMainOutputAsync(string device, CancellationToken ct = default) => Live.SetMainOutputAsync(device, ct);

    public Task<double> SetOutputVolumeAsync(string device, double volume, bool relative = false, CancellationToken ct = default) =>
        Live.SetOutputVolumeAsync(device, volume, relative, ct);

    public Task<bool> SetOutputMuteAsync(string device, SwitchMode mode, CancellationToken ct = default) => Live.SetOutputMuteAsync(device, mode, ct);

    /// <summary>Wave Link 3: play a mix on an output device.</summary>
    public Task AddOutputToMixAsync(string device, string mix, CancellationToken ct = default) => Live.AddOutputToMixAsync(device, mix, ct);

    /// <summary>Monitors a mix (Wave Link 3: on the main output; 1.x/2.x: switches between Monitor and Stream mix).</summary>
    public Task<WaveLinkMix> SetMonitoredMixAsync(string mix, CancellationToken ct = default) => Live.SetMonitoredMixAsync(mix, ct);

    /// <summary>Wave Link 1.x/2.x: toggles monitoring between the two mixes (Wave Link 3: unsupported).</summary>
    public Task<WaveLinkMix> SwitchMonitoringAsync(CancellationToken ct = default) =>
        Live is WaveLinkLegacyProtocol legacy ? legacy.SwitchMonitoringAsync(ct)
            : throw new WaveLinkUnsupportedException("Switching between Monitor and Stream mix is a Wave Link 1.x/2.x feature; use Set monitored mix.");

    /// <summary>Wave Link 1.x/2.x microphone variables (microphone.*) for "Get Microphone Information".</summary>
    internal void AddMicrophoneArgs(Dictionary<string, object?> args, string microphone)
    {
        if (Live is WaveLinkLegacyProtocol legacy) legacy.AddMicrophoneArgs(args, microphone);
    }

    internal static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
