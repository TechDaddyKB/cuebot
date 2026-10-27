using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>
/// Downloads and decodes emote images once (memory + disk cache under the data folder's cache/emote-images). Avalonia's
/// Skia decoder reads PNG, JPEG, GIF and WebP; animated GIF/WebP show their first frame. Failures yield null so the
/// view falls back to the emote's text.
/// </summary>
public sealed class EmoteImageLoader(HttpClient http, string? cacheDirectory = null)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new(StringComparer.Ordinal);

    public Task<Bitmap?> LoadAsync(string url) =>
        string.IsNullOrEmpty(url) ? Task.FromResult<Bitmap?>(null) : _images.GetOrAdd(url, u => Task.Run(() => FetchAsync(u)));

    /// <summary>The decoded image if it is already loaded, without waiting.</summary>
    public Bitmap? TryGet(string url) =>
        _images.TryGetValue(url, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    private async Task<Bitmap?> FetchAsync(string url)
    {
        try
        {
            var file = cacheDirectory is null ? null : Path.Combine(cacheDirectory, Hash(url));
            byte[] bytes;
            if (file is not null && File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            }
            else
            {
                using var response = await http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                if (response.Content.Headers.ContentLength > MaxBytes) return null;
                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
                if (file is not null)
                {
                    Directory.CreateDirectory(cacheDirectory!);
                    await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
                }
            }
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;   // network, disk or decode failure: show the emote's text instead
        }
    }

    private static string Hash(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
}
