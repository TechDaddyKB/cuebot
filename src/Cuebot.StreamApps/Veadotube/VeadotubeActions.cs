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
    ];
}

/// <summary>veadotube sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VeadotubeSubActions
{
    public static readonly string[] TypeIds = ["veadotube.changeState", "veadotube.setSwitch", "veadotube.setNumber", "veadotube.getState"];

    public static IReadOnlyList<ISubActionHandler> Create(VeadotubeService veadotube)
    {
        ParameterDescriptor Node(string help) => new("node", "Node") { Help = help };
        return
        [
            new StreamAppSubAction("veadotube.changeState", VeadotubeTriggers.Category, "Change avatar state",
                [
                    new("mode", "How", ParameterKind.Choice)
                    {
                        DefaultValue = "Set",
                        Choices = ["Set", "Push", "Pop", "Toggle", "Clear"],
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["Set"] = "Switch to this state",
                            ["Push"] = "Show on top (remember the one before)",
                            ["Pop"] = "Take this state off (go back)",
                            ["Toggle"] = "Show on top, or take off if showing",
                            ["Clear"] = "Back to the first state",
                        },
                    },
                    new("state", "State") { Help = "The avatar state's name, as in veadotube; may use %arguments%", Suggestions = veadotube.AllStateNames },
                    Node("Leave empty: veadotube mini has one (avatar state). In full veadotube, the name of a State Events node."),
                ],
                async (ctx, v, ct) => Store(ctx, "veadotube.state",
                    await veadotube.ChangeStateAsync(v["mode"], v["state"], v["node"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("veadotube.setSwitch", VeadotubeTriggers.Category, "Push-to-talk on/off",
                [
                    Switch("On", "Off"),
                    Node("Leave empty for push-to-talk (veadotube mini). In full veadotube, the name of a Boolean node."),
                ],
                async (ctx, v, ct) => Store(ctx, "veadotube.value",
                    await veadotube.SetSwitchAsync(ParseSwitch(v["state"]), v["node"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("veadotube.setNumber", VeadotubeTriggers.Category, "Set number node",
                [
                    Node("The name of a Number node you exposed in full veadotube (veadotube mini has none)"),
                    new("value", "Value") { Help = "A number; may use %arguments%" },
                    new("add", "Add to the current number instead", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (_, v, ct) => veadotube.SetNumberAsync(v["node"],
                    OptionalNumber(v["value"], "Value") ?? throw new ArgumentException("Value is required."),
                    bool.TryParse(v["add"], out var add) && add, ct)),
            new StreamAppSubAction("veadotube.getState", VeadotubeTriggers.Category, "Get avatar state",
                [
                    Node("Leave empty for veadotube mini's avatar state"),
                ],
                (ctx, v, _) =>
                {
                    var node = veadotube.StateNode(v["node"]) ?? throw new StreamAppRequestException("veadotube is not connected, or has no avatar state node.");
                    ctx.Args["veadotube.state"] = veadotube.CurrentState(node) ?? "";
                    ctx.Args["veadotube.states"] = string.Join(", ", veadotube.States(node).Select(s => s.Name));
                    ctx.Args["veadotube.stateCount"] = veadotube.States(node).Count.ToString(CultureInfo.InvariantCulture);
                    if (veadotube.SwitchNode() is { } ptt && veadotube.SwitchValue(ptt) is { } on) ctx.Args["veadotube.pushToTalk"] = on;
                    return Task.CompletedTask;
                }),
        ];
    }
}
