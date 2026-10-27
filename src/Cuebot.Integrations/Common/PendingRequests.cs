using System.Text.Json;

namespace Cuebot.Integrations.Common;

/// <summary>
/// Requests waiting for their response. Responses are matched by id; when a service answers without an id (Voicemod
/// sometimes sends a null actionID) the oldest pending request expecting that response kind is completed instead.
/// </summary>
internal sealed class PendingRequests
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    private sealed record Entry(string Id, string? Kind, TaskCompletionSource<JsonElement> Completion);

    public Task<JsonElement> Add(string id, string? kind = null)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _entries.Add(new Entry(id, kind, tcs));
        return tcs.Task;
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Completes the request with <paramref name="id"/>, or else the oldest one expecting <paramref name="kind"/>.</summary>
    public bool Complete(string? id, string? kind, JsonElement value) => Resolve(id, kind, e => e.Completion.TrySetResult(value.Clone()));

    public bool Fail(string? id, string? kind, Exception error) => Resolve(id, kind, e => e.Completion.TrySetException(error));

    public void Remove(string id)
    {
        lock (_gate) _entries.RemoveAll(e => e.Id == id);
    }

    public void FailAll(Exception error)
    {
        Entry[] all;
        lock (_gate)
        {
            all = _entries.ToArray();
            _entries.Clear();
        }
        foreach (var e in all) e.Completion.TrySetException(error);
    }

    private bool Resolve(string? id, string? kind, Action<Entry> complete)
    {
        Entry? match = null;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(id)) match = _entries.FirstOrDefault(e => e.Id == id);
            if (match is null && kind is not null)
                match = _entries.FirstOrDefault(e => e.Kind is not null && string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (match is not null) _entries.Remove(match);
        }
        if (match is null) return false;
        complete(match);
        return true;
    }
}
