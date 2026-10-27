using System.Collections.Concurrent;
using Cuebot.Core.Events;

namespace Cuebot.Obs.Tests.Fakes;

/// <summary>Records events published on a bus and lets tests await a specific one.</summary>
internal sealed class BusRecorder : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private readonly IDisposable _subscription;
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BusRecorder(EventBus bus) => _subscription = bus.Subscribe(e =>
    {
        _events.Enqueue(e);
        Interlocked.Exchange(ref _published, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    });

    public IReadOnlyList<BotEvent> Events => _events.ToArray();

    /// <summary>Waits for the <paramref name="occurrence"/>-th event of the given type (1-based).</summary>
    public async Task<BotEvent> WaitForAsync(string type, int occurrence = 1)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var published = _published.Task;
            var matches = _events.Where(e => e.Type == type).ToArray();
            if (matches.Length >= occurrence) return matches[occurrence - 1];
            await published.WaitAsync(timeout.Token);
        }
    }

    public void Dispose() => _subscription.Dispose();
}
