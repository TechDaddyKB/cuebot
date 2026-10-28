using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Controllers;

/// <summary>
/// Game-controller triggers (category "Game Controller"). Streamer.bot 1.1 (alpha) has button, axis and hat input
/// events; Connected/Disconnected and Button Combo are Cuebot extras. Type ids are stored in actions.json and must
/// never change. The pure match functions are public for tests.
/// </summary>
public sealed class ControllerTriggers(ControllerService controllers) : ITriggerProvider
{
    public static readonly string[] TriggerIds =
    [
        ControllerEvents.Button, ControllerEvents.ButtonCombo, ControllerEvents.Axis, ControllerEvents.Hat,
        ControllerEvents.Connected, ControllerEvents.Disconnected,
    ];

    public static readonly string[] ButtonStates = ["Pressed", "Released", "Both"];
    public static readonly string[] AxisDirections = ["Either", "Positive", "Negative"];
    public static readonly string[] AxisModes = ["Enter", "Exit", "While beyond", "Any movement"];

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor Controller() => new("controller", "Controller")
        {
            Help = "Empty = any controller. Otherwise comma-separated: #slot (#1), part of the name (DualSense), type (PS5, Xbox One, "
                   + "Joystick) or GUID. The suggestions list the controllers connected now.",
            Suggestions = () => ["", .. controllers.Controllers.Select(c => $"#{c.Slot}"), .. controllers.Controllers.Select(c => c.Name).Distinct()],
        };

        bool Device(BotEvent e, ParameterValues s) => ControllerFilter.Matches(s["controller"], e.Args);

