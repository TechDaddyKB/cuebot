using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Storage;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Changes;

public enum ChangeKind { Create, Update, Delete }

public enum ChangeStatus { Applied, Pending, Rejected, Expired, Undone }

/// <summary>One change a member made or asked for, with full before and after copies for approval and undo.</summary>
public sealed class PortalChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemberId { get; set; }
    public string MemberName { get; set; } = "";
    public DateTimeOffset At { get; set; }

    /// <summary>The kind of thing changed: <see cref="ActionArea.Name"/>, <see cref="CommandArea.Name"/>.</summary>
    public string Area { get; set; } = "";

    public Guid TargetId { get; set; }
    public string TargetName { get; set; } = "";
    public ChangeKind Kind { get; set; }
    public JsonNode? Before { get; set; }
    public JsonNode? After { get; set; }
    public ChangeStatus Status { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>"the streamer", or the member who undid it.</summary>
    public string DecidedBy { get; set; } = "";
}

/// <summary>portal-changes.json.</summary>
public sealed class PortalChangeLog
{
    public List<PortalChange> Changes { get; set; } = [];
}

/// <summary>Something mods can change: reads the current copy of a thing and puts a new one (null deletes it).</summary>
public interface IPortalArea
{
    string Name { get; }
    JsonNode? Current(Guid id);
    string NameOf(JsonNode value);
    Task ApplyAsync(Guid id, JsonNode? value);
}

public enum ChangeResult { Applied, Pending, Rejected, NotFound, NotPending, Conflict, NotAllowed }

