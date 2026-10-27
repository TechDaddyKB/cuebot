using System.Globalization;
using Cuebot.Core.Desktop.Hotkeys;

namespace Cuebot.Core.Desktop.Input;

/// <summary>
/// Parses the text of a "Keyboard Press": one or more key combinations, pressed one after the other.
/// <list type="bullet">
/// <item>Cuebot syntax, as for hotkeys: <c>Ctrl+Shift+F1</c>; several separated by spaces or commas
/// (<c>Ctrl+C, Ctrl+V</c>). Key names are <see cref="HotkeyKeys.Names"/> (aliases such as Esc, PgUp accepted).</item>
/// <item>SendKeys syntax (as Windows tools use): <c>^</c> Ctrl, <c>+</c> Shift, <c>%</c> Alt, <c>~</c> Enter,
/// <c>{F1}</c> named keys, <c>{TAB 3}</c> repeats, <c>+(ab)</c> a modifier held over a group, plain characters typed
/// (upper case and shifted symbols use Shift, assuming a US layout). Used when the text is not Cuebot syntax and
/// either contains one of <c>{ ^ % ~ (</c> or has no <c>+</c> at all (plain text such as <c>Hello</c>).</item>
/// </list>
/// </summary>
public static class KeyCombos
{
    /// <summary>Most combos one press may send (guards against "{A 100000}").</summary>
    public const int MaxCombos = 200;

    public static IReadOnlyList<HotkeyGesture> Parse(string? text) =>
        TryParse(text, out var combos, out var error) ? combos : throw new FormatException(error);

    public static bool TryParse(string? text, out IReadOnlyList<HotkeyGesture> combos, out string error)
    {
        combos = [];
        var t = text?.Trim() ?? "";
        if (t.Length == 0)
        {
            error = "No keys given";
            return false;
        }
        if (TryParseCuebot(t, out var list, out error))
        {
            combos = list;
            return true;
        }
        // SendKeys when the text uses its special characters, or is plain text to type (no "Ctrl+X"-style '+'):
        // "Ctrl+Nope" is a mistyped combo, not the letters C-t-r-l followed by Shift+N.
        var looksLikeSendKeys = t.IndexOfAny(['{', '^', '%', '~', '(']) >= 0 || !t.Contains('+');
        if (!looksLikeSendKeys) return false;
        if (TryParseSendKeys(t, out list, out var sendKeysError))
        {
            combos = list;
            error = "";
            return true;
        }
        if (t.IndexOfAny(['{', '^', '%', '~', '(']) >= 0) error = sendKeysError;
        return false;
    }

