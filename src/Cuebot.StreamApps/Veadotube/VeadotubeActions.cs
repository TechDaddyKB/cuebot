using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Veadotube.VeadotubeEvents;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>veadotube service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class VeadotubeRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new VeadotubeService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<VeadotubeService>());
        services.AddSingleton<ITriggerProvider>(sp => new VeadotubeTriggers(sp.GetRequiredService<VeadotubeService>()));
        services.AddSingleton<Cuebot.Core.Scripting.IScriptFunctionProvider>(sp => new VeadotubeScriptFunctions(sp.GetRequiredService<VeadotubeService>()));
    }
}

/// <summary>veadotube triggers (category "veadotube"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class VeadotubeTriggers(VeadotubeService veadotube) : ITriggerProvider
{
    public const string Category = "veadotube";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.StateChanged, "Avatar state changed",
            [
                new("state", "States")
                {
                    Help = "Comma-separated avatar state names; leave empty for any",
                    Suggestions = veadotube.AllStateNames,
                },
            ],
            (e, s) => MatchesList(s["state"], e.Text("state"), e.Text("stateId"))),
        Trigger(Category, E.SwitchChanged, "Push-to-talk (on/off node) changed",
            [
                new("node", "Nodes") { Help = "Comma-separated node names, e.g. push-to-talk; leave empty for any" },
                new("state", "Turned", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "On", "Off"] },
            ],
            (e, s) => MatchesList(s["node"], e.Text("nodeName"), e.Text("nodeId"))
                      && (s["state"] is "" or "Any" || string.Equals(s["state"], e.Text("state"), StringComparison.OrdinalIgnoreCase))),
        Trigger(Category, E.NumberChanged, "Number node changed",
            [
                new("node", "Nodes")
                {
                    Help = "Comma-separated Number node names (full veadotube); leave empty for any",
                    Suggestions = () => veadotube.NodeNames(VeadotubeProtocol.NumberType),
                },
            ],
            (e, s) => MatchesList(s["node"], e.Text("nodeName"), e.Text("nodeId"))),
    ];
}

