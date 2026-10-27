using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Variables;

namespace Cuebot.Tests.Variables;

public class VariableValuesTests
{
    [Theory]
    [InlineData("5", 5L)]
    [InlineData("-12", -12L)]
    [InlineData("0", 0L)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    public void Auto_types_numbers_and_booleans(string text, object expected) => Assert.Equal(expected, VariableValues.AutoType(text));

    [Fact]
    public void Auto_typed_decimals_keep_their_scale()
    {
        var value = VariableValues.AutoType("1.50");
        Assert.Equal(1.50m, Assert.IsType<decimal>(value));
        Assert.Equal("1.50", VariableResolver.Format(value));
        Assert.Equal(-0.5m, VariableValues.AutoType("-0.5"));
    }

    [Theory]
    [InlineData("007")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("1,000")]
    [InlineData("1e5")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("99999999999999999999999999999999")]
    [InlineData("")]
    public void Leaves_other_text_alone(string text) => Assert.Equal(text, VariableValues.AutoType(text));

    [Fact]
    public void Adds_keeping_integers_integral()
    {
        Assert.Equal(1L, VariableValues.Add(null, 1));
        Assert.Equal(6L, VariableValues.Add(5L, 1));
        Assert.Equal(4L, VariableValues.Add("5", -1));
        Assert.Equal(2.5m, VariableValues.Add(2L, 0.5m));
        Assert.Equal("2.50", VariableResolver.Format(VariableValues.Add(1.50m, 1)));
        Assert.Equal(0.3m, VariableValues.Add(0.1m, 0.2m));
        Assert.Equal(1L, VariableValues.Add("", 1));
        Assert.Throws<InvalidOperationException>(() => VariableValues.Add("abc", 1));
    }
}

public class GlobalVariablesTypedTests
{
    [Fact]
    public void Typed_values_survive_restart_and_old_string_files_still_load()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.File("globals.json"), """{ "legacy": "7", "flag": true }""");
        using (var globals = new GlobalVariables(paths, log))
        {
            Assert.Equal("7", globals.GetValue("legacy"));
            Assert.Equal(true, globals.GetValue("flag"));
            globals.SetValue("count", 5L, persist: true);
            globals.SetValue("price", 1.50m, persist: true);
            globals.SetValue("temp", "x", persist: false);
        }

        var json = File.ReadAllText(paths.File("globals.json"));
        Assert.Contains("\"count\": 5", json);
        Assert.Contains("\"price\": 1.50", json);
        using var reloaded = new GlobalVariables(paths, log);
        Assert.Equal(5L, reloaded.GetValue("count"));
        Assert.Equal("1.50", reloaded.Get("price"));
        Assert.Null(reloaded.GetValue("temp"));
        Assert.True(reloaded.IsPersisted("count"));
    }

    [Fact]
    public void Increments_atomically_and_raises_change_events()
    {
        using var log = new AppLog();
        using var globals = new GlobalVariables(null, log);
        var changes = new List<GlobalVariableChange>();
        var names = new List<string>();
        globals.VariableChanged += changes.Add;
        globals.Changed += names.Add;

        Assert.Equal(1L, globals.Increment("hits", 1, persist: false));
        Assert.Equal(11L, globals.Increment("HITS", 10, persist: false));
        Assert.Equal(8L, globals.Increment("hits", -3, persist: false));
        Assert.True(globals.Remove("hits"));
        Assert.False(globals.Remove("hits"));

        Assert.Equal(
            [VariableChangeKind.Created, VariableChangeKind.Updated, VariableChangeKind.Updated, VariableChangeKind.Deleted],
            changes.Select(c => c.Kind));
        Assert.All(changes, c => Assert.Equal("hits", c.Name));
        Assert.Equal(11L, changes[2].OldValue);
        Assert.Equal(4, names.Count);

        globals.Set("word", "abc", persist: false);
        Assert.Throws<InvalidOperationException>(() => globals.Increment("word", 1, persist: false));
    }

    [Fact]
    public void Parallel_increments_do_not_lose_updates()
    {
        using var log = new AppLog();
        using var globals = new GlobalVariables(null, log);
        Parallel.For(0, 1000, _ => globals.Increment("n", 1, persist: false));
        Assert.Equal(1000L, globals.GetValue("n"));
    }

    [Fact]
    public void Snapshots_split_persisted_and_temporary_and_clear_temporary()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        using var globals = new GlobalVariables(new AppPaths(dir.Path), log);
        globals.Set("b", "1", persist: true);
        globals.Set("a", "2", persist: true);
        globals.Set("t", "3", persist: false);

        Assert.Equal(["a", "b"], globals.Snapshot(persisted: true).Select(i => i.Name));
        Assert.Equal(["t"], globals.Snapshot(persisted: false).Select(i => i.Name));
        Assert.Equal(3, globals.Snapshot().Count);
        Assert.Equal(1, globals.ClearTemporary());
        Assert.Equal(2, globals.All().Count);

        globals.Set("a", "now temporary", persist: false);
        globals.Flush();
        using var reloaded = new GlobalVariables(new AppPaths(dir.Path), log);
        Assert.Null(reloaded.Get("a"));
        Assert.Equal("1", reloaded.Get("b"));
    }

    [Fact]
    public void Keeps_the_original_name_spelling()
    {
        using var log = new AppLog();
        using var globals = new GlobalVariables(null, log);
        globals.Set("MyCounter", "1", persist: false);
        globals.Set("mycounter", "2", persist: false);
        Assert.Equal("MyCounter", Assert.Single(globals.Snapshot()).Name);
    }
}