    /// <summary>"Ctrl+Shift+F1", "Ctrl+C, Ctrl+V", "Alt+Tab Enter".</summary>
    public static bool TryParseCuebot(string text, out List<HotkeyGesture> combos, out string error)
    {
        combos = [];
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw;
            if (token == ",") continue;
            // A trailing comma separates combos, unless it is the comma key itself ("Ctrl+,").
            if (token.Length > 1 && token.EndsWith(',') && token[^2] != '+') token = token[..^1];
            foreach (var part in SplitCommas(token))
            {
                if (!HotkeyGesture.TryParse(part, out var gesture, out error)) return false;
                combos.Add(gesture);
            }
        }
        if (combos.Count == 0)
        {
            error = "No keys given";
            return false;
        }
        if (combos.Count > MaxCombos)
        {
            error = $"Too many keys (at most {MaxCombos})";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>"Ctrl+C,Ctrl+V" → two combos; "Ctrl+," stays one.</summary>
    private static IEnumerable<string> SplitCommas(string token)
    {
        var start = 0;
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != ',' || i == 0 || token[i - 1] == '+') continue;
            yield return token[start..i];
            start = i + 1;
        }
        if (start < token.Length) yield return token[start..];
    }

    /// <summary>SendKeys-style text: "^c", "+{F1}", "%{TAB}", "{ENTER 2}", "+(ab)", "Hello~".</summary>
    public static bool TryParseSendKeys(string text, out List<HotkeyGesture> combos, out string error)
    {
        combos = [];
        var pending = HotkeyModifiers.None;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            switch (ch)
            {
                case '^': pending |= HotkeyModifiers.Ctrl; i++; continue;
                case '+': pending |= HotkeyModifiers.Shift; i++; continue;
                case '%': pending |= HotkeyModifiers.Alt; i++; continue;
                case '~':
                    combos.Add(new HotkeyGesture(pending, "Enter"));
                    pending = HotkeyModifiers.None;
                    i++;
                    continue;
                case '(':
                {
                    var close = text.IndexOf(')', i + 1);
                    if (close < 0)
                    {
                        error = "A '(' has no matching ')'";
                        return false;
                    }
                    if (!TryParseSendKeys(text[(i + 1)..close], out var inner, out error)) return false;
                    combos.AddRange(inner.Select(g => g with { Modifiers = g.Modifiers | pending }));
                    pending = HotkeyModifiers.None;
                    i = close + 1;
                    continue;
                }
                case '{':
                {
                    // "{}}" and "{{}" are the brace characters themselves.
                    var close = text.IndexOf('}', i + 2);
                    if (close < 0)
                    {
                        error = "A '{' has no matching '}'";
                        return false;
                    }
                    var inside = text[(i + 1)..close];
                    var count = 1;
                    var space = inside.LastIndexOf(' ');
                    if (space > 0 && int.TryParse(inside[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        count = n;
                        inside = inside[..space];
                    }
                    if (!TryNamedKey(inside, out var key, out var shift))
                    {
                        error = $"'{{{inside}}}' is not a supported key";
                        return false;
                    }
                    if (count > MaxCombos)
                    {
                        error = $"Too many keys (at most {MaxCombos})";
                        return false;
                    }
                    for (var r = 0; r < count; r++) combos.Add(new HotkeyGesture(pending | (shift ? HotkeyModifiers.Shift : 0), key));
                    pending = HotkeyModifiers.None;
                    i = close + 1;
                    continue;
                }
                default:
                    if (!TryCharacter(ch, out var k, out var needsShift))
                    {
                        error = $"'{ch}' cannot be typed";
                        return false;
                    }
                    combos.Add(new HotkeyGesture(pending | (needsShift ? HotkeyModifiers.Shift : 0), k));
                    pending = HotkeyModifiers.None;
                    i++;
                    continue;
            }
        }
        if (pending != HotkeyModifiers.None)
        {
            error = "A modifier (^, + or %) is not followed by a key";
            return false;
        }
        if (combos.Count == 0)
        {
            error = "No keys given";
            return false;
        }
        if (combos.Count > MaxCombos)
        {
            error = $"Too many keys (at most {MaxCombos})";
            return false;
        }
        error = "";
        return true;
    }

    private static readonly Dictionary<string, string> SendKeysNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BS"] = "Backspace", ["BKSP"] = "Backspace", ["BREAK"] = "Pause", ["PRTSC"] = "PrintScreen", ["SCROLLLOCK"] = "ScrollLock",
        ["ADD"] = "Add", ["SUBTRACT"] = "Subtract", ["MULTIPLY"] = "Multiply", ["DIVIDE"] = "Divide",
    };

    private static bool TryNamedKey(string name, out string key, out bool shift)
    {
        shift = false;
        key = "";
        if (name.Length == 1) return TryCharacter(name[0], out key, out shift);
        if (SendKeysNames.TryGetValue(name, out var mapped)) name = mapped;
        if (HotkeyKeys.Normalize(name) is { } k)
        {
            key = k;
            return true;
        }
        return false;
    }

    private static readonly Dictionary<char, string> Unshifted = new()
    {
        [' '] = "Space", ['-'] = "Minus", ['='] = "Equals", [','] = "Comma", ['.'] = "Period", [';'] = "Semicolon",
        ['/'] = "Slash", ['\\'] = "Backslash", ['\''] = "Quote", ['['] = "BracketLeft", [']'] = "BracketRight",
        ['`'] = "Backquote", ['\t'] = "Tab", ['\n'] = "Enter",
    };

    /// <summary>US layout: the key under each shifted symbol.</summary>
    private static readonly Dictionary<char, string> Shifted = new()
    {
        ['!'] = "1", ['@'] = "2", ['#'] = "3", ['$'] = "4", ['%'] = "5", ['^'] = "6", ['&'] = "7", ['*'] = "8", ['('] = "9",
        [')'] = "0", ['_'] = "Minus", ['+'] = "Equals", [':'] = "Semicolon", ['"'] = "Quote", ['<'] = "Comma", ['>'] = "Period",
        ['?'] = "Slash", ['|'] = "Backslash", ['{'] = "BracketLeft", ['}'] = "BracketRight", ['~'] = "Backquote",
    };

    /// <summary>The key (and whether Shift is needed) that types <paramref name="ch"/> on a US layout.</summary>
    public static bool TryCharacter(char ch, out string key, out bool shift)
    {
        shift = false;
        key = "";
        if (char.IsAsciiLetter(ch))
        {
            key = char.ToUpperInvariant(ch).ToString();
            shift = char.IsAsciiLetterUpper(ch);
            return true;
        }
        if (char.IsAsciiDigit(ch))
        {
            key = ch.ToString();
            return true;
        }
        if (Unshifted.TryGetValue(ch, out var u))
        {
            key = u;
            return true;
        }
        if (Shifted.TryGetValue(ch, out var s))
        {
            key = s;
            shift = true;
            return true;
        }
        return false;
    }
}

