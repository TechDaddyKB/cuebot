using Cuebot.App.Diagnostics;
using Cuebot.Core;

namespace Cuebot.UiTests.Support;

/// <summary>Crash files: written from a real exception, read back, offered once, scrubbed log tail, pruning.</summary>
public sealed class CrashCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-crash-").FullName;
    private readonly AppPaths _paths;

    public CrashCaptureTests() => _paths = new AppPaths(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Exception Nested()
    {
        try
        {
            try { throw new IOException("disk said no"); }
            catch (IOException inner) { throw new InvalidOperationException("Saving failed", inner); }
        }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void Crash_file_round_trips_with_chain_environment_connections_and_scrubbed_log()
    {
        File.WriteAllLines(Path.Combine(_paths.Logs, "cuebot-20261026.log"), ["10:00:00 [Info] Cuebot: yesterday"]);
        File.WriteAllLines(Path.Combine(_paths.Logs, "cuebot-20261027.log"),
            Enumerable.Range(0, 250).Select(i => $"12:00:00 [Info] Twitch: line {i}").Append("12:00:01 [Info] OBS: password=hunter2secret"));

        var time = new DateTimeOffset(2026, 10, 27, 12, 0, 1, TimeSpan.FromHours(2));
        var path = CrashCapture.WriteCrash(_paths, Nested(), "UI thread", true,
            [new ConnectionInfo("Twitch", "Connected", ""), new ConnectionInfo("OBS", "Error", "")], time, "Wayland");

        Assert.Equal(Path.Combine(_dir, "crashes"), Path.GetDirectoryName(path));
        Assert.Matches(@"crash-\d{8}-\d{6}-\d{3}\.json$", path);
        var record = CrashStore.Read(path)!;
        Assert.Equal(time, record.Time);
        Assert.Equal("UI thread", record.Source);
        Assert.Equal("System.InvalidOperationException", record.Exception.Type);
        Assert.Equal("Saving failed", record.Exception.Message);
        Assert.Contains(nameof(Nested), record.Exception.StackTrace);
        Assert.Equal("System.IO.IOException", Assert.Single(record.Exception.Inner).Type);
        Assert.Equal("disk said no", record.Exception.Root.Message);
        Assert.Equal("Wayland", record.Environment.DisplayBackend);
        Assert.False(string.IsNullOrEmpty(record.Environment.DotNet));
        Assert.Equal(2, record.Connections.Count);
        Assert.Equal(LogTail.DefaultLines, record.Log.Count);
        Assert.Equal("12:00:01 [Info] OBS: password=[redacted]", record.Log[^1]);
        Assert.Contains("InvalidOperationException: Saving failed", record.Exception.Format());
        Assert.Contains("---> (inner)", record.Exception.Format());
        Assert.Equal("InvalidOperationException: Saving failed", record.Summary);
    }

    [Fact]
    public void Aggregate_exceptions_keep_every_inner_exception()
    {
        var info = ExceptionInfo.From(new AggregateException(new TimeoutException("a"), new ArgumentException("b")));
        Assert.Equal(["System.TimeoutException", "System.ArgumentException"], info.Inner.Select(i => i.Type));
    }

    [Fact]
    public void Unseen_crashes_are_offered_until_marked_seen_and_old_files_are_pruned()
    {
        var store = new CrashStore(_paths);
        Assert.Null(store.LatestUnseen());
        Assert.Null(store.Latest());
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        string last = "";
        for (var i = 0; i < CrashStore.Keep + 5; i++)
            last = store.Write(new CrashRecord { Time = start.AddMinutes(i), Exception = new ExceptionInfo { Type = "X", Message = $"#{i}" } });
        Assert.Equal(CrashStore.Keep, Directory.GetFiles(store.Folder).Length);
        Assert.Equal(last, store.LatestUnseen());

        store.MarkAllSeen();
        Assert.Null(store.LatestUnseen());
        Assert.EndsWith(".seen.json", store.Latest());
        Assert.Equal($"#{CrashStore.Keep + 4}", CrashStore.Read(store.Latest()!)!.Exception.Message);
    }

    [Fact]
    public void Unreadable_crash_file_reads_as_null()
    {
        var store = new CrashStore(_paths);
        Directory.CreateDirectory(store.Folder);
        var path = Path.Combine(store.Folder, "crash-20261027-000000-000.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(CrashStore.Read(path));
        Assert.Equal(path, store.LatestUnseen());
    }

    [Fact]
    public void Os_release_pretty_name()
    {
        Assert.Equal("Arch Linux", EnvironmentInfo.ParsePrettyName("NAME=\"Arch Linux\"\nPRETTY_NAME=\"Arch Linux\"\nID=arch\n"));
        Assert.Equal("Fedora", EnvironmentInfo.ParsePrettyName("NAME=Fedora\nID=fedora"));
        Assert.Null(EnvironmentInfo.ParsePrettyName(null));
    }

    [Fact]
    public void Environment_on_linux_reports_session_desktop_and_distro()
    {
        var env = new Cuebot.App.Updates.PackagingEnvironment(false, true,
            name => name switch
            {
                "XDG_SESSION_TYPE" => "wayland",
                "WAYLAND_DISPLAY" => "wayland-0",
                "DISPLAY" => ":0",
                "XDG_CURRENT_DESKTOP" => "KDE",
                "APPIMAGE" => "/home/u/Cuebot.AppImage",
                _ => null,
            }, _ => false, "/tmp/.mount_abc/usr/lib/cuebot", System.Runtime.InteropServices.Architecture.X64);
        var snapshot = EnvironmentInfo.Collect("Wayland", env, () => "PRETTY_NAME=\"CachyOS\"");
        Assert.Equal("wayland (WAYLAND_DISPLAY set, DISPLAY set)", snapshot.Session);
        Assert.Equal("KDE", snapshot.Desktop);
        Assert.Equal("CachyOS", snapshot.Distro);
        Assert.Equal("AppImage", snapshot.Package);
        Assert.Contains(snapshot.Rows(), r => r.Label == "Desktop" && r.Value == "KDE");
    }
}