public class UserVariablesTests
{
    [Fact]
    public void Stores_per_platform_and_user_and_persists_only_persisted_values()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        using (var users = new UserVariables(paths, log))
        {
            users.Set("Twitch", "123", "Alice", "points", 10L, persist: true);
            users.Set("twitch", "123", "AliceRenamed", "mood", "happy", persist: false);
            users.Set("youtube", "123", "Bob", "points", 3L, persist: true);
            Assert.Equal(10L, users.Get("TWITCH", "123", "Points"));
            Assert.Equal(3L, users.Get("youtube", "123", "points"));
            Assert.Null(users.Get("kick", "123", "points"));
            Assert.Equal("123", users.FindUserId("twitch", "alicerenamed"));
        }

        var json = File.ReadAllText(paths.File(UserVariables.FileName));
        Assert.Contains("\"userName\": \"AliceRenamed\"", json);
        Assert.DoesNotContain("mood", json);

        using var reloaded = new UserVariables(paths, log);
        Assert.Equal(10L, reloaded.Get("twitch", "123", "points"));
        Assert.Null(reloaded.Get("twitch", "123", "mood"));
        Assert.Equal(["twitch", "youtube"], reloaded.Platforms());
        var row = Assert.Single(reloaded.Snapshot("twitch"));
        Assert.Equal(("AliceRenamed", "points", "10", true), (row.UserName, row.Name, row.Text, row.Persisted));
    }

    [Fact]
    public void Increment_delete_and_clear_raise_events()
    {
        using var log = new AppLog();
        using var users = new UserVariables(null, log);
        var changes = new List<UserVariableChange>();
        users.Changed += changes.Add;

        Assert.Equal(5L, users.Increment("twitch", "1", "A", "points", 5, persist: true));
        Assert.Equal(3L, users.Increment("twitch", "1", null, "points", -2, persist: true));
        users.Set("twitch", "2", "B", "points", 1L, persist: false);
        users.Set("kick", "3", "C", "points", 1L, persist: false);
        users.Set("kick", "3", "C", "other", "x", persist: true);

        Assert.Equal(2, users.ClearTemporary());
        Assert.Equal(1, users.DeleteForAllUsers("points"));
        Assert.True(users.Delete("kick", "3", "OTHER"));
        Assert.False(users.Delete("kick", "3", "other"));
        Assert.Empty(users.Snapshot());

        Assert.Equal(VariableChangeKind.Created, changes[0].Kind);
        Assert.Equal((VariableChangeKind.Updated, (object?)5L, (object?)3L), (changes[1].Kind, changes[1].OldValue, changes[1].NewValue));
        Assert.Equal("A", changes[1].UserName);
        Assert.Equal(4, changes.Count(c => c.Kind == VariableChangeKind.Deleted));
    }

    [Fact]
    public void Platform_comes_from_arguments_or_event_type()
    {
        Assert.Equal("rumble", UserVariables.PlatformFromArgs(new Dictionary<string, object?> { ["platform"] = "Rumble" }));
        Assert.Equal("twitch", UserVariables.PlatformFromArgs(new Dictionary<string, object?> { ["eventType"] = "twitch.follow" }));
        Assert.Null(UserVariables.PlatformFromArgs(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Targets_the_triggering_user_or_an_explicit_one()
    {
        using var log = new AppLog();
        using var users = new UserVariables(null, log);
        users.Set("twitch", "99", "Known", "x", 1L, persist: false);
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventType"] = "twitch.chatMessage",
            ["userId"] = "42",
            ["userName"] = "Chatter",
        };

        Assert.Equal(new UserTarget("twitch", "42", "Chatter"), UserTarget.Find(users, args, "", "", ""));
        Assert.Equal(new UserTarget("twitch", "42", "Chatter"), UserTarget.Find(users, args, "%platform%", "%userId%", ""));
        Assert.Equal(new UserTarget("twitch", "99", "known"), UserTarget.Find(users, args, "", "", "known"));
        Assert.Equal(new UserTarget("kick", "7", null), UserTarget.Find(users, args, "Kick", "7", ""));
        Assert.Null(UserTarget.Find(users, args, "", "", "stranger"));
        Assert.Null(UserTarget.Find(users, new Dictionary<string, object?>(), "", "", ""));
    }
}

public class VariableViewerTests
{
    [Fact]
    public void Lists_scopes_and_forwards_changes()
    {
        using var log = new AppLog();
        using var globals = new GlobalVariables(null, log);
        using var users = new UserVariables(null, log);
        using var viewer = new VariableViewer(globals, users);
        var changes = new List<VariableViewerChange>();
        viewer.Changed += changes.Add;

        viewer.SetGlobal(" kept ", "5", persisted: true);
        viewer.SetGlobal("temp", "007", persisted: false);
        viewer.SetUserVariable("twitch", "1", "A", "points", "12", persisted: true);

        Assert.Equal(5L, Assert.Single(viewer.PersistedGlobals()).Value);
        Assert.Equal("007", Assert.Single(viewer.TemporaryGlobals()).Value);
        Assert.Equal(12L, Assert.Single(viewer.UserVariables("twitch")).Value);
        Assert.Equal(["twitch"], viewer.UserPlatforms());
        Assert.Equal(
            [VariableScope.Persisted, VariableScope.Temporary, VariableScope.User],
            changes.Select(c => c.Scope));

        Assert.Equal(1, viewer.DeleteUserVariableForAllUsers("points", "twitch"));
        Assert.Equal(1, viewer.DeleteAllGlobals(persisted: true));
        Assert.True(viewer.DeleteGlobal("temp"));
        Assert.Equal(6, changes.Count);
    }
}
