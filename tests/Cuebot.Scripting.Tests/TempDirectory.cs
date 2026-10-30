namespace Cuebot.Scripting.Tests;

/// <summary>A unique temporary folder deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cuebot-scripting-").FullName;

    public void Dispose()
    {
        // On Windows a process that is still exiting (a killed Python worker and its child) keeps its files locked
        // for a moment, so retry briefly before leaving the folder for the OS to clean up.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
