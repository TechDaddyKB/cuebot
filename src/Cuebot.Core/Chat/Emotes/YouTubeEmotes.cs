using Cuebot.Core.Overlays;

namespace Cuebot.Core.Chat.Emotes;

/// <summary>
/// The hand-made list of YouTube member emotes. YouTube's API reports them only as text (":_tamaDent:"), so the streamer
/// supplies each code's picture, as a web address or an image file kept in the overlay <see cref="AssetStore"/>.
/// </summary>
public static class YouTubeEmotes
{
    /// <summary>The largest emote image, the same limit the chat page's image loader has.</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    private const string AssetPrefix = "/assets/";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.Ordinal) { "png", "jpg", "jpeg", "gif", "webp" };

    /// <summary>The code with its colons (":name:"), or empty when it cannot be a code (blank, spaces or inner colons).</summary>
    public static string NormalizeCode(string? code)
    {
        var name = (code ?? "").Trim().Trim(':');
        return name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c == ':') ? "" : $":{name}:";
    }

    /// <summary>An http(s) address or an uploaded "/assets/{name}" file; nothing else may reach an overlay's img tag.</summary>
    public static bool IsImageUrl(string? url)
    {
        var value = (url ?? "").Trim();
        if (value.StartsWith(AssetPrefix, StringComparison.Ordinal))
            return value.Length > AssetPrefix.Length && value.IndexOfAny(['/', '\\', '?', '#'], AssetPrefix.Length) < 0;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>The code for an image file: "tamaDent.png" becomes ":_tamaDent:", as YouTube names member emotes.</summary>
    public static string CodeForFile(string fileName)
    {
        var stem = string.Concat(Path.GetFileNameWithoutExtension(fileName).Where(c => !char.IsWhiteSpace(c) && c != ':'));
        return stem.Length == 0 ? "" : NormalizeCode(stem.StartsWith('_') ? stem : "_" + stem);
    }

    /// <summary>
    /// Stores an image and adds it under the code from its file name, replacing the picture of a code already listed.
    /// Throws <see cref="ArgumentException"/> or <see cref="InvalidDataException"/> for a file that cannot be used.
    /// </summary>
    public static async Task<string> ImportAsync(EmoteSettings settings, AssetStore assets, string fileName, Stream content, CancellationToken ct = default)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (!ImageExtensions.Contains(extension)) throw new ArgumentException($"'{name}' is not a PNG, JPEG, GIF or WebP image.");
        var code = CodeForFile(name);
        if (code.Length == 0) throw new ArgumentException($"'{name}' has no usable name for an emote code.");

        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaxImageBytes) throw new InvalidDataException($"'{name}' is larger than {MaxImageBytes / (1024 * 1024)} MB.");
            copy.Write(buffer, 0, read);
        }
        if (copy.Length == 0) throw new InvalidDataException($"'{name}' is empty.");
        copy.Position = 0;

        var url = AssetPrefix + await assets.AddAsync(copy, name, ct).ConfigureAwait(false);
        var existing = settings.YouTubeEmotes.FirstOrDefault(e => NormalizeCode(e.Code) == code);
        if (existing is not null) existing.ImageUrl = url;
        else settings.YouTubeEmotes.Add(new CustomEmote { Code = code, ImageUrl = url });
        return code;
    }
}
