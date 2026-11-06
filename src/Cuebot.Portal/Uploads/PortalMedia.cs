using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Portal.Changes;

namespace Cuebot.Portal.Uploads;

/// <summary>One cleaned upload in the library.</summary>
public sealed class PortalMediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the mod called it (shown only; the file has a random name).</summary>
    public string Name { get; set; } = "";

    public UploadKind Kind { get; set; }

    /// <summary>Relative to the library folder, e.g. "sounds/3f9c….mp3".</summary>
    public string File { get; set; } = "";

    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Animated { get; set; }
    public Guid MemberId { get; set; }
    public string MemberName { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>portal-media.json: the uploads that are live (pending ones live only in the change log).</summary>
public sealed class PortalMediaLibrary
{
    public List<PortalMediaItem> Items { get; set; } = [];
}

/// <summary>
/// The upload library: cleaned files under one folder with random names, one subfolder per kind, and the list of live
/// items. Files outlive their item while the change log still mentions them, so undo can bring one back; <see
/// cref="Sweep"/> deletes the rest.
/// </summary>
public sealed class PortalMediaStore(PortalMediaLibrary library, Action save, string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);

    public static string SubFolder(UploadKind kind) => kind switch
    {
        UploadKind.Sound => "sounds",
        UploadKind.Image => "images",
        _ => "videos",
    };

    public string KindFolder(UploadKind kind) => Path.Combine(Folder, SubFolder(kind));

    public IReadOnlyList<PortalMediaItem> Items
    {
        get { lock (library) return [.. library.Items]; }
    }

    public PortalMediaItem? Get(Guid id)
    {
        lock (library) return library.Items.FirstOrDefault(i => i.Id == id);
    }

    public string FullPath(PortalMediaItem item) => Path.Combine(Folder, item.File);

    /// <summary>The live item a full path points at (any letter case on Windows), or null.</summary>
    public PortalMediaItem? ByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception) { return null; }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        lock (library) return library.Items.FirstOrDefault(i => string.Equals(FullPath(i), full, comparison));
    }

    /// <summary>Moves a cleaned file into the library under a random name and returns its relative path.</summary>
    public string Adopt(string cleanedFile, UploadKind kind, string extension)
    {
        var dir = KindFolder(kind);
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "." + extension;
        var target = Path.Combine(dir, name);
        File.Move(cleanedFile, target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return Path.Combine(SubFolder(kind), name);
    }

    internal void Put(PortalMediaItem item)
    {
        lock (library)
        {
            library.Items.RemoveAll(i => i.Id == item.Id);
            library.Items.Add(item);
        }
        save();
    }

    internal void Remove(Guid id)
    {
        lock (library) library.Items.RemoveAll(i => i.Id == id);
        save();
    }

    /// <summary>Deletes library files that no live item and no remembered change refers to. Returns how many.</summary>
    public int Sweep(IEnumerable<PortalChange> changes)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (library) foreach (var i in library.Items) keep.Add(i.File.Replace('\\', '/'));
        foreach (var c in changes.Where(c => c.Area == UploadArea.AreaName))
            foreach (var node in new[] { c.Before, c.After })
                if ((string?)node?["file"] is { } f) keep.Add(f.Replace('\\', '/'));
        var removed = 0;
        foreach (var kind in Enum.GetValues<UploadKind>())
        {
            var dir = KindFolder(kind);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (keep.Contains(SubFolder(kind) + "/" + Path.GetFileName(file))) continue;
                try { File.Delete(file); removed++; }
                catch (IOException) { /* in use (playing): next sweep */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        return removed;
    }
}

/// <summary>Uploads as a change area: adding one is a create, removing one a delete, with approval and undo.</summary>
public sealed class UploadArea(PortalMediaStore store) : IPortalArea
{
    public const string AreaName = "upload";

    public string Name => AreaName;

    public JsonNode? Current(Guid id) => store.Get(id) is { } i ? PortalChanges.ToNode(i) : null;

    public string NameOf(JsonNode value) => (string?)value["name"] ?? "";

    public Task ApplyAsync(Guid id, JsonNode? value)
    {
        if (value is null) store.Remove(id);
        else
        {
            var item = PortalChanges.FromNode<PortalMediaItem>(value);
            item.Id = id;
            if (File.Exists(store.FullPath(item))) store.Put(item);
        }
        return Task.CompletedTask;
    }
}
