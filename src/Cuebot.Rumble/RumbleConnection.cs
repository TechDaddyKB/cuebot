using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.LiveApi;

namespace Cuebot.Rumble;

/// <summary>
/// One connected session: polls the Live Stream API, publishes what is new, and keeps a chat stream reader
/// attached while a stream is live. Chat and rants come from the chat stream while it is attached and from the
/// Live Stream API otherwise; an <see cref="ChatEchoFilter"/> per direction keeps the hand-over from publishing a
/// message twice. Follows, subscriptions and gifts always come from the Live Stream API.
/// </summary>
internal sealed class RumbleConnection : IChatStreamListener, IAsyncDisposable
{
    private readonly Uri _apiUrl;
    private readonly LiveStreamApiClient _api;
    private readonly ChatStreamReader _chatReader;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly RumbleTimings _timings;
    private readonly TimeProvider _time;
    private readonly Func<int> _pollSeconds;
    private readonly Action<ConnectionState, string> _setStatus;
    private readonly ActivityTracker _tracker = new();
    private readonly ChatEchoFilter _streamEchoes = new();
    private readonly ChatEchoFilter _apiEchoes = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _poll = Task.CompletedTask;
    private ChatAttachment? _chat;
    private volatile bool _chatAttached;
    private volatile LiveStreamSnapshot? _snapshot;

    private RumbleConnection(Uri apiUrl, LiveStreamApiClient api, ChatStreamReader chatReader, EventBus bus, AppLog log,
        RumbleTimings timings, TimeProvider time, Func<int> pollSeconds, Action<ConnectionState, string> setStatus)
    {
        _apiUrl = apiUrl;
        _api = api;
        _chatReader = chatReader;
        _bus = bus;
        _log = log;
        _timings = timings;
        _time = time;
        _pollSeconds = pollSeconds;
        _setStatus = setStatus;
    }

    /// <summary>The latest Live Stream API data.</summary>
    public LiveStreamSnapshot Snapshot => _snapshot!;

    public ApiLivestream? LiveStream => _tracker.LiveStream;

    /// <summary>Chat id of the live stream, when there is one.</summary>
    public long? LiveChatId => LiveStream is { } live ? ChatId.FromStreamId(live.Id) : null;

    public bool IsChatAttached => _chatAttached;

    /// <summary>
    /// Fetches the API once (throwing <see cref="RumbleApiException"/> when that fails), records the current state
    /// as history, attaches to the chat if a stream is live, and starts polling.
    /// </summary>
    public static async Task<RumbleConnection> StartAsync(Uri apiUrl, LiveStreamApiClient api, ChatStreamReader chatReader,
        EventBus bus, AppLog log, RumbleTimings timings, TimeProvider time, Func<int> pollSeconds,
        Action<ConnectionState, string> setStatus, CancellationToken ct)
    {
        var snapshot = await api.FetchAsync(apiUrl, ct).ConfigureAwait(false);
        var connection = new RumbleConnection(apiUrl, api, chatReader, bus, log, timings, time, pollSeconds, setStatus);
        connection._snapshot = snapshot;
        connection._tracker.Seed(snapshot);
        // The chat stream replays recent history on connect; for a stream already live, all of it is old.
        if (connection.LiveStream is { } live) connection.AttachChat(live, publishSince: DateTimeOffset.MaxValue);
        connection.ReportStatus();
        connection._poll = connection.PollAsync(connection._stop.Token);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _poll.ConfigureAwait(false);
        await DetachChatAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    void IChatStreamListener.OnAttached()
    {
        _chatAttached = true;
        _log.Info("Rumble", "Live chat connected");
        ReportStatus();
    }

    void IChatStreamListener.OnDetached(string reason)
    {
        _chatAttached = false;
        _log.Info("Rumble", $"Live chat disconnected: {reason}");
        ReportStatus();
    }

    void IChatStreamListener.OnMessage(ChatStreamMessage message)
    {
        if (_apiEchoes.TryConsume(message.UserName, message.Text, message.Time)) return;
        _streamEchoes.Record(message.UserName, message.Text, message.Time);
        _bus.Publish(RumbleEvents.ChatMessage(message, Snapshot.UserName));
        if (message.IsRant) _bus.Publish(RumbleEvents.Rant(message.UserName, message.UserId, message.Text, message.RantCents, message.Id));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var delay = _timings.PollInterval(_pollSeconds());
        var failing = false;
        try
        {
            while (true)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                try
                {
                    var snapshot = await _api.FetchAsync(_apiUrl, ct).ConfigureAwait(false);
                    await ApplyAsync(snapshot).ConfigureAwait(false);
                    delay = _timings.PollInterval(_pollSeconds());
                    if (failing) _log.Info("Rumble", "Live Stream API reachable again");
                    failing = false;
                }
                catch (Exception ex) when (ex is RumbleApiException or HttpRequestException
                                           || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    var message = ex is RumbleApiException ? ex.Message : "The Rumble Live Stream API did not answer in time";
                    delay = ex is RumbleApiException { StatusCode: 429 }
                        ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaximumPollBackoff.Ticks))
                        : _timings.PollInterval(_pollSeconds());
                    if (!failing) _log.Warn("Rumble", $"{message}; retrying");
                    failing = true;
                    _setStatus(ConnectionState.Connecting, $"{message}; retrying...");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Rumble", "Live Stream API polling stopped", ex);
            _setStatus(ConnectionState.Error, $"Rumble polling stopped: {ex.Message}");
        }
    }

