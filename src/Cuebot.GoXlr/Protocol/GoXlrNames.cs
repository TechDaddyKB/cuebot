namespace Cuebot.GoXlr.Protocol;

/// <summary>
/// The GoXLR Utility's enum names as they appear on the wire (serde's variant names from its goxlr-types crate), with
/// friendly labels and lenient parsing ("line in", "LineIn", "line-in" all mean LineIn).
/// </summary>
public static class GoXlrNames
{
    public static readonly string[] Channels = ["Mic", "LineIn", "Console", "System", "Game", "Chat", "Sample", "Music", "Headphones", "MicMonitor", "LineOut"];
    public static readonly string[] Faders = ["A", "B", "C", "D"];
    public static readonly string[] MuteStates = ["Unmuted", "MutedToX", "MutedToAll"];
    public static readonly string[] MuteFunctions = ["All", "ToStream", "ToVoiceChat", "ToPhones", "ToLineOut", "ToStream2", "ToStreams"];
    public static readonly string[] Effects = ["FX", "Megaphone", "Robot", "HardTune"];
    public static readonly string[] Presets = ["Preset1", "Preset2", "Preset3", "Preset4", "Preset5", "Preset6"];
    public static readonly string[] SampleBanks = ["A", "B", "C"];
    public static readonly string[] SampleButtons = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public static readonly string[] Inputs = ["Microphone", "Chat", "Music", "Game", "Console", "LineIn", "System", "Samples"];
    public static readonly string[] Outputs = ["Headphones", "BroadcastMix", "ChatMic", "Sampler", "LineOut", "StreamMix2"];

    public static readonly string[] Buttons =
    [
        "Fader1Mute", "Fader2Mute", "Fader3Mute", "Fader4Mute", "Bleep", "Cough",
        "EffectSelect1", "EffectSelect2", "EffectSelect3", "EffectSelect4", "EffectSelect5", "EffectSelect6",
        "EffectFx", "EffectMegaphone", "EffectRobot", "EffectHardTune",
        "SamplerSelectA", "SamplerSelectB", "SamplerSelectC",
        "SamplerTopLeft", "SamplerTopRight", "SamplerBottomLeft", "SamplerBottomRight", "SamplerClear",
    ];

    public static readonly string[] AnimationModes = ["RetroRainbow", "RainbowDark", "RainbowBright", "Simple", "Ripple", "None"];

    /// <summary>"LineIn" → "Line In", "MicMonitor" → "Mic Monitor", "HardTune" → "Hard Tune".</summary>
    public static string Label(string wire)
    {
        var chars = new List<char>(wire.Length + 4);
        for (var i = 0; i < wire.Length; i++)
        {
            if (i > 0 && char.IsUpper(wire[i]) && (char.IsLower(wire[i - 1]) || char.IsDigit(wire[i - 1]))) chars.Add(' ');
            if (i > 0 && char.IsDigit(wire[i]) && !char.IsDigit(wire[i - 1])) chars.Add(' ');
            chars.Add(wire[i]);
        }
        return new string([.. chars]);
    }

    private static string Key(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>The wire name matching <paramref name="text"/> (case, spaces and dashes ignored), or null.</summary>
    public static string? Find(IEnumerable<string> names, string text)
    {
        var key = Key(text);
        return key.Length == 0 ? null : names.FirstOrDefault(n => Key(n) == key);
    }

    public static string Parse(IEnumerable<string> names, string text, string what)
    {
        var list = names as string[] ?? names.ToArray();
        return Find(list, text) ?? throw new ArgumentException($"Unknown {what} '{text.Trim()}' (expected one of: {string.Join(", ", list.Select(Label))}).");
    }

    public static string Channel(string text) => Find(Channels, text) ?? Find(Channels, Alias(text)) ?? Parse(Channels, text, "channel");

    private static string Alias(string text) => Key(text) switch
    {
        "microphone" => "Mic",
        "samples" or "sampler" => "Sample",
        "voicechat" => "Chat",
        "phones" => "Headphones",
        _ => text,
    };

    /// <summary>"A", "Fader A", "1" → "A".</summary>
    public static string? FindFader(string text)
    {
        var key = Key(text);
        if (key.StartsWith("fader", StringComparison.Ordinal)) key = key[5..];
        return key switch
        {
            "a" or "1" => "A",
            "b" or "2" => "B",
            "c" or "3" => "C",
            "d" or "4" => "D",
            _ => null,
        };
    }

    public static string Fader(string text) => FindFader(text) ?? throw new ArgumentException($"Unknown fader '{text.Trim()}' (expected A, B, C or D).");

    /// <summary>"Preset1", "Preset 1", "1" → "Preset1"; null when it is not a slot (it may be a preset name).</summary>
    public static string? FindPreset(string text)
    {
        var key = Key(text);
        if (key.StartsWith("preset", StringComparison.Ordinal)) key = key[6..];
        return key.Length == 1 && key[0] is >= '1' and <= '6' ? "Preset" + key : null;
    }

    /// <summary>"FX", "Effects", "Megaphone", "Robot", "Hard Tune".</summary>
    public static string Effect(string text) => Key(text) switch
    {
        "fx" or "effects" or "effect" => "FX",
        "megaphone" or "mega" => "Megaphone",
        "robot" => "Robot",
        "hardtune" or "tune" => "HardTune",
        _ => throw new ArgumentException($"Unknown effect '{text.Trim()}' (expected FX, Megaphone, Robot or Hard Tune)."),
    };

    /// <summary>A colour as the utility expects it: six hex digits, no '#'.</summary>
    public static string Colour(string text, string what = "Colour")
    {
        var t = text.Trim().TrimStart('#');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 3) t = string.Concat(t.Select(c => $"{c}{c}"));
        if (t.Length != 6 || !t.All(Uri.IsHexDigit)) throw new ArgumentException($"{what} must be a hex colour like #FF8800 (got '{text.Trim()}').");
        return t.ToUpperInvariant();
    }

    public static bool IsMuted(string muteState) => muteState.Length > 0 && muteState != "Unmuted";

    /// <summary>0-255 raw volume → 0-100 %.</summary>
    public static int Percent(int raw) => (int)Math.Round(Math.Clamp(raw, 0, 255) * 100 / 255.0);

    /// <summary>0-100 % → 0-255 raw volume.</summary>
    public static int Raw(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);
}