        return
        [
            new EventTriggerType(ControllerEvents.Button, ControllerEvents.Category, "Controller Button", ControllerEvents.Button,
                [
                    Controller(),
                    new("button", "Button")
                    {
                        DefaultValue = "Any",
                        Help = "Any, or comma-separated names. Positional names work on every gamepad (South = Xbox A / PlayStation Cross / "
                               + "Nintendo B); printed labels (A, Cross, Options, Plus...) and LB/RB/L3/Select style aliases also work; "
                               + "joysticks use Button1, Button2... Press a button on the controller, then open this list: it is suggested first.",
                        Suggestions = () => ["Any", .. controllers.RecentButtons, .. ControllerNames.GamepadButtons],
                    },
                    new("state", "When", ParameterKind.Choice) { DefaultValue = "Pressed", Choices = ButtonStates },
                ],
                (e, s, _) => Device(e, s) && ButtonMatches(s["button"], s["state"], e.Args)),
            new EventTriggerType(ControllerEvents.ButtonCombo, ControllerEvents.Category, "Controller Button Combo", ControllerEvents.Button,
                [
                    Controller(),
                    new("buttons", "Buttons")
                    {
                        DefaultValue = "LeftShoulder+RightShoulder",
                        Help = "Buttons held together, joined with +. Fires once, when the last one goes down.",
                        Suggestions = () => ["LeftShoulder+RightShoulder", "Back+Start", "LeftShoulder+RightShoulder+South", "LeftStick+RightStick"],
                    },
                    new("exact", "No other buttons held", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (e, s, _) => Device(e, s) && ComboMatches(s["buttons"], s.GetBool("exact"), e.Args)),
            new EventTriggerType(ControllerEvents.Axis, ControllerEvents.Category, "Controller Axis", ControllerEvents.Axis,
                [
                    Controller(),
                    new("axis", "Axis")
                    {
                        DefaultValue = "Any",
                        Help = "Any, or comma-separated: LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger (LT/RT/L2/R2 work too); "
                               + "joysticks use Axis1, Axis2... Y axes are positive downwards.",
                        Suggestions = () => ["Any", .. controllers.RecentAxes, .. ControllerNames.GamepadAxes],
                    },
                    new("direction", "Direction", ParameterKind.Choice) { DefaultValue = "Either", Choices = AxisDirections },
                    new("threshold", "Threshold (%)", ParameterKind.Number)
                    {
                        DefaultValue = "50", Help = "0-100 % of full travel, after the deadzone",
                    },
                    new("mode", "Fire", ParameterKind.Choice)
                    {
                        DefaultValue = "Enter", Choices = AxisModes,
                        Help = "Enter: once when the axis passes the threshold. Exit: once when it comes back. While beyond: every update past the "
                               + "threshold (throttled). Any movement: every update (throttled; threshold and direction ignored).",
                    },
                ],
                (e, s, _) => Device(e, s) && AxisMatches(s["axis"], s["direction"], s.GetDouble("threshold", 50) / 100, s["mode"], e.Args)),
            new EventTriggerType(ControllerEvents.Hat, ControllerEvents.Category, "Controller Hat / D-Pad", ControllerEvents.Hat,
                [
                    Controller(),
                    new("hat", "Hat")
                    {
                        DefaultValue = "Any", Help = "Any, Hat1, Hat2... (a gamepad's D-pad is Hat1)",
                        Suggestions = () => ["Any", "Hat1", "Hat2"],
                    },
                    new("direction", "Direction", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", .. ControllerNames.HatDirections] },
                ],
                (e, s, _) => Device(e, s) && HatMatches(s["hat"], s["direction"], e.Args)),
            new EventTriggerType(ControllerEvents.Connected, ControllerEvents.Category, "Controller Connected", ControllerEvents.Connected,
                [Controller()], (e, s, _) => Device(e, s)),
            new EventTriggerType(ControllerEvents.Disconnected, ControllerEvents.Category, "Controller Disconnected", ControllerEvents.Disconnected,
                [Controller()], (e, s, _) => Device(e, s)),
        ];
    }

    private static string Arg(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static double Number(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) && v is not null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

    private static bool IsAny(string text) =>
        string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "Any", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> List(string text, params char[] separators) =>
        text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool ButtonMatches(string buttons, string state, IReadOnlyDictionary<string, object?> args)
    {
        var pressed = string.Equals(Arg(args, "pressed"), "True", StringComparison.OrdinalIgnoreCase);
        switch (state.Trim().ToLowerInvariant())
        {
            case "released" when pressed:
            case "pressed" or "" when !pressed:
                return false;
        }
        if (IsAny(buttons)) return true;
        var button = Arg(args, "button");
        var label = Arg(args, "buttonLabel");
        return List(buttons, ',').Any(b => ControllerNames.ButtonMatches(b, button, label));
    }

    /// <summary>The pressed button completes the combo: it is part of it and every other combo button is already held.</summary>
    public static bool ComboMatches(string combo, bool exact, IReadOnlyDictionary<string, object?> args)
    {
        if (!string.Equals(Arg(args, "pressed"), "True", StringComparison.OrdinalIgnoreCase)) return false;
        var wanted = List(combo, '+', ',').ToArray();
        if (wanted.Length == 0) return false;
        var type = Arg(args, "controllerType");
        var held = List(Arg(args, "heldButtons"), ',').ToArray();
        bool IsHeld(string w) => held.Any(h => ControllerNames.ButtonMatches(w, h, ControllerNames.ButtonLabel(type, h)));
        if (!wanted.Any(w => ControllerNames.ButtonMatches(w, Arg(args, "button"), Arg(args, "buttonLabel")))) return false;
        if (!wanted.All(IsHeld)) return false;
        return !exact || held.All(h => wanted.Any(w => ControllerNames.ButtonMatches(w, h, ControllerNames.ButtonLabel(type, h))));
    }

    public static bool AxisMatches(string axes, string direction, double threshold, string mode, IReadOnlyDictionary<string, object?> args)
    {
        if (!IsAny(axes) && !List(axes, ',').Any(a => ControllerNames.AxisMatches(a, Arg(args, "axis")))) return false;
        var value = Number(args, "axisValue");
        var previous = Number(args, "axisPrevious");
        var now = AxisMath.Beyond(value, direction, threshold);
        var before = AxisMath.Beyond(previous, direction, threshold);
        return mode.Trim().ToLowerInvariant() switch
        {
            "any movement" or "any" => true,
            "while beyond" or "while" => now,
            "exit" => before && !now,
            _ => now && !before,
        };
    }

    /// <summary>
    /// Direction Any = the hat left the centre or changed direction; Centered = it returned to the centre; a cardinal
    /// direction (Up) matches when that direction becomes active, diagonals included; a diagonal matches exactly.
    /// </summary>
    public static bool HatMatches(string hat, string direction, IReadOnlyDictionary<string, object?> args)
    {
        if (!IsAny(hat) && !List(hat, ',').Any(h => string.Equals(h, Arg(args, "hat"), StringComparison.OrdinalIgnoreCase))) return false;
        var value = (int)Number(args, "hatValue");
        var previous = (int)Number(args, "hatPrevious");
        if (IsAny(direction)) return value != 0;
        var mask = ControllerNames.HatMask(direction);
        if (mask < 0) return false;
        if (mask == 0) return value == 0 && previous != 0;
        var cardinal = mask is 1 or 2 or 4 or 8;
        return cardinal
            ? (value & mask) != 0 && (previous & mask) == 0
            : value == mask && previous != mask;
    }
}
