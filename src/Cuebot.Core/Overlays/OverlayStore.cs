using System.Security.Cryptography;
using System.Text;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Overlays;

/// <summary>
/// The user's overlays (overlays.json; in memory when <c>paths</c> is null). Reads return copies, so callers can keep
/// them; changes go through <see cref="Update"/> and raise <see cref="Changed"/> with the overlay's id.
/// </summary>
public sealed class OverlayStore : IDisposable
{
    public const int MinSize = 16;
    public const int MaxSize = 7680;

    private readonly object _gate = new();
    private readonly JsonFile<OverlayDocument>? _file;
    private readonly OverlayDocument _memory = new();

    public OverlayStore(AppPaths? paths, AppLog log)
    {
        if (paths is null) return;
        _file = new JsonFile<OverlayDocument>(paths.File("overlays.json"), log);
        var repaired = false;
        foreach (var overlay in Document.Overlays.Where(o => string.IsNullOrWhiteSpace(o.Token)))
        {
            overlay.Token = NewToken();
            repaired = true;
        }
        if (repaired) _file.ScheduleSave();
    }

    /// <summary>Raised (on the caller's thread) after an overlay is created, changed or deleted.</summary>
    public event Action<Guid>? Changed;

    private OverlayDocument Document => _file?.Value ?? _memory;

    public IReadOnlyList<Overlay> All()
    {
        lock (_gate) return Document.Overlays.Select(Json.Clone).ToArray();
    }

    public Overlay? Get(Guid id)
    {
        lock (_gate) return Find(id) is { } overlay ? Json.Clone(overlay) : null;
    }

    /// <summary>
    /// The overlay when <paramref name="token"/> is its token (compared in constant time), otherwise null, so a wrong
    /// token and an unknown id look the same.
    /// </summary>
    public Overlay? Authorize(Guid id, string? token)
    {
        lock (_gate)
        {
            if (Find(id) is not { } overlay || !TokenMatches(overlay.Token, token)) return null;
            return Json.Clone(overlay);
        }
    }

    public Overlay Create(string name, int width = 1920, int height = 1080)
    {
        var overlay = new Overlay
        {
            Name = CleanName(name),
            Width = Math.Clamp(width, MinSize, MaxSize),
            Height = Math.Clamp(height, MinSize, MaxSize),
            Token = NewToken(),
        };
        lock (_gate)
        {
            Document.Overlays.Add(overlay);
            Save();
        }
        Changed?.Invoke(overlay.Id);
        return Json.Clone(overlay);
    }

    /// <summary>Edits an overlay in place; returns false when it does not exist. The id and token cannot be changed here.</summary>
    public bool Update(Guid id, Action<Overlay> edit)
    {
        lock (_gate)
        {
            if (Find(id) is not { } overlay) return false;
            var token = overlay.Token;
            edit(overlay);
            overlay.Id = id;
            overlay.Token = token;
            overlay.Name = CleanName(overlay.Name);
            overlay.Width = Math.Clamp(overlay.Width, MinSize, MaxSize);
            overlay.Height = Math.Clamp(overlay.Height, MinSize, MaxSize);
            overlay.Modified = DateTimeOffset.UtcNow;
            Save();
        }
        Changed?.Invoke(id);
        return true;
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (Document.Overlays.RemoveAll(o => o.Id == id) == 0) return false;
            Save();
        }
        Changed?.Invoke(id);
        return true;
    }

    /// <summary>Gives the overlay a new token; browser sources using the old URL stop working. Null when it does not exist.</summary>
    public string? RegenerateToken(Guid id)
    {
        string token;
        lock (_gate)
        {
            if (Find(id) is not { } overlay) return null;
            token = overlay.Token = NewToken();
            overlay.Modified = DateTimeOffset.UtcNow;
            Save();
        }
        Changed?.Invoke(id);
        return token;
    }

    /// <summary>A URL-safe random token (32 bytes) with an "ov_" prefix.</summary>
    public static string NewToken() =>
        "ov_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TokenMatches(string expected, string? candidate)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }

    private Overlay? Find(Guid id) => Document.Overlays.FirstOrDefault(o => o.Id == id);

    private static string CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? "Overlay" : name.Trim();

    private void Save() => _file?.ScheduleSave();

    public void Dispose() => _file?.Dispose();
}