/// <summary>
/// The change log, the approval queue and undo (docs/MOD_PORTAL_PLAN.md, section 5). A member's change goes live or
/// waits for the streamer by <see cref="PortalDirectory.ChangeModeOf"/>; pending changes expire after 7 days; approve
/// and undo refuse when the thing changed since (a conflict) unless forced.
/// </summary>
public sealed class PortalChanges
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(7);
    public const int MaxDecided = 500;

    private readonly PortalChangeLog _log;
    private readonly Action _save;
    private readonly PortalDirectory _directory;
    private readonly Dictionary<string, IPortalArea> _areas;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PortalChanges(PortalChangeLog log, Action save, PortalDirectory directory, IEnumerable<IPortalArea> areas, TimeProvider? time = null)
    {
        _log = log;
        _save = save;
        _directory = directory;
        _areas = areas.ToDictionary(a => a.Name, StringComparer.Ordinal);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any change to the log.</summary>
    public event Action? Changed;

    /// <summary>A change went live from a member whose changes notify the streamer, or one now waits for approval.</summary>
    public event Action<PortalChange>? StreamerNotice;

    public IReadOnlyList<PortalChange> All
    {
        get
        {
            Expire();
            lock (_log) return [.. _log.Changes];
        }
    }

    public IReadOnlyList<PortalChange> Pending => [.. All.Where(c => c.Status == ChangeStatus.Pending)];

    public PortalChange? Find(Guid id)
    {
        lock (_log) return _log.Changes.FirstOrDefault(c => c.Id == id);
    }

    public IPortalArea? Area(string name) => _areas.GetValueOrDefault(name);

    /// <summary>Records a member's change and applies it now or queues it, by their change mode. The caller checked the policy.</summary>
    public async Task<(ChangeResult Result, PortalChange Change)> SubmitAsync(PortalMember member, string area, Guid targetId, JsonNode? after)
    {
        var a = _areas[area];
        await _gate.WaitAsync().ConfigureAwait(false);
        PortalChange change;
        try
        {
            var before = a.Current(targetId);
            change = new PortalChange
            {
                MemberId = member.Id,
                MemberName = member.Name,
                At = _time.GetUtcNow(),
                Area = area,
                TargetId = targetId,
                TargetName = a.NameOf(after ?? before ?? new JsonObject()),
                Kind = before is null ? ChangeKind.Create : after is null ? ChangeKind.Delete : ChangeKind.Update,
                Before = before?.DeepClone(),
                After = after?.DeepClone(),
            };
            var mode = _directory.ChangeModeOf(member);
            if (mode == ChangeMode.NeedsApproval) change.Status = ChangeStatus.Pending;
            else
            {
                await a.ApplyAsync(targetId, after).ConfigureAwait(false);
                change.Status = ChangeStatus.Applied;
            }
            lock (_log) _log.Changes.Add(change);
            Trim();
        }
        finally
        {
            _gate.Release();
        }
        Saved();
        if (change.Status == ChangeStatus.Pending || _directory.ChangeModeOf(member) == ChangeMode.LiveAndNotify) StreamerNotice?.Invoke(change);
        return (change.Status == ChangeStatus.Pending ? ChangeResult.Pending : ChangeResult.Applied, change);
    }

    /// <summary>The streamer approves a pending change. A conflict means the thing changed since the request.</summary>
    public Task<ChangeResult> ApproveAsync(Guid changeId, bool force = false) => DecideAsync(changeId, approve: true, force);

    public Task<ChangeResult> RejectAsync(Guid changeId) => DecideAsync(changeId, approve: false, force: true);

    private async Task<ChangeResult> DecideAsync(Guid changeId, bool approve, bool force)
    {
        Expire();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var change = Find(changeId);
            if (change is null) return ChangeResult.NotFound;
            if (change.Status != ChangeStatus.Pending) return ChangeResult.NotPending;
            if (approve)
            {
                var a = _areas[change.Area];
                if (!force && !Same(a.Current(change.TargetId), change.Before)) return ChangeResult.Conflict;
                await a.ApplyAsync(change.TargetId, change.After?.DeepClone()).ConfigureAwait(false);
            }
            change.Status = approve ? ChangeStatus.Applied : ChangeStatus.Rejected;
            change.DecidedAt = _time.GetUtcNow();
            change.DecidedBy = "the streamer";
        }
        finally
        {
            _gate.Release();
        }
        Saved();
        return approve ? ChangeResult.Applied : ChangeResult.Rejected;
    }

    /// <summary>
    /// Puts back what was there before an applied change. <paramref name="by"/> is the member undoing it (null: the
    /// streamer); members undo their own changes, or anyone's with <see cref="PortalPermission.UndoOthers"/>.
    /// </summary>
    public async Task<ChangeResult> UndoAsync(Guid changeId, PortalMember? by = null, bool force = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var change = Find(changeId);
            if (change is null) return ChangeResult.NotFound;
            if (change.Status != ChangeStatus.Applied) return ChangeResult.NotPending;
            if (by is not null && by.Id != change.MemberId && !PortalPolicy.Has(_directory.RoleOf(by), PortalPermission.UndoOthers))
                return ChangeResult.NotAllowed;
            var a = _areas[change.Area];
            if (!force && !Same(a.Current(change.TargetId), change.After)) return ChangeResult.Conflict;
            await a.ApplyAsync(change.TargetId, change.Before?.DeepClone()).ConfigureAwait(false);
            change.Status = ChangeStatus.Undone;
            change.DecidedAt = _time.GetUtcNow();
            change.DecidedBy = by?.Name ?? "the streamer";
        }
        finally
        {
            _gate.Release();
        }
        Saved();
        return ChangeResult.Applied;
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        var expired = false;
        lock (_log)
        {
            foreach (var c in _log.Changes.Where(c => c.Status == ChangeStatus.Pending && now - c.At > PendingLifetime))
            {
                c.Status = ChangeStatus.Expired;
                c.DecidedAt = now;
                expired = true;
            }
        }
        if (expired) Saved();
    }

    private void Trim()
    {
        lock (_log)
        {
            var decided = _log.Changes.Where(c => c.Status != ChangeStatus.Pending).ToList();
            foreach (var old in decided.Take(Math.Max(0, decided.Count - MaxDecided))) _log.Changes.Remove(old);
        }
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Normalize(a), Normalize(b));

    /// <summary>Edit marks and layout do not make a conflict.</summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is null) return null;
        var copy = JsonNode.Parse(node.ToJsonString())!;
        Strip(copy);
        return copy;

        static void Strip(JsonNode n)
        {
            if (n is JsonObject o)
            {
                o.Remove("editedBy");
                o.Remove("layout");
                foreach (var (_, v) in o.ToList()) if (v is not null) Strip(v);
            }
            else if (n is JsonArray arr) foreach (var v in arr) if (v is not null) Strip(v);
        }
    }

    private void Saved()
    {
        _save();
        Changed?.Invoke();
    }

    internal static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json.Options)!;

    internal static T FromNode<T>(JsonNode node) => node.Deserialize<T>(Json.Options)!;
}
