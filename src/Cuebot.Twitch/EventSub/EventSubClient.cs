using Cuebot.Core.Logging;

namespace Cuebot.Twitch.EventSub;

/// <summary>
/// Keeps an EventSub WebSocket session alive: runs sessions back to back, reconnecting fresh with exponential
/// backoff whenever one is lost, until cancelled.
/// </summary>
internal sealed class EventSubClient(Uri url, IEventSubListener listener, AppLog log, EventSubTimeouts? timeouts = null)
{
    private readonly EventSubTimeouts _timeouts = timeouts ?? EventSubTimeouts.Default;
    private readonly MessageDeduplicator _deduplicator = new();

    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            string reason;
            var session = new EventSubSession(listener, _deduplicator, _timeouts, log);
            session.Started += () => failures = 0;
            try
            {
                reason = await session.RunAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            var delay = _timeouts.RetryDelay(failures++);
            log.Warn("Twitch", $"EventSub connection lost ({reason}); reconnecting in {delay.TotalSeconds:0.#}s");
            listener.OnConnectionLost(reason, delay);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
