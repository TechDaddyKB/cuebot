namespace Cuebot.Core.Storage;

/// <summary>
/// Deletes data Cuebot only needs for a while: daily logs and saved problem reports after
/// <see cref="KeepDays"/> days, and cached emote images not used for as long (they download again when needed).
/// </summary>
public static class DataRetention
{
    public const int KeepDays = 30;

    public static int PruneLogs(string logsFolder, DateTime now) => Prune(logsFolder, "cuebot-*.log", now);

    /// <summary>Prunes the data folder's reports and emote image cache; returns how many files were deleted.</summary>
    public static int PruneDataFolder(AppPaths paths, DateTime now) =>
        Prune(Path.Combine(paths.Root, "reports"), "*", now)
        + Prune(Path.Combine(paths.Root, "cache", "emote-images"), "*", now);

    private static int Prune(string folder, string pattern, DateTime now)
    {
        if (!Directory.Exists(folder)) return 0;
        var cutoff = now - TimeSpan.FromDays(KeepDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(folder, pattern))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // in use; next time
        }
        return deleted;
    }
}
