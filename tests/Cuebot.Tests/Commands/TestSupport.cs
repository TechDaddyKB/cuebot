using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Commands;

/// <summary>Manually advanced clock in UTC.</summary>
internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
}

internal static class Chat
{
    public static BotEvent Message(string text, string user = "alice", string userId = "1", string platform = "Twitch",
        ChatRoles roles = ChatRoles.None, Dictionary<string, object?>? extra = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["user"] = user,
            ["userId"] = userId,
            ["message"] = text,
            ["rawInput"] = text,
            ["isBroadcaster"] = roles.HasFlag(ChatRoles.Broadcaster),
            ["isModerator"] = roles.HasFlag(ChatRoles.Moderator),
            ["isVip"] = roles.HasFlag(ChatRoles.Vip),
            ["isSubscriber"] = roles.HasFlag(ChatRoles.Subscriber),
        };
        if (extra is not null) foreach (var (k, v) in extra) args[k] = v;
        return new BotEvent(ChatHub.MessageEvent, args);
    }
}

/// <summary>Records the events published on a bus (optionally only some types).</summary>
internal sealed class EventRecorder
{
    private readonly List<BotEvent> _events = [];

    public EventRecorder(EventBus bus, params string[] types) =>
        bus.Subscribe(e =>
        {
            if (types.Length == 0 || types.Contains(e.Type)) lock (_events) _events.Add(e);
        });

    public IReadOnlyList<BotEvent> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    public IReadOnlyList<BotEvent> OfType(string type) => Events.Where(e => e.Type == type).ToArray();

    public void Clear()
    {
        lock (_events) _events.Clear();
    }
}

/// <summary>Runs one sub-action inside a real engine and captures the arguments and the branch it took.</summary>
internal static class SubActionHarness
{
    public sealed record Result(bool Success, Dictionary<string, object?> Args, string? Branch, IReadOnlyList<LogEntry> Log);

    public static async Task<Result> RunAsync(ISubActionHandler handler, Dictionary<string, string> parameters,
        Dictionary<string, object?>? args = null)
    {
        using var log = new AppLog();
        var capture = new CaptureSubAction();
        var profile = new ProfileService(null, log);
        var registry = new Registry([handler, capture], []);
        using var engine = new ActionEngine(new EventBus(log), profile, registry, new GlobalVariables(null, log), log);

        var step = new SubActionDefinition { Type = handler.TypeId, Parameters = parameters };
        foreach (var branch in handler.Branches)
            step.Branch(branch).Add(new SubActionDefinition { Type = CaptureSubAction.Id, Parameters = { ["label"] = branch } });
        var action = new ActionDefinition
        {
            Name = "Harness",
            SubActions = [step, new SubActionDefinition { Type = CaptureSubAction.Id, Parameters = { ["label"] = "end" } }],
        };
        var success = await engine.RunAsync(action, args);
        var branchTaken = capture.Labels.FirstOrDefault(l => l != "end");
        return new Result(success, capture.LastArgs ?? [], branchTaken, log.Recent());
    }

    private sealed class CaptureSubAction : ISubActionHandler
    {
        public const string Id = "test.capture";
        public List<string> Labels { get; } = [];
        public Dictionary<string, object?>? LastArgs { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("label", "Label")];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            Labels.Add(p["label"]);
            LastArgs = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
