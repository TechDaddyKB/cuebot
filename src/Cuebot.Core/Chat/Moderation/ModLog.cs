using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Cuebot.Core.Chat.Moderation;

/// <summary>Who made a moderation action (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 6.1).</summary>
public enum ModActorKind
{
    /// <summary>The streamer, on this computer.</summary>
    Streamer,

    /// <summary>A mod portal member.</summary>
    Member,

    /// <summary>A Cuebot action or rule.</summary>
    Automation,

    /// <summary>Seen in a platform event: done on the platform's website or by another tool.</summary>
    External,
}

/// <summary>The actor of a moderation action: kind, the name at the time, and for members their id and device.</summary>
public sealed record ModActor(ModActorKind Kind, string Name, string? MemberId = null, string? DeviceId = null)
{
    public static ModActor Streamer { get; } = new(ModActorKind.Streamer, "Streamer");

    public static ModActor Member(string memberId, string name, string? deviceId = null) => new(ModActorKind.Member, name, memberId, deviceId);

    public static ModActor Automation(string name) => new(ModActorKind.Automation, name);

    public static ModActor External(string name) => new(ModActorKind.External, name);
}

/// <summary>How a logged action ended.</summary>
public enum ModResult
{
    Ok,
    Failed,

    /// <summary>Not sent: no API, quota, or refused by a safety rule.</summary>
    Skipped,

    /// <summary>A mod confirmed doing it by hand on the website.</summary>
    Manual,
}

/// <summary>One row of the mod log: one platform call (section 6.1).</summary>
public sealed record ModLogEntry
{
    public string Id { get; init; } = "";

    /// <summary>Groups the rows of one decision.</summary>
    public string IncidentId { get; init; } = "";

    public DateTimeOffset At { get; init; }
    public ModActorKind ActorKind { get; init; }
    public string? ActorMemberId { get; init; }
    public string ActorName { get; init; } = "";
    public string? ActorDeviceId { get; init; }
    public string Platform { get; init; } = "";

    /// <summary>The YouTube live chat id where relevant.</summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// <c>ban</c>, <c>timeout</c>, <c>unban</c>, <c>delete</c>, <c>warn</c>, <c>clear</c>, <c>mode</c>, <c>pin</c>, <c>unpin</c>;
    /// phase 3: <c>shield</c>, <c>held-approve</c>, <c>held-deny</c>, <c>suspicious-monitor</c>, <c>suspicious-restrict</c>,
    /// <c>suspicious-clear</c>, <c>unban-request-approve</c>, <c>unban-request-deny</c>, <c>term-add</c>, <c>term-remove</c>;
    /// seen only from the platform: <c>vip</c>, <c>unvip</c>, <c>mod</c>, <c>unmod</c>, <c>raid</c>, <c>unraid</c>,
    /// <c>permit-add</c>, <c>permit-remove</c> and <c>shared-</c> plus ban, timeout, unban or delete (another channel of a shared chat).
    /// </summary>
    public string Action { get; init; } = "";

