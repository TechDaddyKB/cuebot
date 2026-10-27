using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Cuebot.Scripting.Tests;

public sealed class TarGzExtractorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string CreateArchive(params TarEntry[] entries)
    {
        var path = Path.Combine(_temp.Path, "archive.tar.gz");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var entry in entries) writer.WriteEntry(entry);
        return path;
    }

    private static PaxTarEntry FileEntry(string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };

    [Fact]
    public void Extracts_directories_files_and_links()
    {
        var archive = CreateArchive(
            new PaxTarEntry(TarEntryType.Directory, "python/"),
            FileEntry("python/bin/python3.13", "interpreter"),
            FileEntry("python/lib/os.py", "module"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "python/bin/python3") { LinkName = "python3.13" });
        var destination = Path.Combine(_temp.Path, "out");

        TarGzExtractor.Extract(archive, destination);

        Assert.Equal("module", File.ReadAllText(Path.Combine(destination, "python", "lib", "os.py")));
        var link = Path.Combine(destination, "python", "bin", "python3");
        Assert.Equal("interpreter", File.ReadAllText(link));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("python3.13", new FileInfo(link).LinkTarget);
            Assert.True(File.GetUnixFileMode(Path.Combine(destination, "python", "bin", "python3.13")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(destination, "python", "lib", "os.py")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("python/../../evil.txt")]
    public void Rejects_entries_outside_the_destination(string name)
    {
        var archive = CreateArchive(FileEntry(name, "x"));

        Assert.Throws<InvalidDataException>(() => TarGzExtractor.Extract(archive, Path.Combine(_temp.Path, "out")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.txt")));
    }

    [Fact]
    public void Rejects_links_pointing_outside_the_destination()
    {
        var archive = CreateArchive(new PaxTarEntry(TarEntryType.SymbolicLink, "python/escape") { LinkName = "../../outside" });

        Assert.Throws<InvalidDataException>(() => TarGzExtractor.Extract(archive, Path.Combine(_temp.Path, "out")));
    }

    public void Dispose() => _temp.Dispose();
}
