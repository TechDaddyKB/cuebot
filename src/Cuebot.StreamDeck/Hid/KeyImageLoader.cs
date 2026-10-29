using System.Collections.Concurrent;

namespace Cuebot.StreamDeck.Hid;

/// <summary>
/// Turns the image text of Set Background / the key settings (data: URI, file path, file:// or http(s) URL) into
/// encoded image bytes for <see cref="StreamDeckKeyRenderer"/>. Results are cached (a few dozen entries); failures are
/// remembered as null so a broken URL is not fetched on every repaint.
/// </summary>
public sealed class KeyImageLoader(HttpClient? http = null, int maxBytes = 4 * 1024 * 1024)
{
    private const int CacheSize = 64;
    private readonly ConcurrentDictionary<string, byte[]?> _cache = new(StringComparer.Ordinal);

    /// <summary>Raised with a user-facing reason when an image cannot be loaded.</summary>
    public event Action<string>? Failed;

    public async Task<byte[]?> LoadAsync(string? image, CancellationToken ct = default)
    {
        var text = image?.Trim().Trim('"') ?? "";
        if (text.Length == 0) return null;
        if (_cache.TryGetValue(text, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            bytes = await FetchAsync(text, ct).ConfigureAwait(false);
            if (bytes is not null && bytes.Length > maxBytes)
            {
                Failed?.Invoke($"Image is {bytes.Length / 1024} KB; the limit is {maxBytes / 1024} KB");
                bytes = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or HttpRequestException
                                       or TaskCanceledException or ArgumentException or NotSupportedException)
        {
            Failed?.Invoke($"Could not load the key image '{Describe(text)}': {ex.Message}");
        }
        if (_cache.Count >= CacheSize) _cache.Clear();
        _cache[text] = bytes;
        return bytes;
    }

    private async Task<byte[]?> FetchAsync(string text, CancellationToken ct)
    {
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma < 0) throw new FormatException("malformed data: URI");
            var header = text[5..comma];
            if (header.Contains("svg", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("SVG images are not supported on directly driven decks");
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(text[(comma + 1)..])
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(text[(comma + 1)..]));
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (http is null) throw new NotSupportedException("downloading images is not available");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await http.GetByteArrayAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        var path = uri is { IsFile: true } ? uri.LocalPath : text;
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SVG images are not supported on directly driven decks");
        if (!File.Exists(path)) throw new FileNotFoundException("no such file");
        if (new FileInfo(path).Length > maxBytes) throw new IOException($"the file is larger than {maxBytes / 1024} KB");
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Short text for logs: data URIs are not printed in full.</summary>
    private static string Describe(string text) => text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        ? text[..Math.Min(text.Length, 24)] + "…"
        : text.Length > 120 ? text[..120] + "…" : text;
}
