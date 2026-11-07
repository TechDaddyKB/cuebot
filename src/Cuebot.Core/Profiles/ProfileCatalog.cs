using System.Text.Json;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Profiles;

/// <summary>One streamer profile (a persona): its folder name never changes, its name and colour can.</summary>
public sealed class ProfileEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A colour like <c>#8B5CF6</c>, shown next to the name so personas are easy to tell apart.</summary>
    public string Color { get; set; } = ProfileCatalog.Colors[0];

    public DateTimeOffset Created { get; set; }
}

/// <summary>What <c>profiles.json</c> holds.</summary>
public sealed class ProfileList
{
    public string Active { get; set; } = "";
    public List<ProfileEntry> Profiles { get; set; } = [];
}

/// <summary>The parts of a profile that can be copied into a new one. Sign-ins of platforms and services never are.</summary>
[Flags]
public enum ProfileParts
{
    None = 0,
    Actions = 1,
    Commands = 2,
    Overlays = 4,
    DecksAndHotkeys = 8,
    Variables = 16,
    AppsAndDevices = 32,
    Voices = 64,
    All = Actions | Commands | Overlays | DecksAndHotkeys | Variables | AppsAndDevices | Voices,
}

/// <summary>
/// The streamer profiles in a data folder (<c>profiles.json</c> and <c>profiles/&lt;id&gt;/</c>). Each profile is a
/// whole Cuebot of its own (see <see cref="AppPaths.Root"/>); the data folder keeps what they share. Opening a data
/// folder from before profiles moves its persona files into a first profile, "Main". Not thread-safe: used at start-up
/// and from the UI thread.
/// </summary>
public sealed class ProfileCatalog
{
    public const string FileName = "profiles.json";
    public const string FolderName = "profiles";
    public const string FirstId = "main";

    /// <summary>Colours offered for profiles, in the order new profiles get them.</summary>
    public static readonly IReadOnlyList<string> Colors =
        ["#8B5CF6", "#EC4899", "#06B6D4", "#F59E0B", "#10B981", "#EF4444", "#3B82F6", "#84CC16"];

