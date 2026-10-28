namespace Cuebot.Controllers;

/// <summary>
/// Names for buttons, axes, hats and controller families. Gamepad buttons and axes use SDL3's standard layout, named by
/// position (South = the bottom face button: Xbox A, PlayStation Cross, Nintendo B), so one trigger works on every
/// gamepad; <see cref="ButtonLabel"/> gives the printed label. Joysticks without a mapping use Button1.., Axis1.., Hat1..
/// (1-based; the 0-based raw number is in the index variables).
/// </summary>
public static class ControllerNames
{
    /// <summary>SDL_GamepadButton order (index = SDL value).</summary>
    public static readonly string[] GamepadButtons =
    [
        "South", "East", "West", "North", "Back", "Guide", "Start", "LeftStick", "RightStick", "LeftShoulder", "RightShoulder",
        "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Misc1", "RightPaddle1", "LeftPaddle1", "RightPaddle2", "LeftPaddle2",
        "Touchpad", "Misc2", "Misc3", "Misc4", "Misc5", "Misc6",
    ];

    /// <summary>SDL_GamepadAxis order.</summary>
    public static readonly string[] GamepadAxes = ["LeftX", "LeftY", "RightX", "RightY", "LeftTrigger", "RightTrigger"];

    public const int DPadUp = 11, DPadDown = 12, DPadLeft = 13, DPadRight = 14;
    public const int LeftTrigger = 4, RightTrigger = 5;

    /// <summary>SDL_GamepadType order.</summary>
    public static readonly string[] TypeNames =
    [
        "Unknown", "Standard", "Xbox 360", "Xbox One", "PS3", "PS4", "PS5", "Switch Pro", "Joy-Con (L)", "Joy-Con (R)",
        "Joy-Con Pair", "GameCube", "Steam",
    ];

    public const string JoystickType = "Joystick";

