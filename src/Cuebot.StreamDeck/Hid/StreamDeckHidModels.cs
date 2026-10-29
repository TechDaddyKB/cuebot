namespace Cuebot.StreamDeck.Hid;

/// <summary>Report format family.</summary>
public enum StreamDeckHidProtocolKind
{
    /// <summary>Original V2, MK.2, XL, Neo, +, Pedal: input 0x01 with a 4-byte header, 1024-byte 0x02 0x07 image reports, 32-byte feature reports.</summary>
    Gen2,

    /// <summary>Original (2017), Mini, Mini MK.2: BMP images and 17-byte feature reports. Recognized, not driven yet.</summary>
    Legacy,

    /// <summary>Recognized product, report format not described here yet (Studio, + XL).</summary>
    Unknown,
}

/// <summary>How input reports are laid out.</summary>
public enum StreamDeckHidInputLayout
{
    /// <summary>Report 0x01, 3 header bytes, then one byte per key (1 = pressed).</summary>
    KeyStates,

    /// <summary>Stream Deck +: report 0x01, byte 1 says keys (0x00), touch strip (0x02) or dials (0x03).</summary>
    Plus,
}

/// <summary>Key image encoding.</summary>
public enum StreamDeckImageFormat { None, Jpeg, Bmp }

/// <summary>
/// One Stream Deck model as data: USB ids, layout, key image size/format/orientation and report details. New models are
/// new entries in <see cref="StreamDeckHidModels.All"/>. Sources: the public, MIT-licensed python-elgato-streamdeck
/// library (device classes and ProductIDs.py), checked on a real XL; see PROTOCOL.md.
/// </summary>
/// <param name="ElgatoType">Elgato's DeviceType number (the plugin's <c>device.type</c>), so trigger arguments match plugin keys.</param>
/// <param name="KeyCount">Keys reported in input (Neo: 8 display keys + 2 touch keys).</param>
/// <param name="ImageKeyCount">Keys that take an image (the first ones).</param>
/// <param name="FlipX">Mirror images horizontally before sending (the panel is mounted rotated).</param>
/// <param name="Rotation">Clockwise rotation in degrees applied before flipping.</param>
/// <param name="SerialOffset">Where the text starts in feature report 0x06 (including the report id byte).</param>
/// <param name="FirmwareOffset">Where the text starts in feature report 0x05.</param>
public sealed record StreamDeckHidModel(
    string Name,
    ushort[] ProductIds,
    int ElgatoType,
    StreamDeckHidProtocolKind Protocol,
    int Columns,
    int Rows,
    int KeyCount,
    int ImageKeyCount,
    int KeyPixels,
    StreamDeckImageFormat ImageFormat,
    bool FlipX,
    bool FlipY,
    int Rotation,
    StreamDeckHidInputLayout Input = StreamDeckHidInputLayout.KeyStates,
    int DialCount = 0,
    int TouchStripWidth = 0,
    int ImageReportLength = 1024,
    int ImageHeaderLength = 8,
    int FeatureReportLength = 32,
    int SerialOffset = 2,
    int FirmwareOffset = 6,
    int InputReportLength = 0)
{
    public bool Supported => Protocol == StreamDeckHidProtocolKind.Gen2;

    public bool HasDisplay => ImageFormat != StreamDeckImageFormat.None && ImageKeyCount > 0;

    /// <summary>Bytes to read for one input report (the + sends 14-byte reports for every kind).</summary>
    public int InputLength => InputReportLength > 0 ? InputReportLength : Math.Max(4 + KeyCount, 14);

    public int ImagePayloadLength => ImageReportLength - ImageHeaderLength;

    /// <summary>Row and column of a key (left to right, top to bottom, from 0).</summary>
    public (int Row, int Column) Position(int key) => Columns > 0 ? (key / Columns, key % Columns) : (0, key);
}

/// <summary>Every known Stream Deck (vendor 0x0FD9, Elgato).</summary>
public static class StreamDeckHidModels
{
    public const ushort ElgatoVendorId = 0x0FD9;

    public static readonly IReadOnlyList<StreamDeckHidModel> All =
    [
        new("Stream Deck XL", [0x006C, 0x008F, 0x00BA], 2, StreamDeckHidProtocolKind.Gen2, 8, 4, 32, 32, 96,
            StreamDeckImageFormat.Jpeg, FlipX: true, FlipY: true, Rotation: 0),
        new("Stream Deck MK.2", [0x0080, 0x00A5, 0x00B9], 0, StreamDeckHidProtocolKind.Gen2, 5, 3, 15, 15, 72,
            StreamDeckImageFormat.Jpeg, FlipX: true, FlipY: true, Rotation: 0),
        new("Stream Deck", [0x006D], 0, StreamDeckHidProtocolKind.Gen2, 5, 3, 15, 15, 72,
            StreamDeckImageFormat.Jpeg, FlipX: true, FlipY: true, Rotation: 0),
        new("Stream Deck Neo", [0x009A], 9, StreamDeckHidProtocolKind.Gen2, 4, 2, 10, 8, 96,
            StreamDeckImageFormat.Jpeg, FlipX: true, FlipY: true, Rotation: 0),
        new("Stream Deck +", [0x0084], 7, StreamDeckHidProtocolKind.Gen2, 4, 2, 8, 8, 120,
            StreamDeckImageFormat.Jpeg, FlipX: false, FlipY: false, Rotation: 0,
            Input: StreamDeckHidInputLayout.Plus, DialCount: 4, TouchStripWidth: 800, SerialOffset: 5, FirmwareOffset: 5, InputReportLength: 14),
        new("Stream Deck Pedal", [0x0086], 5, StreamDeckHidProtocolKind.Gen2, 3, 1, 3, 0, 0,
            StreamDeckImageFormat.None, FlipX: false, FlipY: false, Rotation: 0),
        new("Stream Deck (2017)", [0x0060], 0, StreamDeckHidProtocolKind.Legacy, 5, 3, 15, 15, 72,
            StreamDeckImageFormat.Bmp, FlipX: true, FlipY: true, Rotation: 0, ImageReportLength: 8191, ImageHeaderLength: 16, FeatureReportLength: 17),
        new("Stream Deck Mini", [0x0063, 0x00B3, 0x0090, 0x00B8], 1, StreamDeckHidProtocolKind.Legacy, 3, 2, 6, 6, 80,
            StreamDeckImageFormat.Bmp, FlipX: false, FlipY: true, Rotation: 90, ImageHeaderLength: 16, FeatureReportLength: 17),
        // Only identity and layout: the report formats of these two are not described here yet.
        new("Stream Deck Studio", [0x00AA], 10, StreamDeckHidProtocolKind.Unknown, 16, 2, 32, 32, 0,
            StreamDeckImageFormat.Jpeg, FlipX: false, FlipY: false, Rotation: 0, DialCount: 2),
        new("Stream Deck + XL", [0x00C6], 13, StreamDeckHidProtocolKind.Unknown, 9, 4, 36, 36, 112,
            StreamDeckImageFormat.Jpeg, FlipX: false, FlipY: false, Rotation: 90, DialCount: 6),
    ];

    public static StreamDeckHidModel? Find(ushort vendorId, ushort productId) =>
        vendorId == ElgatoVendorId ? All.FirstOrDefault(m => m.ProductIds.Contains(productId)) : null;
}