/// <summary>veadotube sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VeadotubeSubActions
{
    public static readonly string[] TypeIds =
    [
        "veadotube.changeState", "veadotube.setSwitch", "veadotube.setNumber", "veadotube.getState", "veadotube.getNumber", "veadotube.resetNode",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(VeadotubeService veadotube)
    {
        ParameterDescriptor Node(string help, Func<IEnumerable<string>>? suggestions = null) => new("node", "Node") { Help = help, Suggestions = suggestions };
        ParameterDescriptor Instance() => new("instance", "Which veadotube")
        {
            Help = "Leave empty for the connected one. To control another open veadotube too, type its name (e.g. veadotube mini) or address",
            Suggestions = veadotube.InstanceNames,
        };
        ParameterDescriptor For(string what) => new("seconds", "For (seconds)", ParameterKind.Number)
        {
            Help = $"Puts {what} back after this many seconds; leave empty to keep it",
        };
        return
        [
            new StreamAppSubAction("veadotube.changeState", VeadotubeTriggers.Category, "Change avatar state",
                [
                    new("mode", "How", ParameterKind.Choice)
                    {
                        DefaultValue = "Set",
                        Choices = ["Set", "Push", "Pop", "Toggle", "Random", "Clear"],
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["Set"] = "Switch to this state",
                            ["Push"] = "Show on top (remember the one before)",
                            ["Pop"] = "Take this state off (go back)",
                            ["Toggle"] = "Show on top, or take off if showing",
                            ["Random"] = "Switch to a random state (from the list, or any)",
                            ["Clear"] = "Back to the first state",
                        },
                    },
                    new("state", "State")
                    {
                        Help = "The avatar state's name, as in veadotube; for Random, a comma-separated list or empty for any; may use %arguments%",
                        Suggestions = veadotube.AllStateNames,
                    },
                    For("the state before"),
                    Node("Leave empty: veadotube mini has one (avatar state). In full veadotube, the name of a State Events node.",
                        () => veadotube.NodeNames(VeadotubeProtocol.StateEventsType)),
                    Instance(),
                ],
                async (ctx, v, ct) => Store(ctx, "veadotube.state",
                    await veadotube.ChangeStateAsync(v["mode"], v["state"], v["node"], v["instance"], Seconds(v["seconds"]), ct).ConfigureAwait(false))),
            new StreamAppSubAction("veadotube.setSwitch", VeadotubeTriggers.Category, "Push-to-talk on/off",
                [
                    Switch("On", "Off"),
                    For("it"),
                    Node("Leave empty for push-to-talk (veadotube mini). In full veadotube, the name of a Boolean node.",
                        () => veadotube.NodeNames(VeadotubeProtocol.BooleanType)),
                    Instance(),
                ],
                async (ctx, v, ct) => Store(ctx, "veadotube.value",
                    await veadotube.SetSwitchAsync(ParseSwitch(v["state"]), v["node"], v["instance"], Seconds(v["seconds"]), ct).ConfigureAwait(false))),
            new StreamAppSubAction("veadotube.setNumber", VeadotubeTriggers.Category, "Set number node",
                [
                    Node("The name of a Number node you exposed in full veadotube (veadotube mini has none)",
                        () => veadotube.NodeNames(VeadotubeProtocol.NumberType)),
                    new("value", "Value") { Help = "A number; may use %arguments%" },
                    new("add", "Add to the current number instead", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("min", "Lowest value", ParameterKind.Number) { Help = "With Highest: the node's range (shown as a graph in veadotube). Empty keeps the range it has" },
                    new("max", "Highest value", ParameterKind.Number) { Help = "With Lowest: the node's range" },
                    Instance(),
                ],
                async (ctx, v, ct) =>
                {
                    var number = await veadotube.SetNumberAsync(v["node"],
                        OptionalNumber(v["value"], "Value") ?? throw new ArgumentException("Value is required."),
                        bool.TryParse(v["add"], out var add) && add, OptionalNumber(v["min"], "Lowest value"), OptionalNumber(v["max"], "Highest value"),
                        v["instance"], ct).ConfigureAwait(false);
                    if (number is not null) ctx.Args["veadotube.number"] = number.Value;
                }),
            new StreamAppSubAction("veadotube.getState", VeadotubeTriggers.Category, "Get avatar state",
                [
                    Node("Leave empty for veadotube mini's avatar state", () => veadotube.NodeNames(VeadotubeProtocol.StateEventsType)),
                    Instance(),
                ],
                async (ctx, v, ct) =>
                {
                    var (state, stateId, states, image) = await veadotube.GetStateAsync(v["node"], v["instance"], ct).ConfigureAwait(false);
                    ctx.Args["veadotube.state"] = state;
                    ctx.Args["veadotube.stateId"] = stateId;
                    ctx.Args["veadotube.states"] = string.Join(", ", states.Select(s => s.Name));
                    ctx.Args["veadotube.stateCount"] = states.Count.ToString(CultureInfo.InvariantCulture);
                    ctx.Args["veadotube.stateImage"] = image;
                    if (v["instance"].Trim().Length == 0)
                    {
                        if (veadotube.SwitchNode() is { } ptt && veadotube.SwitchValue(ptt) is { } on) ctx.Args["veadotube.pushToTalk"] = on;
                        foreach (var number in veadotube.NumberNodes())
                            if (veadotube.NumberValue(number) is { } n) ctx.Args[$"veadotube.number.{number.Label}"] = n.Value;
                    }
                }),
            new StreamAppSubAction("veadotube.getNumber", VeadotubeTriggers.Category, "Get number node",
                [
                    Node("The name of a Number node you exposed in full veadotube", () => veadotube.NodeNames(VeadotubeProtocol.NumberType)),
                    Instance(),
                ],
                async (ctx, v, ct) =>
                {
                    var number = await veadotube.GetNumberAsync(v["node"], v["instance"], ct).ConfigureAwait(false);
                    ctx.Args["veadotube.number"] = number?.Value;
                    ctx.Args["veadotube.numberMin"] = number?.Min;
                    ctx.Args["veadotube.numberMax"] = number?.Max;
                }),
            new StreamAppSubAction("veadotube.resetNode", VeadotubeTriggers.Category, "Reset node",
                [
                    new("type", "Node type", ParameterKind.Choice)
                    {
                        DefaultValue = "OnOff", Choices = ["OnOff", "Number"],
                        ChoiceLabels = new Dictionary<string, string> { ["OnOff"] = "On/off (push-to-talk, Boolean)", ["Number"] = "Number" },
                    },
                    Node("Leave empty for push-to-talk; otherwise the node's name"),
                    Instance(),
                ],
                (_, v, ct) => veadotube.ResetNodeAsync(v["type"], v["node"], v["instance"], ct)),
        ];
    }

    private static TimeSpan? Seconds(string value) =>
        OptionalNumber(value, "Seconds") is { } s && s > 0 ? TimeSpan.FromSeconds(Math.Min(s, 86_400)) : null;
}
