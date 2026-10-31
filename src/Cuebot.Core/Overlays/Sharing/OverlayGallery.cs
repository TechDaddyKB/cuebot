using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Overlays.Sharing;

/// <summary>An item in the community gallery (one entry of its <c>index.json</c>).</summary>
public sealed record GalleryItem(string Id, string Name, string Author, string Description, string Kind, string License,
    IReadOnlyList<string> Tags, bool OriginalCode, string Package, string Preview, long Size, string Sha256);

/// <summary>The gallery could not be read, or a download did not match the index. The message is safe to show.</summary>
public sealed class GalleryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The community gallery: the public <see cref="Repository"/> on GitHub, where items arrive as pull requests and go live
/// only when a maintainer merges them. Cuebot reads its <c>index.json</c> and downloads packages and previews from the
/// repository's raw files, only from the item's own folder, and checks each package's size and SHA-256 against the
/// index before it is imported.
/// </summary>
public sealed partial class OverlayGallery(HttpClient http, Uri? rawBase = null)
{
    public const string Repository = "TechDaddyKB/cuebot-gallery";
    public const string RepositoryUrl = "https://github.com/" + Repository;

    /// <summary>The wiki's guide to sharing an item.</summary>
    public const string ContributeUrl = RepositoryUrl + "/wiki/Detailed-guide";

    public static readonly Uri Raw = new("https://raw.githubusercontent.com/" + Repository + "/main/");

    public const long MaxPackage = 25 * 1024 * 1024;
    public const long MaxPreview = 2 * 1024 * 1024;
    private const long MaxIndex = 4 * 1024 * 1024;

    private readonly Uri _base = rawBase ?? Raw;

    /// <summary>The gallery's items (entries that do not follow the gallery's rules are left out).</summary>
    public async Task<IReadOnlyList<GalleryItem>> ListAsync(CancellationToken ct = default)
    {
        var bytes = await GetAsync("index.json", MaxIndex, ct).ConfigureAwait(false);
        JsonObject? index;
        try
        {
            index = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new GalleryException("The gallery's index is not readable.", ex);
        }
        if (index is null || Str(index["format"]) != "cuebot-gallery") throw new GalleryException("The gallery's index is not readable.");
        return (index["items"] as JsonArray ?? []).OfType<JsonObject>().Select(Parse).OfType<GalleryItem>().ToArray();
    }

    internal static GalleryItem? Parse(JsonObject o)
    {
        var id = Str(o["id"]);
        if (!Slug().IsMatch(id)) return null;
        var package = Str(o["package"]);
        var preview = Str(o["preview"]);
        var size = o["size"] is JsonValue s && s.TryGetValue<long>(out var n) ? n : -1;
        var sha = Str(o["sha256"]);
        var kind = Str(o["kind"]);
        if (package != $"items/{id}/package.cuebot-overlay" || !PreviewPath().IsMatch(preview) || !preview.StartsWith($"items/{id}/", StringComparison.Ordinal)
            || size is <= 0 or > MaxPackage || !Sha().IsMatch(sha) || kind is not ("overlay" or "widget"))
            return null;
        var tags = (o["tags"] as JsonArray ?? []).Select(Str).Where(t => t.Length > 0).Take(8).ToArray();
        return new GalleryItem(id, Limit(Str(o["name"]), 80, id), Limit(Str(o["author"]), 60, ""), Limit(Str(o["description"]), 500, ""),
            kind, Str(o["license"]), tags, o["originalCode"] is JsonValue c && c.TryGetValue<bool>(out var oc) && oc, package, preview, size, sha);
    }

    /// <summary>The item's package, checked against the index's size and SHA-256.</summary>
    public async Task<MemoryStream> DownloadAsync(GalleryItem item, CancellationToken ct = default)
    {
        var bytes = await GetAsync(item.Package, Math.Min(item.Size, MaxPackage), ct).ConfigureAwait(false);
        if (bytes.Length != item.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != item.Sha256)
            throw new GalleryException($"\"{item.Name}\" did not download as the gallery lists it. Try again later.");
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>The item's preview image bytes (at most 2 MB).</summary>
    public Task<byte[]> PreviewAsync(GalleryItem item, CancellationToken ct = default) => GetAsync(item.Preview, MaxPreview, ct);

    /// <summary>The gallery's report form with the item filled in.</summary>
    public static string ReportUrl(GalleryItem item) => $"{RepositoryUrl}/issues/new?template=report.yml&item={Uri.EscapeDataString(item.Id)}";

    /// <summary>The item's folder on GitHub.</summary>
    public static string PageUrl(GalleryItem item) => $"{RepositoryUrl}/tree/main/items/{Uri.EscapeDataString(item.Id)}";

    private async Task<byte[]> GetAsync(string path, long max, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(_base, path), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new GalleryException($"The gallery answered {(int)response.StatusCode}. Try again later.");
            if (response.Content.Headers.ContentLength > max) throw new GalleryException("The gallery sent more than expected.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max) throw new GalleryException("The gallery sent more than expected.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new GalleryException("Could not reach the gallery: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GalleryException("The gallery took too long to answer.", ex);
        }
    }

    private static string Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>().Trim() : "";

    private static string Limit(string text, int max, string fallback) => text.Length == 0 ? fallback : text.Length > max ? text[..max] : text;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,48}$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^items/[a-z0-9][a-z0-9-]{1,48}/preview\.(png|jpg|webp)$")]
    private static partial Regex PreviewPath();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha();
}
