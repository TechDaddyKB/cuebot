namespace Cuebot.Server.Api;

/// <summary>
/// Counts changes to what <c>GET /api/v1/dashboard</c> returns (the activity feed, stream totals, subathon timers and
/// goals, top spenders, connections) so companion apps fetch it again only when something changed. <see cref="Changed"/>
/// fires at most once per <see cref="Interval"/> with the latest revision ("dashboard.changed" on <c>/api/v1/events</c>).
/// </summary>
public sealed class DashboardChanges
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Timer _timer;
    private long _revision;
    private int _pending;

    public DashboardChanges(ServerServices services)
    {
        _timer = new Timer(_ => Flush());
        if (services.SessionData is { } session) session.Changed += Bump;
        if (services.Chat is { } chat) chat.FeedItemAdded += _ => Bump();
        foreach (var connection in services.Connections) connection.StatusChanged += Bump;
    }

    /// <summary>Goes up by one for every change.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>The revision after a burst of changes; raised on a timer thread.</summary>
    public event Action<long>? Changed;

    private void Bump()
    {
        Interlocked.Increment(ref _revision);
        if (Interlocked.Exchange(ref _pending, 1) == 0) _timer.Change(Interval, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _pending, 0);
        Changed?.Invoke(Revision);
    }
}
