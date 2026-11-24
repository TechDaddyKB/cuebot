using System.Formats.Tar;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace Cuebot.Speech;

/// <summary>
/// Unpacks a voice package (<c>.tar.bz2</c> or <c>.tar.gz</c>, as sherpa-onnx and others publish them) into an empty
/// folder, refusing anything that could write outside it or fill the disk: absolute paths, <c>..</c>, symbolic and hard
/// links, devices, more than <see cref="MaxFiles"/> files or more than <see cref="MaxBytes"/> bytes unpacked.
/// </summary>
public static class VoiceArchive
{
    /// <summary>Unpacked size limit. The largest voice packages (multi-language Kokoro, high-quality VITS) are under 1 GB.</summary>
    public const long MaxBytes = 3L << 30;

    /// <summary>File count limit. sherpa-onnx packages carry espeak-ng-data and dictionaries: a few thousand files at most.</summary>
    public const int MaxFiles = 20_000;

    private static readonly string[] Extensions = [".tar.bz2", ".tbz2", ".tbz", ".tar.gz", ".tgz"];

    public static bool IsArchive(string path) => Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The archive's name without its extension ("vits-piper-en_US-glados-high").</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Extensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return extension is null ? Path.GetFileNameWithoutExtension(name) : name[..^extension.Length];
    }

    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct,
        long maxBytes = MaxBytes, int maxFiles = MaxFiles)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);
        await using var file = File.OpenRead(archive);
        await using Stream decompressed = IsBzip2(archive) ? new BZip2InputStream(file) { IsStreamOwner = false }
            : new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        await using var reader = new TarReader(decompressed);
        long total = 0;
        var files = 0;
        var buffer = new byte[1 << 16];
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.GlobalExtendedAttributes:
                    continue;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    throw new InvalidDataException($"The package contains a link ({entry.Name}). Cuebot does not unpack links.");
                case TarEntryType.Directory:
                    if (Target(root, entry.Name) is { } dir) Directory.CreateDirectory(dir);
                    continue;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    break;
                default:
                    throw new InvalidDataException($"The package contains something that is not a file or folder ({entry.Name}).");
            }
            if (Target(root, entry.Name) is not { } path) continue;
            if (++files > maxFiles) throw new InvalidDataException($"The package has more than {maxFiles} files.");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            if (entry.DataStream is not { } data) continue;
            int read;
            while ((read = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException($"The package unpacks to more than {maxBytes / (1 << 30)} GB.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsBzip2(string path) =>
        path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tbz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where an entry goes inside <paramref name="root"/>; null for "." entries. Throws for paths that would leave it.</summary>
    internal static string? Target(string root, string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(name))
            throw new InvalidDataException($"The package contains an absolute path ({name}).");
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Contains("..")) throw new InvalidDataException($"The package contains a path that leaves its folder ({name}).");
        if (parts.Count == 0) return null;
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"The package contains a path that leaves its folder ({name}).");
        return path;
    }
}