    /// <summary>
    /// Top-level entries of the data folder every profile shares; everything else belongs to a profile. Kept in step
    /// with the shared files and folders in <see cref="AppPaths"/> users.
    /// </summary>
    public static readonly IReadOnlySet<string> SharedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        FileName, FolderName, "app.json", "updates.json", "instance.lock", "instance.sock", "restore.pending",
        "logs", "backups", "python", "piper", "models", "cache", "crashes", "reports",
    };

    /// <summary>The files (and folders) each copyable part is made of.</summary>
    public static readonly IReadOnlyDictionary<ProfileParts, string[]> PartFiles = new Dictionary<ProfileParts, string[]>
    {
        [ProfileParts.Actions] = ["actions.json", "watchers.json", "scripting.json"],
        [ProfileParts.Commands] = ["commands.json"],
        [ProfileParts.Overlays] = ["overlays.json", "elements.json", "widget-store.json", "overlays"],
        [ProfileParts.DecksAndHotkeys] = ["decks.json", "hotkeys.json", "controllers.json", "midi.json", "streamdeck-usb.json"],
        [ProfileParts.Variables] = ["globals.json"],
        [ProfileParts.AppsAndDevices] =
            ["obs.json", "streamapps.json", "streamlabs-desktop.json", "elgato.json", "wavelink.json", "goxlr.json", "servers.json", "desktop.json"],
        [ProfileParts.Voices] = ["speech.json", "voices.json", "voicecontrol.json"],
    };

    private readonly string _dataFolder;
    private ProfileList _list;

    private ProfileCatalog(string dataFolder, ProfileList list, bool moved)
    {
        _dataFolder = dataFolder;
        _list = list;
        MovedExistingData = moved;
    }

    public IReadOnlyList<ProfileEntry> Profiles => _list.Profiles;

    public ProfileEntry Active => Find(_list.Active) ?? _list.Profiles[0];

    /// <summary>This open moved a data folder from before profiles into the first profile.</summary>
    public bool MovedExistingData { get; }

    public string FolderOf(string id) => Path.Combine(_dataFolder, FolderName, id);

    public string ActiveFolder => FolderOf(Active.Id);

    /// <summary>A profile by id, or else by name (both ignoring case); null when none matches.</summary>
    public ProfileEntry? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var text = idOrName.Trim();
        return _list.Profiles.FirstOrDefault(p => p.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? _list.Profiles.FirstOrDefault(p => p.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A data folder from before profiles holds persona files at its top level. True when opening it would move them
    /// (callers back it up first).
    /// </summary>
    public static bool NeedsMove(string dataFolder) =>
        !File.Exists(Path.Combine(dataFolder, FileName)) && Directory.Exists(dataFolder)
        && Directory.EnumerateFileSystemEntries(dataFolder).Any(e => !SharedEntries.Contains(Path.GetFileName(e)));

    /// <summary>
    /// The active profile's folder as <c>profiles.json</c> says, without changing anything (for tools that only read,
    /// like the MCP bridge); the data folder itself when it has no profiles yet.
    /// </summary>
    public static string ActiveFolderOf(string dataFolder)
    {
        var list = Read(dataFolder);
        var active = list?.Profiles.FirstOrDefault(p => p.Id == list.Active) ?? list?.Profiles.FirstOrDefault();
        return active is null ? dataFolder : Path.Combine(dataFolder, FolderName, active.Id);
    }

    /// <summary>
    /// Opens the profiles of <paramref name="dataFolder"/>: moves the data of a folder from before profiles into
    /// "Main" (resumable: an interrupted move finishes on the next open), and lists profile folders that
    /// <c>profiles.json</c> does not know (after a restore) so nothing is hidden.
    /// </summary>
    public static ProfileCatalog Open(string dataFolder)
    {
        var root = Path.GetFullPath(dataFolder);
        Directory.CreateDirectory(Path.Combine(root, FolderName));
        var list = Read(root);
        var moved = false;
        if (list is null)
        {
            moved = MoveIntoFirstProfile(root);
            list = new ProfileList
            {
                Active = FirstId,
                Profiles = [new ProfileEntry { Id = FirstId, Name = "Main", Color = Colors[0], Created = DateTimeOffset.Now }],
            };
        }
        var catalog = new ProfileCatalog(root, list, moved);
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, FolderName)))
        {
            var id = Path.GetFileName(dir);
            if (!IsValidId(id) || catalog.Find(id) is not null) continue;
            list.Profiles.Add(new ProfileEntry { Id = id, Name = id, Color = catalog.NextColor(), Created = DateTimeOffset.Now });
        }
        if (list.Profiles.Count == 0)
            list.Profiles.Add(new ProfileEntry { Id = FirstId, Name = "Main", Color = Colors[0], Created = DateTimeOffset.Now });
        if (catalog.Find(list.Active) is null) list.Active = list.Profiles[0].Id;
        Directory.CreateDirectory(catalog.ActiveFolder);
        catalog.Save();
        return catalog;
    }

    /// <summary>Makes <paramref name="id"/> the profile Cuebot opens next (the switch itself is a restart).</summary>
    public void SetActive(string id)
    {
        var profile = Find(id) ?? throw new ArgumentException($"There is no profile '{id}'.", nameof(id));
        _list.Active = profile.Id;
        Save();
    }

    /// <summary>
    /// Makes a new profile, empty or with <paramref name="parts"/> copied from <paramref name="copyFrom"/> (with the
    /// secrets those files refer to, like an OBS password). Returns it; it is not made active.
    /// </summary>
    public ProfileEntry Create(string name, string? color = null, string? copyFrom = null, ProfileParts parts = ProfileParts.None)
    {
        var trimmed = CheckName(name, null);
        var source = copyFrom is null ? null : Find(copyFrom) ?? throw new ArgumentException($"There is no profile '{copyFrom}'.", nameof(copyFrom));
        var id = NewId(trimmed);
        var folder = FolderOf(id);
        Directory.CreateDirectory(folder);
        if (source is not null && parts != ProfileParts.None) CopyParts(FolderOf(source.Id), folder, parts);
        var profile = new ProfileEntry { Id = id, Name = trimmed, Color = color ?? NextColor(), Created = DateTimeOffset.Now };
        _list.Profiles.Add(profile);
        Save();
        return profile;
    }

    public void Rename(string id, string name)
    {
        var profile = Find(id) ?? throw new ArgumentException($"There is no profile '{id}'.", nameof(id));
        profile.Name = CheckName(name, profile);
        Save();
    }

    public void SetColor(string id, string color)
    {
        var profile = Find(id) ?? throw new ArgumentException($"There is no profile '{id}'.", nameof(id));
        profile.Color = color;
        Save();
    }

    /// <summary>Deletes a profile and its folder, sign-ins included. The active profile cannot be deleted.</summary>
    public void Delete(string id)
    {
        var profile = Find(id) ?? throw new ArgumentException($"There is no profile '{id}'.", nameof(id));
        if (profile.Id == _list.Active) throw new InvalidOperationException("Switch to another profile before deleting this one.");
        _list.Profiles.Remove(profile);
        Save();
        var folder = FolderOf(profile.Id);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private string CheckName(string name, ProfileEntry? self)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("Give the profile a name.", nameof(name));
        if (trimmed.Length > 40) throw new ArgumentException("Keep the name to 40 characters.", nameof(name));
        if (_list.Profiles.Any(p => p != self && p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is already a profile called '{trimmed}'.", nameof(name));
        return trimmed;
    }

    private string NewId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "profile";
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        var id = slug;
        for (var n = 2; Find(id) is not null || Directory.Exists(FolderOf(id)); n++) id = $"{slug}-{n}";
        return id;
    }

    private static bool IsValidId(string id) =>
        id.Length > 0 && !id.StartsWith('.') && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private string NextColor() =>
        Colors.FirstOrDefault(c => _list.Profiles.All(p => !p.Color.Equals(c, StringComparison.OrdinalIgnoreCase)))
        ?? Colors[_list.Profiles.Count % Colors.Count];

    private static void CopyParts(string from, string to, ProfileParts parts)
    {
        var source = SecretStore.For(from);
        var target = SecretStore.For(to);
        foreach (var (part, names) in PartFiles)
        {
            if (!parts.HasFlag(part)) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(from, name);
                if (File.Exists(path))
                {
                    File.Copy(path, Path.Combine(to, name), overwrite: true);
                    source.CopyFileTo(name, target);
                }
                else if (Directory.Exists(path))
                {
                    CopyFolder(path, Path.Combine(to, name));
                }
            }
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Moves every persona entry of a data folder from before profiles into <c>profiles/main/</c>. The key of its
    /// secret store is saved for the new folder first (the Linux keyring keeps one per folder), so an interrupted move
    /// still finds it; the secrets of the shared <c>app.json</c> (the Google Drive sign-in) are copied to a new store
    /// in the data folder. True when something was moved.
    /// </summary>
    private static bool MoveIntoFirstProfile(string root)
    {
        var target = Path.Combine(root, FolderName, FirstId);
        var entries = Directory.EnumerateFileSystemEntries(root).Where(e => !SharedEntries.Contains(Path.GetFileName(e))).ToArray();
        if (entries.Length == 0) return false;
        Directory.CreateDirectory(target);

        if (File.Exists(Path.Combine(root, SecretStore.FileName)))
        {
            try
            {
                if (SecretStore.ProtectorFor(root).Load(root) is { } key) SecretStore.ProtectorFor(target).Save(target, key);
            }
            catch (Exception ex)
            {
                // The store could not have been read anyway; it says so (and asks to sign in again) when it is opened.
                SecretStore.Warn?.Invoke($"The key for stored sign-ins could not be moved to the profile ({ex.Message}).");
            }
        }

        foreach (var entry in entries) MoveInto(entry, Path.Combine(target, Path.GetFileName(entry)));

        // Stores opened before the move point at the old places.
        SecretStore.ResetCache();
        SecretStore.For(target).CopyFileTo("app.json", SecretStore.For(root));
        return true;
    }

    /// <summary>Moves a file or folder, merging into what is already there (the newer copy wins).</summary>
    private static void MoveInto(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            if (!Directory.Exists(destination))
            {
                Directory.Move(source, destination);
                return;
            }
            foreach (var child in Directory.EnumerateFileSystemEntries(source)) MoveInto(child, Path.Combine(destination, Path.GetFileName(child)));
            Directory.Delete(source);
            return;
        }
        File.Move(source, destination, overwrite: true);
    }

    private static ProfileList? Read(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<ProfileList>(File.ReadAllText(path), Json.Options);
            return list is { Profiles.Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            // Unreadable: the folders are still there, and Open lists them again.
            File.Copy(path, path + ".unreadable", overwrite: true);
            return new ProfileList();
        }
    }

    private void Save()
    {
        var path = Path.Combine(_dataFolder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_list, Json.Options));
        File.Move(temp, path, overwrite: true);
    }
}
