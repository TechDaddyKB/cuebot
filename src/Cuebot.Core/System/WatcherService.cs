using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.SystemTools;

/// <summary>
/// Runs the file/folder watchers, file tails and process watchers from watchers.json and publishes their events.
/// Edits through this API are saved and applied immediately; <see cref="Changed"/> tells the UI to refresh.
/// </summary>
public sealed class WatcherService : IAsyncDisposable
{
    private const string LogSource = "Watchers";
    private readonly JsonFile<WatcherSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IProcessLister _processes;
    private readonly TimeSpan _tailInterval;
    private readonly TimeSpan _processInterval;
    private readonly TimeSpan _retryInterval;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, IDisposable> _running = [];
    private readonly Dictionary<Guid, WatcherStatus> _status = [];
    private readonly ProcessWatchTracker _processTracker = new();
    private Timer? _processTimer;
    private int _processPolling;
    private bool _started;
    private bool _disposed;

    public WatcherService(JsonFile<WatcherSettings> settings, EventBus bus, AppLog log, IProcessLister? processes = null,
        TimeSpan? tailInterval = null, TimeSpan? processInterval = null, TimeSpan? retryInterval = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _processes = processes ?? new SystemProcessLister();
        _tailInterval = tailInterval ?? TimeSpan.FromMilliseconds(500);
        _processInterval = processInterval ?? TimeSpan.FromSeconds(2);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Raised (on any thread) when a definition or a watcher's status changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<FileWatcherDefinition> FileWatchers
    {
        get { lock (_gate) return _settings.Value.FileWatchers.Select(Json.Clone).ToArray(); }
    }

    public IReadOnlyList<FileTailDefinition> FileTails
    {
        get { lock (_gate) return _settings.Value.FileTails.Select(Json.Clone).ToArray(); }
    }

    public IReadOnlyList<ProcessWatcherDefinition> ProcessWatchers
    {
        get { lock (_gate) return _settings.Value.ProcessWatchers.Select(Json.Clone).ToArray(); }
    }

    /// <summary>Names of every watcher, for trigger and sub-action suggestions.</summary>
    public IEnumerable<string> Names(Func<WatcherSettings, IEnumerable<WatcherDefinition>> select)
    {
        lock (_gate) return select(_settings.Value).Select(d => d.Name).Where(n => n.Length > 0).ToArray();
    }

    public WatcherStatus GetStatus(Guid id)
    {
        lock (_gate) return _status.GetValueOrDefault(id) ?? WatcherStatus.Stopped;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_started || _disposed) return Task.CompletedTask;
            _started = true;
            foreach (var definition in All(_settings.Value)) Apply(definition);
            UpdateProcessTimer();
        }
        if (_processTimer is not null) PollProcesses(); // baseline: programs already running don't fire "started"
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Adds or replaces a definition (matched by id), saves, and restarts that watcher.</summary>
    public void Save(WatcherDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var copy = CloneDefinition(definition);
        lock (_gate)
        {
            var settings = _settings.Value;
            switch (copy)
            {
                case FileWatcherDefinition f: Upsert(settings.FileWatchers, f); break;
                case FileTailDefinition t: Upsert(settings.FileTails, t); break;
                case ProcessWatcherDefinition p: Upsert(settings.ProcessWatchers, p); break;
                default: throw new ArgumentException($"Unknown watcher type {copy.GetType().Name}", nameof(definition));
            }
            _settings.ScheduleSave();
            if (_started) Apply(copy);
            if (copy is ProcessWatcherDefinition) UpdateProcessTimer();
        }
        Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            var settings = _settings.Value;
            removed = settings.FileWatchers.RemoveAll(d => d.Id == id)
                + settings.FileTails.RemoveAll(d => d.Id == id)
                + settings.ProcessWatchers.RemoveAll(d => d.Id == id) > 0;
            if (!removed) return false;
            StopRunning(id);
            _status.Remove(id);
            _settings.ScheduleSave();
            UpdateProcessTimer();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Enables or disables a watcher by id or name; null toggles. Returns false when no watcher matches.</summary>
    public bool SetEnabled(string idOrName, bool? enabled, Func<WatcherSettings, IEnumerable<WatcherDefinition>>? scope = null)
    {
        WatcherDefinition? definition;
        lock (_gate) definition = Find(scope?.Invoke(_settings.Value) ?? All(_settings.Value), idOrName);
        if (definition is null) return false;
        var copy = CloneDefinition(definition);
        copy.Enabled = enabled ?? !copy.Enabled;
        Save(copy);
        return true;
    }

    private static WatcherDefinition? Find(IEnumerable<WatcherDefinition> definitions, string idOrName)
    {
        var key = idOrName.Trim();
        var list = definitions.ToArray();
        return Guid.TryParse(key, out var id) && list.FirstOrDefault(d => d.Id == id) is { } byId
            ? byId
            : list.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<WatcherDefinition> All(WatcherSettings s) =>
        s.FileWatchers.Cast<WatcherDefinition>().Concat(s.FileTails).Concat(s.ProcessWatchers);

    private static void Upsert<T>(List<T> list, T item) where T : WatcherDefinition
    {
        var index = list.FindIndex(d => d.Id == item.Id);
        if (index >= 0) list[index] = item;
        else list.Add(item);
    }

    private static WatcherDefinition CloneDefinition(WatcherDefinition d) => d switch
    {
        FileWatcherDefinition f => Json.Clone(f),
        FileTailDefinition t => Json.Clone(t),
        ProcessWatcherDefinition p => Json.Clone(p),
        _ => throw new ArgumentException($"Unknown watcher type {d.GetType().Name}"),
    };

    /// <summary>(Re)starts one watcher according to its definition. Caller holds the lock.</summary>
    private void Apply(WatcherDefinition definition)
    {
        StopRunning(definition.Id);
        if (!definition.Enabled)
        {
            _status[definition.Id] = WatcherStatus.Stopped;
            return;
        }
        switch (definition)
        {
            case FileWatcherDefinition f:
                _running[f.Id] = new FolderWatch(this, f);
                break;
            case FileTailDefinition t:
                _running[t.Id] = new TailWatch(this, t);
                break;
            case ProcessWatcherDefinition p:
                _status[p.Id] = string.IsNullOrWhiteSpace(p.ProcessName)
                    ? new WatcherStatus(WatcherState.Error, "No process name")
                    : WatcherStatus.Running;
                break;
        }
    }

    private void StopRunning(Guid id)
    {
        if (_running.Remove(id, out var running)) running.Dispose();
    }

    /// <summary>Records a running watcher's status. Callers must not hold their own locks (lock order: service first).</summary>
    private void SetStatus(IDisposable runner, Guid id, WatcherStatus status)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var current) || current != runner || _status.GetValueOrDefault(id) == status) return;
            _status[id] = status;
        }
        Changed?.Invoke();
    }

    private void UpdateProcessTimer()
    {
        var any = _started && !_disposed && _settings.Value.ProcessWatchers.Any(p => p.Enabled);
        if (any) _processTimer ??= new Timer(_ => PollProcesses(), null, _processInterval, _processInterval);
        else
        {
            _processTimer?.Dispose();
            _processTimer = null;
        }
    }

    /// <summary>One process-watcher tick; public for deterministic tests.</summary>
    public void PollProcesses()
    {
        if (Interlocked.Exchange(ref _processPolling, 1) == 1) return;
        try
        {
            ProcessWatcherDefinition[] watchers;
            lock (_gate) watchers = _settings.Value.ProcessWatchers.Where(p => p.Enabled).Select(Json.Clone).ToArray();
            var transitions = _processTracker.Update(_processes.List(), watchers);
            foreach (var t in transitions)
            {
                var args = new Dictionary<string, object?>
                {
                    ["processName"] = t.Process.Name,
                    ["processId"] = t.Process.Id,
                    // Streamer.bot names; the path is only known while the process runs.
                    ["name"] = t.Process.Name,
                    ["executable"] = t.Process.Name + (OperatingSystem.IsWindows() ? ".exe" : ""),
                };
                if (t.Started && ProcessPath(t.Process.Id) is { } fullPath)
                {
                    args["fullPath"] = fullPath;
                    args["path"] = Path.GetDirectoryName(fullPath) ?? "";
                    args["executable"] = Path.GetFileName(fullPath);
                }
                Publish(t.Started ? WatcherEvents.ProcessStarted : WatcherEvents.ProcessStopped, t.Watcher, args);
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "Could not list running processes", ex);
        }
        finally
        {
            Volatile.Write(ref _processPolling, 0);
        }
    }

    /// <summary>The executable of a running process, or null when it is gone or not accessible.</summary>
    private static string? ProcessPath(int id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void Publish(string type, WatcherDefinition watcher, Dictionary<string, object?> args)
    {
        args["watcherName"] = watcher.Name;
        args["watcherId"] = watcher.Id.ToString();
        _bus.Publish(new BotEvent(type, args));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var running in _running.Values) running.Dispose();
            _running.Clear();
            _processTimer?.Dispose();
            _processTimer = null;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>A FileSystemWatcher that waits for a missing folder and restarts after errors.</summary>
    private sealed class FolderWatch : IDisposable
    {
        private static readonly TimeSpan ChangedDebounce = TimeSpan.FromMilliseconds(250);
        private readonly WatcherService _owner;
        private readonly FileWatcherDefinition _definition;
        private readonly Timer _retry;
        private readonly Dictionary<string, long> _lastChanged = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private bool _disposed;

        public FolderWatch(WatcherService owner, FileWatcherDefinition definition)
        {
            _owner = owner;
            _definition = definition;
            _retry = new Timer(_ => TryStart());
            owner._status[definition.Id] = new WatcherStatus(WatcherState.Waiting, "Starting");
            _retry.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        private void TryStart()
        {
            var status = Start();
            if (status is not null) _owner.SetStatus(this, _definition.Id, status);
        }

        /// <summary>Starts the watcher; returns the status to report (outside this lock: the service lock comes first).</summary>
        private WatcherStatus? Start()
        {
            lock (_gate)
            {
                if (_disposed || _watcher is not null) return null;
                var folder = Environment.ExpandEnvironmentVariables(_definition.Folder.Trim());
                if (folder.Length == 0 || !Directory.Exists(folder))
                {
                    _retry.Change(_owner._retryInterval, Timeout.InfiniteTimeSpan);
                    return new WatcherStatus(WatcherState.Waiting, $"Folder not found: {folder}");
                }
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = _definition.IncludeSubfolders,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    foreach (var pattern in _definition.Filter.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        watcher.Filters.Add(pattern);
                    watcher.Created += (_, e) => OnFile("Created", e.FullPath, null);
                    watcher.Changed += (_, e) => OnChanged(e.FullPath);
                    watcher.Deleted += (_, e) => OnFile("Deleted", e.FullPath, null);
                    watcher.Renamed += (_, e) => OnFile("Renamed", e.FullPath, e.OldFullPath);
                    watcher.Error += (_, e) => OnError(e.GetException());
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                    return WatcherStatus.Running;
                }
                catch (Exception ex)
                {
                    watcher?.Dispose();
                    _owner._log.Warn(LogSource, $"File watcher \"{_definition.Name}\" could not start: {ex.Message}");
                    _retry.Change(_owner._retryInterval, Timeout.InfiniteTimeSpan);
                    return new WatcherStatus(WatcherState.Error, ex.Message);
                }
            }
        }

        private void OnChanged(string path)
        {
            if (!_definition.Changed) return;
            // Editors and copy operations raise several Changed events per save.
            var now = Environment.TickCount64;
            lock (_lastChanged)
            {
                if (_lastChanged.TryGetValue(path, out var last) && now - last < ChangedDebounce.TotalMilliseconds) return;
                _lastChanged[path] = now;
                if (_lastChanged.Count > 1000) _lastChanged.Clear();
            }
            OnFile("Changed", path, null);
        }

        private void OnFile(string change, string path, string? oldPath)
        {
            var (wanted, type) = change switch
            {
                "Created" => (_definition.Created, WatcherEvents.FileCreated),
                "Deleted" => (_definition.Deleted, WatcherEvents.FileDeleted),
                "Renamed" => (_definition.Renamed, WatcherEvents.FileRenamed),
                _ => (true, WatcherEvents.FileChanged),
            };
            if (!wanted || _disposed) return;
            var args = new Dictionary<string, object?>
            {
                ["changeType"] = change,
                ["filePath"] = path,
                ["fileName"] = Path.GetFileName(path),
                ["folder"] = Path.GetDirectoryName(path) ?? "",
                // Streamer.bot names.
                ["fullPath"] = path,
                ["watcherFolder"] = _definition.Folder,
                ["watcherFilter"] = _definition.Filter,
            };
            if (oldPath is not null)
            {
                args["oldFilePath"] = oldPath;
                args["oldFileName"] = Path.GetFileName(oldPath);
                args["oldFullPath"] = oldPath;
            }
            if (change == "Changed") AddContent(args, path);
            _owner.Publish(type, _definition, args);
        }

        /// <summary>Streamer.bot's Changed variables: fileSize, empty, lineCount, line#, lineEscaped# (small text files only).</summary>
        private static void AddContent(Dictionary<string, object?> args, string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                args["fileSize"] = info.Length;
                args["empty"] = info.Length == 0;
                if (info.Length > MaxContentBytes) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                args["lineCount"] = lines.Count;
                for (var i = 0; i < lines.Count; i++)
                {
                    args["line" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = lines[i];
                    args["lineEscaped" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Uri.EscapeDataString(lines[i]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being written, or locked: the event goes out without the content.
            }
        }

        /// <summary>Changed events read the file's lines only up to this size.</summary>
        private const long MaxContentBytes = 256 * 1024;

        private void OnError(Exception ex)
        {
            lock (_gate)
            {
                _watcher?.Dispose();
                _watcher = null;
                if (_disposed) return;
                _retry.Change(_owner._retryInterval, Timeout.InfiniteTimeSpan);
            }
            _owner._log.Warn(LogSource, $"File watcher \"{_definition.Name}\" stopped ({ex.Message}); restarting.");
            _owner.SetStatus(this, _definition.Id, new WatcherStatus(WatcherState.Error, ex.Message));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _retry.Dispose();
                _watcher?.Dispose();
                _watcher = null;
            }
        }
    }

    /// <summary>Polls a file with <see cref="FileTailReader"/>.</summary>
    private sealed class TailWatch : IDisposable
    {
        private readonly WatcherService _owner;
        private readonly FileTailDefinition _definition;
        private readonly FileTailReader _reader;
        private readonly Timer _timer;
        private readonly string _path;
        private int _busy;
        private bool _disposed;

        public TailWatch(WatcherService owner, FileTailDefinition definition)
        {
            _owner = owner;
            _definition = definition;
            _path = Environment.ExpandEnvironmentVariables(definition.FilePath.Trim());
            _reader = new FileTailReader(_path);
            owner._status[definition.Id] = new WatcherStatus(WatcherState.Waiting, "Starting");
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, owner._tailInterval);
        }

        private void Poll()
        {
            if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                if (_path.Length == 0)
                {
                    _owner.SetStatus(this, _definition.Id, new WatcherStatus(WatcherState.Error, "No file selected"));
                    return;
                }
                var lines = _reader.ReadNewLines();
                _owner.SetStatus(this, _definition.Id, _reader.FileExists
                    ? WatcherStatus.Running
                    : new WatcherStatus(WatcherState.Waiting, $"File not found: {_path}"));
                foreach (var line in lines)
                {
                    if (_disposed) return;
                    if (_definition.SkipEmptyLines && string.IsNullOrWhiteSpace(line)) continue;
                    _owner.Publish(WatcherEvents.FileTailLine, _definition, new()
                    {
                        ["filePath"] = _path,
                        ["fileName"] = Path.GetFileName(_path),   // Streamer.bot's name
                        ["line"] = line,
                    });
                }
            }
            catch (Exception ex)
            {
                _owner.SetStatus(this, _definition.Id, new WatcherStatus(WatcherState.Error, ex.Message));
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
