using Cuebot.Core.Chat.Moderation;
using Microsoft.Data.Sqlite;

namespace Cuebot.Tests.Moderation;

/// <summary>The SQLite mod log: append, hash chain, undo pairing, retention, reopening.</summary>
public sealed class ModLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-modlog-").FullName;
    private readonly ManualClock _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private ModLog _log;

    public ModLogTests() => _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static ModLogEntry Row(string action, string user = "u1", ModResult result = ModResult.Ok, int? seconds = null, string text = "") => new()
    {
        ActorKind = ModActorKind.Streamer,
        ActorName = "Streamer",
        Platform = "Twitch",
        Action = action,
        TargetUserId = user,
        TargetName = "Bob",
        MessageId = text.Length > 0 ? "m1" : "",
        MessageText = text,
        Reason = "spam",
        DurationAppliedSeconds = seconds,
        Result = result,
    };

    [Fact]
    public void Appends_rows_with_ids_times_and_a_hash_chain_that_survives_reopening()
    {
        var first = _log.Append(Row("ban"));
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = _log.Append(Row("delete", text: "buy followers") with { IncidentId = "incident-1" });

        Assert.NotEmpty(first.Id);
        Assert.Equal(first.Id, first.IncidentId);
        Assert.Equal("incident-1", second.IncidentId);
        Assert.Equal("", first.PrevHash);
        Assert.Equal(first.Hash, second.PrevHash);
        Assert.Equal(_time.GetUtcNow(), second.At);
        Assert.Null(_log.FindBrokenRow());

        _log.Dispose();
        _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);
        Assert.Equal([second.Id, first.Id], _log.Recent().Select(e => e.Id));
        Assert.Equal("buy followers", _log.Get(second.Id)!.MessageText);
        Assert.Equal([second.Id, first.Id], _log.ForTarget("twitch", "u1").Select(e => e.Id));
        Assert.Null(_log.FindBrokenRow());
    }

    [Fact]
    public void An_edited_or_removed_row_breaks_the_chain()
    {
        var first = _log.Append(Row("ban"));
        var second = _log.Append(Row("timeout", seconds: 60));
        var third = _log.Append(Row("warn"));
        _log.Dispose();

        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, ModLog.FileName)};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"UPDATE mod_actions SET reason = 'nothing to see' WHERE id = '{second.Id}'";
            cmd.ExecuteNonQuery();
        }
        _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);
        Assert.Equal(second.Id, _log.FindBrokenRow());
        _log.Dispose();

        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, ModLog.FileName)};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"DELETE FROM mod_actions WHERE id = '{second.Id}'";
            cmd.ExecuteNonQuery();
        }
        _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);
        Assert.Equal(third.Id, _log.FindBrokenRow());
        Assert.NotNull(first);
    }

    [Fact]
    public void Undo_pairs_an_unban_with_the_ban_or_timeout_still_in_force()
    {
        var oldTimeout = _log.Append(Row("timeout", seconds: 60));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(_log.FindInForce("Twitch", "u1"));   // ran out

        var failed = _log.Append(Row("ban", result: ModResult.Failed));
        Assert.Null(_log.FindInForce("Twitch", "u1"));
        var ban = _log.Append(Row("ban") with { PlatformRef = "BAN-1" });
        Assert.Equal(ban.Id, _log.FindInForce("Twitch", "u1")!.Id);

        var unban = _log.Append(Row("unban") with { Undoes = ban.Id });
        Assert.True(_log.MarkUndone(ban.Id, unban.Id));
        Assert.False(_log.MarkUndone(ban.Id, unban.Id));
        var undone = _log.Get(ban.Id)!;
        Assert.Equal((unban.Id, _time.GetUtcNow()), (undone.UndoneBy, undone.UndoneAt));
        Assert.Null(_log.FindInForce("Twitch", "u1"));
        // The undo pointer is outside the hash: marking a row undone keeps the chain intact.
        Assert.Null(_log.FindBrokenRow());
        Assert.NotNull(oldTimeout);
        Assert.NotNull(failed);
    }

    [Fact]
    public void Own_actions_are_found_so_platform_echoes_are_not_logged_twice()
    {
        var since = _time.GetUtcNow();
        _log.Append(Row("delete", text: "x"));
        _log.Append(Row("ban", "u2") with { ActorKind = ModActorKind.External });

        Assert.NotNull(_log.FindOwnSince("Twitch", "delete", "", "m1", since));
        Assert.Null(_log.FindOwnSince("Twitch", "ban", "u2", "", since));   // external rows are not Cuebot's own
        Assert.Null(_log.FindOwnSince("Twitch", "delete", "", "m1", since.AddMinutes(1)));
    }

    [Fact]
    public void Retention_blanks_message_text_timeouts_and_ended_bans_but_keeps_active_bans_and_the_chain()
    {
        var start = _time.GetUtcNow();
        var delete = _log.Append(Row("delete", "u1", text: "rude words"));
        var timeout = _log.Append(Row("timeout", "u2", seconds: 600));
        var activeBan = _log.Append(Row("ban", "u3"));
        var liftedBan = _log.Append(Row("ban", "u4"));
        var unban = _log.Append(Row("unban", "u4") with { Undoes = liftedBan.Id });
        _log.MarkUndone(liftedBan.Id, unban.Id);
        var mode = _log.Append(new ModLogEntry { Platform = "Twitch", Action = "mode", ActorName = "Streamer", Reason = "Slow mode on" });

        _time.Advance(TimeSpan.FromDays(61));
        Assert.Equal(1, _log.ApplyRetention(ModLogRetention.Default, _time.GetUtcNow()));
        Assert.Equal(("", "Bob"), (_log.Get(delete.Id)!.MessageText, _log.Get(delete.Id)!.TargetName));

        _time.Advance(TimeSpan.FromDays(120));   // 181 days
        _log.ApplyRetention(ModLogRetention.Default, _time.GetUtcNow());
        Assert.Equal(("", "", ""), (_log.Get(timeout.Id)!.TargetUserId, _log.Get(timeout.Id)!.TargetName, _log.Get(timeout.Id)!.Reason));
        Assert.Equal("u4", _log.Get(liftedBan.Id)!.TargetUserId);

        _time.Set(start.AddMonths(25));
        _log.ApplyRetention(ModLogRetention.Default, _time.GetUtcNow());
        Assert.Equal("u3", _log.Get(activeBan.Id)!.TargetUserId);   // still banned: kept
        Assert.True(_log.Get(liftedBan.Id)!.Redacted);
        Assert.Equal("", _log.Get(liftedBan.Id)!.TargetUserId);
        Assert.Equal("", _log.Get(unban.Id)!.TargetUserId);
        Assert.Equal("Slow mode on", _log.Get(mode.Id)!.Reason);
        Assert.Equal(6, _log.Recent().Count);
        Assert.Null(_log.FindBrokenRow());
    }

    [Fact]
    public void The_streamer_can_shorten_retention_but_not_lengthen_it()
    {
        Assert.Equal(new ModLogRetention(10, 180, 24), new ModLogRetention(10, 999, 99).Clamped());
        var delete = _log.Append(Row("delete", text: "rude"));
        _time.Advance(TimeSpan.FromDays(11));
        _log.ApplyRetention(new ModLogRetention(MessageTextDays: 10), _time.GetUtcNow());
        Assert.Equal("", _log.Get(delete.Id)!.MessageText);
    }

    [Fact]
    public void A_database_from_a_newer_cuebot_is_refused()
    {
        _log.Dispose();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, ModLog.FileName)};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ModLog(Path.Combine(_dir, ModLog.FileName), _time));
        _log = new ModLog(Path.Combine(_dir, "other.db"), _time);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset now) => _now = now;
}
