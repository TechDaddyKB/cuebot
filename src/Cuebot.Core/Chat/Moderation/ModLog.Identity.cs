using System.Text.Json;
using Cuebot.Core.Users;
using Microsoft.Data.Sqlite;

namespace Cuebot.Core.Chat.Moderation;

/// <summary>
/// Schema version 2 of the mod log: identity links (people and their accounts, confirmations, evidence, rejections) and
/// mod notes (docs/UNIFIED_MOD_CHAT_PLAN.md, sections 6.2 and 6.3). These tables are state, not history: they change
/// and rows go. The history of every link, confirmation, rejection and unlink is a <c>mod_actions</c> row written by
/// <see cref="IdentityLinks"/>. Notes are stored on the account they were written on and shown on every account linked to it.
/// </summary>
public sealed partial class ModLog
{
    /// <summary>The mod log actions that are about identity links.</summary>
    public static readonly IReadOnlyList<string> LinkActions =
        ["link", "link-confirm", "link-verify", "link-reject", "link-move", "link-merge", "unlink"];

    private static string LinkActionSql => "(action IN ('" + string.Join("', '", LinkActions) + "'))";

    private void MigrateIdentity()
    {
        using var tx = _db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS identity_persons (
                id TEXT PRIMARY KEY,
                note TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                created_by TEXT NOT NULL,
                last_reviewed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS identity_accounts (
                link_id TEXT PRIMARY KEY,
                person_id TEXT NOT NULL,
                platform TEXT NOT NULL,
                user_id TEXT NOT NULL,
                name_at_link TEXT NOT NULL,
                current_name TEXT NOT NULL,
                added_at INTEGER NOT NULL,
                added_by TEXT NOT NULL,
                added_by_member TEXT,
                proposal_id TEXT NOT NULL,
                status TEXT NOT NULL,
                status_at INTEGER NOT NULL,
                last_refreshed INTEGER NOT NULL,
                needs_streamer INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_accounts ON identity_accounts(platform COLLATE NOCASE, user_id);
            CREATE INDEX IF NOT EXISTS ix_identity_accounts_person ON identity_accounts(person_id);
            CREATE TABLE IF NOT EXISTS identity_confirmations (
                link_id TEXT NOT NULL,
                by_name TEXT NOT NULL,
                member_id TEXT,
                at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_identity_confirmations ON identity_confirmations(link_id);
            CREATE TABLE IF NOT EXISTS identity_evidence (
                id TEXT PRIMARY KEY,
                person_id TEXT NOT NULL,
                link_id TEXT,
                kind TEXT NOT NULL,
                quote TEXT NOT NULL,
                message_ids TEXT NOT NULL,
                added_by TEXT NOT NULL,
                at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_identity_evidence_person ON identity_evidence(person_id);
            CREATE TABLE IF NOT EXISTS identity_rejections (
                platform_a TEXT NOT NULL,
                user_a TEXT NOT NULL,
                platform_b TEXT NOT NULL,
                user_b TEXT NOT NULL,
                at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS mod_notes (
                id TEXT PRIMARY KEY,
                platform TEXT NOT NULL,
                user_id TEXT NOT NULL,
                text TEXT NOT NULL,
                author TEXT NOT NULL,
                author_member_id TEXT,
                created_at INTEGER NOT NULL,
                edited_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_mod_notes_account ON mod_notes(platform, user_id);
            CREATE INDEX IF NOT EXISTS ix_mod_actions_incident ON mod_actions(incident_id);
            CREATE INDEX IF NOT EXISTS ix_mod_actions_via_link ON mod_actions(via_link_id);
            PRAGMA user_version = 2;
            """, tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- mod log queries for phase 5

    /// <summary>The rows of one incident, oldest first.</summary>
    public IReadOnlyList<ModLogEntry> ForIncident(string incidentId) =>
        Query("WHERE incident_id = $incident", 500, ("$incident", incidentId)).Reverse().ToArray();

    /// <summary>Rows one portal member made since <paramref name="since"/>, newest first.</summary>
    public IReadOnlyList<ModLogEntry> ByMemberSince(string memberId, DateTimeOffset since, int limit = 500) =>
        Query("WHERE actor_member_id = $member AND at >= $since", limit, ("$member", memberId), ("$since", since.ToUnixTimeMilliseconds()));

    /// <summary>Bans and timeouts made through any of these links that are still in force, newest first.</summary>
    public IReadOnlyList<ModLogEntry> InForceVia(IEnumerable<string> linkIds)
    {
        var now = _time.GetUtcNow();
        var rows = new List<ModLogEntry>();
        foreach (var id in linkIds.Distinct(StringComparer.Ordinal))
            rows.AddRange(Query("WHERE via_link_id = $link AND action IN ('ban', 'timeout') AND result = 'Ok' AND undone_by IS NULL", 200,
                ("$link", id)).Where(e => e.IsInForce(now)));
        return rows.OrderByDescending(r => r.At).ToArray();
    }

    /// <summary>
    /// "Forget this viewer" for the mod log (section 9.2): blanks the person's details in every row about this account
    /// and marks the rows redacted. A ban or timeout still in force keeps its row, the platform user id, the reason and
    /// the YouTube ban id, so the ban still works and can still be lifted; only the name and message go. Returns the
    /// rows changed.
    /// </summary>
    public int EraseAccount(string platform, string userId)
    {
        if (userId.Length == 0) return 0;
        var inForce = ForTarget(platform, userId, 500).Where(e => e.IsInForce(_time.GetUtcNow())).Select(e => e.Id).ToHashSet();
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction();
            var changed = 0;
            foreach (var row in QueryLocked("WHERE platform = $platform COLLATE NOCASE AND target_user_id = $user", 100_000, tx,
                         ("$platform", platform), ("$user", userId)))
            {
                var keep = inForce.Contains(row.Id);
                changed += Run(keep
                    ? "UPDATE mod_actions SET target_name = '', message_text = '', redacted = 1 WHERE id = $id"
                    : "UPDATE mod_actions SET target_user_id = '', target_name = '', message_id = '', message_text = '', reason = '', " +
                      "platform_ref = NULL, redacted = 1 WHERE id = $id", tx, ("$id", row.Id));
            }
            tx.Commit();
            return changed;
        }
    }

    /// <summary>
    /// Link rows keep the person while the link stands and <see cref="ModLogRetention.BanMonthsAfterEnd"/> months after
    /// it ended: blanked when the account is in no person now and no link row about it is younger than that.
    /// </summary>
    private int ApplyLinkRowRetention(ModLogRetention r, DateTimeOffset now, SqliteTransaction tx)
    {
        var cutoff = now.AddMonths(-r.BanMonthsAfterEnd).ToUnixTimeMilliseconds();
        return Execute(
            "UPDATE mod_actions SET target_user_id = '', target_name = '', message_id = '', message_text = '', reason = '', platform_ref = NULL, redacted = 1 " +
            $"WHERE {LinkActionSql} AND target_user_id <> '' AND at < {cutoff} " +
            "AND NOT EXISTS (SELECT 1 FROM identity_accounts a WHERE a.platform = mod_actions.platform COLLATE NOCASE AND a.user_id = mod_actions.target_user_id) " +
            $"AND NOT EXISTS (SELECT 1 FROM mod_actions m2 WHERE m2.platform = mod_actions.platform COLLATE NOCASE AND m2.target_user_id = mod_actions.target_user_id " +
            $"AND m2.action IN ('" + string.Join("', '", LinkActions) + $"') AND m2.at >= {cutoff})", tx);
    }

    // ---------------------------------------------------------------- identity links

    /// <summary>Runs <paramref name="work"/> in one transaction under the log's lock (identity changes are all or nothing).</summary>
    internal T Identity<T>(Func<IdentityTx, T> work)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction();
            var result = work(new IdentityTx(this, tx));
            tx.Commit();
            return result;
        }
    }

    internal IdentityPerson? PersonByAccount(string platform, string userId) => Identity(t => t.PersonByAccount(platform, userId));

    internal IdentityPerson? PersonById(string id) => Identity(t => t.PersonById(id));

    internal IReadOnlyList<IdentityPerson> AllPersons() => Identity(t => t.AllPersons());

    /// <summary>Accounts of <paramref name="status"/> added before <paramref name="before"/>.</summary>
    internal IReadOnlyList<IdentityAccount> AccountsWhere(LinkStatus? status, DateTimeOffset? addedBefore, DateTimeOffset? refreshedBefore, string? platform) =>
        Identity(t => t.Accounts(status, addedBefore, refreshedBefore, platform));

    /// <summary>A name seen in chat for an account in a person: keeps its current name and refresh time. False when not linked.</summary>
    internal bool TouchAccount(string platform, string userId, string name, DateTimeOffset at)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Run("UPDATE identity_accounts SET current_name = CASE WHEN $name = '' THEN current_name ELSE $name END, last_refreshed = $at " +
                       "WHERE platform = $platform COLLATE NOCASE AND user_id = $user", null,
                ("$name", name), ("$at", at.ToUnixTimeMilliseconds()), ("$platform", platform), ("$user", userId)) > 0;
        }
    }

    internal bool IsRejected(string platformA, string userA, string platformB, string userB)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM identity_rejections WHERE " +
                              "(platform_a = $pa COLLATE NOCASE AND user_a = $ua AND platform_b = $pb COLLATE NOCASE AND user_b = $ub) OR " +
                              "(platform_a = $pb COLLATE NOCASE AND user_a = $ub AND platform_b = $pa COLLATE NOCASE AND user_b = $ua)";
            cmd.Parameters.AddWithValue("$pa", platformA);
            cmd.Parameters.AddWithValue("$ua", userA);
            cmd.Parameters.AddWithValue("$pb", platformB);
            cmd.Parameters.AddWithValue("$ub", userB);
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
    }

    internal IReadOnlyList<LinkRejection> Rejections()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT platform_a, user_a, platform_b, user_b, at FROM identity_rejections ORDER BY at";
            using var r = cmd.ExecuteReader();
            var list = new List<LinkRejection>();
            while (r.Read())
                list.Add(new LinkRejection(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4))));
            return list;
        }
    }

    internal int DeleteRejections(DateTimeOffset before, string? platform = null, string? userId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return platform is null
                ? Run("DELETE FROM identity_rejections WHERE at < $at", null, ("$at", before.ToUnixTimeMilliseconds()))
                : Run("DELETE FROM identity_rejections WHERE (platform_a = $p COLLATE NOCASE AND user_a = $u) OR (platform_b = $p COLLATE NOCASE AND user_b = $u)",
                    null, ("$p", platform), ("$u", userId ?? ""));
        }
    }

    // ---------------------------------------------------------------- notes

    internal void InsertNote(ModNote note)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Run("INSERT INTO mod_notes (id, platform, user_id, text, author, author_member_id, created_at, edited_at) " +
                "VALUES ($id, $platform, $user, $text, $author, $member, $created, $edited)", null,
                ("$id", note.Id), ("$platform", note.Platform), ("$user", note.UserId), ("$text", note.Text), ("$author", note.Author),
                ("$member", note.AuthorMemberId), ("$created", note.CreatedAt.ToUnixTimeMilliseconds()), ("$edited", note.EditedAt.ToUnixTimeMilliseconds()));
        }
    }

    internal bool UpdateNote(string id, string text, DateTimeOffset at)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Run("UPDATE mod_notes SET text = $text, edited_at = $at WHERE id = $id", null,
                ("$text", text), ("$at", at.ToUnixTimeMilliseconds()), ("$id", id)) > 0;
        }
    }

    internal int DeleteNotes(string? id = null, DateTimeOffset? editedBefore = null, string? platform = null, string? userId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (id is not null) return Run("DELETE FROM mod_notes WHERE id = $id", null, ("$id", id));
            if (editedBefore is { } before) return Run("DELETE FROM mod_notes WHERE edited_at < $at", null, ("$at", before.ToUnixTimeMilliseconds()));
            return Run("DELETE FROM mod_notes WHERE platform = $p COLLATE NOCASE AND user_id = $u", null, ("$p", platform ?? ""), ("$u", userId ?? ""));
        }
    }

    internal ModNote? Note(string id) => Notes("WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>Notes written on any of these accounts, newest first.</summary>
    internal IReadOnlyList<ModNote> NotesFor(IEnumerable<(string Platform, string UserId)> accounts)
    {
        var all = new List<ModNote>();
        foreach (var (platform, userId) in accounts.Distinct())
            all.AddRange(Notes("WHERE platform = $p COLLATE NOCASE AND user_id = $u", ("$p", platform), ("$u", userId)));
        return all.OrderByDescending(n => n.EditedAt).ToArray();
    }

    /// <summary>Every note on accounts of one platform.</summary>
    internal IReadOnlyList<ModNote> NotesForPlatform(string platform) => Notes("WHERE platform = $p COLLATE NOCASE", ("$p", platform));

    private IReadOnlyList<ModNote> Notes(string where, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT id, platform, user_id, text, author, author_member_id, created_at, edited_at FROM mod_notes {where}";
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<ModNote>();
            while (r.Read())
                list.Add(new ModNote
                {
                    Id = r.GetString(0),
                    Platform = r.GetString(1),
                    UserId = r.GetString(2),
                    Text = r.GetString(3),
                    Author = r.GetString(4),
                    AuthorMemberId = r.IsDBNull(5) ? null : r.GetString(5),
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                    EditedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
                });
            return list;
        }
    }

    // ---------------------------------------------------------------- helpers

    private IReadOnlyList<ModLogEntry> QueryLocked(string where, int limit, SqliteTransaction tx, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM mod_actions {where} ORDER BY seq DESC LIMIT $limit";
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = cmd.ExecuteReader();
        var rows = new List<ModLogEntry>();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    private int Run(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Identity table operations inside one transaction (see <see cref="Identity{T}"/>).</summary>
    internal sealed class IdentityTx(ModLog log, SqliteTransaction tx)
    {
        private const string AccountColumns =
            "link_id, person_id, platform, user_id, name_at_link, current_name, added_at, added_by, added_by_member, proposal_id, status, " +
            "status_at, last_refreshed, needs_streamer";

        private int Run(string sql, params (string Name, object? Value)[] p) => log.Run(sql, tx, p);

        public void InsertPerson(IdentityPerson p) =>
            Run("INSERT INTO identity_persons (id, note, created_at, created_by, last_reviewed) VALUES ($id, $note, $created, $by, $reviewed)",
                ("$id", p.Id), ("$note", p.Note), ("$created", p.CreatedAt.ToUnixTimeMilliseconds()), ("$by", p.CreatedBy),
                ("$reviewed", p.LastReviewed.ToUnixTimeMilliseconds()));

        public void UpdatePerson(string id, string note, DateTimeOffset reviewed) =>
            Run("UPDATE identity_persons SET note = $note, last_reviewed = $reviewed WHERE id = $id",
                ("$note", note), ("$reviewed", reviewed.ToUnixTimeMilliseconds()), ("$id", id));

        /// <summary>Deletes the person, its evidence, and any accounts and confirmations still in it.</summary>
        public void DeletePerson(string id)
        {
            Run("DELETE FROM identity_confirmations WHERE link_id IN (SELECT link_id FROM identity_accounts WHERE person_id = $id)", ("$id", id));
            Run("DELETE FROM identity_accounts WHERE person_id = $id", ("$id", id));
            Run("DELETE FROM identity_evidence WHERE person_id = $id", ("$id", id));
            Run("DELETE FROM identity_persons WHERE id = $id", ("$id", id));
        }

        public void InsertAccount(IdentityAccount a) =>
            Run($"INSERT INTO identity_accounts ({AccountColumns}) VALUES ($link, $person, $platform, $user, $nameAt, $current, $added, $by, $member, " +
                "$proposal, $status, $statusAt, $refreshed, $needs)", AccountParameters(a));

        public void UpdateAccount(IdentityAccount a) =>
            Run("UPDATE identity_accounts SET person_id = $person, platform = $platform, user_id = $user, name_at_link = $nameAt, current_name = $current, " +
                "added_at = $added, added_by = $by, added_by_member = $member, proposal_id = $proposal, status = $status, status_at = $statusAt, " +
                "last_refreshed = $refreshed, needs_streamer = $needs WHERE link_id = $link", AccountParameters(a));

        public void DeleteAccount(string linkId)
        {
            Run("DELETE FROM identity_confirmations WHERE link_id = $id", ("$id", linkId));
            Run("DELETE FROM identity_evidence WHERE link_id = $id", ("$id", linkId));
            Run("DELETE FROM identity_accounts WHERE link_id = $id", ("$id", linkId));
        }

        public void InsertConfirmation(string linkId, LinkConfirmation c) =>
            Run("INSERT INTO identity_confirmations (link_id, by_name, member_id, at) VALUES ($link, $by, $member, $at)",
                ("$link", linkId), ("$by", c.By), ("$member", c.MemberId), ("$at", c.At.ToUnixTimeMilliseconds()));

        public void InsertEvidence(LinkEvidence e) =>
            Run("INSERT INTO identity_evidence (id, person_id, link_id, kind, quote, message_ids, added_by, at) VALUES ($id, $person, $link, $kind, $quote, $ids, $by, $at)",
                ("$id", e.Id), ("$person", e.PersonId), ("$link", e.LinkId), ("$kind", e.Kind), ("$quote", e.Quote),
                ("$ids", JsonSerializer.Serialize(e.MessageIds)), ("$by", e.AddedBy), ("$at", e.At.ToUnixTimeMilliseconds()));

        public void MoveEvidence(string fromPerson, string toPerson) =>
            Run("UPDATE identity_evidence SET person_id = $to WHERE person_id = $from", ("$to", toPerson), ("$from", fromPerson));

        public void InsertRejection(LinkRejection r) =>
            Run("INSERT INTO identity_rejections (platform_a, user_a, platform_b, user_b, at) VALUES ($pa, $ua, $pb, $ub, $at)",
                ("$pa", r.PlatformA), ("$ua", r.UserIdA), ("$pb", r.PlatformB), ("$ub", r.UserIdB), ("$at", r.At.ToUnixTimeMilliseconds()));

        public IdentityPerson? PersonByAccount(string platform, string userId)
        {
            var id = Scalar("SELECT person_id FROM identity_accounts WHERE platform = $p COLLATE NOCASE AND user_id = $u", ("$p", platform), ("$u", userId));
            return id is string s ? PersonById(s) : null;
        }

        public IdentityPerson? PersonByLink(string linkId) =>
            Scalar("SELECT person_id FROM identity_accounts WHERE link_id = $id", ("$id", linkId)) is string s ? PersonById(s) : null;

        public IdentityPerson? PersonById(string id)
        {
            using var cmd = Command("SELECT id, note, created_at, created_by, last_reviewed FROM identity_persons WHERE id = $id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var person = new IdentityPerson
            {
                Id = r.GetString(0),
                Note = r.GetString(1),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                CreatedBy = r.GetString(3),
                LastReviewed = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
            };
            r.Close();
            return person with { Accounts = Accounts("WHERE person_id = $id ORDER BY added_at, rowid", ("$id", id)), Evidence = Evidence(id) };
        }

        public IReadOnlyList<IdentityPerson> AllPersons()
        {
            var ids = new List<string>();
            using (var cmd = Command("SELECT id FROM identity_persons ORDER BY created_at"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));
            return ids.Select(PersonById).OfType<IdentityPerson>().ToArray();
        }

        public IReadOnlyList<IdentityAccount> Accounts(LinkStatus? status, DateTimeOffset? addedBefore, DateTimeOffset? refreshedBefore, string? platform)
        {
            var where = new List<string>();
            var p = new List<(string, object?)>();
            if (status is { } s) { where.Add("status = $status"); p.Add(("$status", s.ToString())); }
            if (addedBefore is { } a) { where.Add("added_at < $added"); p.Add(("$added", a.ToUnixTimeMilliseconds())); }
            if (refreshedBefore is { } rb) { where.Add("last_refreshed < $refreshed"); p.Add(("$refreshed", rb.ToUnixTimeMilliseconds())); }
            if (platform is not null) { where.Add("platform = $platform COLLATE NOCASE"); p.Add(("$platform", platform)); }
            return Accounts((where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "") + " ORDER BY added_at", p.ToArray());
        }

        private IReadOnlyList<IdentityAccount> Accounts(string where, params (string, object?)[] p)
        {
            var list = new List<IdentityAccount>();
            using (var cmd = Command($"SELECT {AccountColumns} FROM identity_accounts {where}", p))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new IdentityAccount
                    {
                        LinkId = r.GetString(0),
                        PersonId = r.GetString(1),
                        Platform = r.GetString(2),
                        UserId = r.GetString(3),
                        NameAtLink = r.GetString(4),
                        CurrentName = r.GetString(5),
                        AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                        AddedBy = r.GetString(7),
                        AddedByKey = r.IsDBNull(8) ? "" : r.GetString(8),
                        ProposalId = r.GetString(9),
                        Status = Enum.TryParse<LinkStatus>(r.GetString(10), out var st) ? st : LinkStatus.Suggested,
                        StatusAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
                        LastRefreshed = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(12)),
                        NeedsStreamer = r.GetInt64(13) != 0,
                    });
            return list.Select(a => a with { ConfirmedBy = Confirmations(a.LinkId) }).ToArray();
        }

        private IReadOnlyList<LinkConfirmation> Confirmations(string linkId)
        {
            using var cmd = Command("SELECT by_name, member_id, at FROM identity_confirmations WHERE link_id = $id ORDER BY at", ("$id", linkId));
            using var r = cmd.ExecuteReader();
            var list = new List<LinkConfirmation>();
            while (r.Read()) list.Add(new LinkConfirmation(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));
            return list;
        }

        private IReadOnlyList<LinkEvidence> Evidence(string personId)
        {
            using var cmd = Command("SELECT id, person_id, link_id, kind, quote, message_ids, added_by, at FROM identity_evidence WHERE person_id = $id ORDER BY at",
                ("$id", personId));
            using var r = cmd.ExecuteReader();
            var list = new List<LinkEvidence>();
            while (r.Read())
                list.Add(new LinkEvidence
                {
                    Id = r.GetString(0),
                    PersonId = r.GetString(1),
                    LinkId = r.IsDBNull(2) ? null : r.GetString(2),
                    Kind = r.GetString(3),
                    Quote = r.GetString(4),
                    MessageIds = JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],
                    AddedBy = r.GetString(6),
                    At = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
                });
            return list;
        }

        private object? Scalar(string sql, params (string, object?)[] p)
        {
            using var cmd = Command(sql, p);
            return cmd.ExecuteScalar();
        }

        private SqliteCommand Command(string sql, params (string Name, object? Value)[] p)
        {
            var cmd = log._db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        private static (string, object?)[] AccountParameters(IdentityAccount a) =>
        [
            ("$link", a.LinkId), ("$person", a.PersonId), ("$platform", a.Platform), ("$user", a.UserId), ("$nameAt", a.NameAtLink),
            ("$current", a.CurrentName), ("$added", a.AddedAt.ToUnixTimeMilliseconds()), ("$by", a.AddedBy), ("$member", a.AddedByKey),
            ("$proposal", a.ProposalId), ("$status", a.Status.ToString()), ("$statusAt", a.StatusAt.ToUnixTimeMilliseconds()),
            ("$refreshed", a.LastRefreshed.ToUnixTimeMilliseconds()), ("$needs", a.NeedsStreamer ? 1 : 0),
        ];
    }
}
