namespace Cuebot.Scripting.Tests;

/// <summary>A unique temporary folder deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cuebot-scripting-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process may still hold a file briefly; the OS cleans temp eventually.
        }
    }
}
