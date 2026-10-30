using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;

namespace Cuebot.Tests.Overlays;

public sealed class OverlayAlertQueueTests
{
    private readonly AppLog _log = new();
    private readonly OverlayFeed _feed;
    private readonly List<JsonObject> _sent = [];

    public OverlayAlertQueueTests()
    {
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(m => _sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
    }

    private IEnumerable<string> Commands => _sent.Where(m => m["type"]!.GetValue<string>() == "command").Select(m => m["command"]!.GetValue<string>());

    private async Task<ActionRunContext> RunAsync(ISubActionHandler handler, Dictionary<string, string> values)
    {
        using var engine = new ActionEngine(new EventBus(_log), new ProfileService(null, _log), new Registry([], []), new GlobalVariables(null, _log), _log);
        var context = new ActionRunContext(new ActionDefinition(), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            engine, new GlobalVariables(null, _log), _log, 0);
        await handler.ExecuteAsync(context, ParameterValues.From(handler.Parameters, values), default);
        return context;
    }

    [Fact]
    public void Changes_are_sent_once_and_in_the_snapshot()
    {
        var queue = new OverlayAlertQueue(_feed);
        var changes = 0;
        queue.Changed += () => changes++;

        queue.Paused = true;
        queue.Paused = true;   // no change, nothing sent
        queue.Muted = true;
        queue.Skip();
        queue.Paused = false;

        Assert.Equal(["pause", "mute", "skip", "resume"], Commands);
        Assert.Equal(3, changes);
        Assert.Equal("""{"paused":false,"muted":true}""", queue.Snapshot().ToJsonString());
    }

    [Fact]
    public async Task The_alert_queue_sub_action_toggles_and_reports_the_state()
    {
        var queue = new OverlayAlertQueue(_feed);
        var handler = new AlertQueueSubAction(queue);

        var args = (await RunAsync(handler, new() { ["mode"] = "Toggle pause" })).Args;
        Assert.Equal((true, false), (args["alertsPaused"], args["alertsMuted"]));
        await RunAsync(handler, new() { ["mode"] = "Toggle mute" });
        await RunAsync(handler, new() { ["mode"] = "Skip current alert" });
        args = (await RunAsync(handler, new() { ["mode"] = "Resume" })).Args;
        Assert.Equal((false, true), (args["alertsPaused"], args["alertsMuted"]));
        Assert.Equal(["pause", "mute", "skip", "resume"], Commands);
        Assert.False(AlertQueueSubAction.Apply(queue, "explode"));
    }

    [Fact]
    public async Task Show_alert_sends_a_cuebot_alert_with_the_chosen_design()
    {
        var handler = new ShowAlertSubAction(_feed);
        await RunAsync(handler, new()
        {
            ["alertType"] = "tip", ["name"] = "Kris", ["message"] = "From an action", ["amount"] = "5", ["currency"] = "usd", ["platform"] = "Kick",
        });
        await RunAsync(handler, new() { ["alertType"] = "", ["name"] = "%user%", ["platform"] = "%platform%" });

        var events = _sent.Where(m => m["listener"]?.GetValue<string>() == ShowAlertSubAction.Listener).Select(m => m["event"]!).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(("tip", "Kris", "From an action", 5, "USD", "kick"),
            (events[0]["alertType"]!.GetValue<string>(), events[0]["name"]!.GetValue<string>(), events[0]["message"]!.GetValue<string>(),
                events[0]["amount"]!.GetValue<int>(), events[0]["currency"]!.GetValue<string>(), events[0]["service"]!.GetValue<string>()));
        // No type: the custom design; unresolved variables are left out rather than shown.
        Assert.Equal(("custom", "", ""), (events[1]["alertType"]!.GetValue<string>(), events[1]["name"]!.GetValue<string>(),
            events[1]["service"]!.GetValue<string>()));
    }
}
