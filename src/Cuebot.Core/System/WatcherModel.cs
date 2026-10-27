namespace Cuebot.Core.SystemTools;

/// <summary>Contents of watchers.json.</summary>
public sealed class WatcherSettings
{
    public List<FileWatcherDefinition> FileWatchers { get; set; } = [];
    public List<FileTailDefinition> FileTails { get; set; } = [];
    public List<ProcessWatcherDefinition> ProcessWatchers { get; set; } = [];
}

public abstract class WatcherDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>Raises file created/changed/deleted/renamed events for a folder.</summary>
public sealed class FileWatcherDefinition : WatcherDefinition
{
    public string Folder { get; set; } = "";

    /// <summary>File name patterns separated by ';' (e.g. "*.txt;*.log"); empty = every file.</summary>
    public string Filter { get; set; } = "";

    public bool IncludeSubfolders { get; set; }
    public bool Created { get; set; } = true;
    public bool Changed { get; set; } = true;
    public bool Deleted { get; set; } = true;
    public bool Renamed { get; set; } = true;
}

/// <summary>Raises one event per line appended to a text file.</summary>
public sealed class FileTailDefinition : WatcherDefinition
{
    public string FilePath { get; set; } = "";

    /// <summary>Skip lines that are empty or whitespace.</summary>
    public bool SkipEmptyLines { get; set; } = true;
}

/// <summary>Raises events when a program starts (first instance) or stops (last instance).</summary>
public sealed class ProcessWatcherDefinition : WatcherDefinition
{
    /// <summary>Process name with or without ".exe", e.g. "obs64" or "obs".</summary>
    public string ProcessName { get; set; } = "";
}

public enum WatcherState { Stopped, Running, Waiting, Error }

/// <summary>Live state of a watcher for the UI.</summary>
public sealed record WatcherStatus(WatcherState State, string? Message = null)
{
    public static readonly WatcherStatus Stopped = new(WatcherState.Stopped);
    public static readonly WatcherStatus Running = new(WatcherState.Running);
}

/// <summary>Event types (also the trigger type ids) published by the watchers.</summary>
public static class WatcherEvents
{
    public const string FileCreated = "core.fileCreated";
    public const string FileChanged = "core.fileChanged";
    public const string FileDeleted = "core.fileDeleted";
    public const string FileRenamed = "core.fileRenamed";
    public const string FileTailLine = "core.fileTailLine";
    public const string ProcessStarted = "core.processStarted";
    public const string ProcessStopped = "core.processStopped";
}
