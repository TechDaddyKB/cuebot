using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Compat;

namespace Cuebot.YouTube.Monitoring;

/// <summary>
/// One running connection: polls for active (and optionally upcoming) broadcasts, reads the chat of each monitored
/// broadcast, refreshes live statistics, and polls recent subscribers. Every loop catches its own errors, backs off,
/// and pauses while the daily quota is exhausted.
/// </summary>
internal sealed class YouTubeConnection : IAsyncDisposable
{
    private readonly YouTubeApi _api;
    private readonly AccountTokens _broadcaster;
    private readonly Func<YouTubeSettings> _settings;
    private readonly ChatterDirectory _chatters;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly YouTubeTimings _timings;
    private readonly TimeProvider _time;
    private readonly Action _statusChanged;
    private readonly IChatStream? _chatStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, RunningChat> _chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BroadcastStatistics> _statistics = new(StringComparer.Ordinal);
    private Task _run = Task.CompletedTask;
    private int _disposed;

    public YouTubeConnection(YouTubeApi api, AccountTokens broadcaster, Func<YouTubeSettings> settings, ChatterDirectory chatters,
        EventBus bus, AppLog log, YouTubeTimings timings, TimeProvider time, Action statusChanged, IChatStream? chatStream = null)
    {
        _api = api;
        _broadcaster = broadcaster;
        _settings = settings;
        _chatters = chatters;
        _bus = bus;
        _log = log;
        _timings = timings;
        _time = time;
        _statusChanged = statusChanged;
        _chatStream = chatStream;
    }

    public BroadcastTracker Broadcasts { get; } = new();

    /// <summary>True while at least one live chat arrives through the chat stream rather than by polling.</summary>
    public bool IsChatStreaming => _chats.Values.Any(c => c.Poller.IsStreaming);

    /// <summary>Publishes an event with Streamer.bot's argument names added (broadcaster and broadcast included).</summary>
    private void Publish(BotEvent e)
    {
        var broadcast = e.Args.TryGetValue("broadcastId", out var id) && id is string { Length: > 0 } bid ? Broadcasts.Find(bid) : null;
        _bus.Publish(StreamerBotVariables.Apply(e, _broadcaster.Account, broadcast));
    }

    /// <summary>Last problem of any loop, cleared by the next successful broadcast check.</summary>
    public string? Problem { get; private set; }

    /// <summary>True once the first broadcast check has completed.</summary>
    public bool HasChecked { get; private set; }

    public BroadcastStatistics? StatisticsOf(string broadcastId) => _statistics.GetValueOrDefault(broadcastId);

    public bool IsReadingChat(string broadcastId) => _chats.ContainsKey(broadcastId);

    public void Start()
    {
        var ct = _cts.Token;
        _run = Task.WhenAll(
            Task.Run(() => LoopAsync(BroadcastPassAsync, () => YouTubeTimings.Clamp(_settings().BroadcastPollSeconds, _timings.MinimumBroadcastPoll), ct), ct),
            Task.Run(() => LoopAsync(LivePassAsync, () => YouTubeTimings.Clamp(_settings().LivePollSeconds, _timings.MinimumLivePoll), ct, initialDelay: true), ct),
            Task.Run(() => LoopAsync(SubscriberPassAsync, () => YouTubeTimings.Clamp(_settings().SubscriberPollSeconds, _timings.MinimumSubscriberPoll), ct), ct));
    }

