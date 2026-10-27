using System.Net;
using Cuebot.Core.Logging;

namespace Cuebot.Rumble.Chat;

/// <summary>Receives what a <see cref="ChatStreamReader"/> reads. Called from the reader's task.</summary>
internal interface IChatStreamListener
{
    /// <summary>The stream connected and delivered its initial state.</summary>
    void OnAttached();

    /// <summary>The connection was lost (a reconnect follows) or the chat ended.</summary>
    void OnDetached(string reason);

    void OnMessage(ChatStreamMessage message);
}

/// <summary>
/// Reads one live stream's chat from Rumble's server-sent-events endpoint (unofficial, see PROTOCOL.md) and
/// reconnects with backoff when the connection drops or goes silent. Every (re)connect starts with an "init" event
/// replaying recent history, so messages are de-duplicated by id; the first init only seeds that set, except for
/// messages newer than the caller's <c>publishSince</c>. HTTP 204 means Rumble has closed the chat for good.
/// </summary>
internal sealed class ChatStreamReader(HttpClient http, RumbleEndpoints endpoints, RumbleTimings timings, TimeProvider time, AppLog log)
{
    private const int RememberedMessageIds = 5000;

    /// <summary>Runs until the chat ends or <paramref name="ct"/> is cancelled. Never throws.</summary>
    public async Task RunAsync(long chatId, DateTimeOffset publishSince, IChatStreamListener listener, CancellationToken ct)
    {
        var session = new Session(publishSince);
        var delay = timings.ChatReconnectDelay;
        try
        {
            while (true)
            {
                string reason;
                try
                {
                    if (await ReadUntilClosedAsync(chatId, session, listener, ct).ConfigureAwait(false))
                    {
                        listener.OnDetached("Rumble closed the chat");
                        return;
                    }
                    reason = "chat connection closed";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    reason = "chat went silent";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or RumbleApiException)
                {
                    reason = ex is RumbleApiException ? ex.Message : $"chat connection failed ({ex.GetType().Name})";
                }

                if (session.Attached)
                {
                    session.Attached = false;
                    delay = timings.ChatReconnectDelay;
                    listener.OnDetached(reason);
                }
                log.Debug("Rumble", $"Chat {reason}; reconnecting in {delay.TotalSeconds:0.#} s");
                await Task.Delay(delay, time, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, timings.ChatMaximumReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Returns true when Rumble says the chat is over; false when the connection simply ended.</summary>
    private async Task<bool> ReadUntilClosedAsync(long chatId, Session session, IChatStreamListener listener, CancellationToken ct)
    {
        using var idle = new CancellationTokenSource(timings.ChatIdleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.ChatStream(chatId));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return true;
        if (!response.IsSuccessStatusCode)
            throw new RumbleApiException($"chat stream answered HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        var parser = new SseParser();
        while (await reader.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
        {
            idle.CancelAfter(timings.ChatIdleTimeout);
            if (parser.Feed(line) is { } e && session.Decoder.Decode(e.Data) is { } batch) Deliver(batch, session, listener);
        }
        return false;
    }

    private static void Deliver(ChatStreamBatch batch, Session session, IChatStreamListener listener)
    {
        var seeding = false;
        if (batch.Type == ChatStreamBatch.Init)
        {
            seeding = !session.Seeded;
            session.Seeded = true;
            if (!session.Attached)
            {
                session.Attached = true;
                listener.OnAttached();
            }
        }

        foreach (var message in batch.Messages)
        {
            var isNew = message.Id.Length == 0 || session.SeenIds.Add(message.Id);
            if (isNew && (!seeding || message.Time >= session.PublishSince)) listener.OnMessage(message);
        }
    }

    /// <summary>State that survives reconnects within one chat.</summary>
    private sealed class Session(DateTimeOffset publishSince)
    {
        public DateTimeOffset PublishSince { get; } = publishSince;
        public ChatStreamDecoder Decoder { get; } = new();
        public RecentSet<string> SeenIds { get; } = new(RememberedMessageIds);
        public bool Seeded { get; set; }
        public bool Attached { get; set; }
    }
}
