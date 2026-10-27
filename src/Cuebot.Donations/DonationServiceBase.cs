using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Donations;

/// <summary>
/// Shared plumbing of the Streamlabs and StreamElements connections: status, a background run loop with
/// connect/disconnect, dedupe, publishing, the "last events" list and local test events.
/// </summary>
public abstract class DonationServiceBase : IConnectionStatus, IAsyncDisposable
{
    private const int RecentSize = 50;
    private readonly RecentIds _seen = new();
    private readonly LinkedList<DonationRecord> _recent = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;

    private protected DonationServiceBase(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log)
    {
        SettingsFile = settings;
        Bus = bus;
        Log = log;
    }

    private protected JsonFile<DonationsSettings> SettingsFile { get; }
    private protected EventBus Bus { get; }
    private protected AppLog Log { get; }

    public abstract string Name { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (from any thread) when an event is added to <see cref="RecentEvents"/>.</summary>
    public event Action? RecentChanged;

    public bool IsRunning => _run is not null;

    /// <summary>The token currently configured (never log it).</summary>
    protected abstract string Token { get; }

    protected abstract bool AutoConnect { get; }

    /// <summary>Whether relayed platform events are published.</summary>
    protected abstract bool RelayPlatformEvents { get; }

    protected abstract string ConnectedEvent { get; }
    protected abstract string DisconnectedEvent { get; }

    /// <summary>Kinds accepted by <see cref="SendTestEvent"/>.</summary>
    public abstract IReadOnlyList<string> TestEventKinds { get; }

    public void SaveSettings() => SettingsFile.ScheduleSave();

    public IReadOnlyList<DonationRecord> RecentEvents()
    {
        lock (_gate) return _recent.ToArray();
    }

    /// <summary>Starts the connection in the background (replacing a running one). Throws when no token is set.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Token.Trim().Length == 0)
        {
            SetStatus(ConnectionState.Error, $"Enter your {Name} token first.");
            throw new InvalidOperationException(StatusMessage);
        }
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, $"Connecting to {Name}...");
            await PrepareAsync(ct).ConfigureAwait(false);
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(ConnectionState.Error, Secrets.Redact(ex.Message, Token));
            throw;
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
            if (await StopAsync().ConfigureAwait(false)) SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects when a token is set and auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!AutoConnect || Token.Trim().Length == 0) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Error(Name, $"Automatic connection failed: {Secrets.Redact(ex.Message, Token)}");
        }
    }

    /// <summary>
    /// Publishes a sample event of <paramref name="kind"/> locally (never sent to the service), marked
    /// <c>isTest</c>, so triggers, speech and overlays can be tried out. Returns false for an unknown kind.
    /// </summary>
    public abstract bool SendTestEvent(string kind);

    /// <summary>Optional work before the loop starts (e.g. looking up the channel); may throw.</summary>
    private protected virtual Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Runs the connection until cancelled; reports through <see cref="OnConnected"/> etc.</summary>
    private protected abstract Task RunAsync(CancellationToken ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            Log.Error(Name, $"Connection stopped: {Secrets.Redact(ex.Message, Token)}");
            SetStatus(ConnectionState.Error, Secrets.Redact(ex.Message, Token));
        }
    }

    private protected void OnConnected(string message)
    {
        var wasConnected = State == ConnectionState.Connected;
        SetStatus(ConnectionState.Connected, message);
        if (!wasConnected)
        {
            Log.Info(Name, message);
            Bus.Publish(new BotEvent(ConnectedEvent, new Dictionary<string, object?> { ["service"] = Name }));
        }
    }

    private protected void OnDisconnected(string reason, bool fatal)
    {
        if (_run is null) return;
        var wasConnected = State == ConnectionState.Connected;
        reason = Secrets.Redact(reason, Token);
        SetStatus(fatal ? ConnectionState.Error : ConnectionState.Connecting, fatal ? reason : $"Reconnecting ({reason})");
        if (wasConnected) Bus.Publish(new BotEvent(DisconnectedEvent, new Dictionary<string, object?> { ["service"] = Name, ["reason"] = reason }));
    }

    /// <summary>Publishes a mapped event unless it is a duplicate or a relayed event that is switched off.</summary>
    private protected bool Publish(DonationEvent e)
    {
        var isTest = e.Args.TryGetValue("isTest", out var t) && t is true;
        if (e.Relayed && !RelayPlatformEvents && !isTest) return false;
        if (!isTest && !_seen.AddAll(e.Keys)) return false;
        var bot = new BotEvent(e.Type, e.Args);
        var summary = DonationChatAdapter.Describe(bot)?.Title ?? e.Type;
        lock (_gate)
        {
            _recent.AddFirst(new DonationRecord(bot.Time, e.Type, summary, isTest));
            while (_recent.Count > RecentSize) _recent.RemoveLast();
        }
        Bus.Publish(bot);
        RecentChanged?.Invoke();
        return true;
    }

    private async Task<bool> StopAsync()
    {
        var run = _run;
        if (run is null) return false;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(Name, "Error while disconnecting", ex);
        }
        run.Dispose();
        if (State == ConnectionState.Connected)
            Bus.Publish(new BotEvent(DisconnectedEvent, new Dictionary<string, object?> { ["service"] = Name, ["reason"] = "disconnected" }));
        return true;
    }

    private protected void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    /// <summary>Disconnects. Safe to call more than once (DI disposes each registration of this singleton).</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
