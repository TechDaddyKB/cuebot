using Cuebot.Core;

namespace Cuebot.Tests.Storage;

public class AppPathsTests
{
    private static string Folder(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.ApplicationData => "/home/me/.config",
        Environment.SpecialFolder.LocalApplicationData => "/Users/me/Library/Application Support",
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    [Fact]
    public void Windows_and_linux_keep_the_roaming_application_data_folder()
    {
        Assert.Equal(Path.Combine("/home/me/.config", "Cuebot"), AppPaths.DefaultRoot(isMacOS: false, Folder));
    }

    [Fact]
    public void Macos_uses_application_support()
    {
        Assert.Equal(Path.Combine("/Users/me/Library/Application Support", "Cuebot"), AppPaths.DefaultRoot(isMacOS: true, Folder));
    }

    [Fact]
    public void An_explicit_folder_wins_over_the_default()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "data");
        var paths = new AppPaths(root);
        Assert.Equal(root, paths.Root);
        Assert.True(Directory.Exists(paths.Logs));
    }
}
