using System.Net.WebSockets;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.StreamApps.Common;

/// <summary>
/// A websocket client connection to a local app: dashboard status, a background loop that connects, runs a session and
/// reconnects with exponential backoff (when enabled) until the user disconnects, and Connected/Disconnected events.
/// </summary>
public abstract class StreamAppConnection : IConnectable, IAsyncDisposable
{
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile IWebSocketTransport? _transport;
    private volatile bool _sessionConnected;

    private protected StreamAppConnection(string name, EventBus bus, AppLog log, Func<IWebSocketTransport> transportFactory, StreamAppsTimings timings)
    {
        Name = name;
        Bus = bus;
        Log = log;
        _transportFactory = transportFactory;
        Timings = timings;
    }

    public string Name { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>True while the background loop is connecting, connected or waiting to reconnect.</summary>
    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    private protected EventBus Bus { get; }
    private protected AppLog Log { get; }
    private protected StreamAppsTimings Timings { get; }

    protected abstract bool AutoConnect { get; }

    protected abstract bool AutoReconnect { get; }

    /// <summary>The websocket address and its display text.</summary>
    private protected abstract (Uri Url, string Display) Endpoint();

    /// <summary>Runs one session on an open socket: handshake, then <see cref="MarkConnected"/>, then read until it closes.</summary>
    private protected abstract Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct);

    /// <summary>Arguments added to the connected/disconnected events.</summary>
    private protected virtual Dictionary<string, object?> ConnectionArgs() => [];

    private protected virtual void OnSessionEnded()
    {
    }

    /// <summary>Starts connecting in the background (replacing a running loop).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, $"Connecting to {Name}...");
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

    /// <summary>Connects when auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!AutoConnect) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Error(Name, $"Automatic connection failed: {ex.Message}");
        }
    }

    private protected Task SendAsync(string text, CancellationToken ct)
    {
        if (_transport is not { } transport || !_sessionConnected) throw new StreamAppNotConnectedException(Name);
        return transport.SendAsync(text, ct);
    }

    private protected void EnsureConnected()
    {
        if (_transport is null || !_sessionConnected) throw new StreamAppNotConnectedException(Name);
    }

    private protected void MarkConnected(string message)
    {
        SetStatus(ConnectionState.Connected, message);
        if (_sessionConnected) return;
        _sessionConnected = true;
        Log.Info(Name, message);
        Publish("connected", ConnectionArgs());
    }

    private protected abstract void Publish(string name, Dictionary<string, object?> args);

    private protected void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = Timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (url, display) = Endpoint();
                var transport = _transportFactory();
                string reason;
                var hadConnected = false;
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(Timings.ConnectTimeout);
                        await transport.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
                    }
                    (reason, hadConnected) = await RunOneSessionAsync(transport, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    reason = $"{display} not reachable ({(ex is OperationCanceledException ? "timed out" : Describe(ex))})";
                }
                if (ct.IsCancellationRequested) return;
                if (hadConnected) delay = Timings.ReconnectDelay;
                if (!AutoReconnect)
                {
                    SetStatus(ConnectionState.Disconnected, reason);
                    return;
                }
                if (hadConnected) Log.Warn(Name, $"Connection lost: {reason}");
                SetStatus(ConnectionState.Connecting, $"{reason}; retrying in {Seconds(delay)}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Timings.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            Log.Error(Name, $"Connection loop stopped: {ex.Message}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    private async Task<(string Reason, bool HadConnected)> RunOneSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        _transport = transport;
        string reason;
        try
        {
            await RunSessionAsync(transport, ct).ConfigureAwait(false);
            reason = transport.CloseReason ?? "connection closed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "disconnected";
        }
        catch (Exception ex)
        {
            reason = transport.CloseReason ?? Describe(ex);
        }
        finally
        {
            _transport = null;
            await transport.DisposeAsync().ConfigureAwait(false);
            try
            {
                OnSessionEnded();
            }
            catch (Exception ex)
            {
                Log.Error(Name, "Cleanup after the session failed", ex);
            }
        }
        var hadConnected = _sessionConnected;
        if (hadConnected)
        {
            _sessionConnected = false;
            var args = ConnectionArgs();
            args["reason"] = reason;
            Publish("disconnected", args);
        }
        return (reason, hadConnected);
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    private protected static string Seconds(TimeSpan delay) =>
        delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";

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
            Log.Error(Name, $"Error while disconnecting: {ex.Message}");
        }
        run.Dispose();
    }

    /// <summary>Disconnects. Safe to call more than once (DI disposes each registration of this singleton).</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
