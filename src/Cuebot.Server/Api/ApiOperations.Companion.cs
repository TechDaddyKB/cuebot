using System.Text.Json.Nodes;
using Cuebot.Core.Subathon;

namespace Cuebot.Server.Api;

/// <summary>What the companion app (docs/design/IOS_COMPANION_PLAN.md) reads and controls: the dashboard, subathon timers and goals.</summary>
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

    // ---- Subathon history -------------------------------------------------------------------------------------------

    /// <summary>
    /// The newest changes to timers and goals matching the filter (<c>owner</c>, <c>from</c>, <c>to</c>, <c>name</c>,
    /// <c>source</c>, <c>testOnly</c>, <c>includeRemoved</c>), with what they add up to by source.
    /// </summary>
    public JsonObject SubathonHistory(JsonObject a)
    {
        var filter = HistoryFilterOf(a);
        var limit = a["limit"] is JsonValue l && l.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 2000) : 100;
        var sum = Subathon.Summarize(filter);
        return new JsonObject
        {
            ["entries"] = new JsonArray([.. Subathon.History(filter, limit).Select(e => (JsonNode)HistoryJson(e))]),
            ["summary"] = new JsonObject
            {
                ["count"] = sum.Count,
                ["removed"] = sum.Removed,
                ["value"] = sum.Value,
                ["amount"] = sum.Amount,
                ["bySource"] = new JsonArray([.. sum.BySource.Select(x => (JsonNode)new JsonObject
                {
                    ["source"] = x.Source, ["count"] = x.Count, ["amount"] = x.Amount, ["value"] = x.Value,
                })]),
            },
        };
    }

    /// <summary>Corrects one history entry: <c>remove</c>, <c>restore</c>, a new <c>value</c> or <c>amount</c>, a <c>note</c>.</summary>
    public JsonObject CorrectSubathonHistory(string id, JsonObject a)
    {
        try
        {
            if (a["remove"] is JsonValue r && r.TryGetValue<bool>(out var remove) && remove)
                return HistoryJson(Subathon.RemoveEntry(id, a["note"]?.GetValue<string>()));
            if (a["restore"] is JsonValue p && p.TryGetValue<bool>(out var restore) && restore) return HistoryJson(Subathon.RestoreEntry(id));
            var value = Number(a, "value");
            var amount = Number(a, "amount");
            var note = a["note"] is JsonValue nv && nv.TryGetValue<string>(out var text) ? text : null;
            if (value is null && amount is null && note is null) throw ApiException.BadRequest("Send remove, restore, value, amount or note");
            return HistoryJson(Subathon.ChangeEntry(id, value, amount, note));
        }
        catch (ArgumentException ex) when (ex.Message.StartsWith("No history entry", StringComparison.Ordinal))
        {
            throw ApiException.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    /// <summary>Takes back every history entry matching the filter (at least one filter, or <c>all: true</c>).</summary>
    public JsonObject RemoveSubathonHistory(JsonObject a)
    {
        var filter = HistoryFilterOf(a);
        if (filter is { Owner: null, From: null, To: null, User: null or "", Source: null or "", TestOnly: false } && a["all"]?.GetValue<bool>() != true)
            throw ApiException.BadRequest("Give a filter (owner, from, to, name, source, testOnly), or all: true");
        return new JsonObject { ["removed"] = Subathon.RemoveEntries(filter) };
    }

    private HistoryFilter HistoryFilterOf(JsonObject a)
    {
        string? Text(string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var t) && t.Trim().Length > 0 ? t.Trim() : null;
        DateTimeOffset? Date(string key) => Text(key) is { } t
            ? DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d)
                ? d : throw ApiException.BadRequest($"{key} is not a date and time (e.g. 2026-11-17T18:00)")
            : null;
        bool Flag(string key, bool otherwise) => a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : otherwise;
        var owner = Text("owner");
        if (owner is not null && Subathon.Timer(owner) is null && Subathon.Goal(owner) is null) throw ApiException.NotFound($"No timer or goal '{owner}'");
        return new HistoryFilter(owner, Date("from"), Date("to"), Text("name"), Text("source"), Flag("testOnly", false), Flag("includeRemoved", true));
    }

    private JsonObject HistoryJson(SubathonHistoryEntry e) => new()
    {
        ["id"] = e.Id,
        ["timerOrGoal"] = Subathon.OwnerName(e),
        ["isGoal"] = e.IsGoal,
        ["time"] = e.Time.ToString("o"),
        ["kind"] = e.Kind.ToString(),
        ["name"] = e.User,
        ["source"] = SubathonService.SourceName(e),
        ["what"] = e.Reason,
        ["unit"] = e.Unit,
        ["quantity"] = e.Quantity,
        ["amount"] = e.Amount,
        ["currency"] = e.Currency,
        ["value"] = e.Value,
        ["change"] = SubathonService.Change(e),
        ["multiplier"] = e.Multiplier,
        ["removed"] = e.Removed,
        ["isTest"] = e.IsTest,
        ["changesTimerOrGoal"] = Subathon.IsCurrent(e),
        ["originalValue"] = e.OriginalValue,
        ["originalAmount"] = e.OriginalAmount,
        ["note"] = e.Note,
    };

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
