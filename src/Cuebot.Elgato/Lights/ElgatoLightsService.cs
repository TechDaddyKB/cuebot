using System.Globalization;
using System.Net;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Elgato.Common;

namespace Cuebot.Elgato.Lights;

/// <summary>
/// Elgato lights (what Control Center manages), talked to directly over each light's HTTP API on port 9123. Lights are
/// found by mDNS or entered by hand, then polled: lights have no push channel, so changes made in Control Center, the
/// Stream Deck or the phone app are noticed on the next poll and published as Streamer.bot's Control Center triggers.
/// Works the same on Windows and Linux and does not need Control Center to be installed.
/// </summary>
public sealed class ElgatoLightsService : IConnectionStatus, IAsyncDisposable
{
    public const string ServiceName = "Elgato Lights";
    private const string LogSource = "Elgato Lights";

    /// <summary>Consecutive failed polls before a light is shown offline (one lost packet is not an outage).</summary>
    private const int OfflineAfterFailures = 2;

    private readonly JsonFile<ElgatoSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly ElgatoLightClient _client;
    private readonly IMdnsTransport _mdns;
    private readonly TimeSpan _discoveryWindow;
    private readonly object _gate = new();
    private readonly List<ElgatoLight> _lights = [];
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;

    public ElgatoLightsService(JsonFile<ElgatoSettings> settings, EventBus bus, AppLog log,
        HttpClient? http = null, IMdnsTransport? mdns = null, TimeSpan? discoveryWindow = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _client = new ElgatoLightClient(http ?? CreateHttpClient());
        _mdns = mdns ?? new UdpMdnsTransport();
        _discoveryWindow = discoveryWindow ?? TimeSpan.FromSeconds(3);
        foreach (var known in Settings.Devices.Where(d => d.Address.Length > 0)) _lights.Add(new ElgatoLight(known));
    }

    public string Name => ServiceName;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not started";
    public event Action? StatusChanged;

    /// <summary>A light was added, removed, or changed state, name or reachability.</summary>
    public event Action? LightsChanged;

