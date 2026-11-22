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
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseAttemptGap = TimeSpan.FromMilliseconds(100);
    private const int CloseAttempts = 3;
    private readonly string _path;
    private readonly AppLog _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string? _pending;
    private TimeSpan _retryDelay;   // zero while saves are succeeding
    private bool _disposed;

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
        lock (_gate)
        {
            _pending = text;
            if (_disposed) Flush();   // no timer any more: a late edit during shutdown is written straight away
            else _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void SaveNow()
    {
        ScheduleSave();
        Flush();
    }

    /// <summary>
    /// Writes the pending save. Never throws, because it runs on a timer thread where an exception ends the process.
    /// A failed save stays pending and is retried with a growing delay until it succeeds or a newer edit replaces it.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_pending is not { } text) return;
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, _path, overwrite: true);
                _pending = null;
                if (_retryDelay > TimeSpan.Zero) TryLog(LogLevel.Info, $"Saved {Path.GetFileName(_path)} after an earlier failure");
                _retryDelay = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                if (_retryDelay == TimeSpan.Zero) TryLog(LogLevel.Error, $"Failed to save {Path.GetFileName(_path)}, will retry: {ex.Message}");
                _retryDelay = _retryDelay == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetry.Ticks));
                if (!_disposed) _timer.Change(_retryDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void TryLog(LogLevel level, string message)
    {
        try { _log.Write(level, "Storage", message); }
        catch (Exception) { /* a log listener must not turn a storage problem into a crash */ }
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

    /// <summary>Stops the timer and makes a few quick last attempts, since a brief file lock is the usual cause of a failure.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        for (var attempt = 0; attempt < CloseAttempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(CloseAttemptGap);
            Flush();
            lock (_gate)
            {
                if (_pending is null) return;
            }
        }
        TryLog(LogLevel.Error, $"Could not save {Path.GetFileName(_path)} before closing; its latest changes were not written");
    }
}
