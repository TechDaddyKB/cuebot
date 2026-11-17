using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Core.Overlays.Session;

/// <summary>Sub-actions that read and maintain the stream totals (the data behind labels, goal bars and event lists).</summary>
internal static class StreamTotalsParams
{
    public const string Category = "Stream totals";

    public static ParameterDescriptor Type(SessionDataStore s, string help = "follower, subscriber, tip, cheer, raid, superchat, kicks...") =>
        new("type", "Type") { Help = help, Suggestions = () => s.Types() };

    public static ParameterDescriptor TallyPeriod() =>
        new("period", "Period", ParameterKind.Choice) { Choices = ["session", "week", "month", "total"], DefaultValue = "session",
            ChoiceLabels = new Dictionary<string, string> { ["session"] = "This stream", ["week"] = "This week", ["month"] = "This month", ["total"] = "All time" } };

    public static ParameterDescriptor BoardPeriod() =>
        new("period", "Period", ParameterKind.Choice) { Choices = ["session", "weekly", "monthly", "alltime"], DefaultValue = "alltime",
            ChoiceLabels = new Dictionary<string, string> { ["session"] = "This stream", ["weekly"] = "This week", ["monthly"] = "This month", ["alltime"] = "All time" } };

    /// <summary>Runs a store call, turning a bad parameter into a readable sub-action error.</summary>
    public static Task<SubActionResult> Do(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        return Task.FromResult(SubActionResult.Next);
    }

    public static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static DateTimeOffset? Date(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d
            : throw new InvalidOperationException($"'{text}' is not a date and time for {what} (e.g. 2026-11-17 18:00)");
    }
}

public sealed class StreamTotalsGetSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.get";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Get a stream total";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [StreamTotalsParams.Type(s), StreamTotalsParams.TallyPeriod()];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var type = p["type"].Trim();
        if (type.Length == 0) throw new InvalidOperationException("Pick a type, like follower or tip");
        var period = p["period"].Trim() is { Length: > 0 } x ? x : "session";
        var data = s.Snapshot();
        var tally = data[$"{type}-{period}"];
        var board = period switch { "week" => "weekly", "month" => "monthly", "total" => "alltime", _ => "session" };
        var args = context.Args;
        args["totalType"] = type;
        args["totalPeriod"] = period;
        args["totalCount"] = tally?["count"]?.GetValue<long>() ?? 0;
        args["totalAmount"] = OverlayAlerts.NumberOf(tally?["amount"]);
        args["totalGoal"] = OverlayAlerts.NumberOf(data[$"{type}-goal"]?["amount"]);
        args["latestName"] = data[$"{type}-latest"]?["name"]?.ToString() ?? "";
        args["latestAmount"] = OverlayAlerts.NumberOf(data[$"{type}-latest"]?["amount"]);
        args["topDonationName"] = data[$"{type}-{board}-top-donation"]?["name"]?.ToString() ?? "";
        args["topDonationAmount"] = OverlayAlerts.NumberOf(data[$"{type}-{board}-top-donation"]?["amount"]);
        args["topDonatorName"] = data[$"{type}-{board}-top-donator"]?["name"]?.ToString() ?? "";
        args["topDonatorAmount"] = OverlayAlerts.NumberOf(data[$"{type}-{board}-top-donator"]?["amount"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class StreamTotalsResetSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.reset";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Reset this stream's totals";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(s.ResetSession);
}

public sealed class StreamTotalsSetGoalSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.setGoal";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Set a stream total's goal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [StreamTotalsParams.Type(s), new("amount", "Goal", ParameterKind.Number) { DefaultValue = "100", Help = "0 clears the goal" }];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(() => s.SetGoal(p["type"], p.GetDouble("amount")));
}

public sealed class StreamTotalsSetSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.set";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Set a stream total";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        StreamTotalsParams.Type(s),
        StreamTotalsParams.TallyPeriod(),
        new("count", "Count") { Help = "Empty keeps the count" },
        new("amount", "Amount") { Help = "Empty keeps the amount (for counted types like follows it follows the count)" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(() => s.SetTally(p["type"], p["period"], StreamTotalsParams.Number(p["count"]) is { } c ? (long)c : null,
            StreamTotalsParams.Number(p["amount"])));
}

public sealed class StreamTotalsSetLatestSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.setLatest";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Set or clear a latest entry";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        StreamTotalsParams.Type(s),
        new("name", "Name") { Help = "Empty clears the latest entry" },
        new("amount", "Amount", ParameterKind.Number) { DefaultValue = "0" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(() => s.SetLatest(p["type"], p["name"], p.GetDouble("amount")));
}

public sealed class StreamTotalsSetTopDonationSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.setTopDonation";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Set or clear a top donation";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        StreamTotalsParams.Type(s, "tip, cheer, superchat, kicks, jewels, merch..."),
        StreamTotalsParams.BoardPeriod(),
        new("name", "Name") { Help = "Empty clears the top donation" },
        new("amount", "Amount", ParameterKind.Number) { DefaultValue = "0" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(() => s.SetTopDonation(p["type"], p["period"], p["name"], p.GetDouble("amount")));
}

public sealed class StreamTotalsRemoveEventsSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.removeEvents";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Remove stored events from the totals";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("testOnly", "Only test events", ParameterKind.Boolean) { DefaultValue = "true" },
        StreamTotalsParams.Type(s, "Empty for every type"),
        new("platform", "Platform") { Help = "Empty for every platform (twitch, kick, youtube, StreamElements...)" },
        new("from", "From") { Help = "Empty for the oldest; a date and time like 2026-11-17 18:00" },
        new("to", "To") { Help = "Empty for now" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var filter = new StreamEventFilter(p["type"], p["platform"], StreamTotalsParams.Date(p["from"], "From"),
            StreamTotalsParams.Date(p["to"], "To"), p.GetBool("testOnly", true));
        context.Args["removedEvents"] = s.RemoveEvents(filter);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class StreamTotalsResetModeSubAction(SessionDataStore s) : ISubActionHandler
{
    public string TypeId => "streamTotals.resetMode";
    public string Category => StreamTotalsParams.Category;
    public string DisplayName => "Choose when this stream's totals reset";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Reset", ParameterKind.Choice) { Choices = ["afterOffline", "manual"], DefaultValue = "afterOffline",
            ChoiceLabels = new Dictionary<string, string> { ["afterOffline"] = "15 minutes after going offline", ["manual"] = "Only by hand" } },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        StreamTotalsParams.Do(() => s.SetResetMode(SessionDataStore.ParseResetMode(p["mode"])));
}
