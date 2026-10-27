using System.Globalization;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Storage;

namespace Cuebot.App.Diagnostics;

/// <summary>One exception in a crash's chain (outer first; aggregate exceptions list every inner one).</summary>
public sealed record ExceptionInfo
{
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
    public string StackTrace { get; init; } = "";
    public List<ExceptionInfo> Inner { get; init; } = [];

    public static ExceptionInfo From(Exception ex, int depth = 0)
    {
        var info = new ExceptionInfo
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace ?? "",
        };
        if (depth >= 8) return info;   // pathological chains
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions.Take(10)) info.Inner.Add(From(inner, depth + 1));
        else if (ex.InnerException is { } inner)
            info.Inner.Add(From(inner, depth + 1));
        return info;
    }

    /// <summary>"Type: message" plus stack, then each inner exception, like <see cref="Exception.ToString"/>.</summary>
    public string Format(int indent = 0)
    {
        var pad = new string(' ', indent);
        var lines = new List<string> { $"{pad}{Type}: {Message}" };
        lines.AddRange(StackTrace.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => pad + l.TrimEnd()));
        foreach (var inner in Inner)
        {
            lines.Add($"{pad}---> (inner)");
            lines.Add(inner.Format(indent + 2));
        }
        return string.Join('\n', lines);
    }

    /// <summary>The innermost exception (usually the root cause).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ExceptionInfo Root => Inner.Count == 0 ? this : Inner[0].Root;
}

public sealed record ConnectionInfo(string Name, string State, string Message);

/// <summary>What <c>crashes/crash-*.json</c> holds.</summary>
public sealed record CrashRecord
{
    public DateTimeOffset Time { get; init; }

    /// <summary>Which handler saw it: "AppDomain", "UI thread".</summary>
    public string Source { get; init; } = "";

    public bool Terminating { get; init; } = true;
    public ExceptionInfo Exception { get; init; } = new();
    public EnvironmentSnapshot Environment { get; init; } = new();
    public List<ConnectionInfo> Connections { get; init; } = [];

    /// <summary>The last log lines (secrets already scrubbed when written).</summary>
    public List<string> Log { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{Exception.Type.Split('.').Last()}: {Exception.Message}";
}

/// <summary>
/// The <c>crashes</c> folder: <c>crash-yyyyMMdd-HHmmss-fff.json</c> until the user reported or dismissed it, then
/// renamed to <c>*.seen.json</c>. The newest <see cref="Keep"/> files are kept.
/// </summary>
public sealed class CrashStore(AppPaths paths)
{
    public const int Keep = 20;
    private const string SeenSuffix = ".seen.json";

    public string Folder => Path.Combine(paths.Root, "crashes");

    public string Write(CrashRecord record)
    {
        Directory.CreateDirectory(Folder);
        var name = $"crash-{record.Time.LocalDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.json";
        var path = Path.Combine(Folder, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json.Options));
        File.Move(temp, path, overwrite: true);
        Prune();
        return path;
    }

    public static CrashRecord? Read(string path)
    {
        try { return JsonSerializer.Deserialize<CrashRecord>(File.ReadAllText(path), Json.Options); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Crash files nobody has looked at yet, newest first.</summary>
    public IReadOnlyList<string> Unseen()
    {
        if (!Directory.Exists(Folder)) return [];
        try
        {
            return Directory.GetFiles(Folder, "crash-*.json")
                .Where(f => !f.EndsWith(SeenSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public string? LatestUnseen() => Unseen().FirstOrDefault();

    /// <summary>The newest crash file, seen or not.</summary>
    public string? Latest()
    {
        if (!Directory.Exists(Folder)) return null;
        try
        {
            return Directory.GetFiles(Folder, "crash-*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Marks every unseen crash as seen (the prompt only offers the newest).</summary>
    public void MarkAllSeen()
    {
        foreach (var file in Unseen())
        {
            try { File.Move(file, file[..^".json".Length] + SeenSuffix, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.GetFiles(Folder, "crash-*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Keep))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