    private async Task ApplyAsync(LiveStreamSnapshot snapshot)
    {
        _snapshot = snapshot;
        var changes = _tracker.Update(snapshot);
        var channel = snapshot.DisplayName;

        if (changes.WentOffline is { } ended)
        {
            await DetachChatAsync().ConfigureAwait(false);
            _log.Info("Rumble", "Stream ended");
            _bus.Publish(RumbleEvents.StreamOffline(ended, channel));
        }
        if (changes.WentOnline is { } started)
        {
            _log.Info("Rumble", "Stream went live");
            _bus.Publish(RumbleEvents.StreamOnline(started, channel));
            AttachChat(started, _tracker.ChatSince);
        }

        foreach (var follower in changes.Followers) _bus.Publish(RumbleEvents.Follow(follower));
        foreach (var subscriber in changes.Subscribers) _bus.Publish(RumbleEvents.Subscribe(subscriber));
        foreach (var gift in changes.GiftedSubs) _bus.Publish(RumbleEvents.GiftSub(gift));
        if (!_chatAttached) PublishApiChat(changes, snapshot.UserName);
        ReportStatus();
    }

    /// <summary>Chat and rants from the Live Stream API, used while the chat stream is not attached.</summary>
    private void PublishApiChat(PollChanges changes, string broadcaster)
    {
        foreach (var message in changes.ChatMessages)
        {
            if (_streamEchoes.TryConsume(message.UserName, message.Text, message.CreatedOn)) continue;
            _apiEchoes.Record(message.UserName, message.Text, message.CreatedOn);
            var rant = changes.Rants.FirstOrDefault(r => r.UserName == message.UserName && r.Text == message.Text && r.CreatedOn == message.CreatedOn);
            _bus.Publish(RumbleEvents.ChatMessage(message, rant?.AmountCents ?? 0, broadcaster));
        }
        foreach (var rant in changes.Rants) _bus.Publish(RumbleEvents.Rant(rant.UserName, "", rant.Text, rant.AmountCents));
    }

    private void AttachChat(ApiLivestream stream, DateTimeOffset publishSince)
    {
        if (ChatId.FromStreamId(stream.Id) is not { } chatId)
        {
            _log.Warn("Rumble", "The live stream's id is not in Rumble's usual format; reading chat from the Live Stream API only");
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _chat = new ChatAttachment(cts, _chatReader.RunAsync(chatId, publishSince, this, cts.Token));
    }

    private async Task DetachChatAsync()
    {
        if (_chat is not { } chat) return;
        _chat = null;
        await chat.Cancellation.CancelAsync().ConfigureAwait(false);
        await chat.Run.ConfigureAwait(false);
        chat.Cancellation.Dispose();
        _chatAttached = false;
    }

    private void ReportStatus()
    {
        if (_stop.IsCancellationRequested) return;
        var channel = Snapshot.DisplayName;
        _setStatus(ConnectionState.Connected, LiveStream is { } live
            ? $"{channel} is live: {live.Title} ({(_chatAttached ? "live chat connected" : "chat via Live Stream API")})"
            : $"Connected to {channel} (offline)");
    }

    private sealed record ChatAttachment(CancellationTokenSource Cancellation, Task Run);
}
