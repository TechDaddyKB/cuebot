using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core.Logging;

namespace Cuebot.Kick.Pusher;

/// <summary>How a Pusher session ended and how soon to reconnect (Pusher protocol close/error code ranges).</summary>
internal sealed record PusherSessionEnd(string Reason, PusherReconnect Reconnect);

internal enum PusherReconnect
{
    /// <summary>Reconnect after the usual backoff (4100-4199, network errors, timeouts).</summary>
    Backoff,

    /// <summary>Reconnect right away (4200-4299: "reconnect immediately").</summary>
    Immediately,

    /// <summary>4000-4099: the server says not to reconnect with the same settings; retried only at the maximum delay.</summary>
    Fatal,
}

/// <summary>
/// A minimal Pusher protocol 7 client for public channels: connects, waits for
/// <c>pusher:connection_established</c>, subscribes, answers <c>pusher:ping</c>, sends its own ping after
/// <c>activity_timeout</c> of silence and reconnects (with backoff) when the pong does not come back or the
/// connection drops. Channel events are raised through <see cref="Message"/>.
/// </summary>
internal sealed class PusherClient(Func<IPusherTransport> transports, Uri url, KickTimings timings, AppLog log)
{
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? SubscriptionSucceeded;
    public event Action<PusherFrame>? Message;

    /// <summary>Keeps a connection open until <paramref name="ct"/> is cancelled. Never throws except for cancellation.</summary>
    public async Task RunAsync(IReadOnlyList<string> channels, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            PusherSessionEnd end;
            var established = false;
            try
            {
                end = await RunSessionAsync(channels, () => established = true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                end = new PusherSessionEnd(ex.Message, PusherReconnect.Backoff);
            }

            if (established) delay = timings.ReconnectDelay;
            Disconnected?.Invoke(end.Reason);
            var wait = end.Reconnect switch
            {
                PusherReconnect.Immediately => TimeSpan.Zero,
                PusherReconnect.Fatal => timings.MaxReconnectDelay,
                _ => delay,
            };
            if (end.Reconnect == PusherReconnect.Fatal) log.Error("Kick", $"Realtime connection refused: {end.Reason}");
            else log.Warn("Kick", $"Realtime connection lost ({end.Reason}); reconnecting in {wait.TotalSeconds:0.#}s");
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (end.Reconnect == PusherReconnect.Backoff)
                delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>One connection from connect to loss.</summary>
    internal async Task<PusherSessionEnd> RunSessionAsync(IReadOnlyList<string> channels, Action onEstablished, CancellationToken ct)
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
                return new PusherSessionEnd("connect timed out", PusherReconnect.Backoff);
            }
        }

        var inbox = Channel.CreateUnbounded<string?>();
        using var stopPump = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, inbox.Writer, stopPump.Token);
        try
        {
            var first = await ReadAsync(inbox.Reader, timings.EstablishTimeout, ct).ConfigureAwait(false);
            if (first.TimedOut) return new PusherSessionEnd("no connection_established from Pusher", PusherReconnect.Backoff);
            if (first.Text is null) return Closed(transport);
            var hello = TryParse(first.Text);
            if (hello?.Event == PusherFrame.Error) return ErrorEnd(hello);
            if (hello?.Event != PusherFrame.ConnectionEstablished)
                return new PusherSessionEnd($"unexpected first message '{hello?.Event}'", PusherReconnect.Backoff);

            var (socketId, activitySeconds) = hello.ReadEstablished();
            var activity = activitySeconds > 0 ? TimeSpan.FromSeconds(activitySeconds) : timings.MaxActivityTimeout;
            if (activity > timings.MaxActivityTimeout) activity = timings.MaxActivityTimeout;

            foreach (var channel in channels)
                await transport.SendAsync(PusherFrame.SubscribeMessage(channel), ct).ConfigureAwait(false);
            onEstablished();
            Connected?.Invoke(socketId);

            var awaitingPong = false;
            while (true)
            {
                var next = await ReadAsync(inbox.Reader, awaitingPong ? timings.PongTimeout : activity, ct).ConfigureAwait(false);
                if (next.TimedOut)
                {
                    if (awaitingPong) return new PusherSessionEnd("no pong from Pusher", PusherReconnect.Backoff);
                    await transport.SendAsync(PusherFrame.PingMessage, ct).ConfigureAwait(false);
                    awaitingPong = true;
                    continue;
                }
                if (next.Text is null) return Closed(transport);
                awaitingPong = false; // any traffic proves the connection is alive

                if (TryParse(next.Text) is not { } frame) continue;
                switch (frame.Event)
                {
                    case PusherFrame.Ping:
                        await transport.SendAsync(PusherFrame.PongMessage, ct).ConfigureAwait(false);
                        break;
                    case PusherFrame.Pong:
                        break;
                    case PusherFrame.SubscriptionSucceeded:
                        if (frame.Channel is not null) SubscriptionSucceeded?.Invoke(frame.Channel);
                        break;
                    case PusherFrame.SubscriptionError:
                        log.Warn("Kick", $"Could not subscribe to {frame.Channel}: {frame.Data}");
                        break;
                    case PusherFrame.Error:
                        var end = ErrorEnd(frame);
                        if (end.Reconnect != PusherReconnect.Backoff || frame.ReadError().Code != 0) return end;
                        log.Warn("Kick", $"Pusher error: {end.Reason}");
                        break;
                    default:
                        if (!frame.Event.StartsWith("pusher", StringComparison.Ordinal)) Message?.Invoke(frame);
                        break;
                }
            }
        }
        finally
        {
            await stopPump.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The pump reports failures through the inbox; nothing more to do while tearing down.
            }
        }
    }

    private static async Task PumpAsync(IPusherTransport transport, ChannelWriter<string?> inbox, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) inbox.TryWrite(text);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Treated as a closed connection below.
        }
        inbox.TryWrite(null);
    }

    private readonly record struct ReadResult(bool TimedOut, string? Text);

    private static async Task<ReadResult> ReadAsync(ChannelReader<string?> reader, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return new ReadResult(false, await reader.ReadAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReadResult(true, null);
        }
        catch (ChannelClosedException)
        {
            return new ReadResult(false, null);
        }
    }

    private PusherFrame? TryParse(string text)
    {
        try
        {
            return PusherFrame.Parse(text);
        }
        catch (JsonException)
        {
            log.Debug("Kick", "Ignoring a malformed realtime message");
            return null;
        }
    }

    internal static PusherSessionEnd ErrorEnd(PusherFrame frame)
    {
        var (code, message) = frame.ReadError();
        return new PusherSessionEnd($"Pusher error {code}: {message}", Classify(code));
    }

    private static PusherSessionEnd Closed(IPusherTransport transport) =>
        new(transport.CloseReason ?? "connection closed", Classify(transport.CloseCode ?? 0));

    /// <summary>Pusher protocol: 4000-4099 do not reconnect, 4100-4199 back off, 4200-4299 reconnect now.</summary>
    internal static PusherReconnect Classify(int code) => code switch
    {
        >= 4000 and < 4100 => PusherReconnect.Fatal,
        >= 4200 and < 4300 => PusherReconnect.Immediately,
        _ => PusherReconnect.Backoff,
    };
}
