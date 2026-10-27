using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Cuebot.Core.Desktop.DBus;

/// <summary>A D-Bus variant: a value with its signature (e.g. <c>new DBusVariant("s", "text")</c>).</summary>
internal sealed record DBusVariant(string Signature, object? Value);

internal sealed class DBusException(string name, string message) : Exception($"{name}: {message}")
{
    public string Name { get; } = name;
}

/// <summary>
/// Signature-driven marshalling of the D-Bus wire format (little-endian only). Values: y byte, b bool, n short,
/// q ushort, i int, u uint, x long, t ulong, d double, s/o/g string, v <see cref="DBusVariant"/>, a{..}
/// <see cref="IDictionary"/> (read as Dictionary&lt;object, object?&gt;), a.. <see cref="IEnumerable"/> (read as
/// List&lt;object?&gt;), (..) object?[]. Offsets are relative to the start of the buffer, which must be 8-aligned in the
/// message (true for the header and for the body).
/// </summary>
internal static class DBusWire
{
    public static int Alignment(char code) => code switch
    {
        'y' or 'g' or 'v' => 1,
        'n' or 'q' => 2,
        'b' or 'i' or 'u' or 'h' or 's' or 'o' or 'a' => 4,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => throw new FormatException($"Unsupported D-Bus type '{code}'"),
    };

    /// <summary>Index just past the single complete type starting at <paramref name="start"/>.</summary>
    public static int EndOfType(string signature, int start)
    {
        if (start >= signature.Length) throw new FormatException($"Truncated signature '{signature}'");
        switch (signature[start])
        {
            case 'a':
                return EndOfType(signature, start + 1);
            case '(':
            case '{':
                var close = signature[start] == '(' ? ')' : '}';
                var i = start + 1;
                while (i < signature.Length && signature[i] != close) i = EndOfType(signature, i);
                if (i >= signature.Length) throw new FormatException($"Unbalanced signature '{signature}'");
                return i + 1;
            default:
                Alignment(signature[start]);
                return start + 1;
        }
    }

    /// <summary>Splits a signature into its complete types ("sa{sv}u" → s, a{sv}, u).</summary>
    public static List<string> SplitTypes(string signature)
    {
        var types = new List<string>();
        for (var i = 0; i < signature.Length;)
        {
            var end = EndOfType(signature, i);
            types.Add(signature[i..end]);
            i = end;
        }
        return types;
    }
}

internal sealed class DBusWriter
{
    private byte[] _buffer = new byte[256];

    public int Position { get; private set; }

    public byte[] ToArray() => _buffer.AsSpan(0, Position).ToArray();

