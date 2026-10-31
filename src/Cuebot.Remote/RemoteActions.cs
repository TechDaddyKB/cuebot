using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Remote;

/// <summary>
/// Remote triggers (Streamer.bot: Remote Instance Trigger, Remote Instance Connected / Disconnected). Filters are empty
/// for any; the instance filter matches the local label, announced name or id.
/// </summary>
internal sealed class RemoteTriggers(RemoteHub hub) : ITriggerProvider
{
    public const string Category = "Remote";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(RemoteHub.SignalEvent, Category, "Remote signal received", RemoteHub.SignalEvent,
            [
                new("signalName", "Signal name") { Help = "Empty for any signal" },
                Instance(),
            ],
            (e, s, _) => MatchesName(e, s["signalName"]) && MatchesInstance(e, s["instance"])),
        new EventTriggerType(RemoteHub.ConnectedEvent, Category, "Remote instance connected", RemoteHub.ConnectedEvent,
            [Instance()], (e, s, _) => MatchesInstance(e, s["instance"])),
        new EventTriggerType(RemoteHub.DisconnectedEvent, Category, "Remote instance disconnected", RemoteHub.DisconnectedEvent,
            [Instance()], (e, s, _) => MatchesInstance(e, s["instance"])),
    ];

    private ParameterDescriptor Instance() =>
        new("instance", "Instance") { Help = "Empty for any instance", Suggestions = () => hub.KnownNames };

    internal static bool MatchesName(BotEvent e, string name) =>
        name.Trim().Length == 0 || string.Equals(Text(e, "signalName"), name.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesInstance(BotEvent e, string instance) =>
        RemoteHub.Matches(instance, Text(e, "instanceLabel"),
            new RemoteInstanceInfo(Text(e, "instanceId"), Text(e, "instanceName"), Text(e, "instanceOs"), Text(e, "instanceVersion")));

    private static string Text(BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>
/// "Send remote signal": sends a named signal, with arguments, to one connected instance or all of them. Sets
/// %remoteSignalSent% to how many instances got it.
/// </summary>
internal sealed class SendRemoteSignalSubAction(RemoteHub hub) : ISubActionHandler
{
    public const string Id = "remote.sendSignal";

    public string TypeId => Id;
    public string Category => RemoteTriggers.Category;
    public string DisplayName => "Send remote signal";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("instance", "Instance") { Help = "Name of a remote instance; empty for every connected instance", Suggestions = () => hub.KnownNames },
        new("name", "Signal name"),
        new("includeArgs", "Include the action's variables", ParameterKind.Boolean) { DefaultValue = "false" },
        new("arguments", "Values to send", ParameterKind.Multiline) { Help = "One key=value per line (supports %variables%); always sent" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (p.GetBool("includeArgs"))
        {
            foreach (var (key, value) in context.Args) args[key] = value;
        }
        foreach (var (key, value) in RemoteProtocol.ParseArgumentLines(p["arguments"])) args[key] = value;
        context.Args["remoteSignalSent"] = await hub.SendSignalAsync(p["instance"], p["name"], context.Action.Name, args, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>
/// "Wait for remote signal": waits until a connected instance sends the named signal (only signals arriving after the
/// wait starts count). Sets %signalReceived% and %signalTimeout%; on success the signal's arguments (existing ones are
/// only replaced with "Overwrite variables") and %signalName%, %signalSender%, %instanceName%, %instanceId%, ...
/// </summary>
internal sealed class WaitForRemoteSignalSubAction(RemoteHub hub) : ISubActionHandler
{
    public const string Id = "remote.waitForSignal";

    public string TypeId => Id;
    public string Category => RemoteTriggers.Category;
    public string DisplayName => "Wait for remote signal";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("instance", "Instance") { Help = "Only accept the signal from this instance; empty for any", Suggestions = () => hub.KnownNames },
        new("name", "Signal name"),
        new("timeoutMs", "Timeout (ms, 0 = wait forever)", ParameterKind.Number) { DefaultValue = "30000" },
        new("stopOnTimeout", "Stop the action on timeout", ParameterKind.Boolean) { DefaultValue = "false" },
        new("overwrite", "Overwrite variables", ParameterKind.Boolean)
        {
            DefaultValue = "false",
            Help = "Let the signal's arguments replace this action's arguments of the same name",
        },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var timeoutMs = p.GetInt("timeoutMs", 30000);
        var signal = await hub.WaitForSignalAsync(p["name"], p["instance"], timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : null, ct)
            .ConfigureAwait(false);
        context.Args["signalReceived"] = signal is not null;
        context.Args["signalTimeout"] = signal is null;
        if (signal is null) return p.GetBool("stopOnTimeout") ? SubActionResult.Stop : SubActionResult.Next;

        var overwrite = p.GetBool("overwrite");
        foreach (var (key, value) in signal.Args)
        {
            if (overwrite || !context.Args.ContainsKey(key)) context.Args[key] = value;
        }
        foreach (var (key, value) in RemoteHub.SignalArgs(signal, includeSent: false)) context.Args[key] = value;
        context.Args["hasResult"] = signal.Args.Count > 0;
        return SubActionResult.Next;
    }
}