    public ElgatoLightsSettings Settings => _settings.Value.Lights;

    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    public IReadOnlyList<ElgatoLight> Lights
    {
        get { lock (_gate) return _lights.ToArray(); }
    }

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Starts polling when auto-start is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoStart) return;
        try
        {
            await StartPollingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Automatic start failed: {ex.Message}");
        }
    }

    /// <summary>Starts (or restarts) discovery and polling in the background.</summary>
    public async Task StartPollingAsync()
    {
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            var run = new CancellationTokenSource();
            _run = run;
            SetStatus(ConnectionState.Connecting, Settings.Discovery ? "Looking for lights..." : "Polling lights...");
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Stopped");
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Polls every light once (state, and name/settings when due or <paramref name="includeInfo"/>).</summary>
    public async Task RefreshAsync(bool includeInfo = false, CancellationToken ct = default)
    {
        await Task.WhenAll(Lights.Select(l => PollAsync(l, includeInfo, ct))).ConfigureAwait(false);
        if (IsRunning) UpdateRunningStatus();
        LightsChanged?.Invoke();
    }

    /// <summary>Sends one mDNS query; adds lights it has not seen before and follows known lights to new addresses.</summary>
    public async Task<IReadOnlyList<ElgatoLight>> DiscoverAsync(CancellationToken ct = default)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var packets = await _mdns.QueryAsync(Mdns.BuildQuery(Mdns.ElgatoService), _discoveryWindow, ct).ConfigureAwait(false);
            var found = new Dictionary<string, MdnsService>(StringComparer.OrdinalIgnoreCase);
            foreach (var packet in packets)
            {
                IReadOnlyList<DnsRecord> records;
                try { records = Mdns.Parse(packet.Data); }
                catch (FormatException) { continue; }
                foreach (var service in Mdns.Resolve(records, Mdns.ElgatoService, packet.Source))
                {
                    var address = service.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                  ?? service.Addresses.FirstOrDefault();
                    if (address is not null) found[$"{address}:{service.Port}"] = service with { Addresses = [address] };
                }
            }
            var added = new List<ElgatoLight>();
            foreach (var service in found.Values)
            {
                var address = service.Addresses[0].ToString();
                if (Lights.Any(l => l.Address == address && l.Port == service.Port)) continue;
                try
                {
                    var (light, isNew) = await AddOrMoveAsync(address, service.Port, manual: false, ct).ConfigureAwait(false);
                    if (light is not null && isNew) added.Add(light);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.Warn(LogSource, $"Found {service.InstanceName} at {address}:{service.Port} but it did not answer: {ex.Message}");
                }
            }
            LightsChanged?.Invoke();
            return added;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    /// <summary>Adds a light by address ("192.168.1.20" or "keylight.local:9123"); it must answer.</summary>
    public async Task<ElgatoLight> AddLightAsync(string address, CancellationToken ct = default)
    {
        var (host, port) = ParseAddress(address);
        var (light, _) = await AddOrMoveAsync(host, port, manual: true, ct).ConfigureAwait(false);
        if (light is null) throw new InvalidOperationException("The light could not be added.");
        LightsChanged?.Invoke();
        return light;
    }

    /// <summary>Forgets lights (by id, name or address). Discovered lights are not added back by discovery.</summary>
    public IReadOnlyList<ElgatoLight> RemoveLights(string device)
    {
        var removed = Resolve(device);
        lock (_gate)
        {
            foreach (var light in removed)
            {
                _lights.Remove(light);
                Settings.Devices.Remove(light.Known);
                if (!light.IsManual && !Settings.Ignored.Contains(light.Id, StringComparer.OrdinalIgnoreCase)) Settings.Ignored.Add(light.Id);
            }
        }
        SaveSettings();
        foreach (var light in removed) Publish(LightEvents.DeviceRemoved, light);
        UpdateStatusIfRunning();
        LightsChanged?.Invoke();
        return removed;
    }

    /// <summary>Lights matching a comma-separated list of ids, names or addresses; empty, "All" or "*" = every light.</summary>
    public IReadOnlyList<ElgatoLight> Resolve(string device)
    {
        var lights = Lights;
        var wanted = device.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0 || wanted.Any(w => w is "*" || w.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return lights.Count > 0 ? lights : throw new InvalidOperationException("No Elgato lights are known yet.");
        var matched = lights.Where(l => wanted.Any(w =>
            string.Equals(w, l.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(w, l.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w, l.Address, StringComparison.OrdinalIgnoreCase) || string.Equals(w, $"{l.Address}:{l.Port}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matched.Length > 0 ? matched : throw new ArgumentException($"No Elgato light matches '{device}'.");
    }

    public Task<IReadOnlyList<ElgatoLight>> SetPowerAsync(string device, SwitchMode mode, CancellationToken ct = default) =>
        ChangeAsync(device, s => new LightChange(On: Steps.Desired(mode, s.On)), ct);

    /// <summary>Brightness 0-100 %; with <paramref name="adjust"/> the value is added to the current brightness.</summary>
    public Task<IReadOnlyList<ElgatoLight>> SetBrightnessAsync(string device, double value, bool adjust = false, CancellationToken ct = default) =>
        ChangeAsync(device, s => new LightChange(Brightness: Math.Clamp((int)Math.Round(adjust ? s.Brightness + value : value), 0, 100)), ct);

    /// <summary>Temperature in Kelvin (2900-7000) or API units (143-344); with <paramref name="adjust"/> Kelvin are added.</summary>
    public Task<IReadOnlyList<ElgatoLight>> SetTemperatureAsync(string device, double value, bool adjust = false, CancellationToken ct = default) =>
        ChangeAsync(device, s =>
        {
            if (s.Temperature is null) throw new InvalidOperationException("This light has no colour temperature (a Light Strip in colour mode?). Use Set Color.");
            var mired = adjust ? ElgatoColor.KelvinToMired((int)Math.Round((s.Kelvin ?? 0) + value)) : ElgatoColor.ToMired(value);
            return new LightChange(Temperature: mired);
        }, ct);

    /// <summary>Hue 0-360 and saturation 0-100 (Light Strip and other colour lights).</summary>
    public Task<IReadOnlyList<ElgatoLight>> SetColorAsync(string device, double? hue, double? saturation, CancellationToken ct = default) =>
        ChangeAsync(device, _ => new LightChange(Hue: hue, Saturation: saturation), ct);

    public async Task IdentifyAsync(string device, CancellationToken ct = default)
    {
        foreach (var light in Resolve(device)) await _client.IdentifyAsync(light.BaseUri, ct).ConfigureAwait(false);
    }

    /// <summary>Sets the light's display name (what Control Center shows).</summary>
    public async Task RenameAsync(string device, string name, CancellationToken ct = default)
    {
        foreach (var light in Resolve(device))
        {
            await _client.SetDisplayNameAsync(light.BaseUri, name.Trim(), ct).ConfigureAwait(false);
            await PollAsync(light, includeInfo: true, ct).ConfigureAwait(false);
        }
        LightsChanged?.Invoke();
    }

    private async Task<IReadOnlyList<ElgatoLight>> ChangeAsync(string device, Func<LightState, LightChange> change, CancellationToken ct)
    {
        var lights = Resolve(device);
        foreach (var light in lights)
        {
            await light.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = light.State ?? (await _client.GetLightsAsync(light.BaseUri, ct).ConfigureAwait(false)).FirstOrDefault()
                    ?? throw new InvalidOperationException($"{light.Name} reports no lights.");
                light.State ??= current;
                var states = await _client.PutLightsAsync(light.BaseUri, light.LightCount, change(current), ct).ConfigureAwait(false);
                MarkReachable(light);
                ApplyState(light, states);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new InvalidOperationException($"{light.Name} ({light.Address}) did not answer: {ex.Message}", ex);
            }
            finally
            {
                light.Gate.Release();
            }
        }
        LightsChanged?.Invoke();
        return lights;
    }

    private async Task<(ElgatoLight? Light, bool IsNew)> AddOrMoveAsync(string host, int port, bool manual, CancellationToken ct)
    {
        var info = await _client.GetAccessoryInfoAsync(ElgatoLightClient.BaseUri(host, port), ct).ConfigureAwait(false);
        var id = info.SerialNumber.Length > 0 ? info.SerialNumber : $"{host}:{port}";
        ElgatoLight light;
        bool isNew;
        lock (_gate)
        {
            var existing = _lights.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // Same light at a new address (DHCP), or entered by hand after being discovered.
                existing.Known.Address = host;
                existing.Known.Port = port;
                if (manual) existing.Known.Manual = true;
                light = existing;
                isNew = false;
            }
            else
            {
                if (!manual && Settings.Ignored.Contains(id, StringComparer.OrdinalIgnoreCase)) return (null, false);
                Settings.Ignored.RemoveAll(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
                var known = new KnownLight { Id = id, Address = host, Port = port, Manual = manual };
                Settings.Devices.Add(known);
                light = new ElgatoLight(known);
                _lights.Add(light);
                isNew = true;
            }
        }
        await light.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ApplyInfo(light, info);
        }
        finally
        {
            light.Gate.Release();
        }
        SaveSettings();
        await PollAsync(light, includeInfo: true, ct).ConfigureAwait(false);
        if (isNew)
        {
            _log.Info(LogSource, $"Added {light.Name} ({light.ProductName}) at {host}:{port}");
            Publish(LightEvents.DeviceAdded, light);
        }
        UpdateStatusIfRunning();
        return (light, isNew);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var nextDiscovery = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Settings.Discovery && DateTimeOffset.UtcNow >= nextDiscovery)
                {
                    nextDiscovery = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(5, Settings.DiscoveryIntervalSeconds));
                    try
                    {
                        await DiscoverAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _log.Warn(LogSource, $"mDNS discovery failed: {ex.Message}");
                    }
                }
                await RefreshAsync(false, ct).ConfigureAwait(false);
                UpdateRunningStatus();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100, Settings.PollIntervalMs)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "Polling stopped", ex);
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    private async Task PollAsync(ElgatoLight light, bool includeInfo, CancellationToken ct)
    {
        await light.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var states = await _client.GetLightsAsync(light.BaseUri, ct).ConfigureAwait(false);
            var cameOnline = MarkReachable(light);
            ApplyState(light, states);
            if (includeInfo || cameOnline || DateTimeOffset.UtcNow >= light.NextInfo)
            {
                light.NextInfo = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, Settings.InfoIntervalSeconds));
                ApplyInfo(light, await _client.GetAccessoryInfoAsync(light.BaseUri, ct).ConfigureAwait(false));
                try
                {
                    ApplySettings(light, await _client.GetSettingsAsync(light.BaseUri, ct).ConfigureAwait(false));
                }
                catch (HttpRequestException)
                {
                    // Older firmware without /elgato/lights/settings.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            light.LastError = ex is TaskCanceledException ? "timed out" : ex.Message;
            light.Failures++;
            if (light.IsOnline && light.Failures >= OfflineAfterFailures)
            {
                light.IsOnline = false;
                _log.Warn(LogSource, $"{light.Name} ({light.Address}) is not answering: {light.LastError}");
                Publish(LightEvents.DeviceOffline, light);
            }
        }
        finally
        {
            light.Gate.Release();
        }
    }

    /// <summary>Returns true when the light was offline (or never seen) before.</summary>
    private bool MarkReachable(ElgatoLight light)
    {
        light.Failures = 0;
        light.LastError = null;
        light.LastSeen = DateTimeOffset.Now;
        if (light.IsOnline) return false;
        light.IsOnline = true;
        if (light.State is not null) Publish(LightEvents.DeviceOnline, light);
        return true;
    }

    private void ApplyState(ElgatoLight light, IReadOnlyList<LightState> states)
    {
        if (states.Count == 0) return;
        var old = light.State;
        var now = states[0];
        light.State = now;
        light.LightCount = states.Count;
        if (old is null) return;   // first answer: nothing "changed"
        if (old.On != now.On)
        {
            var extra = new Dictionary<string, object?> { ["oldOn"] = old.On, ["oldPowerState"] = old.On ? "On" : "Off" };
            Publish(LightEvents.DevicePowerStateChanged, light, extra);
            Publish(now.On ? LightEvents.DevicePoweredOn : LightEvents.DevicePoweredOff, light, extra);
        }
        if (old.Brightness != now.Brightness)
            Publish(LightEvents.BrightnessChanged, light, new() { ["oldBrightness"] = old.Brightness });
        if (old.Temperature != now.Temperature && now.Temperature is not null)
            Publish(LightEvents.TemperatureChanged, light, new() { ["oldTemperature"] = old.Kelvin, ["oldTemperatureRaw"] = old.Temperature });
        if (!Same(old.Hue, now.Hue) || !Same(old.Saturation, now.Saturation))
            Publish(LightEvents.ColorChanged, light, new() { ["oldHue"] = old.Hue, ["oldSaturation"] = old.Saturation });
    }

    private void ApplyInfo(ElgatoLight light, AccessoryInfo info)
    {
        var hadInfo = light.Info is not null;
        var oldName = light.Name;
        light.Info = info;
        var changed = false;
        if (light.Known.Name != light.Name) { light.Known.Name = light.Name; changed = true; }
        if (info.ProductName.Length > 0 && light.Known.ProductName != info.ProductName) { light.Known.ProductName = info.ProductName; changed = true; }
        if (info.SerialNumber.Length > 0 && light.Known.Id != info.SerialNumber && light.Known.Id == $"{light.Address}:{light.Port}")
        {
            light.Known.Id = info.SerialNumber;
            changed = true;
        }
        if (changed) SaveSettings();
        if (hadInfo && oldName != light.Name)
            Publish(LightEvents.DeviceRenamed, light, new() { ["oldDeviceName"] = oldName });
    }

    private void ApplySettings(ElgatoLight light, string? settings)
    {
        var old = light.SettingsJson;
        light.SettingsJson = settings;
        if (old is not null && settings is not null && old != settings)
            Publish(LightEvents.DeviceConfigurationChanged, light, new() { ["settings"] = settings, ["oldSettings"] = old });
    }

    private static bool Same(double? a, double? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 0.01;

    private void Publish(string type, ElgatoLight light, Dictionary<string, object?>? extra = null)
    {
        var args = light.ToArgs();
        if (light.SettingsJson is { } settings) args.TryAdd("settings", settings);
        if (extra is not null)
            foreach (var (key, value) in extra) args[key] = value;
        _bus.Publish(new BotEvent(type, args));
    }

    private void UpdateStatusIfRunning()
    {
        if (IsRunning) UpdateRunningStatus();
    }

    private void UpdateRunningStatus()
    {
        var lights = Lights;
        var online = lights.Count(l => l.IsOnline);
        if (lights.Count == 0)
            SetStatus(ConnectionState.Connecting, Settings.Discovery ? "No lights found yet (searching with mDNS)" : "No lights added");
        else if (online == 0)
            SetStatus(ConnectionState.Connecting, $"None of {Count(lights.Count)} is answering; retrying");
        else
            SetStatus(ConnectionState.Connected, online == lights.Count ? $"{Count(online)} reachable" : $"{online} of {Count(lights.Count)} reachable");
    }

    private static string Count(int n) => n == 1 ? "1 light" : $"{n.ToString(CultureInfo.InvariantCulture)} lights";

    private void SetStatus(ConnectionState state, string message)
    {
        if (State == state && StatusMessage == message) return;
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    internal static (string Host, int Port) ParseAddress(string address)
    {
        var text = address.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        text = text.TrimEnd('/');
        if (text.Length == 0) throw new ArgumentException("Enter the light's IP address or host name.");
        if (IPAddress.TryParse(text, out var ip)) return (ip.ToString(), 9123);
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
        {
            var host = text[1..close];
            var rest = text[(close + 1)..];
            return (host, rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) ? p6 : 9123);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            return (text[..colon], port);
        return (text, 9123);
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private async Task StopLoopAsync()
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
            _log.Error(LogSource, $"Error while stopping: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
