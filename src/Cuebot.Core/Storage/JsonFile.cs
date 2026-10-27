using System.Text.Json;
using System.Text.Json.Serialization;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

/// <summary>
/// A JSON document persisted to disk. Saves are debounced and atomic (write temp file, then rename).
/// The value is serialized on the calling thread when a save is scheduled, so the writer never
/// observes a half-edited object.
/// </summary>
public sealed class JsonFile<T> : IDisposable where T : class, new()
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly string _path;
    private readonly AppLog _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string? _pending;

    public JsonFile(string path, AppLog log)
    {
        _path = path;
        _log = log;
        _timer = new Timer(_ => Flush());
        Value = Load();
    }

    public T Value { get; private set; }

    public void ScheduleSave()
    {
        var text = JsonSerializer.Serialize(Value, Json.Options);
        lock (_gate) _pending = text;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    public void SaveNow()
    {
        ScheduleSave();
        Flush();
    }

    public void Flush()
    {
        string? text;
        lock (_gate)
        {
            text = _pending;
            _pending = null;
            if (text is null) return;
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error("Storage", $"Failed to save {Path.GetFileName(_path)}", ex);
            }
        }
    }

    private T Load()
    {
        if (!File.Exists(_path)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json.Options) ?? new T();
        }
        catch (Exception ex)
        {
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_path, backup, overwrite: true);
            _log.Error("Storage", $"{Path.GetFileName(_path)} was unreadable and has been backed up to {Path.GetFileName(backup)}", ex);
            return new T();
        }
    }

    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }
}
