using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Controllers;

/// <summary>
/// Game-controller sub-actions. Streamer.bot documents none for controllers, so all four are Cuebot extras.
/// Type ids are stored in actions.json and must never change.
/// </summary>
public static class ControllerSubActions
{
    public static readonly string[] TypeIds = ["controller.rumble", "controller.setLed", "controller.getState", "controller.getList"];

    public static IReadOnlyList<ISubActionHandler> Create(ControllerService controllers)
    {
        ParameterDescriptor Controller(string defaultValue = "Any") => new("controller", "Controller")
        {
            DefaultValue = defaultValue,
            Help = "Any = every connected controller; or #slot, part of the name, type (PS5) or GUID (comma-separated for several)",
            Suggestions = () => ["Any", .. controllers.Controllers.Select(c => $"#{c.Slot}"), .. controllers.Controllers.Select(c => c.Name).Distinct()],
        };

        return
        [
            new Step("controller.rumble", "Rumble",
                [
                    Controller(),
                    new("low", "Low-frequency motor (%)", ParameterKind.Number) { DefaultValue = "50", Help = "0-100; the big, heavy motor" },
                    new("high", "High-frequency motor (%)", ParameterKind.Number) { DefaultValue = "50", Help = "0-100; the small, buzzy motor" },
                    new("duration", "Duration (ms)", ParameterKind.Number) { DefaultValue = "500", Help = "Up to 60000; 0 stops rumbling" },
                ],
                async (ctx, p, ct) =>
                {
                    var done = await controllers.RumbleAsync(p["controller"], p.GetDouble("low", 50), p.GetDouble("high", 50),
                        p.GetInt("duration", 500), ct).ConfigureAwait(false);
                    ctx.Args["controllersAffected"] = done.Count;
                }),
            new Step("controller.setLed", "Set LED Colour",
                [
                    Controller(),
                    new("color", "Colour") { DefaultValue = "#0080FF", Help = "#RRGGBB or r,g,b. Only controllers with an RGB light (PS4/PS5, some others)" },
                ],
                async (ctx, p, ct) =>
                {
                    var (r, g, b) = ControllerService.ParseColor(p["color"]);
                    var done = await controllers.SetLedAsync(p["controller"], r, g, b, ct).ConfigureAwait(false);
                    ctx.Args["controllersAffected"] = done.Count;
                }),
            new Step("controller.getState", "Get Controller State",
                [Controller("#1")],
                async (ctx, p, ct) =>
                {
                    await controllers.EnsureRunningAsync(ct).ConfigureAwait(false);
                    var c = controllers.Resolve(p["controller"]).FirstOrDefault();
                    ctx.Args["controllerConnected"] = c is not null;
                    if (c is null) return;
                    foreach (var (key, value) in c.ToArgs()) ctx.Args[key] = value;
                    ctx.Args["heldButtons"] = string.Join(",", c.HeldButtons);
                    foreach (var axis in c.Axes) ctx.Args["axis" + axis.Name] = Math.Round(axis.Value, 3);
                    for (var i = 0; i < c.Hats.Count; i++)
                        ctx.Args["hat" + (i + 1).ToString(CultureInfo.InvariantCulture)] = ControllerNames.HatDirection(c.Hats[i]);
                }),
            new Step("controller.getList", "Get Controllers",
                [],
                async (ctx, _, ct) =>
                {
                    await controllers.EnsureRunningAsync(ct).ConfigureAwait(false);
                    var list = controllers.Controllers;
                    ctx.Args["controllerCount"] = list.Count;
                    ctx.Args["controllerNames"] = string.Join(", ", list.Select(c => c.Name));
                    for (var i = 0; i < list.Count; i++)
                    {
                        var n = i.ToString(CultureInfo.InvariantCulture);
                        ctx.Args[$"controller{n}Name"] = list[i].Name;
                        ctx.Args[$"controller{n}Index"] = list[i].Slot;
                        ctx.Args[$"controller{n}Type"] = list[i].Device.Type;
                        ctx.Args[$"controller{n}Guid"] = list[i].Device.Guid;
                    }
                }),
        ];
    }

    private sealed class Step(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => ControllerEvents.Category;
        public string DisplayName => displayName;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            await run(context, parameters, ct).ConfigureAwait(false);
            return SubActionResult.Next;
        }
    }
}
