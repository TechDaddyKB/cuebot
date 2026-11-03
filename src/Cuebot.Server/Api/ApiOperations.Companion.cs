using System.Text.Json.Nodes;
using Cuebot.Core.Subathon;

namespace Cuebot.Server.Api;

/// <summary>What the companion app (docs/IOS_COMPANION_PLAN.md) reads and controls: the dashboard, subathon timers and goals.</summary>
public sealed partial class ApiOperations
{
    private const int DashboardFeedSize = 50;
    private const string SubathonKey = "cuebot-subathon";
    private const string TopSpendersKey = "cuebot-top-spenders";

    private DashboardChanges? _dashboardChanges;

    /// <summary>Counts dashboard changes; created on first use (the REST event stream or the dashboard).</summary>
    public DashboardChanges DashboardChanges => LazyInitializer.EnsureInitialized(ref _dashboardChanges, () => new DashboardChanges(services));

    // ---- Dashboard -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One snapshot for the companion app's dashboard: connections, this stream's totals, subathon timers and goals,
    /// top spenders and the latest activity (newest first). <c>revision</c> matches "dashboard.changed" events.
    /// </summary>
    public JsonObject Dashboard()
    {
        var revision = DashboardChanges.Revision;
        var session = services.SessionData?.Snapshot() ?? new JsonObject();
        var subathon = session[SubathonKey]?.DeepClone();
        var topSpenders = session[TopSpendersKey]?.DeepClone();
        var totals = new JsonObject();
        foreach (var (key, value) in session)
            if (!key.StartsWith("cuebot-", StringComparison.Ordinal)) totals[key] = value?.DeepClone();
        var feed = services.Chat?.RecentFeed() ?? [];
        return new JsonObject
        {
            ["instanceId"] = Cuebot.Server.StreamerBot.StreamerBotRequests.InstanceId,
            ["version"] = AppVersion,
            ["revision"] = revision,
            ["connections"] = new JsonArray([.. services.Connections.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["state"] = c.State.ToString(),
                ["status"] = c.StatusMessage,
            })]),
            ["totals"] = totals,
            ["subathon"] = subathon ?? new JsonObject { ["timers"] = new JsonArray(), ["goals"] = new JsonArray() },
            ["topSpenders"] = topSpenders,
            ["feed"] = new JsonArray([.. feed.Reverse().Take(DashboardFeedSize).Select(i => (JsonNode)new JsonObject
            {
                ["platform"] = i.Platform,
                ["kind"] = i.Kind.ToString(),
                ["user"] = i.User,
                ["title"] = i.Title,
                ["detail"] = i.Detail,
                ["amount"] = i.Amount,
                ["time"] = i.Time.ToString("o"),
                ["isTest"] = i.IsTest,
            })]),
        };
    }

    // ---- Subathon timers and goals ---------------------------------------------------------------------------------

    private SubathonService Subathon => services.Subathon ?? throw ApiException.Unavailable("Subathon timers are not available");

    /// <summary>
    /// <c>start</c>, <c>pause</c>, <c>resume</c>, <c>toggle</c>, <c>reset</c>, <c>time</c> (<c>{"add": seconds}</c> or
    /// <c>{"set": seconds}</c>) or <c>multiplier</c> (<c>{"value": 2}</c>) for one timer, by id or name.
    /// </summary>
    public JsonObject SubathonCommand(string timer, string command, JsonObject? body)
    {
        var found = Subathon.Timer(timer) ?? throw ApiException.NotFound($"No subathon timer '{timer}'");
        var id = found.Id;
        switch (command)
        {
            case "start": Subathon.Start(id); break;
            case "pause": Subathon.Pause(id); break;
            case "resume": Subathon.Resume(id); break;
            case "toggle": Subathon.Toggle(id); break;
            case "reset": Subathon.Reset(id); break;
            case "time":
                if (Number(body, "add") is { } add) Subathon.AddTime(id, add, reason: "API");
                else if (Number(body, "set") is { } set) Subathon.SetTime(id, set);
                else throw ApiException.BadRequest("Send {\"add\": seconds} or {\"set\": seconds}");
                break;
            case "multiplier":
                Subathon.SetTimerMultiplier(id, Number(body, "value") ?? throw ApiException.BadRequest("Send {\"value\": number}"));
                break;
            default: throw ApiException.NotFound($"Unknown command '{command}' (start, pause, resume, toggle, reset, time, multiplier)");
        }
        return SubathonState();
    }

    /// <summary><c>points</c> (<c>{"add": n}</c> or <c>{"set": n}</c>), <c>multiplier</c> or <c>reset</c> for one goal, by id or name.</summary>
    public JsonObject GoalCommand(string goal, string command, JsonObject? body)
    {
        var found = Subathon.Goal(goal) ?? throw ApiException.NotFound($"No goal '{goal}'");
        var id = found.Id;
        switch (command)
        {
            case "points":
                if (Number(body, "add") is { } add) Subathon.AddPoints(id, add, reason: "API");
                else if (Number(body, "set") is { } set) Subathon.SetPoints(id, set);
                else throw ApiException.BadRequest("Send {\"add\": points} or {\"set\": points}");
                break;
            case "multiplier":
                Subathon.SetGoalMultiplier(id, Number(body, "value") ?? throw ApiException.BadRequest("Send {\"value\": number}"));
                break;
            case "reset": Subathon.ResetGoal(id); break;
            default: throw ApiException.NotFound($"Unknown command '{command}' (points, multiplier, reset)");
        }
        return SubathonState();
    }

    /// <summary>The timers and goals as the dashboard shows them.</summary>
    private JsonObject SubathonState() =>
        services.SessionData?.Snapshot()[SubathonKey]?.DeepClone() as JsonObject
        ?? new JsonObject { ["timers"] = new JsonArray(), ["goals"] = new JsonArray() };

    private static double? Number(JsonObject? body, string name)
    {
        if (body?[name] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        return value.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out d) ? d : throw ApiException.BadRequest($"{name} must be a number");
    }
}
