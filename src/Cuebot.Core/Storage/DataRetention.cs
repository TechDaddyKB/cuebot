namespace Cuebot.Core.Storage;

/// <summary>
/// Deletes data Cuebot only needs for a while: daily logs and saved problem reports after
/// <see cref="KeepDays"/> days, and cached emote images not used for as long (they download again when needed). The
/// mod log has its own, longer periods (<see cref="PruneModLog"/>).
/// </summary>
public static class DataRetention
{
    public const int KeepDays = 30;

    public static int PruneLogs(string logsFolder, DateTime now) => Prune(logsFolder, "cuebot-*.log", now);

    /// <summary>Prunes the data folder's reports and emote image cache; returns how many files were deleted.</summary>
    public static int PruneDataFolder(AppPaths paths, DateTime now) =>
        Prune(Path.Combine(paths.Shared, "reports"), "*", now)
        + Prune(Path.Combine(paths.Shared, "cache", "emote-images"), "*", now);

    /// <summary>
    /// Blanks personal details in the mod log past its own periods (deleted-message text 60 days, timeouts 180 days,
    /// bans 24 months after they end; the streamer can shorten them in moderation.json). Returns the rows changed.
    /// </summary>
    public static int PruneModLog(Chat.Moderation.ModLog log, Chat.Moderation.ModLogRetention retention, DateTimeOffset now) =>
        log.ApplyRetention(retention, now);

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
