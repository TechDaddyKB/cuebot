using System.Text.Json.Nodes;
using Cuebot.Core.Portal;
using Cuebot.Portal.Uploads;

namespace Cuebot.Portal.Changes;

/// <summary>The approval queue for the REST API (<see cref="IPortalApprovals"/>): what the desktop's Mod portal page shows.</summary>
public sealed class PortalApprovals(PortalChanges changes) : IPortalApprovals
{
    public JsonObject Pending() => new()
    {
        ["changes"] = new JsonArray([.. changes.Pending.OrderBy(c => c.At).Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id.ToString(),
            ["member"] = c.MemberName,
            ["area"] = c.Area,
            ["kind"] = c.Kind.ToString().ToLowerInvariant(),
            ["target"] = c.TargetName,
            ["summary"] = Summary(c),
            ["at"] = c.At.ToUnixTimeMilliseconds(),
            ["expires"] = (c.At + PortalChanges.PendingLifetime).ToUnixTimeMilliseconds(),
        })]),
    };

    public async Task<string> DecideAsync(Guid id, bool approve, bool force)
    {
        var result = approve ? await changes.ApproveAsync(id, force).ConfigureAwait(false) : await changes.RejectAsync(id).ConfigureAwait(false);
        return result switch
        {
            ChangeResult.Applied => "applied",
            ChangeResult.Rejected => "rejected",
            ChangeResult.Conflict => "conflict",
            ChangeResult.NotPending => "notPending",
            _ => "notFound",
        };
    }

    /// <summary>"Sam changed the action 'Hype'", "Sam uploaded the picture 'Logo'".</summary>
    public static string Summary(PortalChange c)
    {
        if (c.Area == UploadArea.AreaName)
        {
            var kind = (string?)(c.After ?? c.Before)?["kind"] switch { "Image" => "picture", "Video" => "video", _ => "sound" };
            var verb = c.Kind == ChangeKind.Delete ? "wants to remove" : "uploaded";
            return $"{c.MemberName} {verb} the {kind} '{c.TargetName}'";
        }
        var what = c.Kind switch { ChangeKind.Create => "created", ChangeKind.Delete => "deleted", _ => "changed" };
        return $"{c.MemberName} {what} the {c.Area} '{c.TargetName}'";
    }
}
