using System.Collections;
using System.Threading.Channels;
using Cuebot.Core.Desktop.DBus;
using Cuebot.Core.Logging;

namespace Cuebot.Integrations.NowPlaying.Mpris;

/// <summary>The parts of a D-Bus connection the MPRIS watcher uses (tests substitute a fake bus).</summary>
internal interface IMprisBus : IDisposable
{
    /// <summary>Raised on the connection's reader thread; handlers must not wait for a call's reply there.</summary>
    event Action<DBusMessage>? Signal;

    event Action<Exception?>? Closed;

    Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct);
}

/// <summary>The real session bus, through Cuebot's own D-Bus client.</summary>
internal sealed class SessionBus(DBusConnection connection) : IMprisBus
{
    public static async Task<IMprisBus> ConnectAsync(CancellationToken ct) => new SessionBus(await DBusConnection.ConnectSessionAsync(ct).ConfigureAwait(false));

    public event Action<DBusMessage>? Signal
    {
        add => connection.Signal += value;
        remove => connection.Signal -= value;
    }

    public event Action<Exception?>? Closed
    {
        add => connection.Closed += value;
        remove => connection.Closed -= value;
    }

    public Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct) => connection.CallAsync(call, ct);

    public void Dispose() => connection.Dispose();
}

/// <summary>
/// Watches MPRIS players on the Linux session bus: lists org.mpris.MediaPlayer2.* names, reads each player's Metadata
/// and PlaybackStatus, follows PropertiesChanged and NameOwnerChanged signals, and sends PlayPause/Next/Previous.
/// Reconnects when the bus goes away.
/// </summary>
internal sealed class MprisSource : ISystemMediaSource
{
    private const string Category = "Now playing";
    private const string BusService = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly AppLog _log;
    private readonly Func<CancellationToken, Task<IMprisBus>> _connect;
    private readonly TimeSpan _retry;
    private readonly object _gate = new();
    private readonly Dictionary<string, MprisPlayer> _players = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IMprisBus? _bus;
    private string _status = "Not watching music players yet.";
    private string? _lastError;

    public MprisSource(AppLog log, Func<CancellationToken, Task<IMprisBus>>? connect = null, TimeSpan? retry = null)
    {
        _log = log;
        _connect = connect ?? SessionBus.ConnectAsync;
        _retry = retry ?? TimeSpan.FromSeconds(10);
    }

    public string Name => "Linux media players (MPRIS)";
    public bool IsSupported => true;
    public bool IsRunning => _loop is { IsCompleted: false };
    public string StatusText { get { lock (_gate) return _status; } }
    public string? CurrentSessionId => null;

