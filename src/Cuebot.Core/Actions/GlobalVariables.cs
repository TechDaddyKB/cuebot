using System.Collections.Concurrent;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Actions;

/// <summary>Named values shared between action runs. Persisted ones survive restarts (globals.json).</summary>
public sealed class GlobalVariables : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonFile<Dictionary<string, string>>? _file;
    private readonly object _fileGate = new();

    public GlobalVariables(AppPaths? paths, AppLog log)
    {
        if (paths is null) return;
        _file = new JsonFile<Dictionary<string, string>>(paths.File("globals.json"), log);
        foreach (var (key, value) in _file.Value) _values[key] = value;
    }

    public event Action<string>? Changed;

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> All() => new Dictionary<string, string>(_values);

    public bool IsPersisted(string name)
    {
        lock (_fileGate) return _file?.Value.ContainsKey(name) ?? false;
    }

    public void Set(string name, string value, bool persist)
    {
        _values[name] = value;
        if (_file is not null)
        {
            lock (_fileGate)
            {
                if (persist) _file.Value[name] = value;
                else _file.Value.Remove(name);
                _file.ScheduleSave();
            }
        }
        Changed?.Invoke(name);
    }

    public void Remove(string name)
    {
        _values.TryRemove(name, out _);
        if (_file is not null)
        {
            lock (_fileGate)
            {
                _file.Value.Remove(name);
                _file.ScheduleSave();
            }
        }
        Changed?.Invoke(name);
    }

    public void Dispose() => _file?.Dispose();
}
