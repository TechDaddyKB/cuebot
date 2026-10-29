using System.Text;

namespace Cuebot.StreamDeck.Hid;

/// <summary>Direct USB mode settings (streamdeck-usb.json).</summary>
public sealed class StreamDeckHidSettings
{
    /// <summary>Look for Stream Decks on USB and drive them (Linux). Turn off when another program (the Stream Deck app,
    /// StreamController, OpenDeck) should own the deck.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default key brightness, 0-100.</summary>
    public int Brightness { get; set; } = 60;

    /// <summary>How often USB is scanned for plugged / unplugged decks.</summary>
    public int ScanIntervalMs { get; set; } = 2000;

    /// <summary>Blank the keys when Cuebot exits.</summary>
    public bool ResetOnExit { get; set; } = true;

    /// <summary>Per-deck key mapping, by serial number.</summary>
    public List<StreamDeckHidDeviceConfig> Devices { get; set; } = [];
}

/// <summary>One deck's key mapping.</summary>
public sealed class StreamDeckHidDeviceConfig
{
    /// <summary>The deck's USB serial number (shown on the Stream Deck page).</summary>
    public string Serial { get; set; } = "";

    /// <summary>A name for triggers (<c>%deviceName%</c>); empty uses the model name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Overrides the default brightness for this deck.</summary>
    public int? Brightness { get; set; }

    public List<StreamDeckHidKeyConfig> Keys { get; set; } = [];

    /// <summary>Stream Deck + dials (index 0-3, left to right).</summary>
    public List<StreamDeckHidKeyConfig> Dials { get; set; } = [];
}

/// <summary>One key (or dial): its button id and what it shows until a sub-action changes it.</summary>
public sealed class StreamDeckHidKeyConfig
{
    /// <summary>Key number from 0, left to right then top to bottom.</summary>
    public int Index { get; set; }

    /// <summary>The button id used by the Stream Deck Action trigger and the Stream Deck sub-actions; empty = the default id.</summary>
    public string ButtonId { get; set; } = "";

    /// <summary>The key's own title (Set Title with an empty title restores it).</summary>
    public string Title { get; set; } = "";

    /// <summary>The key's own image: a file, an http(s) URL or a data: URI.</summary>
    public string Image { get; set; } = "";
}

/// <summary>Key → button id mapping.</summary>
public static class StreamDeckHidMapping
{
    /// <summary>The id a key has when none is set: <c>hid-&lt;serial&gt;-&lt;index&gt;</c> (dials: <c>…-dial&lt;index&gt;</c>).</summary>
    public static string DefaultButtonId(string serial, int index, bool dial = false) =>
        $"hid-{SafeSerial(serial)}-{(dial ? "dial" : "")}{index}";

    /// <summary>Letters, digits, '-' and '_' only, so the id is easy to type; "unknown" when there is no serial.</summary>
    public static string SafeSerial(string serial)
    {
        var sb = new StringBuilder(serial.Length);
        foreach (var c in serial.Trim()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "unknown";
    }

    public static StreamDeckHidDeviceConfig? Device(StreamDeckHidSettings settings, string serial) =>
        settings.Devices.FirstOrDefault(d => string.Equals(d.Serial.Trim(), serial, StringComparison.OrdinalIgnoreCase));

    public static StreamDeckHidKeyConfig? Key(StreamDeckHidDeviceConfig? device, int index, bool dial = false) =>
        (dial ? device?.Dials : device?.Keys)?.LastOrDefault(k => k.Index == index);

    /// <summary>The button id of a key or dial: the configured one, else the default.</summary>
    public static string ButtonId(StreamDeckHidSettings settings, string serial, int index, bool dial = false) =>
        Key(Device(settings, serial), index, dial)?.ButtonId.Trim() is { Length: > 0 } id ? id : DefaultButtonId(serial, index, dial);
}