    public void Align(int n)
    {
        while (Position % n != 0) WriteByte(0);
    }

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buffer[Position++] = b;
    }

    public void WriteUInt32(uint value)
    {
        Align(4);
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Position), value);
        Position += 4;
    }

    public void PatchUInt32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(at), value);

    /// <summary>Writes a value of one complete type.</summary>
    public void Write(string signature, object? value)
    {
        var code = signature[0];
        Align(DBusWire.Alignment(code));
        switch (code)
        {
            case 'y': WriteByte(Convert.ToByte(value)); break;
            case 'b': WriteUInt32(value is true ? 1u : 0u); break;
            case 'n': WriteFixed(2, s => BinaryPrimitives.WriteInt16LittleEndian(s, Convert.ToInt16(value))); break;
            case 'q': WriteFixed(2, s => BinaryPrimitives.WriteUInt16LittleEndian(s, Convert.ToUInt16(value))); break;
            case 'i': WriteFixed(4, s => BinaryPrimitives.WriteInt32LittleEndian(s, Convert.ToInt32(value))); break;
            case 'u' or 'h': WriteUInt32(Convert.ToUInt32(value)); break;
            case 'x': WriteFixed(8, s => BinaryPrimitives.WriteInt64LittleEndian(s, Convert.ToInt64(value))); break;
            case 't': WriteFixed(8, s => BinaryPrimitives.WriteUInt64LittleEndian(s, Convert.ToUInt64(value))); break;
            case 'd': WriteFixed(8, s => BinaryPrimitives.WriteDoubleLittleEndian(s, Convert.ToDouble(value))); break;
            case 's' or 'o': WriteString((string?)value ?? ""); break;
            case 'g': WriteSignature((string?)value ?? ""); break;
            case 'v':
                var variant = value as DBusVariant ?? throw new ArgumentException("A 'v' value must be a DBusVariant");
                WriteSignature(variant.Signature);
                Write(variant.Signature, variant.Value);
                break;
            case 'a': WriteArray(signature, value); break;
            case '(':
                var fields = value as object?[] ?? throw new ArgumentException("A struct value must be an object?[]");
                var types = DBusWire.SplitTypes(signature[1..^1]);
                if (types.Count != fields.Length) throw new ArgumentException($"Struct {signature} needs {types.Count} fields");
                for (var i = 0; i < types.Count; i++) Write(types[i], fields[i]);
                break;
            default:
                throw new FormatException($"Cannot write D-Bus type '{signature}'");
        }
    }

    private void WriteArray(string signature, object? value)
    {
        var element = signature[1..];
        WriteUInt32(0);
        var lengthAt = Position - 4;
        Align(DBusWire.Alignment(element[0]));
        var start = Position;
        if (element[0] == '{')
        {
            var kv = DBusWire.SplitTypes(element[1..^1]);
            foreach (DictionaryEntry entry in value as IDictionary ?? throw new ArgumentException("An a{..} value must be an IDictionary"))
            {
                Align(8);
                Write(kv[0], entry.Key);
                Write(kv[1], entry.Value);
            }
        }
        else if (value is not null)
        {
            foreach (var item in (IEnumerable)value) Write(element, item);
        }
        PatchUInt32(lengthAt, (uint)(Position - start));
    }

    private void WriteString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteUInt32((uint)bytes.Length);
        Ensure(bytes.Length + 1);
        bytes.CopyTo(_buffer, Position);
        Position += bytes.Length;
        _buffer[Position++] = 0;
    }

    private void WriteSignature(string signature)
    {
        var bytes = Encoding.ASCII.GetBytes(signature);
        WriteByte((byte)bytes.Length);
        Ensure(bytes.Length + 1);
        bytes.CopyTo(_buffer, Position);
        Position += bytes.Length;
        _buffer[Position++] = 0;
    }

    private void WriteFixed(int size, SpanAction write)
    {
        Ensure(size);
        write(_buffer.AsSpan(Position, size));
        Position += size;
    }

    private delegate void SpanAction(Span<byte> span);

    private void Ensure(int extra)
    {
        if (Position + extra <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Position + extra));
    }
}

internal sealed class DBusReader(byte[] buffer, int position = 0)
{
    public int Position { get; private set; } = position;

    public void Align(int n)
    {
        while (Position % n != 0) Position++;
    }

    public byte ReadByte() => buffer[Position++];

    public uint ReadUInt32()
    {
        Align(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(Position));
        Position += 4;
        return v;
    }

    public object? Read(string signature)
    {
        var code = signature[0];
        Align(DBusWire.Alignment(code));
        switch (code)
        {
            case 'y': return ReadByte();
            case 'b': return ReadUInt32() != 0;
            case 'n': return BinaryPrimitives.ReadInt16LittleEndian(Take(2));
            case 'q': return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            case 'i': return BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            case 'u' or 'h': return ReadUInt32();
            case 'x': return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
            case 't': return BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
            case 'd': return BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
            case 's' or 'o':
                var length = (int)ReadUInt32();
                var text = Encoding.UTF8.GetString(buffer, Position, length);
                Position += length + 1;
                return text;
            case 'g': return ReadSignature();
            case 'v':
                var sig = ReadSignature();
                return new DBusVariant(sig, Read(sig));
            case 'a': return ReadArray(signature[1..]);
            case '(':
                var types = DBusWire.SplitTypes(signature[1..^1]);
                var fields = new object?[types.Count];
                for (var i = 0; i < types.Count; i++) fields[i] = Read(types[i]);
                return fields;
            default:
                throw new FormatException($"Cannot read D-Bus type '{signature}'");
        }
    }

    private object ReadArray(string element)
    {
        var length = (int)ReadUInt32();
        Align(DBusWire.Alignment(element[0]));
        var end = Position + length;
        if (element[0] == '{')
        {
            var kv = DBusWire.SplitTypes(element[1..^1]);
            var dict = new Dictionary<object, object?>();
            while (Position < end)
            {
                Align(8);
                var key = Read(kv[0])!;
                dict[key] = Read(kv[1]);
            }
            return dict;
        }
        var list = new List<object?>();
        while (Position < end) list.Add(Read(element));
        return list;
    }

