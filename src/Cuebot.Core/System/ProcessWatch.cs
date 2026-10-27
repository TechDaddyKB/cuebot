using System.Diagnostics;

namespace Cuebot.Core.SystemTools;

public readonly record struct ProcessInfo(int Id, string Name);

/// <summary>Lists running processes (injectable for tests).</summary>
public interface IProcessLister
{
    IReadOnlyList<ProcessInfo> List();
}

/// <summary>Running processes from <see cref="Process.GetProcesses()"/>; works on Windows and Linux.</summary>
public sealed class SystemProcessLister : IProcessLister
{
    public IReadOnlyList<ProcessInfo> List()
    {
        var result = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { result.Add(new ProcessInfo(process.Id, process.ProcessName)); }
                catch (InvalidOperationException) { } // exited while listing
            }
        }
        return result;
    }
}

/// <summary>A watcher that saw its process start (first instance) or stop (last instance).</summary>
public readonly record struct ProcessTransition(ProcessWatcherDefinition Watcher, bool Started, ProcessInfo Process);

/// <summary>
/// Compares successive process lists. The first list a watcher sees is its baseline, so programs that were
/// already running when Cuebot started don't fire "started".
/// </summary>
public sealed class ProcessWatchTracker
{
    /// <summary>Linux truncates /proc comm names to 15 characters.</summary>
    private const int LinuxCommLength = 15;

    private readonly Dictionary<Guid, ProcessInfo?> _seen = [];

    public IReadOnlyList<ProcessTransition> Update(IReadOnlyList<ProcessInfo> processes, IEnumerable<ProcessWatcherDefinition> watchers)
    {
        var transitions = new List<ProcessTransition>();
        var active = new HashSet<Guid>();
        foreach (var watcher in watchers)
        {
            if (!watcher.Enabled || string.IsNullOrWhiteSpace(watcher.ProcessName)) continue;
            active.Add(watcher.Id);
            ProcessInfo? running = null;
            foreach (var process in processes)
            {
                if (!Matches(watcher.ProcessName, process.Name)) continue;
                running = process;
                break;
            }
            if (_seen.TryGetValue(watcher.Id, out var previous))
            {
                if (previous is null && running is { } started) transitions.Add(new(watcher, true, started));
                else if (previous is { } stopped && running is null) transitions.Add(new(watcher, false, stopped));
            }
            _seen[watcher.Id] = running;
        }
        foreach (var stale in _seen.Keys.Where(id => !active.Contains(id)).ToArray()) _seen.Remove(stale);
        return transitions;
    }

    /// <summary>Case-insensitive, ignoring ".exe"; accepts Linux's 15-character truncated names.</summary>
    public static bool Matches(string watched, string actual)
    {
        var a = Normalize(watched);
        var b = Normalize(actual);
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return b.Length == LinuxCommLength && a.Length > LinuxCommLength && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string name)
    {
        // Accept both separators on every OS: watchers may be configured with Windows paths and run on Linux.
        var trimmed = name.Trim();
        trimmed = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