    public string TargetUserId { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string MessageId { get; init; } = "";

    /// <summary>The deleted message, so a reviewer can judge the action; blanked after the retention period.</summary>
    public string MessageText { get; init; } = "";

    public int? DurationRequestedSeconds { get; init; }
    public int? DurationAppliedSeconds { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>Set when this row exists because of an identity link (phase 5).</summary>
    public string? ViaLinkId { get; init; }

    /// <summary>The platform's id for the action: the YouTube ban id, needed to unban.</summary>
    public string? PlatformRef { get; init; }

    public ModResult Result { get; init; }

    /// <summary>The platform's error, or why the action was skipped.</summary>
    public string Error { get; init; } = "";

    /// <summary>For an unban: the row it reverses.</summary>
    public string? Undoes { get; init; }

    /// <summary>The row that reversed this one.</summary>
    public string? UndoneBy { get; init; }

    public DateTimeOffset? UndoneAt { get; init; }

    /// <summary>Personal fields were blanked by retention; the row's content can no longer be checked against its hash.</summary>
    public bool Redacted { get; init; }

    public string PrevHash { get; init; } = "";
    public string Hash { get; init; } = "";

    /// <summary>A ban not lifted, or a timeout not lifted that has not run out yet.</summary>
    public bool IsInForce(DateTimeOffset now) =>
        Result == ModResult.Ok && UndoneBy is null
        && (Action == "ban" || (Action == "timeout" && At.AddSeconds(DurationAppliedSeconds ?? 0) > now));
}

/// <summary>How long the mod log keeps personal details (section 9.2, decision 8). The streamer can only shorten them.</summary>
public sealed record ModLogRetention(int MessageTextDays = 60, int TimeoutDays = 180, int BanMonthsAfterEnd = 24)
{
    public static ModLogRetention Default { get; } = new();

    /// <summary>The streamer's choice, never longer than the defaults and at least one day.</summary>
    public ModLogRetention Clamped() => new(
        Math.Clamp(MessageTextDays, 1, Default.MessageTextDays),
        Math.Clamp(TimeoutDays, 1, Default.TimeoutDays),
        Math.Clamp(BanMonthsAfterEnd, 1, Default.BanMonthsAfterEnd));
}

/// <summary>
/// The append-only mod log: one SQLite file in the profile's data folder (<see cref="FileName"/>), so backups include it.
/// Rows are hash-chained (each row's hash covers the previous row's hash and its own content), which makes edits and
/// gaps detectable, not impossible. Two things change a row after it is written: the undo pointer (<c>undone_by</c>,
/// outside the hash) and retention, which blanks personal fields and marks the row redacted (the chain still checks;
/// the blanked content no longer can). Schema version 2 adds the identity link and note tables (phase 4,
/// <c>ModLog.Identity.cs</c>); a version 1 file is upgraded in place. Thread-safe.
/// </summary>
public sealed partial class ModLog : IDisposable
{
    public const string FileName = "modlog.db";

    /// <summary>
    /// <c>PRAGMA user_version</c>. 1: <c>mod_actions</c>. 2 (phase 4): <c>identity_persons</c>, <c>identity_accounts</c>,
    /// <c>identity_confirmations</c>, <c>identity_evidence</c>, <c>identity_rejections</c> and <c>mod_notes</c>.
    /// </summary>
    public const int SchemaVersion = 2;

    private const string Columns =
        "id, incident_id, at, actor_kind, actor_member_id, actor_name, actor_device_id, platform, channel_id, action, " +
        "target_user_id, target_name, message_id, message_text, duration_requested, duration_applied, reason, via_link_id, " +
        "platform_ref, result, error, undoes, undone_by, undone_at, redacted, content_hash, prev_hash, hash";

    private readonly SqliteConnection _db;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private bool _disposed;

    public ModLog(string path, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Path = path;
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        // No pooling: the file is closed on Dispose (tests, restores). Rollback journal (not WAL): one file to back up.
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Migrate();
    }

    public string Path { get; }

    /// <summary>Raised after a row was added or marked undone (on the caller's thread).</summary>
    public event Action<ModLogEntry>? Changed;

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"The mod log was written by a newer Cuebot (version {version}); update Cuebot.");
        if (version == SchemaVersion) return;
        if (version < 1)
            Execute("""
            CREATE TABLE IF NOT EXISTS mod_actions (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                incident_id TEXT NOT NULL,
                at INTEGER NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_member_id TEXT,
                actor_name TEXT NOT NULL,
                actor_device_id TEXT,
                platform TEXT NOT NULL,
                channel_id TEXT,
                action TEXT NOT NULL,
                target_user_id TEXT NOT NULL,
                target_name TEXT NOT NULL,
                message_id TEXT NOT NULL,
                message_text TEXT NOT NULL,
                duration_requested INTEGER,
                duration_applied INTEGER,
                reason TEXT NOT NULL,
                via_link_id TEXT,
                platform_ref TEXT,
                result TEXT NOT NULL,
                error TEXT NOT NULL,
                undoes TEXT,
                undone_by TEXT,
                undone_at INTEGER,
                redacted INTEGER NOT NULL DEFAULT 0,
                content_hash TEXT NOT NULL,
                prev_hash TEXT NOT NULL,
                hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_mod_actions_target ON mod_actions(platform, target_user_id);
            CREATE INDEX IF NOT EXISTS ix_mod_actions_at ON mod_actions(at);
            PRAGMA user_version = 1;
            """);
        if (version < 2) MigrateIdentity();
    }

