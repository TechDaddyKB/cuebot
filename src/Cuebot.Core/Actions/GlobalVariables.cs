using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Actions;

public enum VariableChangeKind { Created, Updated, Deleted }

/// <summary>A global variable as shown in the viewer. <see cref="Text"/> is how it appears in ~name~.</summary>
public sealed record GlobalVariableInfo(string Name, object? Value, bool Persisted, DateTimeOffset LastWrite)
{
    public string Text => VariableResolver.Format(Value);
    public string TypeName => VariableValues.TypeName(Value);
}

public sealed record GlobalVariableChange(string Name, VariableChangeKind Kind, object? OldValue, object? NewValue, bool Persisted);

/// <summary>
/// Named values shared between action runs. Persisted ones survive restarts (globals.json, a JSON object of
/// name → value where values keep their JSON type: string, number or boolean); temporary ones last until the
/// app exits or <see cref="ClearTemporary"/>. Values are stored typed (see <see cref="VariableValues"/>); the
/// string API returns them formatted as they appear in text.
/// </summary>
public sealed class GlobalVariables : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonFile<Dictionary<string, object?>>? _file;

    public GlobalVariables(AppPaths? paths, AppLog log, TextEnvironment? text = null)
    {
        Text = text ?? new TextEnvironment { Log = log };
        if (paths is null) return;
        _file = new JsonFile<Dictionary<string, object?>>(paths.File("globals.json"), log);
        var loadedAt = Text.Clock.GetUtcNow();
        foreach (var key in _file.Value.Keys.ToArray())
        {
            var value = VariableValues.Normalize(_file.Value[key]);
            _file.Value[key] = value;
            _values[key] = new Entry(key, value, true, loadedAt);
        }
    }

    /// <summary>Clock, culture and error log used when text is resolved against these globals.</summary>
    public TextEnvironment Text { get; }

    /// <summary>Raised with the variable name after any change (kept for simple listeners).</summary>
    public event Action<string>? Changed;

    /// <summary>Raised after a variable is created, updated or deleted, with old and new values.</summary>
    public event Action<GlobalVariableChange>? VariableChanged;

    /// <summary>The value as text (as inserted by ~name~), or null when it does not exist.</summary>
    public string? Get(string name)
    {
        lock (_gate) return _values.TryGetValue(name, out var e) ? VariableResolver.Format(e.Value) : null;
    }

    /// <summary>The typed value (string, long, decimal, double, bool or null).</summary>
    public object? GetValue(string name)
    {
        lock (_gate) return _values.TryGetValue(name, out var e) ? e.Value : null;
    }

    public bool TryGetValue(string name, out object? value)
    {
        lock (_gate)
        {
            var found = _values.TryGetValue(name, out var e);
            value = e?.Value;
            return found;
        }
    }

    public bool Contains(string name)
    {
        lock (_gate) return _values.ContainsKey(name);
    }

    public IReadOnlyDictionary<string, string> All()
    {
        lock (_gate) return _values.ToDictionary(p => p.Key, p => VariableResolver.Format(p.Value.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All variables, or only persisted (true) / temporary (false) ones, sorted by name.</summary>
    public IReadOnlyList<GlobalVariableInfo> Snapshot(bool? persisted = null)
    {
        lock (_gate)
        {
            return _values
                .Where(p => persisted is null || p.Value.Persisted == persisted)
                .Select(p => new GlobalVariableInfo(p.Key, p.Value.Value, p.Value.Persisted, p.Value.LastWrite))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool IsPersisted(string name)
    {
        lock (_gate) return _values.TryGetValue(name, out var e) && e.Persisted;
    }

    /// <summary>Stores text as-is (no auto typing), as the original API did.</summary>
    public void Set(string name, string value, bool persist) => SetValue(name, value, persist);

    /// <summary>Stores a value (normalized to a stored type). Switching to temporary removes it from the file.</summary>
    public void SetValue(string name, object? value, bool persist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        value = VariableValues.Normalize(value);
        GlobalVariableChange change;
        lock (_gate) change = Store(name, value, persist);
        Raise(change);
    }

    /// <summary>
    /// Atomically adds <paramref name="amount"/> (negative to decrement) to a numeric variable; a missing one
    /// starts at 0. Returns the new value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current value is not a number.</exception>
    public object Increment(string name, decimal amount, bool persist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        GlobalVariableChange change;
        object result;
        lock (_gate)
        {
            var current = _values.TryGetValue(name, out var e) ? e.Value : null;
            try
            {
                result = VariableValues.Add(current, amount);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Global '{name}': {ex.Message}", ex);
            }
            change = Store(name, result, persist);
        }
        Raise(change);
        return result;
    }

    public bool Remove(string name)
    {
        GlobalVariableChange? change = null;
        lock (_gate)
        {
            if (_values.Remove(name, out var old))
            {
                change = new GlobalVariableChange(old.Name, VariableChangeKind.Deleted, old.Value, null, old.Persisted);
                if (old.Persisted && _file is not null)
                {
                    _file.Value.Remove(old.Name);
                    _file.ScheduleSave();
                }
            }
        }
        if (change is null) return false;
        Raise(change);
        return true;
    }

    /// <summary>Deletes every temporary (non-persisted) variable. Returns how many were removed.</summary>
    public int ClearTemporary() => Clear(persisted: false);

    /// <summary>Deletes all variables, or only persisted / temporary ones. Returns how many were removed.</summary>
    public int Clear(bool? persisted = null)
    {
        var changes = new List<GlobalVariableChange>();
        lock (_gate)
        {
            foreach (var (name, entry) in _values.ToArray())
            {
                if (persisted is not null && entry.Persisted != persisted) continue;
                _values.Remove(name);
                if (entry.Persisted) _file?.Value.Remove(name);
                changes.Add(new GlobalVariableChange(name, VariableChangeKind.Deleted, entry.Value, null, entry.Persisted));
            }
            if (_file is not null && changes.Any(c => c.Persisted)) _file.ScheduleSave();
        }
        foreach (var change in changes) Raise(change);
        return changes.Count;
    }

    /// <summary>Writes pending changes to disk now.</summary>
    public void Flush() => _file?.Flush();

    private GlobalVariableChange Store(string name, object? value, bool persist)
    {
        var existed = _values.TryGetValue(name, out var old);
        // Keep the original spelling of an existing name.
        var key = old?.Name ?? name;
        _values[key] = new Entry(key, value, persist, Text.Clock.GetUtcNow());
        if (_file is not null && (persist || old?.Persisted == true))
        {
            if (persist) _file.Value[key] = value;
            else _file.Value.Remove(key);
            _file.ScheduleSave();
        }
        return new GlobalVariableChange(key, existed ? VariableChangeKind.Updated : VariableChangeKind.Created, old?.Value, value, persist);
    }

    private void Raise(GlobalVariableChange change)
    {
        Changed?.Invoke(change.Name);
        VariableChanged?.Invoke(change);
    }

    public void Dispose() => _file?.Dispose();

    private sealed record Entry(string Name, object? Value, bool Persisted, DateTimeOffset LastWrite);
}
