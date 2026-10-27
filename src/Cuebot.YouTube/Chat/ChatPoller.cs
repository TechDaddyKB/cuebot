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
/// Long-polls one live chat with liveChatMessages.list. Honors YouTube's <c>pollingIntervalMillis</c> (raised to the
/// configured minimum to save quota), follows <c>nextPageToken</c>, de-duplicates items, and does not replay
/// history: the first page only seeds the de-duplication set. Pauses while the daily quota is exhausted and backs
/// off on errors. Never throws except for cancellation.
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
    Action<string>? reportProblem = null)
{
    private readonly RecentIds _seen = new();

    /// <summary>Number of pages fetched (for tests and diagnostics).</summary>
    public int Polls { get; private set; }

    public async Task<ChatEndReason> RunAsync(CancellationToken ct)
    {
        string? pageToken = null;
        var seeded = false;
        var backoff = TimeSpan.Zero;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await api.Quota.WaitWhileExhaustedAsync(ct).ConfigureAwait(false);
                ChatPage page;
                try
                {
                    page = await api.ListChatMessagesAsync(account, context.LiveChatId, pageToken, ct).ConfigureAwait(false);
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

                var ended = Process(page, seeded);
                seeded = true;
                if (page.NextPageToken.Length > 0) pageToken = page.NextPageToken;
                if (ended || page.OfflineAt is not null) return ChatEndReason.ChatEnded;

                await Task.Delay(timings.ChatDelay(page.PollingInterval, minimumSeconds()), time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        return ChatEndReason.Cancelled;
    }

    /// <summary>Publishes new items (none on the seeding page); returns true when the chat ended.</summary>
    internal bool Process(ChatPage page, bool seeded)
    {
        var ended = false;
        foreach (var item in page.Items)
        {
            if (!_seen.Add(ChatItemMapper.DedupeKey(item))) continue;
            if (!seeded)
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
