using System.Text.Json.Nodes;
using Cuebot.Core.Portal;

namespace Cuebot.Server.Tests;

/// <summary>A mod portal queue in memory: ids waiting, and what the next decision answers.</summary>
internal sealed class FakeApprovals : IPortalApprovals
{
    public List<Guid> Waiting { get; } = [];
    public List<(Guid Id, bool Approve, bool Force)> Decisions { get; } = [];
    public string Next { get; set; } = "applied";

    public JsonObject Pending() => new()
    {
        ["changes"] = new JsonArray([.. Waiting.Select(id => (JsonNode)new JsonObject { ["id"] = id.ToString(), ["summary"] = "Mod changed the action 'a'" })]),
    };

    public Task<string> DecideAsync(Guid id, bool approve, bool force)
    {
        if (!Waiting.Contains(id)) return Task.FromResult("notFound");
        Decisions.Add((id, approve, force));
        if (Next is "applied" or "rejected") Waiting.Remove(id);
        return Task.FromResult(Next);
    }
}
