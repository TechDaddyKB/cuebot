using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Auth;

namespace Cuebot.YouTube.Chat;

/// <summary>Insertion-ordered bounded set for de-duplication.</summary>
internal sealed class RecentIds(int capacity = 4096)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public int Count => _set.Count;

    /// <summary>Adds the id; false when it was already present.</summary>
    public bool Add(string id)
    {
        if (!_set.Add(id)) return false;
        _order.Enqueue(id);
        while (_order.Count > capacity) _set.Remove(_order.Dequeue());
        return true;
    }
}

/// <summary>Why a chat poller stopped.</summary>
internal enum ChatEndReason
{
    Cancelled,

    /// <summary>The broadcast's chat ended (chatEndedEvent, offlineAt, or liveChatEnded): the broadcast is over.</summary>
    ChatEnded,

    /// <summary>The chat is disabled or cannot be found; the broadcast may still be live.</summary>
    Unavailable,
}

/// <summary>
/// Reads one live chat. With a <paramref name="stream"/> it holds liveChatMessages.streamList open (one quota charge per
/// connection) and reconnects from the last <c>nextPageToken</c> when YouTube closes it; when the stream cannot be
/// opened at all it falls back to polling. Polling uses liveChatMessages.list, honoring YouTube's
/// <c>pollingIntervalMillis</c> (raised to the configured minimum to save quota) and following <c>nextPageToken</c>.
/// Either way items are de-duplicated and history is not replayed: items from before Cuebot connected only seed the
/// de-duplication set. Pauses while the daily quota is exhausted and backs off on errors. Never throws except for
/// cancellation.
/// </summary>
internal sealed class ChatPoller(
    YouTubeApi api,
    AccountTokens account,
    ChatContext context,
    Action<BotEvent> publish,
    YouTubeTimings timings,
    TimeProvider time,
    Func<int> minimumSeconds,
    AppLog log,
    Action<string>? reportProblem = null,
    IChatStream? stream = null)
{
    /// <summary>Failed attempts to open the stream, with no page ever received, before falling back to polling.</summary>
    internal const int StreamAttemptsBeforePolling = 3;

    /// <summary>A stream that YouTube closes sooner than this waits before reconnecting, so it cannot spin on quota.</summary>
    private static readonly TimeSpan ShortStream = TimeSpan.FromSeconds(30);

    private readonly RecentIds _seen = new();
    private string? _pageToken;
    private bool _seeded;

    /// <summary>Number of pages fetched (for tests and diagnostics).</summary>
    public int Polls { get; private set; }

    /// <summary>Number of stream connections opened (for tests and diagnostics).</summary>
    public int StreamConnections { get; private set; }

    /// <summary>True while chat arrives through the stream rather than by polling.</summary>
    public bool IsStreaming { get; private set; }

    public async Task<ChatEndReason> RunAsync(CancellationToken ct)
    {
        try
        {
            if (stream is not null && await StreamAsync(stream, ct).ConfigureAwait(false) is { } reason) return reason;
            return await PollAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ChatEndReason.Cancelled;
        }
        finally
        {
            IsStreaming = false;
        }
    }

    /// <summary>Reads the chat stream until the chat ends; null when the stream never opened and polling should take over.</summary>
    private async Task<ChatEndReason?> StreamAsync(IChatStream source, CancellationToken ct)
    {
        var failures = 0;
        var received = false;
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            await api.Quota.WaitWhileExhaustedAsync(ct).ConfigureAwait(false);
            var openedAt = time.GetUtcNow();
            // Without a token YouTube starts with recent history; anything published before this connection is history.
            DateTimeOffset? historyBefore = string.IsNullOrEmpty(_pageToken) ? openedAt : null;
            try
            {
                StreamConnections++;
                await foreach (var page in source.ReadAsync(account, context.LiveChatId, _pageToken, ct).ConfigureAwait(false))
                {
                    if (!received) log.Info("YouTube", $"Live chat of broadcast {context.BroadcastId} is streaming");
                    received = IsStreaming = true;
                    failures = 0;
                    backoff = TimeSpan.Zero;
                    var ended = Process(page, _seeded, historyBefore);
                    _seeded = true;
                    if (page.NextPageToken.Length > 0) _pageToken = page.NextPageToken;
                    if (ended || page.OfflineAt is not null) return ChatEndReason.ChatEnded;
                }
                // YouTube closed the stream: reconnect from the last token, after a pause if it closed quickly.
                if (time.GetUtcNow() - openedAt < ShortStream)
                    await Task.Delay(timings.ChatDelay(TimeSpan.Zero, minimumSeconds()), time, ct).ConfigureAwait(false);
            }
            catch (YouTubeApiException ex) when (ex.IsChatGone)
            {
                log.Info("YouTube", $"Live chat of broadcast {context.BroadcastId} is no longer available ({ex.Reason})");
                return ex.Reason == "liveChatEnded" ? ChatEndReason.ChatEnded : ChatEndReason.Unavailable;
            }
            catch (YouTubeApiException ex) when (ex.IsQuotaExceeded)
            {
                reportProblem?.Invoke(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                IsStreaming = false;
                if (!received && ++failures >= StreamAttemptsBeforePolling)
                {
                    log.Warn("YouTube", $"Live chat stream unavailable, polling instead: {ex.Message}");
                    return null;
                }
                backoff = timings.NextBackoff(backoff);
                log.Warn("YouTube", $"Live chat stream failed, reconnecting in {backoff.TotalSeconds:0}s: {ex.Message}");
                reportProblem?.Invoke(ex.Message);
                await Task.Delay(backoff, time, ct).ConfigureAwait(false);
            }
        }
        return ChatEndReason.Cancelled;
    }

    private async Task<ChatEndReason> PollAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            await api.Quota.WaitWhileExhaustedAsync(ct).ConfigureAwait(false);
            ChatPage page;
            try
            {
                page = await api.ListChatMessagesAsync(account, context.LiveChatId, _pageToken, ct).ConfigureAwait(false);
                Polls++;
                backoff = TimeSpan.Zero;
            }
            catch (YouTubeApiException ex) when (ex.IsChatGone)
            {
                log.Info("YouTube", $"Live chat of broadcast {context.BroadcastId} is no longer available ({ex.Reason})");
                return ex.Reason == "liveChatEnded" ? ChatEndReason.ChatEnded : ChatEndReason.Unavailable;
            }
            catch (YouTubeApiException ex) when (ex.IsQuotaExceeded)
            {
                reportProblem?.Invoke(ex.Message);
                continue;   // the tracker is now exhausted; the next iteration waits for the reset
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                backoff = timings.NextBackoff(backoff);
                log.Warn("YouTube", $"Reading live chat failed, retrying in {backoff.TotalSeconds:0}s: {ex.Message}");
                reportProblem?.Invoke(ex.Message);
                await Task.Delay(backoff, time, ct).ConfigureAwait(false);
                continue;
            }

            var ended = Process(page, _seeded);
            _seeded = true;
            if (page.NextPageToken.Length > 0) _pageToken = page.NextPageToken;
            if (ended || page.OfflineAt is not null) return ChatEndReason.ChatEnded;

            await Task.Delay(timings.ChatDelay(page.PollingInterval, minimumSeconds()), time, ct).ConfigureAwait(false);
        }
        return ChatEndReason.Cancelled;
    }

    /// <summary>
    /// Publishes new items; returns true when the chat ended. Items of the seeding page, and items published before
    /// <paramref name="historyBefore"/>, are history: remembered but not published.
    /// </summary>
    internal bool Process(ChatPage page, bool seeded, DateTimeOffset? historyBefore = null)
    {
        var ended = false;
        foreach (var item in page.Items)
        {
            if (!_seen.Add(ChatItemMapper.DedupeKey(item))) continue;
            if (!seeded || (historyBefore is { } cutoff && item.Prop("snippet").Time("publishedAt") is { } published && published < cutoff))
            {
                // History from before Cuebot connected: remember authors and polls, but trigger nothing.
                var snippet = item.Prop("snippet");
                if (snippet.Str("type") == "pollEvent")
                    context.FirstSightOfPoll(snippet.At("pollDetails").Str("id") is { Length: > 0 } id ? id : item.Str("id"));
                if (item.Prop("authorDetails") is var a && a.Str("channelId").Length > 0)
                    context.Chatters.Remember(a.Str("channelId"), a.Str("displayName"));
                continue;
            }
            ChatItemResult result;
            try
            {
                result = ChatItemMapper.Map(item, context);
            }
            catch (Exception ex)
            {
                log.Error("YouTube", "Could not read a live chat item", ex);
                continue;
            }
            foreach (var e in result.Events) publish(e);
            ended |= result.ChatEnded;
        }
        return ended;
    }
}
