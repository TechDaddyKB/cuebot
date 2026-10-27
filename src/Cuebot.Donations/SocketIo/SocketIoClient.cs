using System.Diagnostics;
using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.SocketIo;

/// <summary>How one Socket.IO session ended.</summary>
/// <param name="Opened">The Engine.IO handshake arrived (so the protocol version was accepted).</param>
/// <param name="Fatal">The server refused us (CONNECT_ERROR, e.g. a bad token); retried only at the maximum delay.</param>
internal sealed record SocketIoSessionEnd(string Reason, bool Opened, bool Fatal = false);

/// <summary>
/// A minimal Socket.IO client over a websocket (no long-polling): Engine.IO v4 (Socket.IO v3/v4) and, as a
/// fallback, Engine.IO v3 (Socket.IO v2). Handles the open packet, namespace connect, heartbeats in both
/// directions (v4: the server pings and we answer; v3: we ping every pingInterval), EVENT packets, and
/// reconnects with exponential backoff. When a version is refused before the handshake, the other one is tried.
/// </summary>
internal sealed class SocketIoClient(Func<IWebSocketTransport> transports, DonationsTimings timings, AppLog log, string source)
{
    public event Action<int>? Connected;
    public event Action<string>? Disconnected;
    public event Action<string, IReadOnlyList<JsonElement>>? EventReceived;

    /// <summary>Called once the namespace is connected; use the sender to emit (e.g. an "authenticate" event).</summary>
    public Func<Func<string, Task>, CancellationToken, Task>? OnConnectedAsync { get; set; }

    /// <summary>Version tried first; updated to the one that worked.</summary>
    public int EngineIoVersion { get; set; } = 4;

    /// <summary>Removes secrets from reasons before they are logged or shown.</summary>
    public Func<string, string> Redact { get; set; } = s => s;

    /// <summary>Keeps a session open until <paramref name="ct"/> is cancelled. Never throws except for cancellation.</summary>
    public async Task RunAsync(Func<int, Uri> url, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        var flipped = false;
        while (!ct.IsCancellationRequested)
        {
            SocketIoSessionEnd end;
            try
            {
                end = await RunSessionAsync(url(EngineIoVersion), EngineIoVersion, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                end = new SocketIoSessionEnd(ex.Message, false);
            }

            var reason = Redact(end.Reason);
            Disconnected?.Invoke(reason);
            TimeSpan wait;
            if (end.Opened)
            {
                delay = timings.ReconnectDelay;
                flipped = false;
                wait = end.Fatal ? timings.MaxReconnectDelay : delay;
            }
            else if (!flipped)
            {
                // The server may only speak the other protocol version: try it right away, once.
                EngineIoVersion = EngineIoVersion == 4 ? 3 : 4;
                flipped = true;
                wait = TimeSpan.Zero;
            }
            else
            {
                flipped = false;
                wait = delay;
                delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
            }
            if (end.Fatal) log.Error(source, $"Connection refused: {reason}");
            else log.Warn(source, $"Connection lost ({reason}); reconnecting in {wait.TotalSeconds:0.#}s");
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection, from connect to loss.</summary>
    internal async Task<SocketIoSessionEnd> RunSessionAsync(Uri url, int eio, CancellationToken ct)
    {
        await using var transport = transports();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(timings.ConnectTimeout);
            try
            {
                await transport.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SocketIoSessionEnd("connect timed out", false);
            }
        }

        await using var inbox = new TransportInbox(transport, ct);
        var first = await inbox.ReadAsync(timings.OpenTimeout, ct).ConfigureAwait(false);
        if (first.TimedOut) return new SocketIoSessionEnd("no Engine.IO handshake", false);
        if (first.Text is null) return new SocketIoSessionEnd(transport.CloseReason ?? "connection closed", false);
        if (SocketIoFraming.ParseOpen(first.Text) is not { } open)
            return new SocketIoSessionEnd("unexpected first message", false);

        Task Send(string text) => transport.SendAsync(text, ct);
        var clock = Stopwatch.StartNew();
        var awaitingPong = false;
        var nextPing = open.PingInterval;
        var pongDeadline = TimeSpan.Zero;
        var namespaceConnected = false;

        if (eio >= 4)
        {
            await Send(SocketIoFraming.Connect()).ConfigureAwait(false);
        }
        else
        {
            // Socket.IO v2 servers connect the default namespace on their own (and send "40").
            namespaceConnected = true;
            await OnNamespaceConnectedAsync(Send, eio, ct).ConfigureAwait(false);
        }

        while (true)
        {
            TimeSpan timeout;
            if (!namespaceConnected) timeout = timings.OpenTimeout - clock.Elapsed;
            else if (eio >= 4) timeout = open.PingInterval + open.PingTimeout;
            else timeout = (awaitingPong ? pongDeadline : nextPing) - clock.Elapsed;

            var next = await inbox.ReadAsync(timeout, ct).ConfigureAwait(false);
            if (next.TimedOut)
            {
                if (!namespaceConnected) return new SocketIoSessionEnd("no namespace connect from the server", true);
                if (eio >= 4) return new SocketIoSessionEnd("no ping from the server", true);
                if (awaitingPong) return new SocketIoSessionEnd("no pong from the server", true);
                await Send(SocketIoFraming.Ping).ConfigureAwait(false);
                awaitingPong = true;
                pongDeadline = clock.Elapsed + open.PingTimeout;
                nextPing = clock.Elapsed + open.PingInterval;
                continue;
            }
            if (next.Text is null) return new SocketIoSessionEnd(transport.CloseReason ?? "connection closed", true);
            var frame = next.Text;
            switch (SocketIoFraming.TypeOf(frame))
            {
                case EngineType.Ping:
                    await Send(SocketIoFraming.Pong + frame[1..]).ConfigureAwait(false);
                    break;
                case EngineType.Pong:
                    awaitingPong = false;
                    break;
                case EngineType.Close:
                    return new SocketIoSessionEnd("server closed the session", true);
                case EngineType.Message:
                    if (SocketIoFraming.ParsePacket(frame) is not { } packet || packet.Namespace != "/") break;
                    switch (packet.Type)
                    {
                        case SocketIoType.Connect when !namespaceConnected:
                            namespaceConnected = true;
                            await OnNamespaceConnectedAsync(Send, eio, ct).ConfigureAwait(false);
                            break;
                        case SocketIoType.Disconnect:
                            return new SocketIoSessionEnd("server disconnected the socket", true);
                        case SocketIoType.ConnectError:
                            return new SocketIoSessionEnd($"server refused the connection: {packet.ErrorMessage()}", true, Fatal: true);
                        case SocketIoType.Event or SocketIoType.BinaryEvent:
                            if (packet.ReadEvent() is { } ev) Raise(ev.Name, ev.Args);
                            break;
                    }
                    break;
            }
        }
    }

    private async Task OnNamespaceConnectedAsync(Func<string, Task> send, int eio, CancellationToken ct)
    {
        EngineIoVersion = eio;
        Connected?.Invoke(eio);
        if (OnConnectedAsync is { } hook) await hook(send, ct).ConfigureAwait(false);
    }

    private void Raise(string name, IReadOnlyList<JsonElement> args)
    {
        try
        {
            EventReceived?.Invoke(name, args);
        }
        catch (Exception ex)
        {
            log.Error(source, $"Error handling '{name}'", ex);
        }
    }
}
