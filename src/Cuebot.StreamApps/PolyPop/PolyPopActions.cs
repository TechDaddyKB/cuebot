using Cuebot.Core.Actions;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.PolyPop;

/// <summary>PolyPop triggers (Cuebot extras: Streamer.bot has none for PolyPop).</summary>
internal sealed class PolyPopTriggers : ITriggerProvider
{
    public const string Category = "PolyPop";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, PolyPopService.ConnectedEvent, "Plugin connected"),
        Trigger(Category, PolyPopService.DisconnectedEvent, "Plugin disconnected"),
        Trigger(Category, PolyPopService.MessageEvent, "Message from PolyPop",
            [
                new("type", "Message types") { Help = "e.g. TRIGGER; comma-separated, empty for any" },
                new("name", "Names") { Help = "The name the PolyPop side sent; comma-separated, empty for any" },
            ],
            (e, s) => MatchesList(s["type"], e.Text("polypop.type")) && MatchesList(s["name"], e.Text("polypop.name"))),
    ];
}

/// <summary>PolyPop sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class PolyPopSubActions
{
    public static readonly string[] TypeIds = ["polypop.triggerAlert"];

    public static IReadOnlyList<ISubActionHandler> Create(PolyPopService polyPop) =>
    [
        new StreamAppSubAction("polypop.triggerAlert", PolyPopTriggers.Category, "Trigger alert",
            [
                new("alertName", "Alert name") { Help = "The name of a WebSocket Alert in PolyPop's WebSocket source" },
                new("variables", "Variables", ParameterKind.Multiline)
                {
                    Help = "One name=value per line (or a JSON object). Each name becomes <name> in PolyPop; values may use %arguments%.",
                },
            ],
            async (ctx, v, ct) => ctx.Args["polypopClients"] = await polyPop.TriggerAlertAsync(Require(v["alertName"], "Alert name"),
                ParsePairs(v["variables"]), ct).ConfigureAwait(false)),
    ];
}
