using System.IO.Compression;
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
/// observes a half-edited object. <see cref="SecretAttribute"/> properties go to the folder's
/// <see cref="SecretStore"/> instead of the file (see <see cref="SecretJson"/>).
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
        Value = Load(out var needsSave);
        if (needsSave) SaveNow();   // move secrets still stored in the file into the secret store
    }

    public T Value { get; private set; }

    public void ScheduleSave()
    {
        var text = SecretJson.Serialize(Value, _path);
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

    private T Load(out bool needsSave)
    {
        needsSave = false;
        if (!File.Exists(_path)) return new T();
        try
        {
            return SecretJson.Deserialize<T>(File.ReadAllText(_path), _path, out needsSave) ?? new T();
        }
        catch (Exception ex)
        {
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_path, backup, overwrite: true);
            // Only damaged content is replaced: a file that could not be read (locked, no access) may still be fine.
            if (ex is not (IOException or UnauthorizedAccessException) && RestoreFromBackup() is { } restored)
            {
                _log.Error("Storage", $"{Path.GetFileName(_path)} was damaged and has been restored from {restored.Zip}. "
                    + $"The damaged file is kept as {Path.GetFileName(backup)}.", ex);
                needsSave = true;
                return restored.Value;
            }
            _log.Error("Storage", $"{Path.GetFileName(_path)} was unreadable and has been backed up to {Path.GetFileName(backup)}", ex);
            return new T();
        }
    }

    /// <summary>
    /// The newest copy of this file that parses, from the automatic backups next to it (<c>backups/cuebot-*.zip</c>, whose
    /// names sort by time). A backup taken after the damage holds the same bad file, so older ones are tried too.
    /// </summary>
    private (T Value, string Zip)? RestoreFromBackup()
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "backups");
        if (!Directory.Exists(folder)) return null;
        foreach (var zip in Directory.EnumerateFiles(folder, "cuebot-*.zip").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                if (archive.GetEntry(Path.GetFileName(_path)) is not { } entry) continue;
                using var reader = new StreamReader(entry.Open());
                if (SecretJson.Deserialize<T>(reader.ReadToEnd(), _path, out _) is { } value) return (value, Path.GetFileName(zip));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
            {
                _log.Warn("Storage", $"Backup {Path.GetFileName(zip)} has no usable {Path.GetFileName(_path)}: {ex.Message}");
            }
        }
        return null;
    }

    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }
}
