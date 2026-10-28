using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cuebot.StreamApps.Obsbot;

/// <summary>One OSC message: an address such as <c>/OBSBOT/WebCam/General/SetZoom</c> and typed arguments.</summary>
/// <remarks>
/// Argument values are CLR values: <see cref="int"/> (i), <see cref="float"/> (f), <see cref="string"/> (s),
/// <see cref="byte"/>[] (b), <see cref="long"/> (h), <see cref="double"/> (d), <see cref="bool"/> (T/F) and null (N).
/// </remarks>
public sealed record OscMessage(string Address, IReadOnlyList<object?> Args)
{
    public OscMessage(string address, params object?[] args) : this(address, (IReadOnlyList<object?>)args)
    {
    }

    /// <summary>Argument <paramref name="index"/> as an int (ints, floats, longs, doubles and bools convert), or null.</summary>
    public int? Int(int index) => index < Args.Count ? Args[index] switch
    {
        int i => i,
        float f => (int)Math.Round(f),
        long l => (int)l,
        double d => (int)Math.Round(d),
        bool b => b ? 1 : 0,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    } : null;

    public string? Str(int index) => index < Args.Count ? Args[index] as string : null;

    public override string ToString() =>
        Args.Count == 0 ? Address : $"{Address} {string.Join(' ', Args.Select(a => a switch
        {
            null => "nil",
            string s => '"' + s + '"',
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            byte[] b => $"<{b.Length} bytes>",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(a, CultureInfo.InvariantCulture),
        }))}";
}

/// <summary>
/// A small OSC 1.0 encoder/decoder (opensoundcontrol.org spec 1.0): messages and bundles, 32-bit big-endian, every
/// field padded to a multiple of four bytes; plus OSC 1.0 stream framing for TCP (each packet prefixed with its int32
/// length). Pure functions, no sockets, so every byte is unit-tested.
/// </summary>
public static class Osc
{
    /// <summary>Longest packet accepted on a stream (OBSBOT replies are a few hundred bytes).</summary>
    public const int MaxPacketLength = 0xFFFF;

