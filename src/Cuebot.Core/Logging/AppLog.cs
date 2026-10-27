namespace Cuebot.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} [{Level}] {Source}: {Message}";
}

/// <summary>Application log: keeps a bounded in-memory history for the UI and appends to a daily file.</summary>
public sealed class AppLog : IDisposable
{
    private const int HistorySize = 1000;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _history = new();
    private readonly StreamWriter? _file;

    public AppLog(AppPaths? paths = null)
    {
        if (paths is null) return;
        var path = Path.Combine(paths.Logs, $"cuebot-{DateTime.Now:yyyyMMdd}.log");
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate) return _history.ToArray();
    }

    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message);
        lock (_gate)
        {
            _history.Enqueue(entry);
            while (_history.Count > HistorySize) _history.Dequeue();
            _file?.WriteLine(entry.ToString());
        }
        EntryAdded?.Invoke(entry);
    }

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    public void Error(string source, string message, Exception? ex = null) =>
        Write(LogLevel.Error, source, ex is null ? message : $"{message}: {ex.Message}");

    public void Dispose()
    {
        lock (_gate) _file?.Dispose();
    }
}
