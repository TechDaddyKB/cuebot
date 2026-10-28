namespace Cuebot.StreamDeck;

/// <summary>A Stream Deck device reported by a plugin instance (Stream Deck app's device list).</summary>
public sealed record StreamDeckDevice(string Id, string Name, int Type, int Columns, int Rows, bool Connected = true)
{
    /// <summary>Elgato's DeviceType enum as a readable name.</summary>
    public string TypeName => Type switch
    {
        0 => "Stream Deck",
        1 => "Stream Deck Mini",
        2 => "Stream Deck XL",
        3 => "Stream Deck Mobile",
        4 => "Corsair G-Keys",
        5 => "Stream Deck Pedal",
        6 => "Corsair Voyager",
        7 => "Stream Deck +",
        8 => "SCUF controller",
        9 => "Stream Deck Neo",
        10 => "Stream Deck Studio",
        11 => "Virtual Stream Deck",
        12 => "Galleon 100 SD",
        13 => "Stream Deck + XL",
        _ => $"Device type {Type}",
    };

    /// <summary>"Stream Deck XL (8×4)".</summary>
    public string Summary => Columns > 0 && Rows > 0 ? $"{Name} · {TypeName} ({Columns}×{Rows})" : $"{Name} · {TypeName}";
}

/// <summary>Which plugin action a key or dial runs.</summary>
public static class StreamDeckActionKinds
{
    public const string Run = "run";
    public const string Toggle = "toggle";
    public const string Global = "global";
    public const string Trigger = "trigger";

    public static string Describe(string kind) => kind switch
    {
        Run => "Run Cuebot action",
        Toggle => "Toggle action enabled",
        Global => "Set global variable",
        Trigger => "Cuebot trigger",
        _ => kind,
    };
}

/// <summary>A visible key or dial of the Cuebot plugin (one Stream Deck "action instance").</summary>
/// <param name="Context">The Stream Deck app's id for this instance (unique per placement).</param>
/// <param name="ButtonId">The Cuebot button id from the key's settings; sub-actions target keys by it (shared ids update together).</param>
/// <param name="Kind">One of <see cref="StreamDeckActionKinds"/>.</param>
/// <param name="Controller">"Keypad" or "Encoder" (dial).</param>
public sealed record StreamDeckButton(
    string Context,
    string ButtonId,
    string Kind,
    string Controller,
    string DeviceId,
    int? Row,
    int? Column,
    int State,
    bool InMultiAction)
{
    public bool IsDial => string.Equals(Controller, "Encoder", StringComparison.OrdinalIgnoreCase);

    public string Position => Row is null || Column is null ? (InMultiAction ? "in a multi action" : "—") : $"row {Row + 1}, column {Column + 1}";
}

/// <summary>A snapshot of one connected plugin (one Stream Deck app).</summary>
public sealed record StreamDeckInstanceInfo(
    string Id,
    string Remote,
    DateTimeOffset ConnectedAt,
    string PluginVersion,
    string AppVersion,
    string Platform,
    IReadOnlyList<StreamDeckDevice> Devices,
    IReadOnlyList<StreamDeckButton> Buttons);
