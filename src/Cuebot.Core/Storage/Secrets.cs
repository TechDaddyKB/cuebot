using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cuebot.Core.Storage;

/// <summary>
/// Marks a string property of a settings class as a secret (token, key, password). Settings files hold only a
/// reference to it (<see cref="SecretJson.Prefix"/>); the value lives in the data folder's <see cref="SecretStore"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>Keeps the key that encrypts a <see cref="SecretStore"/>.</summary>
public interface ISecretKeyProtector
{
    /// <summary>Shown in the log, e.g. "Windows data protection".</summary>
    string Name { get; }

    /// <summary>The stored key, or null when there is none yet. Throws when a key exists but cannot be read.</summary>
    byte[]? Load(string folder);

    void Save(string folder, byte[] key);
}

/// <summary>
/// Keeps the key in <c>secrets.key</c> in the data folder: encrypted for the current Windows user (DPAPI) on Windows,
/// otherwise as plain bytes readable only by its owner. Either way it is never part of a backup. On Linux the app
/// prefers the desktop keyring and uses this only where none is available.
/// </summary>
public sealed class FileKeyProtector : ISecretKeyProtector
{
    public const string FileName = "secrets.key";

    public static FileKeyProtector Instance { get; } = new();

    public string Name => OperatingSystem.IsWindows() ? "Windows data protection" : "key file";

    public byte[]? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)
            : bytes;
    }

    public void Save(string folder, byte[] key)
    {
        var path = Path.Combine(folder, FileName);
        var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser) : key;
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temp, bytes);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string folder) => File.Delete(Path.Combine(folder, FileName));
}

/// <summary>
/// The secrets of one data folder: <c>secrets.bin</c>, encrypted with AES-GCM under a key kept by an
/// <see cref="ISecretKeyProtector"/>. Settings files refer to entries by a random id, so a settings file restored from
/// a backup finds its secrets again on the same computer, while a copy on another computer (or a backup on Google
/// Drive) holds no secrets at all. Thread-safe.
/// </summary>
public sealed class SecretStore
{
    public const string FileName = "secrets.bin";
    private static readonly byte[] Magic = "CBS1"u8.ToArray();
    private static readonly ConcurrentDictionary<string, SecretStore> Stores = new(StringComparer.Ordinal);

    private readonly string _folder;
    private readonly ISecretKeyProtector _protector;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _idsByFile = new(StringComparer.Ordinal);
    private byte[]? _key;
    private bool _loaded;

    private SecretStore(string folder, ISecretKeyProtector protector)
    {
        _folder = folder;
        _protector = protector;
    }

    /// <summary>
    /// Picks the key protector for a data folder. The app points its own data folder at the desktop keyring on
    /// Linux; everything else (tests, other folders) uses <see cref="FileKeyProtector"/>.
    /// </summary>
    public static Func<string, ISecretKeyProtector> ProtectorFor { get; set; } = _ => FileKeyProtector.Instance;

    /// <summary>Problems worth logging (an unreadable store, a keyring that refused); set by the app.</summary>
    public static Action<string>? Warn { get; set; }

    public static SecretStore For(string folder)
    {
        var full = Path.GetFullPath(folder);
        return Stores.GetOrAdd(full, f => new SecretStore(f, ProtectorFor(f)));
    }

    /// <summary>Forgets cached stores (tests that reuse a folder path).</summary>
    public static void ResetCache() => Stores.Clear();

    public string ProtectorName => _protector.Name;

    /// <summary>The value for <paramref name="id"/>, remembering which file and position referred to it.</summary>
    public string? Get(string file, string pointer, string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            Ids(file)[pointer] = id;
            return _entries.TryGetValue(id, out var entry) ? entry.Value : null;
        }
    }

    /// <summary>
    /// Stores the secrets of one settings file (position → value) and returns the id for each position. Positions
    /// keep their id while the file has them; secrets the file no longer has are removed.
    /// </summary>
    public IReadOnlyDictionary<string, string> Update(string file, IReadOnlyDictionary<string, string> secrets)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var ids = Ids(file);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var changed = false;
            foreach (var (pointer, value) in secrets)
            {
                if (!ids.TryGetValue(pointer, out var id) || !_entries.TryGetValue(id, out var existing) || existing.File != file)
                {
                    id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                    ids[pointer] = id;
                    _entries[id] = new Entry(file, value);
                    changed = true;
                }
                else if (existing.Value != value)
                {
                    _entries[id] = existing with { Value = value };
                    changed = true;
                }
                result[pointer] = id;
            }
            foreach (var pointer in ids.Keys.Where(p => !secrets.ContainsKey(p)).ToList()) ids.Remove(pointer);
            var keep = result.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _entries.Where(e => e.Value.File == file && !keep.Contains(e.Key)).Select(e => e.Key).ToList())
            {
                _entries.Remove(stale);
                changed = true;
            }
            if (changed) Write();
            return result;
        }
    }

    private Dictionary<string, string> Ids(string file)
    {
        if (!_idsByFile.TryGetValue(file, out var ids)) _idsByFile[file] = ids = new Dictionary<string, string>(StringComparer.Ordinal);
        return ids;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var path = Path.Combine(_folder, FileName);
        if (!File.Exists(path)) return;
        try
        {
            _key = _protector.Load(_folder) ?? throw new CryptographicException("its key is missing");
            _entries = Decrypt(File.ReadAllBytes(path), _key);
        }
        catch (Exception ex)
        {
            // Keep the unreadable store for inspection; sign-ins it held must be repeated.
            _key = null;
            var keep = $"{path}.unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(path, keep); } catch (IOException) { }
            Warn?.Invoke($"Stored sign-ins and keys could not be read ({ex.Message}); sign in again. The old store was kept as {Path.GetFileName(keep)}.");
        }
    }

    private void Write()
    {
        if (_key is null)
        {
            if (_entries.Count == 0) return;
            _key = RandomNumberGenerator.GetBytes(32);
            _protector.Save(_folder, _key);
        }
        var path = Path.Combine(_folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Encrypt(_entries, _key));
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Encrypt(Dictionary<string, Entry> entries, byte[] key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
        CryptographicOperations.ZeroMemory(plain);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    private static Dictionary<string, Entry> Decrypt(byte[] data, byte[] key)
    {
        int nonceSize = AesGcm.NonceByteSizes.MaxSize, tagSize = AesGcm.TagByteSizes.MaxSize;
        if (data.Length < Magic.Length + nonceSize + tagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("not a Cuebot secret store");
        var nonce = data.AsSpan(Magic.Length, nonceSize);
        var tag = data.AsSpan(Magic.Length + nonceSize, tagSize);
        var cipher = data.AsSpan(Magic.Length + nonceSize + tagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, tagSize)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
        var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(plain) ?? [];
        CryptographicOperations.ZeroMemory(plain);
        return new Dictionary<string, Entry>(entries, StringComparer.Ordinal);
    }

    private sealed record Entry(string File, string Value);
}
