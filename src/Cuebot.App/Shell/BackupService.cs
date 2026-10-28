using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Cuebot.Core;

namespace Cuebot.App.Shell;

public sealed record BackupInfo(string Path, DateTime Created, string Reason, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Zips the data folder into <c>backups/</c> as <c>cuebot-yyyyMMdd-HHmmss-reason[-vVersion].zip</c>, keeping the
/// newest N. Logs, the managed Python runtime, the backups themselves and transient files are left out.
/// A restore never touches live files: it is scheduled (<see cref="ScheduleRestore"/>) and applied at the next
/// start, before any store is open (<see cref="ApplyPendingRestore"/>), after a safety backup of the current data.
/// </summary>
public sealed class BackupService
{
    public const string FolderName = "backups";
    public const string PendingRestoreFile = "restore.pending";

    /// <summary>Subfolder of <see cref="Folder"/> for backups downloaded from Google Drive (never listed or pruned).</summary>
    public const string DownloadsFolderName = "downloads";
    private const string Prefix = "cuebot-";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Top-level folders never included in (or replaced by) a backup.</summary>
    public static readonly IReadOnlySet<string> ExcludedFolders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logs", "python", FolderName };

    /// <summary>Top-level files never included in (or replaced by) a backup.</summary>
    public static readonly IReadOnlySet<string> ExcludedFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PendingRestoreFile, SingleInstance.LockFileName, SingleInstance.SocketFileName };

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;

    public BackupService(AppPaths paths, Func<int> keep, Func<DateTime>? clock = null)
    {
        Root = paths.Root;
        Folder = Path.Combine(Root, FolderName);
        Keep = keep;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string Root { get; }

    public string Folder { get; }

    public Func<int> Keep { get; }

    /// <summary>Where backups downloaded for a restore go; outside the list, so pruning and uploads ignore them.</summary>
    public string DownloadsFolder => Path.Combine(Folder, DownloadsFolderName);

    public static string Version { get; } =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public event Action? Changed;

    /// <summary>Raised (on the creating thread) after a backup zip was written, e.g. to upload it.</summary>
    public event Action<BackupInfo>? Created;

    /// <summary>The pending restore's zip, if one is scheduled.</summary>
    public string? PendingRestore
    {
        get
        {
            var marker = Path.Combine(Root, PendingRestoreFile);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, Prefix + "*.zip")
            .Select(Describe)
            .OrderByDescending(b => b.Created)
            .ThenByDescending(b => File.GetLastWriteTimeUtc(b.Path))
            .ToArray();
    }

    /// <summary>Zips the data folder now and prunes old backups. <paramref name="reason"/> becomes part of the name.</summary>
    public BackupInfo Create(string reason) => Create(reason, prune: true);

    private BackupInfo Create(string reason, bool prune)
    {
        BackupInfo info;
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var safeReason = new string(reason.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '_').ToArray());
            var baseName = $"{Prefix}{_clock().ToString(StampFormat, CultureInfo.InvariantCulture)}-{safeReason}-v{Version}";
            var path = Path.Combine(Folder, baseName + ".zip");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(Folder, $"{baseName}-{n}.zip");

            var temp = path + ".tmp";
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in DataFiles())
                {
                    var entryName = Path.GetRelativePath(Root, file).Replace('\\', '/');
                    try
                    {
                        // Open with sharing so files that are being written (logs of other tools, stores) never block a backup.
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        using var target = entry.Open();
                        source.CopyTo(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished or is locked is skipped rather than failing the whole backup.
                    }
                }
            }
            File.Move(temp, path);
            info = Describe(path);
            if (prune) Prune();
        }
        Changed?.Invoke();
        Created?.Invoke(info);
        return info;
    }

    /// <summary>The data folder holds something worth backing up (a fresh folder does not).</summary>
    public bool HasData => DataFiles().Any();

    /// <summary>Deletes the oldest backups beyond the configured count.</summary>
    public void Prune()
    {
        var keep = Math.Max(1, Keep());
        var pending = PendingRestore;
        foreach (var old in List().Skip(keep))
        {
            if (pending is not null && string.Equals(Path.GetFullPath(old.Path), pending, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Marks <paramref name="zip"/> to be restored at the next start.</summary>
    public void ScheduleRestore(string zip)
    {
        if (!File.Exists(zip)) throw new FileNotFoundException("Backup not found", zip);
        using (ZipFile.OpenRead(zip)) { }   // fail now, not at the next start, when the file is not a zip
        File.WriteAllText(Path.Combine(Root, PendingRestoreFile), Path.GetFullPath(zip));
        Changed?.Invoke();
    }

    public void CancelRestore()
    {
        File.Delete(Path.Combine(Root, PendingRestoreFile));
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a scheduled restore. Call before any store is opened. Returns the safety backup made of the
    /// replaced data, or null when nothing was pending.
    /// </summary>
    public BackupInfo? ApplyPendingRestore()
    {
        var zip = PendingRestore;
        if (zip is null) return null;
        File.Delete(Path.Combine(Root, PendingRestoreFile));
        if (!File.Exists(zip)) throw new FileNotFoundException("The backup scheduled for restore no longer exists", zip);
        // Pruned only after the restore: the safety backup must not push the backup being restored out of the list.
        var safety = Create("pre-restore", prune: false);
        RestoreNow(zip);
        Prune();
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(zip)), Path.GetFullPath(DownloadsFolder), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zip); }   // a downloaded copy has served its purpose; Drive keeps the original
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return safety;
    }

    /// <summary>Replaces the data folder's contents (except excluded folders) with the backup's. No safety copy.</summary>
    public void RestoreNow(string zip)
    {
        lock (_gate)
        {
            using var archive = ZipFile.OpenRead(zip);
            var rootFull = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            // Validate every entry before deleting anything.
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
            foreach (var entry in entries)
            {
                var target = Path.GetFullPath(Path.Combine(Root, entry.FullName));
                if (!target.StartsWith(rootFull, StringComparison.Ordinal))
                    throw new InvalidDataException($"Backup entry {entry.FullName} points outside the data folder");
            }

            foreach (var file in DataFiles().ToArray()) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(Root).Where(d => !ExcludedFolders.Contains(Path.GetFileName(d))))
                Directory.Delete(dir, recursive: true);

            foreach (var entry in entries)
            {
                var top = entry.FullName.Split('/', 2)[0];
                if (ExcludedFolders.Contains(top) && entry.FullName.Contains('/')) continue;
                if (ExcludedFiles.Contains(entry.FullName)) continue;
                var target = Path.Combine(Root, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Every file that belongs in a backup.</summary>
    private IEnumerable<string> DataFiles()
    {
        foreach (var file in Directory.EnumerateFiles(Root))
        {
            var name = Path.GetFileName(file);
            if (ExcludedFiles.Contains(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (ExcludedFolders.Contains(Path.GetFileName(dir))) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) yield return file;
        }
    }

    private static BackupInfo Describe(string path)
    {
        var created = File.GetLastWriteTime(path);
        var reason = "";
        if (TryParseName(Path.GetFileName(path), out var stamp, out var parsedReason))
        {
            created = stamp;
            reason = parsedReason;
        }
        return new BackupInfo(path, created, reason, new FileInfo(path).Length);
    }

    /// <summary>Reads the time and reason out of a backup file name (<c>cuebot-yyyyMMdd-HHmmss-reason-vX.Y.Z.zip</c>).</summary>
    public static bool TryParseName(string fileName, out DateTime created, out string reason)
    {
        created = default;
        reason = "";
        if (!fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = fileName[..^4];
        if (name.Length <= Prefix.Length + StampFormat.Length
            || !DateTime.TryParseExact(name.AsSpan(Prefix.Length, StampFormat.Length), StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out created))
            return false;
        var rest = name[(Prefix.Length + StampFormat.Length)..].TrimStart('-');
        var v = rest.IndexOf("-v", StringComparison.Ordinal);
        reason = v >= 0 ? rest[..v] : rest;
        return true;
    }
}
