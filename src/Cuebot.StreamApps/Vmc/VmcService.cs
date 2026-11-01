using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Vmc;

/// <summary>
/// Sends avatar expressions with the VMC protocol (protocol.vmc.info): OSC over UDP to a 3D VTuber app's VMC receiver
/// (VSeeFace, VMagicMirror, VNyan, Warudo...). An expression is <c>/VMC/Ext/Blend/Val name value</c> followed by
/// <c>/VMC/Ext/Blend/Apply</c>. VMC is one-way with no handshake, so "connected" only means the socket is open.
/// Receivers expect a stream, so an expression held for a time is re-sent ten times a second until it ends.
/// </summary>
public sealed class VmcService : IConnectable, IAsyncDisposable
{
    public const string BlendValueAddress = "/VMC/Ext/Blend/Val";
    public const string BlendApplyAddress = "/VMC/Ext/Blend/Apply";

    /// <summary>The expressions every VRM 0.x avatar has (VRM 1.0 apps map these too).</summary>
    public static readonly IReadOnlyList<string> PresetExpressions =
        ["Joy", "Angry", "Sorrow", "Fun", "Surprised", "Neutral", "A", "I", "U", "E", "O", "Blink", "Blink_L", "Blink_R", "LookUp", "LookDown", "LookLeft", "LookRight"];

    internal static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(100);

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly AppLog _log;
    private readonly Func<IOscTransport> _transports;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _holds = new(StringComparer.OrdinalIgnoreCase);
    private IOscTransport? _transport;

    public VmcService(JsonFile<StreamAppsSettings> settings, AppLog log)
        : this(settings, log, () => new UdpOscTransport())
    {
    }

    internal VmcService(JsonFile<StreamAppsSettings> settings, AppLog log, Func<IOscTransport> transports)
    {
        _settings = settings;
        _log = log;
        _transports = transports;
    }

    public string Name => "VMC";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public VmcSettings Settings => _settings.Value.Vmc;

    public void SaveSettings() => _settings.ScheduleSave();

    public bool IsConnected => State == ConnectionState.Connected;

    private string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
    private int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 39539;

    /// <summary>Opens the UDP socket (it sends only; no port is listened on).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            var transport = _transports();
            try
            {
                await transport.OpenAsync(Host, Port, 0, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                SetStatus(ConnectionState.Error, $"Could not open VMC: {ex.Message}");
                throw;
            }
            _transport = transport;
            SetStatus(ConnectionState.Connected, $"Sending VMC to {Host}:{Port}");
            _log.Info(Name, StatusMessage);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects when auto-connect is on. Never throws.</summary>
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

    private async Task StopAsync()
    {
        foreach (var name in _holds.Keys)
            if (_holds.TryRemove(name, out var hold)) await hold.CancelAsync().ConfigureAwait(false);
        if (_transport is { } transport)
        {
            _transport = null;
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IOscTransport Transport => _transport ?? throw new StreamAppNotConnectedException(Name);

    private async Task SendBlendAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct)
    {
        var transport = Transport;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (name, value) in values)
                await transport.SendAsync(new OscMessage(BlendValueAddress, name, value), ct).ConfigureAwait(false);
            await transport.SendAsync(new OscMessage(BlendApplyAddress), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal static float ParseValue(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return 1;
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return (float)Math.Clamp(percent / 100, 0, 1);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 1
            ? (float)d
            : throw new ArgumentException($"Strength is a number from 0 to 1, or a percentage (got '{value}').");
    }

    /// <summary>
    /// Sets an expression (a VRM blend shape clip, e.g. Joy) to a strength from 0 to 1. With <paramref name="seconds"/>
    /// it is held that long (re-sent ten times a second) and then set back to 0; the step does not wait for that.
    /// Setting the same expression again replaces a hold in progress. Returns the strength sent.
    /// </summary>
    public async Task<float> SetExpressionAsync(string expression, float strength, double? seconds, CancellationToken ct = default)
    {
        var name = Common.Steps.Require(expression, "Expression");
        if (seconds is <= 0) throw new ArgumentException("Seconds must be more than 0, or empty to keep the expression.");
        if (_holds.TryRemove(name, out var previous)) await previous.CancelAsync().ConfigureAwait(false);
        var values = new Dictionary<string, float> { [name] = strength };
        await SendBlendAsync(values, ct).ConfigureAwait(false);
        if (seconds is { } hold)
        {
            var cts = new CancellationTokenSource();
            _holds[name] = cts;
            _ = HoldAsync(name, strength, TimeSpan.FromSeconds(hold), cts);
        }
        return strength;
    }

    private async Task HoldAsync(string name, float strength, TimeSpan duration, CancellationTokenSource cts)
    {
        var values = new Dictionary<string, float> { [name] = strength };
        var end = DateTimeOffset.UtcNow + duration;
        try
        {
            while (DateTimeOffset.UtcNow + ResendInterval < end)
            {
                await Task.Delay(ResendInterval, cts.Token).ConfigureAwait(false);
                await SendBlendAsync(values, cts.Token).ConfigureAwait(false);
            }
            var left = end - DateTimeOffset.UtcNow;
            if (left > TimeSpan.Zero) await Task.Delay(left, cts.Token).ConfigureAwait(false);
            values[name] = 0;
            await SendBlendAsync(values, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer step, or disconnected.
        }
        catch (Exception ex) when (ex is StreamAppNotConnectedException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _log.Warn(Name, $"Stopped holding {name}: {ex.Message}");
        }
        finally
        {
            _holds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(name, cts));
            cts.Dispose();
        }
    }

    /// <summary>Sets every expression named to 0 (e.g. to clear what earlier steps set). Stops holds of those.</summary>
    public async Task ClearExpressionsAsync(IEnumerable<string> expressions, CancellationToken ct = default)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in expressions.Select(e => e.Trim()).Where(e => e.Length > 0))
        {
            values[name] = 0;
            if (_holds.TryRemove(name, out var hold)) await hold.CancelAsync().ConfigureAwait(false);
        }
        if (values.Count == 0) throw new ArgumentException("Name at least one expression.");
        await SendBlendAsync(values, ct).ConfigureAwait(false);
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