    /// <summary>
    /// Appends a row: fills in the id (when empty), the incident (its own id when empty), the time, and the hashes.
    /// Returns the stored row.
    /// </summary>
    public ModLogEntry Append(ModLogEntry draft)
    {
        ModLogEntry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            var id = draft.Id.Length > 0 ? draft.Id : NewId();
            var prev = Scalar("SELECT hash FROM mod_actions ORDER BY seq DESC LIMIT 1") as string ?? "";
            entry = draft with
            {
                Id = id,
                IncidentId = draft.IncidentId.Length > 0 ? draft.IncidentId : id,
                At = draft.At == default ? _time.GetUtcNow() : draft.At,
                UndoneBy = null,
                UndoneAt = null,
                Redacted = false,
                PrevHash = prev,
            };
            var content = ContentHash(entry);
            entry = entry with { Hash = ChainHash(prev, content) };
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"INSERT INTO mod_actions ({Columns}) VALUES ({string.Join(", ", Columns.Split(", ").Select(c => "$" + c))})";
            Bind(cmd, entry, content);
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke(entry);
        return entry;
    }

    /// <summary>Records that <paramref name="undoneBy"/> reversed the row <paramref name="id"/>. False when it was already undone.</summary>
    public bool MarkUndone(string id, string undoneBy)
    {
        ModLogEntry? entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE mod_actions SET undone_by = $by, undone_at = $at WHERE id = $id AND undone_by IS NULL";
            cmd.Parameters.AddWithValue("$by", undoneBy);
            cmd.Parameters.AddWithValue("$at", _time.GetUtcNow().ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) return false;
            entry = GetLocked(id);
        }
        if (entry is not null) Changed?.Invoke(entry);
        return true;
    }

    public ModLogEntry? Get(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return GetLocked(id);
        }
    }

    /// <summary>The newest rows first, optionally for one platform.</summary>
    public IReadOnlyList<ModLogEntry> Recent(int limit = 200, string? platform = null) =>
        Query(platform is null ? "" : "WHERE platform = $platform COLLATE NOCASE", limit, ("$platform", platform));

    /// <summary>The rows about one account, newest first.</summary>
    public IReadOnlyList<ModLogEntry> ForTarget(string platform, string userId, int limit = 100) =>
        Query("WHERE platform = $platform COLLATE NOCASE AND target_user_id = $user", limit, ("$platform", platform), ("$user", userId));

    /// <summary>The newest ban or timeout of this account that is still in force, if any (what an unban reverses).</summary>
    public ModLogEntry? FindInForce(string platform, string userId)
    {
        var now = _time.GetUtcNow();
        return Query("WHERE platform = $platform COLLATE NOCASE AND target_user_id = $user AND action IN ('ban', 'timeout') " +
                     "AND result = 'Ok' AND undone_by IS NULL", 20, ("$platform", platform), ("$user", userId))
            .FirstOrDefault(e => e.IsInForce(now));
    }

    /// <summary>
    /// The newest row of <paramref name="action"/> on this account or message since <paramref name="since"/> that Cuebot
    /// itself made (not <see cref="ModActorKind.External"/>): a platform event for it is Cuebot's own action echoed back.
    /// </summary>
    public ModLogEntry? FindOwnSince(string platform, string action, string targetUserId, string messageId, DateTimeOffset since)
    {
        var where = "WHERE platform = $platform COLLATE NOCASE AND action = $action AND actor_kind <> 'External' AND at >= $since AND result = 'Ok' AND " +
                    (messageId.Length > 0 ? "message_id = $key" : "target_user_id = $key");
        return Query(where, 1, ("$platform", platform), ("$action", action), ("$since", since.ToUnixTimeMilliseconds()),
            ("$key", messageId.Length > 0 ? messageId : targetUserId)).FirstOrDefault();
    }

    /// <summary>
    /// Rows of <paramref name="action"/> that Cuebot itself made (not <see cref="ModActorKind.External"/>) and that
    /// worked, since <paramref name="since"/>, newest first: the candidates a platform event may be the echo of.
    /// </summary>
    public IReadOnlyList<ModLogEntry> OwnSince(string platform, string action, DateTimeOffset since, int limit = 50) =>
        Query("WHERE platform = $platform COLLATE NOCASE AND action = $action AND actor_kind <> 'External' AND at >= $since AND result = 'Ok'",
            limit, ("$platform", platform), ("$action", action), ("$since", since.ToUnixTimeMilliseconds()));

    /// <summary>Checks every row's link to the previous one and the content of rows not redacted. Null when intact.</summary>
    public string? FindBrokenRow()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM mod_actions ORDER BY seq";
            using var reader = cmd.ExecuteReader();
            string? prev = null;
            while (reader.Read())
            {
                var entry = Read(reader);
                var content = reader.GetString(25);
                // The oldest remaining row anchors the chain.
                if (prev is not null && entry.PrevHash != prev) return entry.Id;
                if (entry.Hash != ChainHash(entry.PrevHash, content)) return entry.Id;
                if (!entry.Redacted && content != ContentHash(entry)) return entry.Id;
                prev = entry.Hash;
            }
            return null;
        }
    }

    /// <summary>
    /// Blanks personal details past the retention periods: deleted-message text after
    /// <see cref="ModLogRetention.MessageTextDays"/>; the person (id, name, message, reason) of timeouts, warnings and
    /// deletions after <see cref="ModLogRetention.TimeoutDays"/>; of bans and unbans
    /// <see cref="ModLogRetention.BanMonthsAfterEnd"/> months after the ban ended (an active ban keeps its row). Rows
    /// stay, so the chain and the counts stay; returns how many rows changed.
    /// </summary>
    public int ApplyRetention(ModLogRetention retention, DateTimeOffset now)
    {
        var r = retention.Clamped();
        var text = now.AddDays(-r.MessageTextDays).ToUnixTimeMilliseconds();
        var shortRows = now.AddDays(-r.TimeoutDays).ToUnixTimeMilliseconds();
        var bans = now.AddMonths(-r.BanMonthsAfterEnd).ToUnixTimeMilliseconds();
        const string person = "target_user_id = '', target_name = '', message_id = '', message_text = '', reason = '', platform_ref = NULL, redacted = 1";
        const string hasPerson = "(target_user_id <> '' OR target_name <> '' OR message_text <> '')";
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction();
            var changed = Execute($"UPDATE mod_actions SET message_text = '', redacted = 1 WHERE message_text <> '' AND at < {text}", tx);
            changed += Execute($"UPDATE mod_actions SET {person} WHERE action NOT IN ('ban', 'unban') AND NOT {LinkActionSql} AND {hasPerson} AND at < {shortRows}", tx);
            // A ban ends when it is lifted; one that never took effect (failed, skipped) ended when it was made.
            changed += Execute($"UPDATE mod_actions SET {person} WHERE action = 'ban' AND {hasPerson} AND " +
                               $"((undone_at IS NOT NULL AND undone_at < {bans}) OR (result <> 'Ok' AND at < {bans}))", tx);
            changed += Execute($"UPDATE mod_actions SET {person} WHERE action = 'unban' AND {hasPerson} AND at < {bans}", tx);
            changed += ApplyLinkRowRetention(r, now, tx);
            tx.Commit();
            return changed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    // ---------------------------------------------------------------- helpers

    private IReadOnlyList<ModLogEntry> Query(string where, int limit, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM mod_actions {where} ORDER BY seq DESC LIMIT $limit";
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = cmd.ExecuteReader();
            var rows = new List<ModLogEntry>();
            while (reader.Read()) rows.Add(Read(reader));
            return rows;
        }
    }

    private ModLogEntry? GetLocked(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mod_actions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static ModLogEntry Read(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        return new ModLogEntry
        {
            Id = r.GetString(0),
            IncidentId = r.GetString(1),
            At = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
            ActorKind = Enum.TryParse<ModActorKind>(r.GetString(3), out var kind) ? kind : ModActorKind.External,
            ActorMemberId = S(4),
            ActorName = r.GetString(5),
            ActorDeviceId = S(6),
            Platform = r.GetString(7),
            ChannelId = S(8),
            Action = r.GetString(9),
            TargetUserId = r.GetString(10),
            TargetName = r.GetString(11),
            MessageId = r.GetString(12),
            MessageText = r.GetString(13),
            DurationRequestedSeconds = I(14),
            DurationAppliedSeconds = I(15),
            Reason = r.GetString(16),
            ViaLinkId = S(17),
            PlatformRef = S(18),
            Result = Enum.TryParse<ModResult>(r.GetString(19), out var result) ? result : ModResult.Failed,
            Error = r.GetString(20),
            Undoes = S(21),
            UndoneBy = S(22),
            UndoneAt = r.IsDBNull(23) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(23)),
            Redacted = r.GetInt64(24) != 0,
            PrevHash = r.GetString(26),
            Hash = r.GetString(27),
        };
    }

    private static void Bind(SqliteCommand cmd, ModLogEntry e, string content)
    {
        void P(string name, object? value) => cmd.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        P("id", e.Id);
        P("incident_id", e.IncidentId);
        P("at", e.At.ToUnixTimeMilliseconds());
        P("actor_kind", e.ActorKind.ToString());
        P("actor_member_id", e.ActorMemberId);
        P("actor_name", e.ActorName);
        P("actor_device_id", e.ActorDeviceId);
        P("platform", e.Platform);
        P("channel_id", e.ChannelId);
        P("action", e.Action);
        P("target_user_id", e.TargetUserId);
        P("target_name", e.TargetName);
        P("message_id", e.MessageId);
        P("message_text", e.MessageText);
        P("duration_requested", e.DurationRequestedSeconds);
        P("duration_applied", e.DurationAppliedSeconds);
        P("reason", e.Reason);
        P("via_link_id", e.ViaLinkId);
        P("platform_ref", e.PlatformRef);
        P("result", e.Result.ToString());
        P("error", e.Error);
        P("undoes", e.Undoes);
        P("undone_by", null);
        P("undone_at", null);
        P("redacted", 0);
        P("content_hash", content);
        P("prev_hash", e.PrevHash);
        P("hash", e.Hash);
    }

    /// <summary>SHA-256 of the row's content (everything but the undo pointer, the redaction flag and the hashes).</summary>
    internal static string ContentHash(ModLogEntry e)
    {
        var fields = new[]
        {
            e.Id, e.IncidentId, e.At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), e.ActorKind.ToString(),
            e.ActorMemberId ?? "", e.ActorName, e.ActorDeviceId ?? "", e.Platform, e.ChannelId ?? "", e.Action, e.TargetUserId,
            e.TargetName, e.MessageId, e.MessageText, Num(e.DurationRequestedSeconds), Num(e.DurationAppliedSeconds), e.Reason,
            e.ViaLinkId ?? "", e.PlatformRef ?? "", e.Result.ToString(), e.Error, e.Undoes ?? "",
        };
        return Hex(string.Join('\u001f', fields));
    }

    private static string ChainHash(string prev, string content) => Hex(prev + "|" + content);

    private static string Num(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private int Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
