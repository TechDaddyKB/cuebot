namespace Cuebot.Speech;

/// <summary>Finds command-line tools on PATH or at well-known install locations.</summary>
internal static class ExecutableLocator
{
    /// <summary>Returns the full path of the first candidate that exists, or null.</summary>
    /// <param name="names">Program names searched on PATH (".exe" is tried as well on Windows).</param>
    /// <param name="fullPaths">Absolute paths checked after PATH.</param>
    public static string? Find(IEnumerable<string> names, IEnumerable<string>? fullPaths = null)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                foreach (var fileName in FileNames(name))
                {
                    var path = Path.Combine(directory, fileName);
                    if (File.Exists(path)) return path;
                }
            }
        }
        return fullPaths?.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> FileNames(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) yield return name + ".exe";
    }
}
