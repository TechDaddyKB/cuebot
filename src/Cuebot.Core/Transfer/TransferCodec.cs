using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Transfer;

/// <summary>What a pasted string or opened file turned out to be.</summary>
public enum PayloadKind
{
    /// <summary>A Cuebot bundle (<see cref="TransferBundle"/>).</summary>
    Cuebot,

    /// <summary>A Streamer.bot export (base64 "SBAE" + gzip JSON, or its plain JSON).</summary>
    StreamerBot,
}

/// <summary>Decoded text of an import: the JSON and which importer understands it.</summary>
public sealed record DecodedPayload(PayloadKind Kind, string Json);

/// <summary>An import that is not readable (corrupt, unknown, too large). The message is safe to show.</summary>
public sealed class TransferFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Encodes and decodes the transfer forms. Nothing here executes or interprets content beyond parsing JSON.
/// <list type="bullet">
/// <item>String: <c>CUEBOT1:</c> + base64(gzip(UTF-8 bundle JSON)). Line breaks and spaces inside are ignored.</item>
/// <item>File (<c>.cuebot</c>): the bundle JSON, indented (the string form is accepted too).</item>
/// <item>Streamer.bot: base64("SBAE" + gzip(JSON)), or a bare gzip/JSON payload.</item>
/// </list>
/// Input is capped at <see cref="MaxInputChars"/> characters and decompression at <see cref="MaxDecompressedBytes"/>
/// bytes, so oversized pastes and zip bombs are rejected before they are parsed.
/// </summary>
public static class TransferCodec
{
    public const string StringPrefix = "CUEBOT1:";
    public const string FileExtension = ".cuebot";

    /// <summary>Streamer.bot's 4-byte marker in front of its gzip data ("SBAE").</summary>
    public static ReadOnlySpan<byte> StreamerBotMarker => "SBAE"u8;

    public const int MaxDecompressedBytes = 20 * 1024 * 1024;
    public const int MaxInputChars = 32 * 1024 * 1024;

    public static string ToJson(TransferBundle bundle) => JsonSerializer.Serialize(bundle, Json.Options);

    /// <summary>The copy-pasteable string.</summary>
    public static string Encode(TransferBundle bundle)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, Compact));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(json);
        return StringPrefix + Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>Reads a string or file content into a bundle. Throws <see cref="TransferFormatException"/>.</summary>
    public static TransferBundle DecodeBundle(string text)
    {
        var payload = Decode(text);
        if (payload.Kind != PayloadKind.Cuebot) throw new TransferFormatException("This is a Streamer.bot export, not a Cuebot bundle.");
        return ParseBundle(payload.Json);
    }

    public static TransferBundle ParseBundle(string json)
    {
        TransferBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<TransferBundle>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException("The bundle JSON is not valid: " + ex.Message, ex);
        }
        if (bundle is null || !string.Equals(bundle.Format, TransferBundle.FormatId, StringComparison.Ordinal))
            throw new TransferFormatException("Not a Cuebot bundle.");
        if (bundle.Version > TransferBundle.CurrentVersion)
            throw new TransferFormatException(
                $"This bundle was made by a newer Cuebot (format version {bundle.Version}); update Cuebot to import it.");
        bundle.Header ??= new TransferHeader();
        bundle.Actions ??= [];
        bundle.Queues ??= [];
        bundle.Commands ??= [];
        bundle.Timers ??= [];
        bundle.Globals ??= [];
        return bundle;
    }

    /// <summary>Works out what <paramref name="text"/> is and returns its JSON. Never executes anything.</summary>
    public static DecodedPayload Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInputChars) throw new TransferFormatException("The import is too large.");
        var trimmed = text.Trim().TrimStart('﻿');
        if (trimmed.Length == 0) throw new TransferFormatException("Nothing to import.");

        if (trimmed[0] == '{') return Classify(trimmed);

        if (trimmed.StartsWith(StringPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bytes = FromBase64(trimmed[StringPrefix.Length..]);
            var json = Gunzip(bytes, 0);
            var payload = Classify(json);
            if (payload.Kind != PayloadKind.Cuebot) throw new TransferFormatException("Not a Cuebot bundle.");
            return payload;
        }

        var raw = FromBase64(trimmed);
        if (raw.AsSpan().StartsWith(StreamerBotMarker))
            return new DecodedPayload(PayloadKind.StreamerBot, Gunzip(raw, StreamerBotMarker.Length));
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B) return Classify(Gunzip(raw, 0));
        throw new TransferFormatException("Unrecognised import: expected a Cuebot string (CUEBOT1:…), a Streamer.bot export or JSON.");
    }

    private static DecodedPayload Classify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new TransferFormatException("The import is not a JSON object.");
            var isCuebot = doc.RootElement.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String
                           && f.GetString() == TransferBundle.FormatId;
            return new DecodedPayload(isCuebot ? PayloadKind.Cuebot : PayloadKind.StreamerBot, json);
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException("The import is not valid JSON: " + ex.Message, ex);
        }
    }

    private static byte[] FromBase64(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        try
        {
            return Convert.FromBase64String(sb.ToString());
        }
        catch (FormatException ex)
        {
            throw new TransferFormatException("The import string is damaged (not valid base64). Copy it again in full.", ex);
        }
    }

    /// <summary>Decompresses gzip data starting at <paramref name="offset"/>, refusing more than the size cap.</summary>
    internal static string Gunzip(byte[] data, int offset, int maxBytes = MaxDecompressedBytes)
    {
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new TransferFormatException($"The import expands to more than {maxBytes / (1024 * 1024)} MB and was rejected.");
                output.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length).TrimStart('﻿');
        }
        catch (InvalidDataException ex)
        {
            throw new TransferFormatException("The import string is damaged (compressed data is corrupt).", ex);
        }
    }

    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 256, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options) { MaxDepth = 256 };
}
