using Cuebot.Core.Logging;

namespace Cuebot.Core;

/// <summary>
/// Automatic connection at startup for a platform service: one attempt right away and, when it fails (no network yet,
/// a brief platform outage), more attempts in the background with a growing delay until one succeeds. A manual
/// Connect or Disconnect, or disposing the service, calls <see cref="Cancel"/> so the user's choice wins.
/// </summary>
public sealed class AutoConnectRetry(string source, AppLog log, TimeProvider time, IReadOnlyList<TimeSpan>? delays = null)
    : IDisposable
{
    /// <summary>5 s, 10 s, 20 s, 40 s, then every 60 s.</summary>
    public static IReadOnlyList<TimeSpan> DefaultDelays { get; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];

    private readonly IReadOnlyList<TimeSpan> _delays = delays is { Count: > 0 } ? delays : DefaultDelays;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    /// <summary>The background retries, while they run (for tests).</summary>
    public Task? Retrying { get; private set; }

    /// <summary>
    /// Tries <paramref name="connect"/> once and returns; on failure keeps retrying in the background. Never throws.
    /// <paramref name="isPermanent"/> stops the retries for errors another attempt cannot fix (for example a missing
    /// sign-in).
    /// </summary>
    public async Task StartAsync(Func<CancellationToken, Task> connect, Func<Exception, bool>? isPermanent = null,
        CancellationToken ct = default)
    {
        var cts = Restart(ct);
        try
        {
            await connect(cts.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (isPermanent?.Invoke(ex) == true)
            {
                log.Error(source, "Automatic connection failed", ex);
                return;
            }
            log.Error(source, $"Automatic connection failed; retrying in {Describe(_delays[0])}", ex);
        }
        lock (_gate)
            if (ReferenceEquals(_cts, cts)) Retrying = RetryAsync(connect, isPermanent, cts.Token);
    }

    /// <summary>Stops any background retries.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose() => Cancel();

    private CancellationTokenSource Restart(CancellationToken ct)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Retrying = null;
            return _cts;
        }
    }

    private async Task RetryAsync(Func<CancellationToken, Task> connect, Func<Exception, bool>? isPermanent, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await Task.Delay(_delays[Math.Min(attempt, _delays.Count - 1)], time, ct).ConfigureAwait(false);
                await connect(ct).ConfigureAwait(false);
                log.Info(source, $"Connected after {attempt + 2} attempts");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (isPermanent?.Invoke(ex) == true)
                {
                    log.Error(source, "Automatic connection stopped", ex);
                    return;
                }
                // Only the first failure is an error; later ones would flood the log during a long outage.
                log.Debug(source, $"Automatic connection attempt {attempt + 2} failed: {ex.Message}");
            }
        }
    }

    private static string Describe(TimeSpan delay) => delay.TotalSeconds < 1 ? $"{delay.TotalMilliseconds:0} ms" : $"{delay.TotalSeconds:0} s";
}
