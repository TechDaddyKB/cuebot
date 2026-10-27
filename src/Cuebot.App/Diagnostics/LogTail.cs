using System.Text;

namespace Cuebot.App.Diagnostics;

/// <summary>Reads the last lines of Cuebot's daily log files (<c>logs/cuebot-yyyyMMdd.log</c>), newest last.</summary>
public static class LogTail
{
    public const int DefaultLines = 200;
    private const int MaxBytesPerFile = 512 * 1024;

    public static IReadOnlyList<string> Read(string logsFolder, int lines = DefaultLines)
    {
        try
        {
            if (!Directory.Exists(logsFolder)) return [];
            var files = Directory.GetFiles(logsFolder, "cuebot-*.log").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(2);
            var result = new List<string>();
            foreach (var file in files)
            {
                var tail = Tail(file, lines - result.Count);
                result.InsertRange(0, tail);
                if (result.Count >= lines) break;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Tail(string path, int lines)
    {
        if (lines <= 0) return [];
        // The log stays open for writing (FileShare.ReadWrite); read only the end of a large file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaxBytesPerFile);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n').ToList();
        if (start > 0 && all.Count > 0) all.RemoveAt(0);   // partial first line
        while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
        return all.Count > lines ? all.GetRange(all.Count - lines, lines) : all;
    }
}
