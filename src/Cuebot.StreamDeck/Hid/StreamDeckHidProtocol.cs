using System.Text;

namespace Cuebot.StreamDeck.Hid;

/// <summary>Something that happened on a deck, decoded from one input report.</summary>
public abstract record StreamDeckHidInput;

/// <summary>Every key's state (true = pressed), in key order.</summary>
public sealed record KeyStatesInput(bool[] Pressed) : StreamDeckHidInput;

/// <summary>Dials turned: ticks per dial (negative = counter-clockwise, 0 = not turned).</summary>
public sealed record DialTurnInput(int[] Ticks) : StreamDeckHidInput;

/// <summary>Every dial's pressed state.</summary>
public sealed record DialPressInput(bool[] Pressed) : StreamDeckHidInput;

/// <summary>A touch strip event (Stream Deck +): short or long tap at (X, Y), or a drag to (XOut, YOut).</summary>
public sealed record TouchInput(TouchKind Kind, int X, int Y, int XOut = 0, int YOut = 0) : StreamDeckHidInput;

public enum TouchKind { Short, Long, Drag }

/// <summary>
/// The Stream Deck USB HID protocol (gen-2 devices) as pure functions, so it is unit-tested without hardware. Reports
/// include their report id at byte 0, as written to / read from hidraw. Layout from public sources: the MIT-licensed
/// python-elgato-streamdeck library (see PROTOCOL.md, "Direct USB").
/// </summary>
public static class StreamDeckHidProtocol
{
    public const byte InputReportId = 0x01;
    public const byte ImageReportId = 0x02;
    public const byte ImageCommandKey = 0x07;
    public const byte FeatureCommandId = 0x03;
    public const byte FeatureReset = 0x02;
    public const byte FeatureBrightness = 0x08;
    public const byte FeatureFirmwareId = 0x05;
    public const byte FeatureSerialId = 0x06;

    /// <summary>
    /// The 1024-byte output reports carrying one key image: 0x02 0x07, key, last-page flag, payload length (LE16), page
    /// number (LE16), then the payload, zero-padded. An empty image still sends one (empty, last) page.
    /// </summary>
    public static IReadOnlyList<byte[]> ImageReports(StreamDeckHidModel model, int key, ReadOnlySpan<byte> image)
    {
        RequireGen2(model);
        if (key < 0 || key >= model.ImageKeyCount) throw new ArgumentOutOfRangeException(nameof(key), $"{model.Name} has keys 0-{model.ImageKeyCount - 1} with images.");
        var payloadMax = model.ImagePayloadLength;
        var reports = new List<byte[]>();
        var page = 0;
        var sent = 0;
        do
        {
            var length = Math.Min(image.Length - sent, payloadMax);
            var last = sent + length >= image.Length;
            var report = new byte[model.ImageReportLength];
            report[0] = ImageReportId;
            report[1] = ImageCommandKey;
            report[2] = (byte)key;
            report[3] = last ? (byte)1 : (byte)0;
            report[4] = (byte)(length & 0xFF);
            report[5] = (byte)(length >> 8);
            report[6] = (byte)(page & 0xFF);
            report[7] = (byte)(page >> 8);
            image.Slice(sent, length).CopyTo(report.AsSpan(model.ImageHeaderLength));
            reports.Add(report);
            sent += length;
            page++;
        }
        while (sent < image.Length);
        return reports;
    }

    /// <summary>An image report with only the id, sent on open to drop a half-sent image stream.</summary>
    public static byte[] ResetKeyStreamReport(StreamDeckHidModel model)
    {
        var report = new byte[model.ImageReportLength];
        report[0] = ImageReportId;
        return report;
    }

    /// <summary>Feature report 0x03 0x08 &lt;percent&gt;, clamped to 0-100.</summary>
    public static byte[] BrightnessReport(StreamDeckHidModel model, int percent)
    {
        RequireGen2(model);
        var report = new byte[model.FeatureReportLength];
        report[0] = FeatureCommandId;
        report[1] = FeatureBrightness;
        report[2] = (byte)Math.Clamp(percent, 0, 100);
        return report;
    }

    /// <summary>Feature report 0x03 0x02: clears the keys and shows the device's own boot screen.</summary>
    public static byte[] ResetReport(StreamDeckHidModel model)
    {
        RequireGen2(model);
        var report = new byte[model.FeatureReportLength];
        report[0] = FeatureCommandId;
        report[1] = FeatureReset;
        return report;
    }

    /// <summary>The serial number from feature report 0x06.</summary>
    public static string ParseSerial(StreamDeckHidModel model, ReadOnlySpan<byte> report) =>
        report.Length > model.SerialOffset ? ExtractString(report[model.SerialOffset..]) : "";

    /// <summary>The firmware version from feature report 0x05.</summary>
    public static string ParseFirmware(StreamDeckHidModel model, ReadOnlySpan<byte> report) =>
        report.Length > model.FirmwareOffset ? ExtractString(report[model.FirmwareOffset..]) : "";

    /// <summary>Text up to the first NUL, without surrounding whitespace or non-printable bytes.</summary>
    public static string ExtractString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        return new string([.. text.Where(c => c >= ' ' && c < 0x7F)]).Trim();
    }

    /// <summary>Decodes one input report; null when it is not an input report this model sends (or is too short).</summary>
    public static StreamDeckHidInput? ParseInput(StreamDeckHidModel model, ReadOnlySpan<byte> report)
    {
        if (report.Length < 4 || report[0] != InputReportId) return null;
        if (model.Input == StreamDeckHidInputLayout.KeyStates)
        {
            if (report.Length < 4 + model.KeyCount) return null;
            return new KeyStatesInput([.. report.Slice(4, model.KeyCount).ToArray().Select(b => b != 0)]);
        }

        // Stream Deck +: [1] kind, [2..3] length, then kind-specific data.
        switch (report[1])
        {
            case 0x00 when report.Length >= 4 + model.KeyCount:
                return new KeyStatesInput([.. report.Slice(4, model.KeyCount).ToArray().Select(b => b != 0)]);
            case 0x02 when report.Length >= 14:
            {
                TouchKind? kind = report[4] switch { 1 => TouchKind.Short, 2 => TouchKind.Long, 3 => TouchKind.Drag, _ => null };
                if (kind is null) return null;
                var x = U16(report, 6);
                var y = U16(report, 8);
                return kind == TouchKind.Drag
                    ? new TouchInput(kind.Value, x, y, U16(report, 10), U16(report, 12))
                    : new TouchInput(kind.Value, x, y);
            }
            case 0x03 when report.Length >= 5 + model.DialCount:
            {
                var values = report.Slice(5, model.DialCount).ToArray();
                return report[4] switch
                {
                    0x00 => new DialPressInput([.. values.Select(b => b != 0)]),
                    0x01 => new DialTurnInput([.. values.Select(b => b < 0x80 ? b : b - 0x100)]),
                    _ => null,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>Keys whose state differs between two snapshots: (index, pressed now).</summary>
    public static IEnumerable<(int Index, bool Pressed)> Changes(bool[] before, bool[] after)
    {
        for (var i = 0; i < after.Length; i++)
        {
            var was = i < before.Length && before[i];
            if (was != after[i]) yield return (i, after[i]);
        }
    }

    private static int U16(ReadOnlySpan<byte> bytes, int index) => bytes[index] | (bytes[index + 1] << 8);

    private static void RequireGen2(StreamDeckHidModel model)
    {
        if (!model.Supported) throw new NotSupportedException($"{model.Name} is not supported in direct USB mode yet.");
    }
}
