using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.Pally;

/// <summary>How a Pally session ended.</summary>
internal sealed record PallySessionEnd(string Reason, bool Fatal = false);

/// <summary>
/// Client for Pally.gg's event websocket (plain websocket, JSON text messages). Keeps the connection alive by sending
/// the text <c>ping</c> every <see cref="DonationsTimings.KeepAlive"/> (Pally asks for one at least every 60 s) and
/// expecting <c>pong</c>; reconnects with backoff. Pally does not replay events missed while disconnected.
/// </summary>
internal sealed class PallyClient(Func<IWebSocketTransport> transports, DonationsTimings timings, AppLog log)
{
    private IWebSocketTransport? _current;

    public event Action? Connected;
    public event Action<string, bool>? Disconnected;
    public event Action<JsonElement>? MessageReceived;

    /// <summary>Sends a text message on the open connection; false when not connected.</summary>
    public async Task<bool> SendAsync(string text, CancellationToken ct)
    {
        if (Volatile.Read(ref _current) is not { } transport) return false;
        await transport.SendAsync(text, ct).ConfigureAwait(false);
        return true;
    }

    public async Task RunAsync(Uri url, Func<string, string> redact, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            PallySessionEnd end;
            var healthy = false;
            try
            {
                end = await RunSessionAsync(url, () => healthy = true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException ex) when (IsRefusal(ex.Message))
            {
                // Pally answers the upgrade with 400/401/403 when the key (or channel) is wrong.
                end = new PallySessionEnd("Pally.gg rejected the API key", Fatal: true);
            }
            catch (Exception ex)
            {
                end = new PallySessionEnd(redact(ex.Message));
            }
            if (healthy) delay = timings.ReconnectDelay;
            var reason = redact(end.Reason);
            Disconnected?.Invoke(reason, end.Fatal);
            var wait = end.Fatal ? timings.MaxReconnectDelay : delay;
            if (end.Fatal) log.Error("Pally.gg", $"Connection refused: {reason}");
            else log.Warn("Pally.gg", $"Connection lost ({reason}); reconnecting in {wait.TotalSeconds:0.#}s");
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!end.Fatal) delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
        }
    }

    internal static bool IsRefusal(string message) =>
        message.Contains("'400'", StringComparison.Ordinal) || message.Contains("'401'", StringComparison.Ordinal)
        || message.Contains("'403'", StringComparison.Ordinal);

    private async Task<PallySessionEnd> RunSessionAsync(Uri url, Action onHealthy, CancellationToken ct)
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
                return new PallySessionEnd("connect timed out");
            }
        }
        await using var inbox = new TransportInbox(transport, ct);
        Volatile.Write(ref _current, transport);
        try
        {
            onHealthy();
            Connected?.Invoke();
            var awaitingPong = false;
            while (true)
            {
                var next = await inbox.ReadAsync(awaitingPong ? timings.OpenTimeout : timings.KeepAlive, ct).ConfigureAwait(false);
                if (next.TimedOut)
                {
                    if (awaitingPong) return new PallySessionEnd("no pong from Pally.gg");
                    await transport.SendAsync("ping", ct).ConfigureAwait(false);
                    awaitingPong = true;
                    continue;
                }
                if (next.Text is null) return new PallySessionEnd(transport.CloseReason ?? "connection closed");
                awaitingPong = false;   // any message proves the connection is alive
                var text = next.Text.Trim();
                if (text.Equals("pong", StringComparison.OrdinalIgnoreCase) || text.Length == 0) continue;
                if (JsonReaders.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                try
                {
                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    log.Error("Pally.gg", "Error handling a Pally.gg message", ex);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _current, null);
        }
    }
}
