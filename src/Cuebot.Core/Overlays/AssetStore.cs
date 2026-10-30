using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Overlays;

/// <summary>
/// Files used by overlays (images, video, sound, fonts), stored once under <c>overlays/assets</c> and named by the
/// SHA-256 of their content plus a lower-case extension, so the same file added twice is kept once and a name can be
/// cached forever. With no <c>paths</c> they live in a temporary folder deleted on dispose (tests).
/// </summary>
public sealed partial class AssetStore : IDisposable
{
    /// <summary>The largest file accepted (videos included).</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>Extensions overlays may use, by kind.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "png", "jpg", "jpeg", "gif", "webp", "avif", "svg", "apng",
        "mp4", "webm", "mov",
        "mp3", "ogg", "oga", "wav", "m4a", "flac", "opus",
        "woff", "woff2", "ttf", "otf",
    };

    private readonly bool _temporary;

    public AssetStore(AppPaths? paths)
    {
        if (paths is not null)
        {
            Folder = paths.GetDirectory(Path.Combine("overlays", "assets"));
            return;
        }
        Folder = Path.Combine(Path.GetTempPath(), "cuebot-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        _temporary = true;
    }

    public string Folder { get; }

    /// <summary>
    /// Stores the content of <paramref name="content"/> with the extension of <paramref name="fileName"/> and returns
    /// the asset's name ("{sha256}.{ext}"). Throws <see cref="ArgumentException"/> for an extension overlays cannot use
    /// and <see cref="InvalidDataException"/> for a file larger than <see cref="MaxBytes"/>.
    /// </summary>
    public async Task<string> AddAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var extension = ExtensionOf(fileName) ?? throw new ArgumentException($"'{Path.GetFileName(fileName)}' is not a file type overlays can use");
        var temp = Path.Combine(Folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string hash;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new InvalidDataException($"Files for overlays can be at most {MaxBytes / (1024 * 1024)} MB");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            var name = $"{hash}.{extension}";
            var target = Path.Combine(Folder, name);
            if (File.Exists(target)) File.Delete(temp);
            else File.Move(temp, target);
            return name;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The file for an asset name, or null when the name is not a valid asset name or the file is gone.</summary>
    public string? PathOf(string? name)
    {
        if (name is null || !AssetName().IsMatch(name) || !Extensions.Contains(name[(name.IndexOf('.') + 1)..])) return null;
        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The lower-case extension without the dot when overlays can use it, otherwise null.</summary>
    public static string? ExtensionOf(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return Extensions.Contains(extension) ? extension : null;
    }

    [GeneratedRegex("^[0-9a-f]{64}\\.[a-z0-9]{1,8}$")]
    private static partial Regex AssetName();

    public void Dispose()
    {
        if (!_temporary) return;
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