    public static byte[] Encode(OscMessage message)
    {
        if (!message.Address.StartsWith('/')) throw new ArgumentException($"OSC addresses start with '/' (got '{message.Address}').");
        using var stream = new MemoryStream();
        WriteString(stream, message.Address);
        var tags = new StringBuilder(",");
        foreach (var arg in message.Args) tags.Append(Tag(arg));
        WriteString(stream, tags.ToString());
        Span<byte> buffer = stackalloc byte[8];
        foreach (var arg in message.Args)
        {
            switch (arg)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(buffer, i);
                    stream.Write(buffer[..4]);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(buffer, f);
                    stream.Write(buffer[..4]);
                    break;
                case long l:
                    BinaryPrimitives.WriteInt64BigEndian(buffer, l);
                    stream.Write(buffer);
                    break;
                case double d:
                    BinaryPrimitives.WriteDoubleBigEndian(buffer, d);
                    stream.Write(buffer);
                    break;
                case string s:
                    WriteString(stream, s);
                    break;
                case byte[] blob:
                    BinaryPrimitives.WriteInt32BigEndian(buffer, blob.Length);
                    stream.Write(buffer[..4]);
                    stream.Write(blob);
                    Pad(stream, blob.Length);
                    break;
            }
        }
        return stream.ToArray();
    }

    private static char Tag(object? arg) => arg switch
    {
        int => 'i',
        float => 'f',
        long => 'h',
        double => 'd',
        string => 's',
        byte[] => 'b',
        true => 'T',
        false => 'F',
        null => 'N',
        _ => throw new ArgumentException($"OSC cannot encode a {arg.GetType().Name}."),
    };

    /// <summary>Every message in a packet (a bundle is flattened, its time tags ignored).</summary>
    /// <exception cref="FormatException">The packet is not valid OSC.</exception>
    public static IReadOnlyList<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        var messages = new List<OscMessage>();
        DecodeInto(packet, messages, depth: 0);
        return messages;
    }

    private static void DecodeInto(ReadOnlySpan<byte> packet, List<OscMessage> into, int depth)
    {
        if (packet.Length == 0 || packet.Length % 4 != 0) throw new FormatException("OSC packets are a non-empty multiple of 4 bytes.");
        if (packet[0] == (byte)'#')
        {
            if (depth > 8) throw new FormatException("OSC bundles nested too deeply.");
            var pos = 0;
            if (ReadString(packet, ref pos) != "#bundle") throw new FormatException("Unknown OSC packet type.");
            pos += 8; // time tag
            while (pos < packet.Length)
            {
                var size = ReadInt(packet, ref pos);
                if (size <= 0 || size > packet.Length - pos) throw new FormatException("Bad OSC bundle element size.");
                DecodeInto(packet.Slice(pos, size), into, depth + 1);
                pos += size;
            }
            return;
        }
        into.Add(DecodeMessage(packet));
    }

    private static OscMessage DecodeMessage(ReadOnlySpan<byte> packet)
    {
        var pos = 0;
        var address = ReadString(packet, ref pos);
        if (!address.StartsWith('/')) throw new FormatException("OSC address must start with '/'.");
        if (pos >= packet.Length) return new OscMessage(address, []);   // very old senders omit the type tags
        var tags = ReadString(packet, ref pos);
        if (!tags.StartsWith(',')) throw new FormatException("OSC type tags must start with ','.");
        var args = new List<object?>(tags.Length - 1);
        foreach (var tag in tags.AsSpan(1))
        {
            args.Add(tag switch
            {
                'i' => ReadInt(packet, ref pos),
                'f' => BinaryPrimitives.ReadSingleBigEndian(Take(packet, ref pos, 4)),
                'h' => BinaryPrimitives.ReadInt64BigEndian(Take(packet, ref pos, 8)),
                'd' => BinaryPrimitives.ReadDoubleBigEndian(Take(packet, ref pos, 8)),
                's' or 'S' => ReadString(packet, ref pos),
                'b' => ReadBlob(packet, ref pos),
                'T' => true,
                'F' => false,
                'N' => null,
                'I' => double.PositiveInfinity,
                'c' => ((char)ReadInt(packet, ref pos)).ToString(),
                'r' or 'm' => ReadInt(packet, ref pos),
                't' => BinaryPrimitives.ReadInt64BigEndian(Take(packet, ref pos, 8)),
                _ => throw new FormatException($"Unsupported OSC type tag '{tag}'."),
            });
        }
        return new OscMessage(address, args);
    }

    /// <summary>A packet framed for a TCP stream (OSC 1.0: int32 big-endian length, then the packet).</summary>
    public static byte[] Frame(byte[] packet)
    {
        var frame = new byte[4 + packet.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, packet.Length);
        packet.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// Length of the next frame in <paramref name="buffer"/>, or null when it is not length-prefixed OSC. OBSBOT Center
    /// writes the length big-endian; Tail hardware reportedly little-endian. A plausible length (≤ 64 KiB) in one byte
    /// order is implausible in the other, so the order is picked per frame.
    /// </summary>
    public static int? FrameLength(ReadOnlySpan<byte> buffer)
    {
        var big = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (big <= MaxPacketLength) return (int)big;
        var little = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return little <= MaxPacketLength ? (int)little : null;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> packet, ref int pos, int count)
    {
        if (pos + count > packet.Length) throw new FormatException("OSC packet ends early.");
        var slice = packet.Slice(pos, count);
        pos += count;
        return slice;
    }

    private static int ReadInt(ReadOnlySpan<byte> packet, ref int pos) => BinaryPrimitives.ReadInt32BigEndian(Take(packet, ref pos, 4));

    private static string ReadString(ReadOnlySpan<byte> packet, ref int pos)
    {
        var rest = packet[pos..];
        var end = rest.IndexOf((byte)0);
        if (end < 0) throw new FormatException("OSC string is not terminated.");
        var text = Encoding.UTF8.GetString(rest[..end]);
        pos += (end + 4) & ~3;
        if (pos > packet.Length) throw new FormatException("OSC string padding missing.");
        return text;
    }

    private static byte[] ReadBlob(ReadOnlySpan<byte> packet, ref int pos)
    {
        var length = ReadInt(packet, ref pos);
        if (length < 0) throw new FormatException("Negative OSC blob size.");
        var blob = Take(packet, ref pos, length).ToArray();
        pos += (4 - length % 4) % 4;
        if (pos > packet.Length) throw new FormatException("OSC blob padding missing.");
        return blob;
    }

    private static void WriteString(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Contains((byte)0)) throw new ArgumentException("OSC strings cannot contain NUL.");
        stream.Write(bytes);
        stream.WriteByte(0);
        Pad(stream, bytes.Length + 1);
    }

    private static void Pad(Stream stream, int written)
    {
        for (var i = written % 4; i is > 0 and < 4; i++) stream.WriteByte(0);
    }
}

/// <summary>Reassembles length-prefixed OSC packets from TCP reads.</summary>
public sealed class OscStreamDecoder
{
    private byte[] _pending = [];

    /// <summary>Adds received bytes and returns the messages of every complete frame (malformed packets are skipped).</summary>
    /// <exception cref="FormatException">The stream is not length-prefixed OSC.</exception>
    public IReadOnlyList<OscMessage> Push(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[_pending.Length + data.Length];
        _pending.CopyTo(buffer, 0);
        data.CopyTo(buffer.AsSpan(_pending.Length));
        var messages = new List<OscMessage>();
        var pos = 0;
        while (buffer.Length - pos >= 4)
        {
            var length = Osc.FrameLength(buffer.AsSpan(pos)) ?? throw new FormatException("Received data that is not length-prefixed OSC.");
            if (buffer.Length - pos - 4 < length) break;
            try
            {
                if (length > 0) messages.AddRange(Osc.Decode(buffer.AsSpan(pos + 4, length)));
            }
            catch (FormatException)
            {
                // Skip one bad packet rather than dropping the connection.
            }
            pos += 4 + length;
        }
        _pending = buffer[pos..];
        return messages;
    }
}
