namespace Cuebot.Core.Desktop.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,

    /// <summary>Windows key / Super / Command.</summary>
    Meta = 8,
}

/// <summary>
/// A key combination such as "Ctrl+Shift+F1". Text form: modifiers (Ctrl, Alt, Shift, Meta; aliases Control, Win,
/// Super, Cmd) then one key name from <see cref="HotkeyKeys.Names"/>, joined with '+'. Case-insensitive when parsed;
/// <see cref="ToString"/> gives the canonical form.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = default;
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        // "Ctrl++" means Ctrl and the plus key.
        if (parts.Length >= 2 && parts[^1].Length == 0 && parts[^2].Length == 0) parts = [.. parts[..^2], "Plus"];
        if (parts.Length == 0 || parts.All(p => p.Length == 0))
        {
            error = "No key given";
            return false;
        }
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" or "option" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "meta" or "win" or "windows" or "super" or "cmd" or "command" or "logo" => HotkeyModifiers.Meta,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None)
            {
                error = $"'{part}' is not a modifier (use Ctrl, Alt, Shift or Meta)";
                return false;
            }
            modifiers |= modifier;
        }
        var key = HotkeyKeys.Normalize(parts[^1]);
        if (key is null)
        {
            error = $"'{parts[^1]}' is not a supported key";
            return false;
        }
        gesture = new HotkeyGesture(modifiers, key);
        error = "";
        return true;
    }

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var g, out var error) ? g : throw new FormatException(error);

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>The XDG "shortcuts" trigger format used by the GlobalShortcuts portal, e.g. "CTRL+SHIFT+F1".</summary>
    public string ToPortalTrigger()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("CTRL");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("ALT");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("SHIFT");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("LOGO");
        parts.Add(HotkeyKeys.XKeysym(Key) ?? Key);
        return string.Join('+', parts);
    }
}

/// <summary>Supported key names with their Windows virtual-key codes and X11 keysym names.</summary>
public static class HotkeyKeys
{
    private static readonly Dictionary<string, (int Vk, string Keysym)> Table = Build();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = "Escape", ["Return"] = "Enter", ["Del"] = "Delete", ["Ins"] = "Insert", ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown", ["PageDn"] = "PageDown", ["Prior"] = "PageUp", ["Next"] = "PageDown", ["Back"] = "Backspace",
        ["BackSpace"] = "Backspace", ["Bksp"] = "Backspace", ["PrtSc"] = "PrintScreen", ["Print"] = "PrintScreen",
        ["Snapshot"] = "PrintScreen", ["Scroll"] = "ScrollLock", ["Spacebar"] = "Space", ["ArrowUp"] = "Up",
        ["ArrowDown"] = "Down", ["ArrowLeft"] = "Left", ["ArrowRight"] = "Right", ["-"] = "Minus", ["="] = "Equals",
        ["Plus"] = "Equals", [","] = "Comma", ["."] = "Period", [";"] = "Semicolon", ["/"] = "Slash", ["\\"] = "Backslash",
        ["'"] = "Quote", ["["] = "BracketLeft", ["]"] = "BracketRight", ["`"] = "Backquote", ["Grave"] = "Backquote",
        ["Tilde"] = "Backquote", ["Apostrophe"] = "Quote", ["Equal"] = "Equals", ["NumMultiply"] = "Multiply",
        ["NumAdd"] = "Add", ["NumSubtract"] = "Subtract", ["NumDecimal"] = "Decimal", ["NumDivide"] = "Divide",
        ["PlayPause"] = "MediaPlayPause", ["MediaNextTrack"] = "MediaNext", ["MediaPreviousTrack"] = "MediaPrevious",
        ["Mute"] = "VolumeMute",
    };

    /// <summary>Canonical key names, in display order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Table.Keys];

    /// <summary>Canonical name for a key name or alias ("esc" → "Escape", "a" → "A", "kp_1" → "NumPad1"); null when unknown.</summary>
    public static string? Normalize(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        if (name.Length == 2 && (name[0] is 'D' or 'd') && char.IsAsciiDigit(name[1])) return name[1..];
        if (name.StartsWith("KP_", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsAsciiDigit(name[3])) return "NumPad" + name[3];
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && name.Length == 4 && char.IsAsciiDigit(name[3])) return "NumPad" + name[3];
        foreach (var key in Table.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        }
        return null;
    }

    public static int? WindowsVirtualKey(string key) => Table.TryGetValue(key, out var e) ? e.Vk : null;

    public static string? XKeysym(string key) => Table.TryGetValue(key, out var e) ? e.Keysym : null;

    private static Dictionary<string, (int, string)> Build()
    {
        var t = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++) t[c.ToString()] = (c, char.ToLowerInvariant(c).ToString());
        for (var d = '0'; d <= '9'; d++) t[d.ToString()] = (d, d.ToString());
        for (var f = 1; f <= 24; f++) t["F" + f] = (0x6F + f, "F" + f);
        for (var n = 0; n <= 9; n++) t["NumPad" + n] = (0x60 + n, "KP_" + n);
        (string Name, int Vk, string Sym)[] rest =
        [
            ("Space", 0x20, "space"), ("Enter", 0x0D, "Return"), ("Tab", 0x09, "Tab"), ("Escape", 0x1B, "Escape"),
            ("Backspace", 0x08, "BackSpace"), ("Insert", 0x2D, "Insert"), ("Delete", 0x2E, "Delete"), ("Home", 0x24, "Home"),
            ("End", 0x23, "End"), ("PageUp", 0x21, "Prior"), ("PageDown", 0x22, "Next"), ("Left", 0x25, "Left"),
            ("Up", 0x26, "Up"), ("Right", 0x27, "Right"), ("Down", 0x28, "Down"), ("PrintScreen", 0x2C, "Print"),
            ("Pause", 0x13, "Pause"), ("ScrollLock", 0x91, "Scroll_Lock"), ("Multiply", 0x6A, "KP_Multiply"),
            ("Add", 0x6B, "KP_Add"), ("Subtract", 0x6D, "KP_Subtract"), ("Decimal", 0x6E, "KP_Decimal"),
            ("Divide", 0x6F, "KP_Divide"), ("Minus", 0xBD, "minus"), ("Equals", 0xBB, "equal"), ("Comma", 0xBC, "comma"),
            ("Period", 0xBE, "period"), ("Semicolon", 0xBA, "semicolon"), ("Slash", 0xBF, "slash"),
            ("Backslash", 0xDC, "backslash"), ("Quote", 0xDE, "apostrophe"), ("BracketLeft", 0xDB, "bracketleft"),
            ("BracketRight", 0xDD, "bracketright"), ("Backquote", 0xC0, "grave"), ("VolumeMute", 0xAD, "XF86AudioMute"),
            ("VolumeDown", 0xAE, "XF86AudioLowerVolume"), ("VolumeUp", 0xAF, "XF86AudioRaiseVolume"),
            ("MediaNext", 0xB0, "XF86AudioNext"), ("MediaPrevious", 0xB1, "XF86AudioPrev"), ("MediaStop", 0xB2, "XF86AudioStop"),
            ("MediaPlayPause", 0xB3, "XF86AudioPlay"),
        ];
        foreach (var (name, vk, sym) in rest) t[name] = (vk, sym);
        return t;
    }
}
