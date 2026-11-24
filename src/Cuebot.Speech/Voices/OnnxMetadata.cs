using System.Text;

namespace Cuebot.Speech;

/// <summary>
/// Reads and adds the string metadata of an ONNX model (<c>ModelProto.metadata_props</c>, field 14) without loading
/// the model. sherpa-onnx reads a voice's sample rate, language and kind from there, and Piper's own voice files do not
/// have it. Protobuf merges repeated fields that appear later in a message, so new entries are appended to the file:
/// the model itself is not rewritten, and Piper still reads the file as before.
/// </summary>
public static class OnnxMetadata
{
    private const int MetadataField = 14;

    /// <summary>The model's metadata (empty when it has none). Throws <see cref="InvalidDataException"/> for a file that is not an ONNX model.</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var length = file.Length;
        var sawField = false;
        while (file.Position < length)
        {
            var tag = ReadVarint(file);
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == 0) throw new InvalidDataException("This is not an ONNX model.");
            sawField = true;
            switch (wireType)
            {
                case 0:
                    ReadVarint(file);
                    break;
                case 1:
                    Skip(file, 8, length);
                    break;
                case 5:
                    Skip(file, 4, length);
                    break;
                case 2:
                    var size = (long)ReadVarint(file);
                    if (size < 0 || file.Position + size > length) throw new InvalidDataException("This ONNX model is cut short.");
                    if (field == MetadataField && size < 1 << 20)
                    {
                        var entry = new byte[size];
                        file.ReadExactly(entry);
                        var (key, value) = ParseEntry(entry);
                        if (key is not null) result[key] = value ?? "";
                    }
                    else Skip(file, size, length);
                    break;
                default:
                    throw new InvalidDataException("This is not an ONNX model.");
            }
        }
        if (!sawField) throw new InvalidDataException("The model file is empty.");
        return result;
    }

    /// <summary>Appends entries to the model's metadata. Keys already present keep their first value for most readers, so only pass new keys.</summary>
    public static void Append(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var output = new MemoryStream();
        foreach (var (key, value) in entries)
        {
            var entry = new MemoryStream();
            WriteString(entry, 1, key);
            WriteString(entry, 2, value);
            WriteVarint(output, (MetadataField << 3) | 2);
            WriteVarint(output, (ulong)entry.Length);
            entry.WriteTo(output);
        }
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        output.WriteTo(file);
    }

    private static (string? Key, string? Value) ParseEntry(byte[] entry)
    {
        string? key = null, value = null;
        using var stream = new MemoryStream(entry);
        while (stream.Position < stream.Length)
        {
            var tag = ReadVarint(stream);
            if ((tag & 7) != 2)
            {
                if ((tag & 7) == 0) ReadVarint(stream);
                else return (key, value);
                continue;
            }
            var size = (int)ReadVarint(stream);
            var bytes = new byte[size];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            if (tag >> 3 == 1) key = text;
            else if (tag >> 3 == 2) value = text;
        }
        return (key, value);
    }

    private static void WriteString(Stream stream, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void WriteVarint(Stream stream, int value) => WriteVarint(stream, (ulong)value);

    private static ulong ReadVarint(Stream stream)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new InvalidDataException("This ONNX model is cut short.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("This is not an ONNX model.");
    }

    private static void Skip(Stream stream, long count, long length)
    {
        if (stream.Position + count > length) throw new InvalidDataException("This ONNX model is cut short.");
        stream.Seek(count, SeekOrigin.Current);
    }
}
