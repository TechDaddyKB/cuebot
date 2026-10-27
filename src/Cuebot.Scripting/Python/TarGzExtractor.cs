using System.Formats.Tar;
using System.IO.Compression;

namespace Cuebot.Scripting;

/// <summary>
/// Extracts .tar.gz archives safely: entries may not escape the destination, symbolic links are recreated on Linux
/// (copied on Windows), and files under a "bin" folder are made executable on Linux.
/// </summary>
internal static class TarGzExtractor
{
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public static void Extract(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var links = new List<(string Path, string Target, TarEntryType Type)>();

        using (var file = File.OpenRead(archivePath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                var path = ResolveInside(root, entry.Name);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(path);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        entry.ExtractToFile(path, overwrite: true);
                        ApplyMode(root, path, entry.Mode);
                        break;
                    case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                        links.Add((path, entry.LinkName, entry.EntryType));
                        break;
                }
            }
        }

        foreach (var link in links) CreateLink(root, link.Path, link.Target, link.Type);
    }

    /// <summary>Combines an archive-relative name with the root, rejecting names that resolve outside it.</summary>
    internal static string ResolveInside(string root, string name)
    {
        var full = Path.GetFullPath(Path.Combine(root, name));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSeparator, comparison) && !string.Equals(full, root, comparison))
            throw new InvalidDataException($"Archive entry '{name}' points outside the extraction folder");
        return full;
    }

    private static void CreateLink(string root, string path, string target, TarEntryType type)
    {
        // Hard link names are archive-relative; symbolic link targets are relative to the link's folder.
        var resolvedTarget = type == TarEntryType.HardLink
            ? ResolveInside(root, target)
            : ResolveInside(root, Path.Combine(Path.GetDirectoryName(path)!, target));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        if (type == TarEntryType.SymbolicLink && !OperatingSystem.IsWindows())
            File.CreateSymbolicLink(path, target);
        else if (File.Exists(resolvedTarget))
            File.Copy(resolvedTarget, path);
    }

    private static void ApplyMode(string root, string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var inBin = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).SkipLast(1).Contains("bin");
        File.SetUnixFileMode(path, inBin ? mode | ExecuteBits : mode);
    }
}
