using System.Text.Json.Nodes;

namespace Cuebot.Core.Portal;

/// <summary>
/// The mod portal's approval queue for the REST API, which the streamer's paired companion app uses (docs/MOD_PORTAL_PLAN.md,
/// section 5). Cuebot.Portal implements it; the REST API answers 503 without it.
/// </summary>
public interface IPortalApprovals
{
    /// <summary><c>{changes: [{id, member, area, kind, target, summary, at, expires}]}</c>, the oldest first.</summary>
    JsonObject Pending();

    /// <summary>
    /// Approves or rejects one pending change: <c>applied</c>, <c>rejected</c>, <c>conflict</c> (the thing changed since
    /// the mod asked; approve again with <paramref name="force"/> to use the mod's version anyway), <c>notPending</c>
    /// (already decided or expired) or <c>notFound</c>.
    /// </summary>
    Task<string> DecideAsync(Guid id, bool approve, bool force);
}