    /// <summary>Runs a pass forever at an interval, backing off on errors and waiting out quota exhaustion.</summary>
    private async Task LoopAsync(Func<CancellationToken, Task> pass, Func<TimeSpan> interval, CancellationToken ct, bool initialDelay = false)
    {
        var backoff = TimeSpan.Zero;
        try
        {
            if (initialDelay) await Task.Delay(interval(), _time, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await _api.Quota.WaitWhileExhaustedAsync(ct).ConfigureAwait(false);
                TimeSpan wait;
                try
                {
                    await pass(ct).ConfigureAwait(false);
                    backoff = TimeSpan.Zero;
                    wait = interval();
                }
                catch (YouTubeApiException ex) when (ex.IsQuotaExceeded)
                {
                    SetProblem(ex.Message);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    backoff = _timings.NextBackoff(backoff);
                    wait = backoff > interval() ? backoff : interval();
                    _log.Warn("YouTube", ex.Message);
                    SetProblem(ex.Message);
                }
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task BroadcastPassAsync(CancellationToken ct)
    {
        var active = await _api.ListBroadcastsAsync(_broadcaster, "active", ct).ConfigureAwait(false);
        Handle(Broadcasts.UpdateActive(active));
        if (_settings().MonitorUpcoming)
        {
            var upcoming = await _api.ListBroadcastsAsync(_broadcaster, "upcoming", ct).ConfigureAwait(false);
            Handle(Broadcasts.UpdateUpcoming(upcoming));
        }
        SetProblem(null);
        if (!HasChecked)
        {
            HasChecked = true;
            _statusChanged();
        }
    }

    private async Task LivePassAsync(CancellationToken ct)
    {
        var monitored = Broadcasts.Monitored;
        if (monitored.Count == 0) return;
        var videos = await _api.GetVideosAsync(_broadcaster, monitored.Select(b => b.Id).ToArray(), ct).ConfigureAwait(false);
        foreach (var video in videos)
        {
            var current = Broadcasts.Find(video.Id);
            Handle(Broadcasts.ApplyVideo(video));
            if (current is null || video.ActualEnd is not null) continue;
            var previous = _statistics.GetValueOrDefault(video.Id);
            _statistics[video.Id] = video.Statistics;
            if (previous != video.Statistics)
                Publish(YouTubeEvents.Statistics(Broadcasts.Find(video.Id) ?? current, video.Statistics, _broadcaster.Account));
        }
        _statusChanged();
    }

    private readonly RecentIds _subscribers = new();
    private bool _subscribersSeeded;

    private async Task SubscriberPassAsync(CancellationToken ct)
    {
        if (!_settings().PollNewSubscribers) return;
        var recent = await _api.ListRecentSubscribersAsync(_broadcaster, ct).ConfigureAwait(false);
        // Newest first from the API; publish oldest first.
        var fresh = recent.Where(s => _subscribers.Add(s.ChannelId.Length > 0 ? s.ChannelId : s.SubscriptionId)).Reverse().ToArray();
        if (_subscribersSeeded)
            foreach (var s in fresh) Publish(YouTubeEvents.NewSubscriber(s));
        _subscribersSeeded = true;
    }

    private void Handle(IReadOnlyList<BroadcastChange> changes)
    {
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case BroadcastChangeKind.MonitoringStarted:
                    _log.Info("YouTube", $"Monitoring broadcast \"{change.Broadcast.Title}\" ({change.Broadcast.Id})");
                    StartChat(change.Broadcast);
                    break;
                case BroadcastChangeKind.MonitoringEnded:
                    _log.Info("YouTube", $"Stopped monitoring broadcast {change.Broadcast.Id}");
                    StopChat(change.Broadcast.Id);
                    _statistics.TryRemove(change.Broadcast.Id, out _);
                    break;
            }
            Publish(YouTubeEvents.Broadcast(change, _broadcaster.Account));
        }
        if (changes.Count > 0) _statusChanged();
    }

    private void StartChat(YouTubeBroadcast broadcast)
    {
        if (broadcast.LiveChatId.Length == 0)
        {
            _log.Warn("YouTube", $"Broadcast {broadcast.Id} has no live chat");
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var context = new ChatContext(broadcast.Id, broadcast.LiveChatId, _chatters) { BotChannelId = _settings().Bot.ChannelId };
        var poller = new ChatPoller(_api, _broadcaster, context, Publish, _timings, _time, () => _settings().MinimumChatPollSeconds,
            _log, SetProblem, _settings().StreamChat ? _chatStream : null);
        var task = Task.Run(async () =>
        {
            try
            {
                var reason = await poller.RunAsync(cts.Token).ConfigureAwait(false);
                _chats.TryRemove(broadcast.Id, out _);
                if (reason == ChatEndReason.ChatEnded) Handle(Broadcasts.End(broadcast.Id));
                _statusChanged();
            }
            catch (Exception ex)
            {
                _log.Error("YouTube", "Live chat reader stopped unexpectedly", ex);
            }
        });
        _chats[broadcast.Id] = new RunningChat(cts, task, poller);
    }

    private void StopChat(string broadcastId)
    {
        if (_chats.TryRemove(broadcastId, out var chat)) chat.Cancellation.Cancel();
    }

    private void SetProblem(string? problem)
    {
        if (Problem == problem) return;
        Problem = problem;
        _statusChanged();
    }

    /// <summary>Stops every loop and chat; monitored broadcasts get MonitoringEnded. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        var chats = _chats.Values.ToArray();
        try
        {
            await Task.WhenAll(chats.Select(c => c.Run).Append(_run)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("YouTube", "Error while disconnecting", ex);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var change in Broadcasts.StopMonitoring()) Publish(YouTubeEvents.Broadcast(change, _broadcaster.Account));
        foreach (var chat in chats) chat.Cancellation.Dispose();
        _cts.Dispose();
    }

    private sealed record RunningChat(CancellationTokenSource Cancellation, Task Run, ChatPoller Poller);
}
