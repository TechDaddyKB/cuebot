using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Scripting.Compat;

/// <summary>A custom trigger a script registered (Streamer.bot <c>CPH.RegisterCustomTrigger</c>).</summary>
public sealed record CodeTrigger(string Name, string EventName, IReadOnlyList<string> Categories);

/// <summary>
/// Custom ("code") triggers: scripts register them by event name and fire them with <c>CPH.TriggerCodeEvent</c>; the
/// WebSocket server lists them (GetCodeTriggers) and fires them (ExecuteCodeTrigger). Firing publishes
/// <see cref="EventType"/> with an <c>eventName</c> argument; the "Custom code event" trigger filters on it.
/// </summary>
public sealed class CodeTriggerRegistry
{
    public const string EventType = "custom.codeEvent";
    public const string TriggerTypeId = "custom.codeEvent";

    private readonly object _gate = new();
    private readonly Dictionary<string, CodeTrigger> _triggers = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyList<CodeTrigger> All
    {
        get
        {
            lock (_gate) return _triggers.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Adds or replaces the trigger for <paramref name="eventName"/>. Returns false when the name is empty.</summary>
    public bool Register(string name, string eventName, IEnumerable<string>? categories = null)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        var trigger = new CodeTrigger(string.IsNullOrWhiteSpace(name) ? eventName.Trim() : name.Trim(), eventName.Trim(),
            categories?.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray() ?? []);
        lock (_gate) _triggers[trigger.EventName] = trigger;
        Changed?.Invoke();
        return true;
    }

    public CodeTrigger? Find(string eventName)
    {
        lock (_gate) return _triggers.GetValueOrDefault(eventName.Trim());
    }

    /// <summary>The bus event for firing <paramref name="eventName"/> with extra arguments.</summary>
    public static BotEvent CreateEvent(string eventName, IReadOnlyDictionary<string, object?>? args = null)
    {
        var data = args is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        // The engine adds its own run arguments again; keep the caller's from leaking into the new run.
        foreach (var key in ActionEngine.RunArgumentNames) data.Remove(key);
        data["eventName"] = eventName;
        data["customEvent.event"] = eventName;   // Streamer.bot's Custom trigger names the event here
        return new BotEvent(EventType, data);
    }
}

/// <summary>The "Custom code event" trigger: runs when a script or a WebSocket client fires the named event.</summary>
public sealed class CodeTriggerProvider(CodeTriggerRegistry registry) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(CodeTriggerRegistry.TriggerTypeId, "Custom", "Custom code event", CodeTriggerRegistry.EventType,
            [
                new ParameterDescriptor("eventName", "Event name")
                {
                    Suggestions = () => registry.All.Select(t => t.EventName),
                    Help = "Registered by scripts with CPH.RegisterCustomTrigger; empty matches every code event",
                },
            ],
            (e, settings, _) =>
            {
                var wanted = settings["eventName"].Trim();
                return wanted.Length == 0 || string.Equals(e.Args.GetValueOrDefault("eventName") as string, wanted,
                    StringComparison.OrdinalIgnoreCase);
            }),
    ];
}
