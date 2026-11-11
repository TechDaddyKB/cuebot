using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>
/// A veadotube to send commands to: the connected one (WebSocket, with its live caches) or another open one (one-off
/// GET commands, read fresh each time), so steps can control veadotube and veadotube mini, or two minis, at once.
/// </summary>
internal interface IVeadotubeTarget
{
    string Label { get; }
    Task<VeadotubeNode> NodeAsync(string type, string nameOrId, string what, CancellationToken ct);
    Task<IReadOnlyList<VeadotubeState>> StatesAsync(VeadotubeNode node, CancellationToken ct);
    Task<string?> CurrentStateIdAsync(VeadotubeNode node, CancellationToken ct);
    Task<bool?> SwitchAsync(VeadotubeNode node, CancellationToken ct);
    Task<VeadotubeNumber?> NumberAsync(VeadotubeNode node, CancellationToken ct);
    Task SendAsync(VeadotubeNode node, VeadotubeCommand command, CancellationToken ct);
}

/// <summary>Another veadotube, reached by GET requests to its server (veado.tube/docs/tech/api "using GET requests").</summary>
/// <remarks>
/// veadotube 2.2 answers every command with the header <c>Veadotube-Command: Error</c>, also when it worked, so the
/// header is not trusted; an unknown state or node just does nothing.
/// </remarks>
internal sealed class VeadotubeRemoteTarget(HttpClient http, string server, string label) : IVeadotubeTarget
{
    public string Label => label;

    private async Task<string> GetAsync(CancellationToken ct, params string[] arguments)
    {
        try
        {
            using var response = await http.GetAsync(VeadotubeProtocol.CommandUrl(server, arguments), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        }
        catch (HttpRequestException ex)
        {
            throw new StreamAppRequestException($"Could not reach {label} at {server}: {ex.Message}");
        }
    }

    private static string[] Lines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<VeadotubeNode> NodeAsync(string type, string nameOrId, string what, CancellationToken ct)
    {
        var nodes = Lines(await GetAsync(ct, "nodes").ConfigureAwait(false))
            .Select(l => l.Split(' ', 2)).Where(p => p.Length == 2 && p[0] == type).Select(p => p[1]).ToArray();
        var wanted = nameOrId.Trim();
        if (nodes.Length > 0 && wanted.Length == 0) return new VeadotubeNode(type, nodes[0], "");
        foreach (var id in nodes)
        {
            if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) return new VeadotubeNode(type, id, "");
            var name = await GetAsync(ct, "nodes", type, id).ConfigureAwait(false);
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return new VeadotubeNode(type, id, name);
        }
        throw new StreamAppRequestException(wanted.Length == 0 ? $"{label} has no {what}." : $"{label} has no {what} called '{wanted}'.");
    }

    public async Task<IReadOnlyList<VeadotubeState>> StatesAsync(VeadotubeNode node, CancellationToken ct) =>
        [.. Lines(await GetAsync(ct, "nodes", node.Type, node.Id, "list").ConfigureAwait(false)).Select(id => new VeadotubeState(id, id))];

    public async Task<string?> CurrentStateIdAsync(VeadotubeNode node, CancellationToken ct) =>
        await GetAsync(ct, "nodes", node.Type, node.Id, "peek").ConfigureAwait(false) is { Length: > 0 } id ? id : null;

    public async Task<bool?> SwitchAsync(VeadotubeNode node, CancellationToken ct) =>
        (await GetAsync(ct, "nodes", node.Type, node.Id, "get").ConfigureAwait(false)) switch
        {
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => null,
        };

    public async Task<VeadotubeNumber?> NumberAsync(VeadotubeNode node, CancellationToken ct) =>
        VeadotubeProtocol.NumberText(await GetAsync(ct, "nodes", node.Type, node.Id, "get").ConfigureAwait(false));

    public Task SendAsync(VeadotubeNode node, VeadotubeCommand command, CancellationToken ct) =>
        GetAsync(ct, ["nodes", node.Type, node.Id, .. command.Arguments()]);
}

/// <summary>
/// "For N seconds": one pending undo per target. A second timed change of the same target restarts the timer but
/// keeps the first undo (back to how it was before both); a change without a timer cancels it.
/// </summary>
internal sealed class VeadotubeTimedUndo(Action<string, Exception> onError, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Func<Task> Undo, CancellationTokenSource Timer)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPending(string target)
    {
        lock (_gate) return _pending.ContainsKey(target);
    }

    public void Schedule(string target, TimeSpan after, Func<Task> undo)
    {
        var timer = new CancellationTokenSource();
        lock (_gate)
        {
            if (_pending.Remove(target, out var existing))
            {
                existing.Timer.Cancel();
                undo = existing.Undo;
            }
            _pending[target] = (undo, timer);
        }
        _ = RunAsync(target, after, timer);
    }

    public void Cancel(string target)
    {
        lock (_gate)
            if (_pending.Remove(target, out var existing)) existing.Timer.Cancel();
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var (_, timer) in _pending.Values) timer.Cancel();
            _pending.Clear();
        }
    }

    private async Task RunAsync(string target, TimeSpan after, CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(after, time, timer.Token).ConfigureAwait(false);
            Func<Task>? undo = null;
            lock (_gate)
                if (_pending.TryGetValue(target, out var current) && ReferenceEquals(current.Timer, timer))
                {
                    _pending.Remove(target);
                    undo = current.Undo;
                }
            if (undo is not null) await undo().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced or cancelled.
        }
        catch (Exception ex)
        {
            onError(target, ex);
        }
        finally
        {
            timer.Dispose();
        }
    }
}