    public IReadOnlyList<PlayerSession> Sessions
    {
        get
        {
            lock (_gate) return _players.Values.Select(p => p.ToSession()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _players.Clear();
            _status = "Not watching music players.";
        }
        Changed?.Invoke();
    }

    public async Task<bool> ControlAsync(string sessionId, MediaCommand command, CancellationToken ct = default)
    {
        IMprisBus? bus;
        lock (_gate)
        {
            bus = _bus;
            if (bus is null || !_players.ContainsKey(sessionId)) return false;
        }
        var member = command switch
        {
            MediaCommand.Play => "Play",
            MediaCommand.Pause => "Pause",
            MediaCommand.Next => "Next",
            MediaCommand.Previous => "Previous",
            _ => "PlayPause",
        };
        try
        {
            await CallAsync(bus, DBusMessage.Call(sessionId, MprisParser.ObjectPath, MprisParser.PlayerInterface, member), ct).ConfigureAwait(false);
            return true;
        }
        catch (DBusException ex)
        {
            _log.Warn(Category, $"{MprisParser.NameFromBus(sessionId)} did not accept {member}: {ex.Message}");
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                if (message != _lastError) _log.Warn(Category, $"Cannot read music players from the session bus: {message}");
                _lastError = message;
                SetStatus("Cannot reach your desktop's music players right now. Cuebot keeps trying.");
            }
            lock (_gate)
            {
                _bus = null;
                _players.Clear();
            }
            Changed?.Invoke();
            try { await Task.Delay(_retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        using var bus = await _connect(ct).ConfigureAwait(false);
        var signals = Channel.CreateUnbounded<DBusMessage>(new UnboundedChannelOptions { SingleReader = true });
        bus.Signal += m => signals.Writer.TryWrite(m);
        bus.Closed += ex => signals.Writer.TryComplete(ex ?? new IOException("The session bus connection closed."));
        await CallAsync(bus, AddMatch(MprisParser.NameOwnerRule), ct).ConfigureAwait(false);
        await CallAsync(bus, AddMatch(MprisParser.PropertiesRule), ct).ConfigureAwait(false);

        var names = await CallAsync(bus, DBusMessage.Call(BusService, BusPath, BusService, "ListNames"), ct).ConfigureAwait(false);
        foreach (var name in (names.Body.FirstOrDefault() as IEnumerable)?.OfType<string>().Where(MprisParser.IsPlayerBusName) ?? [])
            await RefreshPlayerAsync(bus, name, null, ct).ConfigureAwait(false);
        lock (_gate) _bus = bus;
        _lastError = null;
        UpdateStatus();
        _log.Info(Category, $"Watching music players on the session bus ({Sessions.Count} open)");
        Changed?.Invoke();

        await foreach (var message in signals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (await HandleSignalAsync(bus, message, ct).ConfigureAwait(false))
            {
                UpdateStatus();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Applies one signal; true when something changed.</summary>
    internal async Task<bool> HandleSignalAsync(IMprisBus bus, DBusMessage message, CancellationToken ct)
    {
        if (message is { Member: "NameOwnerChanged", Interface: BusService })
        {
            if (message.Body.Length < 3 || message.Body[0] is not string name || !MprisParser.IsPlayerBusName(name)) return false;
            var newOwner = message.Body[2] as string;
            if (string.IsNullOrEmpty(newOwner))
            {
                lock (_gate) return _players.Remove(name);
            }
            await RefreshPlayerAsync(bus, name, newOwner, ct).ConfigureAwait(false);
            return true;
        }
        if (message is not { Member: "PropertiesChanged", Interface: MprisParser.PropertiesInterface, Path: MprisParser.ObjectPath }
            || message.Body.Length < 2 || message.Body[0] as string != MprisParser.PlayerInterface)
            return false;
        List<MprisPlayer> owners;
        lock (_gate) owners = _players.Values.Where(p => p.UniqueName == message.Sender).ToList();
        if (owners.Count == 0) return false;
        var changed = MprisParser.Unwrap(message.Body[1]) as IDictionary;
        var reload = message.Body.Length > 2 && MprisParser.NeedsReload(message.Body[2]);
        foreach (var player in owners)
        {
            if (reload)
            {
                await RefreshPlayerAsync(bus, player.BusName, player.UniqueName, ct).ConfigureAwait(false);
                continue;
            }
            if (changed is null) continue;
            var updated = MprisParser.Apply(player, changed);
            lock (_gate)
            {
                if (_players.ContainsKey(player.BusName)) _players[player.BusName] = updated;
            }
        }
        return true;
    }

    /// <summary>Reads a player's state; forgets it when it does not answer (it closed).</summary>
    private async Task RefreshPlayerAsync(IMprisBus bus, string name, string? owner, CancellationToken ct)
    {
        try
        {
            owner ??= (await CallAsync(bus, DBusMessage.Call(BusService, BusPath, BusService, "GetNameOwner", "s", name), ct)
                .ConfigureAwait(false)).Body.FirstOrDefault() as string ?? "";
            var all = await CallAsync(bus, DBusMessage.Call(name, MprisParser.ObjectPath, MprisParser.PropertiesInterface, "GetAll", "s",
                MprisParser.PlayerInterface), ct).ConfigureAwait(false);
            var props = all.Body.FirstOrDefault() as IDictionary;
            var identity = "";
            try
            {
                var reply = await CallAsync(bus, DBusMessage.Call(name, MprisParser.ObjectPath, MprisParser.PropertiesInterface, "Get", "ss",
                    MprisParser.RootInterface, "Identity"), ct).ConfigureAwait(false);
                identity = MprisParser.Unwrap(reply.Body.FirstOrDefault()) as string ?? "";
            }
            catch (DBusException) { }
            var player = new MprisPlayer(name, owner, identity.Trim(),
                MprisParser.ParseStatus(props is not null && props.Contains("PlaybackStatus") ? props["PlaybackStatus"] : null),
                MprisParser.ParseMetadata(props is not null && props.Contains("Metadata") ? props["Metadata"] : null));
            lock (_gate) _players[name] = player;
        }
        catch (Exception ex) when (ex is DBusException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            lock (_gate) _players.Remove(name);
        }
    }

    private static async Task<DBusMessage> CallAsync(IMprisBus bus, DBusMessage call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        return await bus.CallAsync(call, timeout.Token).ConfigureAwait(false);
    }

    private static DBusMessage AddMatch(string rule) => DBusMessage.Call(BusService, BusPath, BusService, "AddMatch", "s", rule);

    private void UpdateStatus()
    {
        var count = Sessions.Count;
        SetStatus(count switch
        {
            0 => "No music player is open. Players that support media keys (MPRIS) show up here when they start.",
            1 => "Watching 1 music player.",
            _ => $"Watching {count} music players.",
        });
    }

    private void SetStatus(string text)
    {
        lock (_gate) _status = text;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
