using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Vrchat;

/// <summary>Bot event types published by <see cref="VrchatService"/>.</summary>
public static class VrchatEvents
{
    public const string AvatarChanged = "vrchat.avatarChanged";
    public const string ParameterChanged = "vrchat.parameterChanged";
}

/// <summary>The kinds of avatar parameter VRChat has.</summary>
public enum VrchatParameterType { Bool, Int, Float }

/// <summary>
/// VRChat through OSC over UDP (docs.vrchat.com/docs/osc-overview): Cuebot sends to VRChat's in port (9000) and
/// listens on its out port (9001) for <c>/avatar/change</c> and <c>/avatar/parameters/&lt;name&gt;</c>. OSC has no
/// handshake, so "connected" means the socket is open; VRChat only sends once OSC is on in its Action Menu.
/// </summary>
public sealed class VrchatService : IConnectable, IAsyncDisposable
{
    public const string AvatarChangeAddress = "/avatar/change";
    public const string ParametersPrefix = "/avatar/parameters/";
    public const string ChatboxInputAddress = "/chatbox/input";
    public const string ChatboxTypingAddress = "/chatbox/typing";

    /// <summary>VRChat's chatbox shows at most this many characters.</summary>
    public const int ChatboxLimit = 144;

    /// <summary>Built-in parameters VRChat updates many times a second; they are remembered but are not triggers.</summary>
    public static readonly IReadOnlySet<string> Noisy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "AngularY", "Upright", "Voice", "Viseme",
        "GestureLeftWeight", "GestureRightWeight", "Grounded", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters",
        "EyeHeightAsPercent",
    };

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IOscTransport> _transports;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, object?> _parameters = new(StringComparer.OrdinalIgnoreCase);
    private IOscTransport? _transport;
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile string _avatarId = "";

    public VrchatService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new UdpOscTransport())
    {
    }

    internal VrchatService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IOscTransport> transports)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transports = transports;
    }

    public string Name => "VRChat";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when the avatar or a parameter changed.</summary>
    public event Action? DataChanged;

    public VrchatSettings Settings => _settings.Value.Vrchat;

    public void SaveSettings() => _settings.ScheduleSave();

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>The avatar id VRChat last reported (empty until it does).</summary>
    public string AvatarId => _avatarId;

    /// <summary>Parameter values VRChat reported since connecting (or since the last avatar change).</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    private string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
    private int SendPort => Settings.SendPort is > 0 and < 65536 ? Settings.SendPort : 9000;
    private int ListenPort => Settings.ListenPort is > 0 and < 65536 ? Settings.ListenPort : 9001;

    /// <summary>Opens the OSC socket and starts listening.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            var transport = _transports();
            try
            {
                await transport.OpenAsync(Host, SendPort, ListenPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                SetStatus(ConnectionState.Error, $"Could not open OSC: {ex.Message}");
                throw;
            }
            _transport = transport;
            var warning = transport.Warning is { } w
                ? $"; Cuebot cannot hear VRChat because another app uses port {ListenPort} ({w})"
                : $"; listening on {ListenPort}";
            SetStatus(ConnectionState.Connected, $"Sending to VRChat at {Host}:{SendPort}{warning}");
            if (transport.Warning is not null) _log.Warn(Name, StatusMessage);
            else _log.Info(Name, StatusMessage);
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => ReceiveLoopAsync(transport, run.Token), CancellationToken.None);
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
        var run = _run;
        _run = null;
        if (run is not null)
        {
            await run.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            run.Dispose();
        }
        if (_transport is { } transport)
        {
            _transport = null;
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        _parameters.Clear();
    }

    private async Task ReceiveLoopAsync(IOscTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
            {
                try
                {
                    OnMessage(message);
                }
                catch (Exception ex)
                {
                    _log.Error(Name, "Could not process an OSC message", ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Warn(Name, $"Stopped listening: {ex.Message}");
            SetStatus(ConnectionState.Error, $"Stopped listening: {ex.Message}");
        }
    }

    internal void OnMessage(OscMessage message)
    {
        if (message.Address == AvatarChangeAddress)
        {
            var id = message.Str(0) ?? "";
            _avatarId = id;
            _parameters.Clear();
            DataChanged?.Invoke();
            Publish(VrchatEvents.AvatarChanged, new() { ["avatarId"] = id });
            return;
        }
        if (!message.Address.StartsWith(ParametersPrefix, StringComparison.Ordinal) || message.Args.Count == 0) return;
        var name = message.Address[ParametersPrefix.Length..];
        var value = message.Args[0] switch
        {
            float f => (object?)(double)f,
            double d => d,
            int i => i,
            long l => l,
            bool b => b,
            var other => other,
        };
        var had = _parameters.TryGetValue(name, out var before);
        _parameters[name] = value;
        if (had && Equals(before, value)) return;
        DataChanged?.Invoke();
        if (Noisy.Contains(name)) return;
        Publish(VrchatEvents.ParameterChanged, new()
        {
            ["parameter"] = name,
            ["value"] = value,
            ["previousValue"] = had ? before : null,
            ["type"] = value switch { bool => "Bool", int or long => "Int", _ => "Float" },
        });
    }

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("vrchat.avatarId", _avatarId);
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    private IOscTransport Transport => _transport ?? throw new StreamAppNotConnectedException(Name);

    /// <summary>Sets an avatar parameter. "Toggle" flips a bool VRChat reported (true when it has not reported one).</summary>
    public async Task<object> SetParameterAsync(string name, VrchatParameterType type, string value, CancellationToken ct = default)
    {
        var parameter = Common.Steps.Require(name, "Parameter").TrimStart('/');
        if (parameter.StartsWith(ParametersPrefix.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            parameter = parameter[(ParametersPrefix.Length - 1)..];
        var v = value.Trim();
        object arg = type switch
        {
            VrchatParameterType.Bool => v.ToLowerInvariant() switch
            {
                "toggle" => !(_parameters.TryGetValue(parameter, out var current) && current is true),
                "true" or "on" or "1" or "yes" => true,
                "false" or "off" or "0" or "no" or "" => false,
                _ => throw new ArgumentException($"A Bool value is true, false or toggle (got '{value}')."),
            },
            VrchatParameterType.Int => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i is >= 0 and <= 255
                ? i
                : throw new ArgumentException($"An Int value is a whole number from 0 to 255 (got '{value}')."),
            _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= -1 and <= 1
                ? (float)d
                : throw new ArgumentException($"A Float value is a number from -1 to 1 (got '{value}')."),
        };
        await Transport.SendAsync(new OscMessage(ParametersPrefix + parameter, arg), ct).ConfigureAwait(false);
        return arg;
    }

    /// <summary>Shows text in the chatbox above the avatar (cut to 144 characters). Returns the text sent.</summary>
    public async Task<string> SendChatboxAsync(string text, bool immediately = true, bool sound = true, CancellationToken ct = default)
    {
        var message = text.Length > ChatboxLimit ? text[..ChatboxLimit] : text;
        await Transport.SendAsync(new OscMessage(ChatboxInputAddress, message, immediately, sound), ct).ConfigureAwait(false);
        return message;
    }

    /// <summary>Shows or hides the typing bubble.</summary>
    public Task SetTypingAsync(bool typing, CancellationToken ct = default) =>
        Transport.SendAsync(new OscMessage(ChatboxTypingAddress, typing), ct);

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
