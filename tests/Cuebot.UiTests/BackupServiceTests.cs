using System.IO.Compression;
using Cuebot.App.Shell;
using Cuebot.Core;

namespace Cuebot.UiTests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-backup-").FullName;
    private readonly AppPaths _paths;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0);
    private int _keep = 10;

    public BackupServiceTests()
    {
        _paths = new AppPaths(_root);
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{\"v\":1}");
        File.WriteAllText(Path.Combine(_root, "globals.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "sounds"));
        File.WriteAllText(Path.Combine(_root, "sounds", "ding.wav"), "RIFF");
        File.WriteAllText(Path.Combine(_root, "logs", "cuebot-20261001.log"), "log line");
        Directory.CreateDirectory(Path.Combine(_root, "python", "bin"));
        File.WriteAllText(Path.Combine(_root, "python", "bin", "python3"), "binary");
        File.WriteAllText(Path.Combine(_root, "actions.json.tmp"), "partial");
    }

    private BackupService Service() => new(_paths, () => _keep, () => _now);

    private static string[] Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Backup_contains_data_but_not_logs_python_backups_or_temp_files()
    {
        var service = Service();
        service.Create("startup");
        _now = _now.AddMinutes(1);
        var second = service.Create("manual");

        Assert.Equal(["actions.json", "globals.json", "sounds/ding.wav"], Entries(second.Path));
        Assert.StartsWith("cuebot-20261001-120100-manual-v", second.Name);
        Assert.Equal("manual", second.Reason);
        Assert.Equal(_now, second.Created);
    }

    [Fact]
    public void Keeps_only_the_newest_n_backups()
    {
        _keep = 3;
        var service = Service();
        for (var i = 0; i < 5; i++)
        {
            service.Create("startup");
            _now = _now.AddMinutes(1);
        }
        var list = service.List();
        Assert.Equal(3, list.Count);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 4, 0), list[0].Created);   // newest first
        Assert.Equal(new DateTime(2026, 10, 1, 12, 2, 0), list[^1].Created);
    }

    [Fact]
    public void Two_backups_in_the_same_second_do_not_collide()
    {
        var service = Service();
        var a = service.Create("manual");
        var b = service.Create("manual");
        Assert.NotEqual(a.Path, b.Path);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void Scheduled_restore_is_applied_at_next_start_after_a_safety_backup()
    {
        var service = Service();
        var backup = service.Create("startup");

        // Data changes after the backup.
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{\"v\":2}");
        File.WriteAllText(Path.Combine(_root, "new-file.json"), "{}");
        File.Delete(Path.Combine(_root, "sounds", "ding.wav"));

        service.ScheduleRestore(backup.Path);
        Assert.Equal(backup.Path, service.PendingRestore);
        Assert.Equal("{\"v\":2}", File.ReadAllText(Path.Combine(_root, "actions.json")));   // nothing touched yet

        _now = _now.AddMinutes(5);
        var safety = Service().ApplyPendingRestore();   // "next start": a fresh service

        Assert.NotNull(safety);
        Assert.Equal("pre-restore", safety.Reason);
        Assert.Contains("new-file.json", Entries(safety.Path));
        Assert.Null(service.PendingRestore);
        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(_root, "actions.json")));
        Assert.False(File.Exists(Path.Combine(_root, "new-file.json")));
        Assert.True(File.Exists(Path.Combine(_root, "sounds", "ding.wav")));
        // Excluded folders survive a restore untouched.
        Assert.True(File.Exists(Path.Combine(_root, "logs", "cuebot-20261001.log")));
        Assert.True(File.Exists(Path.Combine(_root, "python", "bin", "python3")));
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void A_fresh_folder_has_nothing_to_back_up()
    {
        var fresh = Directory.CreateTempSubdirectory("cuebot-backup-fresh-").FullName;
        try
        {
            Assert.False(new BackupService(new AppPaths(fresh), () => 10).HasData);   // only an empty logs folder
            Assert.True(Service().HasData);
        }
        finally
        {
            Directory.Delete(fresh, recursive: true);
        }
    }

    [Fact]
    public void Nothing_pending_means_nothing_happens()
    {
        Assert.Null(Service().ApplyPendingRestore());
        Assert.Empty(Service().List());
    }

    [Fact]
    public void A_restore_can_be_cancelled()
    {
        var service = Service();
        service.ScheduleRestore(service.Create("manual").Path);
        service.CancelRestore();
        Assert.Null(service.PendingRestore);
        Assert.Null(service.ApplyPendingRestore());
    }

    [Fact]
    public void Scheduling_a_file_that_is_not_a_zip_fails_immediately()
    {
        var bogus = Path.Combine(_root, "not-a-zip.zip");
        File.WriteAllText(bogus, "nope");
        Assert.ThrowsAny<InvalidDataException>(() => Service().ScheduleRestore(bogus));
        Assert.Null(Service().PendingRestore);
    }

    [Fact]
    public void Restore_rejects_entries_outside_the_data_folder()
    {
        var evil = Path.Combine(_root, "backups", "evil.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(evil)!);
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
            w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => Service().RestoreNow(evil));
        Assert.True(File.Exists(Path.Combine(_root, "actions.json")));   // validated before deleting anything
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
