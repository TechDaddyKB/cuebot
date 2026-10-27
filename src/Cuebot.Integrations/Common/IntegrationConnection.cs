using System.Net.WebSockets;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Integrations.Common;

/// <summary>A websocket address to try, with optional upgrade headers. <see cref="Display"/> never contains secrets.</summary>
internal sealed record IntegrationEndpoint(Uri Url, string Display, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Shared plumbing of every integration: status for the dashboard, a background loop that connects (trying each
/// endpoint in turn), runs a session and reconnects with backoff until the user disconnects or the credentials are
/// refused, Connected/Disconnected bot events, and secret redaction for everything logged or shown.
/// </summary>
public abstract class IntegrationConnection : IConnectionStatus, IAsyncDisposable
{
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile IWebSocketTransport? _transport;
    private volatile bool _sessionConnected;

    private protected IntegrationConnection(string name, EventBus bus, AppLog log, Func<IWebSocketTransport> transportFactory,
        IntegrationsTimings timings)
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
    private protected IntegrationsTimings Timings { get; }

    /// <summary>Event type prefix, e.g. "vtubestudio" (events are "vtubestudio.connected"...).</summary>
    protected abstract string EventPrefix { get; }

    protected abstract bool AutoConnect { get; }

    /// <summary>Every secret of the current settings; redacted from logs and status text.</summary>
    protected abstract IEnumerable<string?> SecretValues { get; }

    /// <summary>A message when settings needed to connect are missing (e.g. no token), else null.</summary>
    protected virtual string? MissingSettings() => null;

    /// <summary>Addresses to try in order on each attempt (Voicemod cycles through several ports).</summary>
    private protected abstract IReadOnlyList<IntegrationEndpoint> Endpoints();

    /// <summary>
    /// Runs one session on an open socket: handshake, then <see cref="MarkConnected"/>, then read until the socket closes.
    /// Throw <see cref="IntegrationAuthException"/> to stop reconnecting.
    /// </summary>
    private protected abstract Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct);

    /// <summary>Called after a session ended (clear per-session caches).</summary>
    private protected virtual void OnSessionEnded()
    {
    }

    /// <summary>Called when an endpoint accepted the websocket (e.g. to remember the working port).</summary>
    private protected virtual void OnEndpointOpened(IntegrationEndpoint endpoint)
    {
    }

    /// <summary>Turns an upgrade failure into a fatal credentials error, or null to keep retrying.</summary>
    private protected virtual string? AuthRejection(Exception error) => null;

    /// <summary>Starts connecting in the background (replacing a running loop). Throws when settings are missing.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (MissingSettings() is { } missing)
        {
            SetStatus(ConnectionState.Error, missing);
            throw new InvalidOperationException(missing);
        }
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

    /// <summary>Connects when auto-connect is on and the settings are complete. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!AutoConnect || MissingSettings() is not null) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Error(Name, $"Automatic connection failed: {Redact(ex.Message)}");
        }
    }

    /// <summary>Sends a text frame on the connected session.</summary>
    /// <exception cref="IntegrationNotConnectedException">No session is connected.</exception>
    private protected Task SendAsync(string text, CancellationToken ct)
    {
        if (_transport is not { } transport || !_sessionConnected) throw new IntegrationNotConnectedException(Name);
        return transport.SendAsync(text, ct);
    }

    private protected void EnsureConnected()
    {
        if (_transport is null || !_sessionConnected) throw new IntegrationNotConnectedException(Name);
    }

    /// <summary>Reports the handshake as complete: status Connected and a "connected" bot event once per session.</summary>
    private protected void MarkConnected(string message, IReadOnlyDictionary<string, object?>? args = null)
    {
        SetStatus(ConnectionState.Connected, message);
        if (_sessionConnected) return;
        _sessionConnected = true;
        Log.Info(Name, Redact(message));
        var eventArgs = new Dictionary<string, object?>(args ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Name,
        };
        Publish("connected", eventArgs);
    }

    /// <summary>Publishes "<see cref="EventPrefix"/>.<paramref name="name"/>" with the service name added.</summary>
    private protected void Publish(string name, Dictionary<string, object?> args)
    {
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent($"{EventPrefix}.{name}", args));
    }

    private protected string Redact(string? text) => Secrets.Redact(text, SecretValues.ToArray());

    private protected void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = Redact(message);
        StatusChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = Timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (transport, endpoint, failure, fatal) = await OpenAsync(ct).ConfigureAwait(false);
                if (fatal)
                {
                    StopWithError(failure);
                    return;
                }
                if (transport is null || endpoint is null)
                {
                    SetStatus(ConnectionState.Connecting, $"{Name} not reachable ({failure}); retrying in {Seconds(delay)}");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Next(delay);
                    continue;
                }

                var (reason, authFailed, hadConnected) = await RunOneSessionAsync(transport, endpoint, ct).ConfigureAwait(false);
                if (hadConnected) delay = Timings.ReconnectDelay;
                if (ct.IsCancellationRequested) return;
                if (authFailed)
                {
                    StopWithError(reason);
                    return;
                }
                if (reason.Length > 0) Log.Warn(Name, $"Connection lost: {reason}");
                SetStatus(ConnectionState.Connecting, $"Reconnecting in {Seconds(delay)} ({reason})");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Next(delay);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            Log.Error(Name, $"Connection loop stopped: {Redact(ex.Message)}");
            SetStatus(ConnectionState.Error, ex.Message);
        }

        TimeSpan Next(TimeSpan current) =>
            TimeSpan.FromTicks(Math.Min(current.Ticks * 2, Timings.MaxReconnectDelay.Ticks));
    }

    private void StopWithError(string reason)
    {
        Log.Warn(Name, reason);
        SetStatus(ConnectionState.Error, reason);
    }

    private async Task<(IWebSocketTransport? Transport, IntegrationEndpoint? Endpoint, string Failure, bool Fatal)> OpenAsync(CancellationToken ct)
    {
        var failure = "no address configured";
        foreach (var endpoint in Endpoints())
        {
            var candidate = _transportFactory();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timings.ConnectTimeout);
                await candidate.ConnectAsync(endpoint.Url, endpoint.Headers, timeout.Token).ConfigureAwait(false);
                OnEndpointOpened(endpoint);
                return (candidate, endpoint, "", false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                if (AuthRejection(ex) is { } rejected) return (null, null, rejected, true);
                failure = ex is OperationCanceledException ? $"{endpoint.Display}: timed out" : $"{endpoint.Display}: {Describe(ex)}";
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        return (null, null, Redact(failure), false);
    }

    private async Task<(string Reason, bool AuthFailed, bool HadConnected)> RunOneSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        _transport = transport;
        var reason = "";
        var authFailed = false;
        try
        {
            await RunSessionAsync(transport, endpoint, ct).ConfigureAwait(false);
            reason = transport.CloseReason ?? "connection closed";
        }
        catch (IntegrationAuthException ex)
        {
            reason = ex.Message;
            authFailed = true;
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
        reason = Redact(reason);
        var hadConnected = _sessionConnected;
        if (hadConnected)
        {
            _sessionConnected = false;
            Publish("disconnected", new Dictionary<string, object?> { ["reason"] = reason });
        }
        return (reason, authFailed, hadConnected);
    }

    private string Describe(Exception ex) => Redact(ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    });

    private static string Seconds(TimeSpan delay) => delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";

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
            Log.Error(Name, $"Error while disconnecting: {Redact(ex.Message)}");
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
