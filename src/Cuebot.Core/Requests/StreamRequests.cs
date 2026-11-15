using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Requests;

/// <summary>
/// Something Cuebot wants to do on stream that a person has to allow first, e.g. "Switch the category to Helldivers 2
/// on Twitch and Kick?". <see cref="Remember"/> is the label of an extra "always" answer ("" when there is none).
/// </summary>
public sealed record StreamRequest(
    Guid Id,
    string Kind,
    string Title,
    string Detail,
    DateTimeOffset At,
    DateTimeOffset Expires,
    string AcceptLabel = "Allow",
    string Remember = "");

public enum RequestAnswer { Accept, AcceptAndRemember, Decline }

/// <summary>What happened to an answer: done (with what the action said), declined, already answered, or failed.</summary>
public sealed record RequestOutcome(string Result, string Message)
{
    public const string Done = "done", Declined = "declined", NotPending = "notPending", Failed = "failed";
}

/// <summary>
/// Requests waiting for a yes from the streamer or a mod. The desktop, the mod portal and the companion app all show
/// the same list; the first answer wins and the request leaves every list. An unanswered request expires (nothing
/// happens). Publishes <see cref="AddedEvent"/> and <see cref="DecidedEvent"/>. Thread-safe.
/// </summary>
public sealed class StreamRequests
{
    public const string AddedEvent = "requests.added";
    public const string DecidedEvent = "requests.decided";

    private sealed record Entry(StreamRequest Request, Func<bool, CancellationToken, Task<string>> OnAccept, Action? OnDecline);

    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly List<Entry> _pending = [];
    private readonly object _gate = new();

    public StreamRequests(EventBus bus, AppLog log, TimeProvider? time = null)
    {
        _bus = bus;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a request is added, answered, withdrawn or expired.</summary>
    public event Action? Changed;

    /// <summary>Raised once per new request (for notifications).</summary>
    public event Action<StreamRequest>? Added;

    /// <summary>The requests waiting, oldest first.</summary>
    public IReadOnlyList<StreamRequest> Pending
    {
        get
        {
            Expire();
            lock (_gate) return [.. _pending.Select(e => e.Request)];
        }
    }

    public StreamRequest? Find(Guid id)
    {
        lock (_gate) return _pending.FirstOrDefault(e => e.Request.Id == id)?.Request;
    }

    /// <summary>
    /// Adds a request. <paramref name="onAccept"/> does the thing (its argument: the "always" answer was given) and
    /// returns what to tell people; it may throw to report a failure. Earlier requests of the same kind are replaced.
    /// </summary>
    public StreamRequest Add(string kind, string title, string detail, TimeSpan lifetime,
        Func<bool, CancellationToken, Task<string>> onAccept, Action? onDecline = null, string acceptLabel = "Allow", string remember = "")
    {
        var now = _time.GetUtcNow();
        var request = new StreamRequest(Guid.NewGuid(), kind, title, detail, now, now + lifetime, acceptLabel, remember);
        lock (_gate)
        {
            _pending.RemoveAll(e => e.Request.Kind == kind);
            _pending.Add(new Entry(request, onAccept, onDecline));
        }
        _log.Info("Requests", $"Waiting for a yes: {title}");
        _bus.Publish(new BotEvent(AddedEvent, Args(request)));
        Added?.Invoke(request);
        Changed?.Invoke();
        return request;
    }

    /// <summary>Takes back the waiting requests of a kind (the reason went away). True when one was waiting.</summary>
    public bool Withdraw(string kind)
    {
        int removed;
        lock (_gate) removed = _pending.RemoveAll(e => e.Request.Kind == kind);
        if (removed > 0) Changed?.Invoke();
        return removed > 0;
    }

    /// <summary>Answers a request. <paramref name="by"/> names who answered, for the log.</summary>
    public async Task<RequestOutcome> AnswerAsync(Guid id, RequestAnswer answer, string by, CancellationToken ct = default)
    {
        Expire();
        Entry? entry;
        lock (_gate)
        {
            entry = _pending.FirstOrDefault(e => e.Request.Id == id);
            if (entry is not null) _pending.Remove(entry);
        }
        if (entry is null) return new RequestOutcome(RequestOutcome.NotPending, "That was already answered or has expired.");
        Changed?.Invoke();
        var request = entry.Request;
        RequestOutcome outcome;
        if (answer == RequestAnswer.Decline)
        {
            entry.OnDecline?.Invoke();
            outcome = new RequestOutcome(RequestOutcome.Declined, $"Not done: {request.Title}");
        }
        else
        {
            try
            {
                var message = await entry.OnAccept(answer == RequestAnswer.AcceptAndRemember, ct).ConfigureAwait(false);
                outcome = new RequestOutcome(RequestOutcome.Done, message);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.Warn("Requests", $"{request.Title} failed: {e.Message}");
                outcome = new RequestOutcome(RequestOutcome.Failed, "That did not work: " + e.Message);
            }
        }
        _log.Info("Requests", $"{by} answered \"{request.Title}\": {outcome.Message}");
        var args = Args(request);
        args["answer"] = answer.ToString();
        args["by"] = by;
        args["result"] = outcome.Result;
        args["message"] = outcome.Message;
        _bus.Publish(new BotEvent(DecidedEvent, args));
        return outcome;
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        List<Entry> expired;
        lock (_gate)
        {
            expired = _pending.Where(e => e.Request.Expires <= now).ToList();
            foreach (var e in expired) _pending.Remove(e);
        }
        if (expired.Count == 0) return;
        foreach (var e in expired) _log.Info("Requests", $"Nobody answered, so nothing was done: {e.Request.Title}");
        Changed?.Invoke();
    }

    /// <summary>Checks for expired requests (the timer calls this).</summary>
    public void Tick() => Expire();

    private static Dictionary<string, object?> Args(StreamRequest r) => new()
    {
        ["requestId"] = r.Id.ToString(),
        ["kind"] = r.Kind,
        ["title"] = r.Title,
        ["detail"] = r.Detail,
    };
}
