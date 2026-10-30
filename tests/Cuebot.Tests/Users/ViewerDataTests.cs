using Cuebot.Core;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Quotes;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;

namespace Cuebot.Tests.Users;

public sealed class ViewerDataTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-viewers-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();

    public ViewerDataTests()
    {
        Directory.CreateDirectory(_dir);
        // Three viewers: a recent and a stale YouTube viewer, and a stale Twitch viewer.
        File.WriteAllText(Path.Combine(_dir, "users.json"), System.Text.Json.JsonSerializer.Serialize(new UsersFile
        {
            Users =
            [
                User("youtube", "UCrecent", "Recent", Now.AddDays(-2)),
                User("youtube", "UCstale", "Stale", Now.AddDays(-45)),
                User("twitch", "111", "oldtwitch", Now.AddDays(-400)),
            ],
        }, Json.Options));
    }

    public void Dispose()
    {
        SecretStore.ResetCache();
        Directory.Delete(_dir, recursive: true);
    }

    private static UserRecord User(string platform, string id, string name, DateTimeOffset seen) => new()
    {
        Platform = platform, UserId = id, UserName = name, DisplayName = name, FirstSeen = seen, LastSeen = seen, MessageCount = 3,
    };

    private sealed class Stores : IDisposable
    {
        public required UserRegistry Users { get; init; }
        public required UserVariables Variables { get; init; }
        public required GroupStore Groups { get; init; }
        public required CommandStore Commands { get; init; }
        public required QuoteStore Quotes { get; init; }
        public required ViewerData Viewers { get; init; }

        public void Dispose()
        {
            Viewers.Dispose();
            Users.Dispose();
            Variables.Dispose();
            Groups.Dispose();
            Commands.Dispose();
            Quotes.Dispose();
        }
    }

    private Stores Open()
    {
        var paths = new AppPaths(_dir);
        var bus = new EventBus(_log);
        var users = new UserRegistry(paths, bus, _log);
        var variables = new UserVariables(paths, _log);
        var groups = new GroupStore(paths, _log, bus);
        var commands = new CommandStore(paths, _log);
        var quotes = new QuoteStore(paths, _log, bus);
        return new Stores
        {
            Users = users, Variables = variables, Groups = groups, Commands = commands, Quotes = quotes,
            Viewers = new ViewerData([users, variables, groups, commands, quotes], users, _log),
        };
    }

    private static void Seed(Stores s, Guid command)
    {
        foreach (var (platform, id, name) in new[] { ("youtube", "UCrecent", "Recent"), ("youtube", "UCstale", "Stale"), ("twitch", "111", "oldtwitch") })
        {
            s.Variables.Set(platform, id, name, "points", 100, persist: true);
            s.Groups.AddUser("regulars", platform, id, name, createGroup: true);
            s.Commands.Increment(command, CommandStore.UserKey(platform, id, name));
            s.Quotes.Add($"something {name} said", name, id, platform);
        }
    }

    [Fact]
    public void Forgetting_a_viewer_removes_them_from_every_store()
    {
        using var s = Open();
        var command = new CommandDefinition();
        s.Commands.Save(command);
        Seed(s, command.Id);

        var result = s.Viewers.Forget("youtube", "UCstale", "Stale");

        Assert.Equal(5, result.PerStore.Count);   // users, variables, groups, command counters, quotes
        Assert.Null(s.Users.Get("youtube", "UCstale"));
        Assert.Empty(s.Variables.GetAll("youtube", "UCstale"));
        Assert.DoesNotContain(s.Groups.All().Single().Users, m => m.UserId == "UCstale");
        Assert.Equal(0, s.Commands.GetUserCounter(command.Id, CommandStore.UserKey("youtube", "UCstale", "Stale")));
        var quote = s.Quotes.All().Single(q => q.Text.Contains("Stale"));
        Assert.Equal("", quote.User);   // the quote stays, without who said it
        Assert.Equal("", quote.UserId);

        // Others are untouched.
        Assert.NotNull(s.Users.Get("youtube", "UCrecent"));
        Assert.Equal(100m, Convert.ToDecimal(s.Variables.Get("twitch", "111", "points")));
    }

    [Fact]
    public void YouTube_viewers_not_seen_for_30_days_are_forgotten_and_others_kept()
    {
        using var s = Open();
        var command = new CommandDefinition();
        s.Commands.Save(command);
        Seed(s, command.Id);

        s.Viewers.ApplyYouTubeRetention(Now);

        Assert.Null(s.Users.Get("youtube", "UCstale"));
        Assert.Empty(s.Variables.GetAll("youtube", "UCstale"));
        Assert.NotNull(s.Users.Get("youtube", "UCrecent"));
        Assert.Equal(100m, Convert.ToDecimal(s.Variables.Get("youtube", "UCrecent", "points")));
        // Other platforms keep their viewers however old.
        Assert.NotNull(s.Users.Get("twitch", "111"));
        Assert.Equal(100m, Convert.ToDecimal(s.Variables.Get("twitch", "111", "points")));
    }

    [Fact]
    public void Forgotten_viewers_stay_forgotten_after_a_restart()
    {
        using (var s = Open())
        {
            var command = new CommandDefinition();
            s.Commands.Save(command);
            Seed(s, command.Id);
            s.Viewers.Forget("youtube", "UCstale", "Stale");
            s.Users.Flush();
            s.Variables.Flush();
        }
        using var reopened = Open();
        Assert.Null(reopened.Users.Get("youtube", "UCstale"));
        Assert.Empty(reopened.Variables.GetAll("youtube", "UCstale"));
        Assert.NotNull(reopened.Users.Get("youtube", "UCrecent"));
    }

    [Fact]
    public void Old_logs_and_reports_are_deleted_and_recent_ones_kept()
    {
        var paths = new AppPaths(_dir);
        var reports = Directory.CreateDirectory(Path.Combine(_dir, "reports")).FullName;
        string Make(string folder, string name, int ageDays)
        {
            var file = Path.Combine(folder, name);
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, Now.LocalDateTime.AddDays(-ageDays));
            return file;
        }
        var oldLog = Make(paths.Logs, "cuebot-20260901.log", 58);
        var newLog = Make(paths.Logs, "cuebot-20261028.log", 1);
        var oldReport = Make(reports, "report-old.md", 31);
        var newReport = Make(reports, "report-new.md", 3);

        Assert.Equal(1, DataRetention.PruneLogs(paths.Logs, Now.LocalDateTime));
        Assert.Equal(1, DataRetention.PruneDataFolder(paths, Now.LocalDateTime));
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.False(File.Exists(oldReport));
        Assert.True(File.Exists(newReport));
    }
}