    private string ReadSignature()
    {
        var length = ReadByte();
        var text = Encoding.ASCII.GetString(buffer, Position, length);
        Position += length + 1;
        return text;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        var span = buffer.AsSpan(Position, n);
        Position += n;
        return span;
    }
}

internal enum DBusMessageType : byte { MethodCall = 1, MethodReturn = 2, Error = 3, Signal = 4 }

/// <summary>A D-Bus message (header fields plus body values).</summary>
internal sealed class DBusMessage
{
    public const byte NoReplyExpected = 0x1;

    public DBusMessageType Type { get; init; }
    public byte Flags { get; init; }
    public uint Serial { get; set; }
    public string? Path { get; init; }
    public string? Interface { get; init; }
    public string? Member { get; init; }
    public string? ErrorName { get; init; }
    public uint? ReplySerial { get; init; }
    public string? Destination { get; init; }
    public string? Sender { get; init; }
    public string Signature { get; init; } = "";
    public object?[] Body { get; init; } = [];

    public static DBusMessage Call(string destination, string path, string iface, string member, string signature = "",
        params object?[] body) =>
        new()
        {
            Type = DBusMessageType.MethodCall, Destination = destination, Path = path, Interface = iface, Member = member,
            Signature = signature, Body = body,
        };

    public byte[] Serialize()
    {
        var body = new DBusWriter();
        var types = DBusWire.SplitTypes(Signature);
        if (types.Count != Body.Length) throw new ArgumentException($"Signature '{Signature}' needs {types.Count} body values");
        for (var i = 0; i < types.Count; i++) body.Write(types[i], Body[i]);

        var fields = new List<object?[]>();
        void Field(byte code, string sig, object? value)
        {
            if (value is not null) fields.Add([code, new DBusVariant(sig, value)]);
        }
        Field(1, "o", Path);
        Field(2, "s", Interface);
        Field(3, "s", Member);
        Field(4, "s", ErrorName);
        Field(5, "u", ReplySerial);
        Field(6, "s", Destination);
        Field(7, "s", Sender);
        if (Signature.Length > 0) Field(8, "g", Signature);

        var header = new DBusWriter();
        header.WriteByte((byte)'l');
        header.WriteByte((byte)Type);
        header.WriteByte(Flags);
        header.WriteByte(1);
        header.WriteUInt32((uint)body.Position);
        header.WriteUInt32(Serial);
        header.Write("a(yv)", fields);
        header.Align(8);
        return [.. header.ToArray(), .. body.ToArray()];
    }

    /// <summary>Total message length from its first 16 bytes.</summary>
    public static int TotalLength(ReadOnlySpan<byte> first16)
    {
        if (first16[0] != (byte)'l') throw new FormatException("Only little-endian D-Bus messages are supported");
        var bodyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(first16[4..]);
        var fieldsLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(first16[12..]);
        var headerEnd = 16 + fieldsLength;
        headerEnd += (8 - headerEnd % 8) % 8;
        return headerEnd + bodyLength;
    }

    public static DBusMessage Parse(byte[] data)
    {
        if (data[0] != (byte)'l') throw new FormatException("Only little-endian D-Bus messages are supported");
        var reader = new DBusReader(data, 4);
        var type = (DBusMessageType)data[1];
        var flags = data[2];
        var bodyLength = (int)reader.ReadUInt32();
        var serial = reader.ReadUInt32();
        var fields = (List<object?>)reader.Read("a(yv)")!;
        reader.Align(8);
        var bodyStart = reader.Position;

        string? S(byte code) => fields.Cast<object?[]>().FirstOrDefault(f => (byte)f[0]! == code) is { } f ? ((DBusVariant)f[1]!).Value?.ToString() : null;
        var signature = S(8) ?? "";
        var replySerial = fields.Cast<object?[]>().FirstOrDefault(f => (byte)f[0]! == 5) is { } rs ? (uint?)((DBusVariant)rs[1]!).Value : null;

        // The body is 8-aligned, so a reader over just the body keeps alignment correct.
        var bodyBytes = data.AsSpan(bodyStart, bodyLength).ToArray();
        var bodyReader = new DBusReader(bodyBytes);
        var body = DBusWire.SplitTypes(signature).Select(t => bodyReader.Read(t)).ToArray();
        return new DBusMessage
        {
            Type = type, Flags = flags, Serial = serial, Path = S(1), Interface = S(2), Member = S(3), ErrorName = S(4),
            ReplySerial = replySerial, Destination = S(6), Sender = S(7), Signature = signature, Body = body,
        };
    }
}
