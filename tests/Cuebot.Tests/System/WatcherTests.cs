using System.Collections.Concurrent;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.SystemTools;

namespace Cuebot.Tests.SystemTools;

public sealed class WatcherTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cuebot-watcher-tests", Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly JsonFile<WatcherSettings> _settings;
    private readonly FakeProcessLister _processes = new();
    private readonly WatcherService _service;
    private readonly ConcurrentQueue<BotEvent> _events = new();

    public WatcherTests()
    {
        Directory.CreateDirectory(_folder);
        _bus = new EventBus(_log);
        _bus.Subscribe(_events.Enqueue);
        _settings = new JsonFile<WatcherSettings>(Path.Combine(_folder, "watchers.json"), _log);
        _service = new WatcherService(_settings, _bus, _log, _processes,
            tailInterval: TimeSpan.FromMilliseconds(50), processInterval: TimeSpan.FromHours(1), retryInterval: TimeSpan.FromMilliseconds(100));
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        _log.Dispose();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<BotEvent> WaitForAsync(Func<BotEvent, bool> match)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            var found = _events.FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("Expected event did not arrive. Got: " + string.Join(", ", _events.Select(e => e.Type)));
    }

    private async Task WaitForStatusAsync(Guid id, WatcherState state)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (_service.GetStatus(id).State != state)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Status stayed {_service.GetStatus(id)}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Folder_watcher_reports_created_changed_renamed_deleted()
    {
        var watched = Path.Combine(_folder, "watched");
        Directory.CreateDirectory(watched);
        var definition = new FileWatcherDefinition { Name = "Drops", Folder = watched, Filter = "*.txt" };
        _service.Save(definition);
        await _service.StartAsync();
        await WaitForStatusAsync(definition.Id, WatcherState.Running);

        var file = Path.Combine(watched, "a.txt");
        await File.WriteAllTextAsync(file, "1");
        var created = await WaitForAsync(e => e.Type == WatcherEvents.FileCreated);
        Assert.Equal("Drops", created.Args["watcherName"]);
        Assert.Equal("a.txt", created.Args["fileName"]);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath((string)created.Args["filePath"]!));
        Assert.Equal((watched, "*.txt"), (created.Args["watcherFolder"], created.Args["watcherFilter"]));
        Assert.Equal(created.Args["filePath"], created.Args["fullPath"]);

        await File.AppendAllTextAsync(file, "2");
        var changed = await WaitForAsync(e => e.Type == WatcherEvents.FileChanged);
        Assert.Equal(changed.Args["filePath"], changed.Args["fullPath"]);

        var renamed = Path.Combine(watched, "b.txt");
        File.Move(file, renamed);
        var rename = await WaitForAsync(e => e.Type == WatcherEvents.FileRenamed);
        Assert.Equal("a.txt", rename.Args["oldFileName"]);
        Assert.Equal(rename.Args["oldFilePath"], rename.Args["oldFullPath"]);
        Assert.Equal("b.txt", rename.Args["fileName"]);

        File.Delete(renamed);
        await WaitForAsync(e => e.Type == WatcherEvents.FileDeleted && (string?)e.Args["fileName"] == "b.txt");

        await File.WriteAllTextAsync(Path.Combine(watched, "ignored.log"), "x");
        await Task.Delay(300);
        Assert.DoesNotContain(_events, e => (string?)e.Args.GetValueOrDefault("fileName") == "ignored.log");
    }

    [Fact]
    public async Task Folder_watcher_waits_for_a_missing_folder()
    {
        var later = Path.Combine(_folder, "later");
        var definition = new FileWatcherDefinition { Name = "Later", Folder = later, Changed = false };
        await _service.StartAsync();
        _service.Save(definition);
        await WaitForStatusAsync(definition.Id, WatcherState.Waiting);

        Directory.CreateDirectory(later);
        await WaitForStatusAsync(definition.Id, WatcherState.Running);
        await File.WriteAllTextAsync(Path.Combine(later, "x.bin"), "x");
        await WaitForAsync(e => e.Type == WatcherEvents.FileCreated && (string?)e.Args["watcherName"] == "Later");
    }

    [Fact]
    public async Task Disabled_and_removed_watchers_stop()
    {
        var definition = new FileWatcherDefinition { Name = "Off", Folder = _folder, Enabled = false };
        _service.Save(definition);
        await _service.StartAsync();
        Assert.Equal(WatcherState.Stopped, _service.GetStatus(definition.Id).State);

        Assert.True(_service.SetEnabled("off", true));
        await WaitForStatusAsync(definition.Id, WatcherState.Running);
        Assert.True(_service.FileWatchers.Single().Enabled);

        Assert.True(_service.Remove(definition.Id));
        Assert.Empty(_service.FileWatchers);
        Assert.False(_service.Remove(definition.Id));
        Assert.False(_service.SetEnabled("off", true));
    }

    [Fact]
    public async Task Definitions_persist_to_watchers_json_and_raise_changed()
    {
        var changes = 0;
        _service.Changed += () => Interlocked.Increment(ref changes);
        _service.Save(new FileTailDefinition { Name = "Chat log", FilePath = Path.Combine(_folder, "chat.log") });
        _service.Save(new ProcessWatcherDefinition { Name = "OBS", ProcessName = "obs64.exe" });
        _settings.Flush();
        Assert.True(changes >= 2);

        using var reloaded = new JsonFile<WatcherSettings>(Path.Combine(_folder, "watchers.json"), _log);
        Assert.Equal("Chat log", reloaded.Value.FileTails.Single().Name);
        Assert.Equal("obs64.exe", reloaded.Value.ProcessWatchers.Single().ProcessName);
        Assert.Equal(["Chat log"], _service.Names(s => s.FileTails));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task File_tail_publishes_appended_lines()
    {
        var path = Path.Combine(_folder, "chat.log");
        await File.WriteAllTextAsync(path, "old line\n");
        var tail = new FileTailDefinition { Name = "Chat", FilePath = path };
        _service.Save(tail);
        await _service.StartAsync();
        await WaitForStatusAsync(tail.Id, WatcherState.Running);

        await File.AppendAllTextAsync(path, "first\n\nsecond\n");
        await WaitForAsync(e => e.Type == WatcherEvents.FileTailLine && (string?)e.Args["line"] == "second");
        var lines = _events.Where(e => e.Type == WatcherEvents.FileTailLine).Select(e => (string)e.Args["line"]!).ToArray();
        Assert.Equal(["first", "second"], lines);
        Assert.Equal("Chat", _events.First().Args["watcherName"]);
    }

    [Fact]
    public void Tail_reader_handles_partial_lines_truncation_and_late_files()
    {
        var path = Path.Combine(_folder, "tail.txt");
        var reader = new FileTailReader(path);
        Assert.Empty(reader.ReadNewLines());
        Assert.False(reader.FileExists);

        File.WriteAllText(path, "\uFEFFa\r\nb");            // created after the reader started: read from the start
        Assert.Equal(["a"], reader.ReadNewLines());
        File.AppendAllText(path, "c\nd");
        Assert.Equal(["bc"], reader.ReadNewLines());
        Assert.Empty(reader.ReadNewLines());

        File.WriteAllText(path, "x\n");                      // truncated: start over
        Assert.Equal(["x"], reader.ReadNewLines());

        var utf8 = "é\n"u8.ToArray();                         // a character split across two reads
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(utf8, 0, 1);
        Assert.Empty(reader.ReadNewLines());
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(utf8, 1, utf8.Length - 1);
        Assert.Equal(["é"], reader.ReadNewLines());
    }

    [Fact]
    public void Tail_reader_skips_existing_content_by_default()
    {
        var path = Path.Combine(_folder, "existing.txt");
        File.WriteAllText(path, "before\n");
        var reader = new FileTailReader(path);
        Assert.Empty(reader.ReadNewLines());
        File.AppendAllText(path, "after\n");
        Assert.Equal(["after"], reader.ReadNewLines());
        Assert.Equal(["before", "after"], new FileTailReader(path, startAtEnd: false).ReadNewLines());
    }

    [Fact]
    public async Task Process_watcher_fires_on_first_start_and_last_stop()
    {
        var watcher = new ProcessWatcherDefinition { Name = "Game", ProcessName = "Game.exe" };
        _service.Save(watcher);
        _processes.Current = [new(1, "explorer")];
        await _service.StartAsync();
        _service.PollProcesses(); // baseline

        _processes.Current = [new(1, "explorer"), new(10, "game")];
        _service.PollProcesses();
        var started = await WaitForAsync(e => e.Type == WatcherEvents.ProcessStarted);
        Assert.Equal("game", started.Args["processName"]);
        Assert.Equal(10, started.Args["processId"]);
        Assert.Equal("Game", started.Args["watcherName"]);

        _processes.Current = [new(11, "game")]; // a second instance took over: still running
        _service.PollProcesses();
        _processes.Current = [];
        _service.PollProcesses();
        await WaitForAsync(e => e.Type == WatcherEvents.ProcessStopped);
        Assert.Single(_events, e => e.Type == WatcherEvents.ProcessStarted);
        Assert.Single(_events, e => e.Type == WatcherEvents.ProcessStopped);
    }

    [Fact]
    public void Process_tracker_uses_the_first_list_as_baseline()
    {
        var tracker = new ProcessWatchTracker();
        ProcessWatcherDefinition[] watchers = [new() { Name = "OBS", ProcessName = "obs" }];
        Assert.Empty(tracker.Update([new(5, "obs")], watchers));     // already running at startup
        var stopped = tracker.Update([], watchers).Single();
        Assert.False(stopped.Started);
        Assert.Equal(5, stopped.Process.Id);
        Assert.Empty(tracker.Update([], [new() { ProcessName = "obs", Enabled = false }]));
    }

    [Theory]
    [InlineData("obs64.exe", "obs64", true)]
    [InlineData("OBS64", "obs64", true)]
    [InlineData("C:\\Games\\game.exe", "Game", true)]
    [InlineData("streamlabs-desktop-x", "streamlabs-desk", true)] // Linux comm truncation (15 chars)
    [InlineData("obs", "obs64", false)]
    [InlineData("", "obs", false)]
    public void Process_names_match_loosely(string watched, string actual, bool expected) =>
        Assert.Equal(expected, ProcessWatchTracker.Matches(watched, actual));

    [Fact]
    public void Real_process_lister_finds_this_process()
    {
        var current = Environment.ProcessId;
        Assert.Contains(new SystemProcessLister().List(), p => p.Id == current);
    }

    [Fact]
    public void Triggers_filter_by_watcher_name_or_id()
    {
        var id = Guid.NewGuid().ToString();
        var e = new BotEvent(WatcherEvents.FileCreated, new Dictionary<string, object?> { ["watcherName"] = "Drops", ["watcherId"] = id });
        bool Match(string filter) => SystemTriggers.MatchesWatcher(e,
            new ParameterValues(new Dictionary<string, string> { ["watcher"] = filter }), new Dictionary<string, object?>());
        Assert.True(Match(""));
        Assert.True(Match("drops"));
        Assert.True(Match(id));
        Assert.False(Match("Other"));

        var triggers = new SystemTriggers(_service).GetTriggers().Select(t => t.TypeId).ToArray();
        Assert.Contains(WatcherEvents.FileRenamed, triggers);
        Assert.Contains(WatcherEvents.FileTailLine, triggers);
        Assert.Contains(WatcherEvents.ProcessStopped, triggers);
    }

    [Fact]
    public async Task Set_watcher_state_sub_action_toggles_a_file_tail()
    {
        var tail = new FileTailDefinition { Name = "Log", FilePath = Path.Combine(_folder, "x.log") };
        _service.Save(tail);
        using var h = new SystemTestHarness(new SetWatcherStateSubAction(_service));
        Assert.NotNull(await h.RunAsync("core.setWatcherState", ("watcher", "Log"), ("state", "Toggle")));
        Assert.False(_service.FileTails.Single().Enabled);
        Assert.NotNull(await h.RunAsync("core.setWatcherState", ("watcher", "log"), ("state", "Enable")));
        Assert.True(_service.FileTails.Single().Enabled);
        Assert.NotNull(await h.RunAsync("core.setWatcherState", ("watcher", "nope"), ("state", "Disable")));
        Assert.Contains(h.Log.Recent(), e => e.Message.Contains("No watcher named"));
    }

    private sealed class FakeProcessLister : IProcessLister
    {
        public IReadOnlyList<ProcessInfo> Current { get; set; } = [];
        public IReadOnlyList<ProcessInfo> List() => Current;
    }
}