/// <summary>One key going down or up. <see cref="Key"/> is a <see cref="HotkeyKeys"/> name or a modifier (Ctrl, Alt, Shift, Meta).</summary>
public readonly record struct KeyEvent(string Key, bool Down)
{
    public static readonly IReadOnlyList<(HotkeyModifiers Flag, string Name)> Modifiers =
        [(HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Meta, "Meta")];

    public bool IsModifier => Key is "Ctrl" or "Alt" or "Shift" or "Meta";

    /// <summary>Modifiers down (Ctrl, Alt, Shift, Meta), the key down and up, then the modifiers up in reverse.</summary>
    public static IReadOnlyList<KeyEvent> For(HotkeyGesture gesture)
    {
        var mods = Modifiers.Where(m => gesture.Modifiers.HasFlag(m.Flag)).Select(m => m.Name).ToArray();
        var events = new List<KeyEvent>(mods.Length * 2 + 2);
        events.AddRange(mods.Select(m => new KeyEvent(m, true)));
        events.Add(new KeyEvent(gesture.Key, true));
        events.Add(new KeyEvent(gesture.Key, false));
        events.AddRange(mods.Reverse().Select(m => new KeyEvent(m, false)));
        return events;
    }
}

/// <summary>Key codes for the output backends: Windows virtual keys (SendInput) and X11 keysyms (XTest, the RemoteDesktop portal).</summary>
public static class KeyCodes
{
    /// <summary>Windows SendInput: virtual-key code and whether KEYEVENTF_EXTENDEDKEY is needed. Null when unknown.</summary>
    public static (ushort Vk, bool Extended)? Windows(string key)
    {
        switch (key)
        {
            case "Ctrl": return (0x11, false);
            case "Alt": return (0x12, false);
            case "Shift": return (0x10, false);
            case "Meta": return (0x5B, true);
        }
        if (HotkeyKeys.WindowsVirtualKey(key) is not { } vk) return null;
        return ((ushort)vk, ExtendedKeys.Contains(key));
    }

    /// <summary>Keys on the extended part of the keyboard (the arrow/navigation block, numpad divide, Print Screen).</summary>
    private static readonly HashSet<string> ExtendedKeys =
        ["Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Left", "Up", "Right", "Down", "Divide", "PrintScreen"];

    /// <summary>X11 keysym value (also what the XDG RemoteDesktop portal's NotifyKeyboardKeysym takes). Null when unknown.</summary>
    public static int? Keysym(string key)
    {
        switch (key)
        {
            case "Ctrl": return 0xffe3;   // Control_L
            case "Alt": return 0xffe9;    // Alt_L
            case "Shift": return 0xffe1;  // Shift_L
            case "Meta": return 0xffeb;   // Super_L
        }
        return HotkeyKeys.XKeysym(key) is { } name ? KeysymValue(name) : null;
    }

    /// <summary>Numeric value of a keysym name from <see cref="HotkeyKeys.XKeysym"/> (the table XStringToKeysym would use).</summary>
    public static int? KeysymValue(string name)
    {
        if (name.Length == 1 && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))) return name[0];
        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 35)
            return 0xffbe + f - 1;
        if (name.Length == 4 && name.StartsWith("KP_", StringComparison.Ordinal) && char.IsAsciiDigit(name[3])) return 0xffb0 + (name[3] - '0');
        return Named.TryGetValue(name, out var v) ? v : null;
    }

    private static readonly Dictionary<string, int> Named = new(StringComparer.Ordinal)
    {
        ["space"] = 0x20, ["Return"] = 0xff0d, ["Tab"] = 0xff09, ["Escape"] = 0xff1b, ["BackSpace"] = 0xff08,
        ["Insert"] = 0xff63, ["Delete"] = 0xffff, ["Home"] = 0xff50, ["End"] = 0xff57, ["Prior"] = 0xff55, ["Next"] = 0xff56,
        ["Left"] = 0xff51, ["Up"] = 0xff52, ["Right"] = 0xff53, ["Down"] = 0xff54, ["Print"] = 0xff61, ["Pause"] = 0xff13,
        ["Scroll_Lock"] = 0xff14, ["KP_Multiply"] = 0xffaa, ["KP_Add"] = 0xffab, ["KP_Subtract"] = 0xffad, ["KP_Decimal"] = 0xffae,
        ["KP_Divide"] = 0xffaf, ["minus"] = 0x2d, ["equal"] = 0x3d, ["comma"] = 0x2c, ["period"] = 0x2e, ["semicolon"] = 0x3b,
        ["slash"] = 0x2f, ["backslash"] = 0x5c, ["apostrophe"] = 0x27, ["bracketleft"] = 0x5b, ["bracketright"] = 0x5d,
        ["grave"] = 0x60, ["XF86AudioMute"] = 0x1008ff12, ["XF86AudioLowerVolume"] = 0x1008ff11,
        ["XF86AudioRaiseVolume"] = 0x1008ff13, ["XF86AudioPlay"] = 0x1008ff14, ["XF86AudioStop"] = 0x1008ff15,
        ["XF86AudioPrev"] = 0x1008ff16, ["XF86AudioNext"] = 0x1008ff17,
    };
}