    /// <summary>Common names people type, mapped to the positional names.</summary>
    private static readonly Dictionary<string, string> ButtonAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cross"] = "South", ["Circle"] = "East", ["Square"] = "West", ["Triangle"] = "North",
        ["LB"] = "LeftShoulder", ["L1"] = "LeftShoulder", ["L"] = "LeftShoulder",
        ["RB"] = "RightShoulder", ["R1"] = "RightShoulder", ["R"] = "RightShoulder",
        ["LS"] = "LeftStick", ["L3"] = "LeftStick", ["RS"] = "RightStick", ["R3"] = "RightStick",
        ["Select"] = "Back", ["View"] = "Back", ["Share"] = "Back", ["Create"] = "Back", ["Minus"] = "Back", ["-"] = "Back",
        ["Menu"] = "Start", ["Options"] = "Start", ["Plus"] = "Start", ["+"] = "Start",
        ["Home"] = "Guide", ["PS"] = "Guide", ["Xbox"] = "Guide",
        ["Up"] = "DPadUp", ["Down"] = "DPadDown", ["Left"] = "DPadLeft", ["Right"] = "DPadRight",
        ["Capture"] = "Misc1", ["Mic"] = "Misc1",
        ["P1"] = "RightPaddle1", ["P2"] = "RightPaddle2", ["P3"] = "LeftPaddle1", ["P4"] = "LeftPaddle2",
    };

    private static readonly Dictionary<string, string> AxisAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LT"] = "LeftTrigger", ["L2"] = "LeftTrigger", ["ZL"] = "LeftTrigger",
        ["RT"] = "RightTrigger", ["R2"] = "RightTrigger", ["ZR"] = "RightTrigger",
        ["LeftStickX"] = "LeftX", ["LeftStickY"] = "LeftY", ["RightStickX"] = "RightX", ["RightStickY"] = "RightY",
    };

    public static string TypeName(int sdlType) => sdlType >= 0 && sdlType < TypeNames.Length ? TypeNames[sdlType] : "Unknown";

    public static string Button(bool gamepad, int index) =>
        gamepad && index >= 0 && index < GamepadButtons.Length ? GamepadButtons[index] : $"Button{index + 1}";

    public static string Axis(bool gamepad, int index) =>
        gamepad && index >= 0 && index < GamepadAxes.Length ? GamepadAxes[index] : $"Axis{index + 1}";

    public static string Hat(int index) => $"Hat{index + 1}";

    public static bool IsTriggerAxis(bool gamepad, int index) => gamepad && index is LeftTrigger or RightTrigger;

    /// <summary>The positional name for a typed name or alias ("A" is ambiguous, so it is resolved per type by
    /// <see cref="ButtonMatches"/>); unknown text is returned unchanged.</summary>
    public static string CanonicalButton(string name)
    {
        name = name.Trim();
        if (ButtonAliases.TryGetValue(name, out var canonical)) return canonical;
        var known = GamepadButtons.FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
        return known ?? name;
    }

    public static string CanonicalAxis(string name)
    {
        name = name.Trim();
        if (AxisAliases.TryGetValue(name, out var canonical)) return canonical;
        return GamepadAxes.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>
    /// Does a typed button name (positional name, alias, printed label such as "A" or "Cross", or Button&lt;n&gt;) mean
    /// this button? Labels are compared with the controller's own label, so "A" is South on Xbox and East on Nintendo.
    /// </summary>
    public static bool ButtonMatches(string wanted, string button, string label)
    {
        wanted = wanted.Trim();
        if (wanted.Length == 0) return false;
        if (string.Equals(wanted, label, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(wanted, button, StringComparison.OrdinalIgnoreCase)) return true;
        // Face labels belong to the controller's family (handled above); only non-face aliases map positionally.
        if (wanted.Length == 1 && char.IsLetter(wanted[0])) return false;
        return string.Equals(CanonicalButton(wanted), button, StringComparison.OrdinalIgnoreCase);
    }

    public static bool AxisMatches(string wanted, string axis) =>
        string.Equals(CanonicalAxis(wanted), axis, StringComparison.OrdinalIgnoreCase);

    private enum Family { Xbox, PlayStation, Nintendo, Other }

    private static Family FamilyOf(string type) => type switch
    {
        "Xbox 360" or "Xbox One" or "Standard" or "Steam" or "Unknown" => Family.Xbox,
        "PS3" or "PS4" or "PS5" => Family.PlayStation,
        "Switch Pro" or "Joy-Con (L)" or "Joy-Con (R)" or "Joy-Con Pair" or "GameCube" => Family.Nintendo,
        _ => Family.Other,
    };

    /// <summary>The label printed on the button for this controller family ("A", "Cross", "Options", "Plus"...).</summary>
    public static string ButtonLabel(string type, string button)
    {
        var family = FamilyOf(type);
        return (family, button) switch
        {
            (Family.Other, _) => button,
            (Family.Xbox, "South") => "A",
            (Family.Xbox, "East") => "B",
            (Family.Xbox, "West") => "X",
            (Family.Xbox, "North") => "Y",
            (Family.PlayStation, "South") => "Cross",
            (Family.PlayStation, "East") => "Circle",
            (Family.PlayStation, "West") => "Square",
            (Family.PlayStation, "North") => "Triangle",
            (Family.Nintendo, "South") => "B",
            (Family.Nintendo, "East") => "A",
            (Family.Nintendo, "West") => "Y",
            (Family.Nintendo, "North") => "X",
            (Family.Xbox, "Back") => type is "Xbox One" ? "View" : "Back",
            (Family.Xbox, "Start") => type is "Xbox One" ? "Menu" : "Start",
            (Family.Xbox, "Guide") => "Xbox",
            (Family.Xbox, "LeftShoulder") => "LB",
            (Family.Xbox, "RightShoulder") => "RB",
            (Family.Xbox, "Misc1") => "Share",
            (Family.PlayStation, "Back") => type is "PS5" ? "Create" : type is "PS4" ? "Share" : "Select",
            (Family.PlayStation, "Start") => type is "PS3" ? "Start" : "Options",
            (Family.PlayStation, "Guide") => "PS",
            (Family.PlayStation, "LeftShoulder") => "L1",
            (Family.PlayStation, "RightShoulder") => "R1",
            (Family.PlayStation, "LeftStick") => "L3",
            (Family.PlayStation, "RightStick") => "R3",
            (Family.PlayStation, "Misc1") => "Mic",
            (Family.Nintendo, "Back") => "Minus",
            (Family.Nintendo, "Start") => "Plus",
            (Family.Nintendo, "Guide") => "Home",
            (Family.Nintendo, "LeftShoulder") => "L",
            (Family.Nintendo, "RightShoulder") => "R",
            (Family.Nintendo, "Misc1") => "Capture",
            _ => button,
        };
    }

    /// <summary>"Up", "UpRight", "Right", ... or "Centered" for a hat bitmask (1 up, 2 right, 4 down, 8 left).</summary>
    public static string HatDirection(int value) => (value & 0x0F) switch
    {
        0 => "Centered",
        1 => "Up",
        2 => "Right",
        3 => "UpRight",
        4 => "Down",
        6 => "DownRight",
        8 => "Left",
        9 => "UpLeft",
        12 => "DownLeft",
        _ => "Invalid",
    };

    public static readonly string[] HatDirections = ["Up", "Down", "Left", "Right", "UpLeft", "UpRight", "DownLeft", "DownRight", "Centered"];

    /// <summary>Hat bitmask for a direction name (0 for Centered, -1 for unknown).</summary>
    public static int HatMask(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "centered" or "centred" or "center" or "centre" or "none" => 0,
        "up" or "n" or "north" => 1,
        "right" or "e" or "east" => 2,
        "down" or "s" or "south" => 4,
        "left" or "w" or "west" => 8,
        "upright" or "rightup" or "ne" or "northeast" => 3,
        "downright" or "rightdown" or "se" or "southeast" => 6,
        "downleft" or "leftdown" or "sw" or "southwest" => 12,
        "upleft" or "leftup" or "nw" or "northwest" => 9,
        _ => -1,
    };

    /// <summary>The D-pad buttons of a gamepad as a hat bitmask, so D-pads and joystick hats share the Hat trigger.</summary>
    public static byte DPadMask(bool up, bool down, bool left, bool right) =>
        (byte)((up ? 1 : 0) | (right ? 2 : 0) | (down ? 4 : 0) | (left ? 8 : 0));
}
