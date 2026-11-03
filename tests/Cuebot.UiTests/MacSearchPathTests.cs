using Cuebot.App.Platform;

namespace Cuebot.UiTests;

public class MacSearchPathTests
{
    [Fact]
    public void Finder_path_gains_homebrew_and_user_tool_folders_once()
    {
        Assert.Equal("/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin:/usr/local/bin:/Users/me/.local/bin",
            MacSearchPath.Extend("/usr/bin:/bin:/usr/sbin:/sbin", "/Users/me"));
        Assert.Equal("/opt/homebrew/bin:/usr/bin:/usr/local/bin:/Users/me/.local/bin",
            MacSearchPath.Extend("/opt/homebrew/bin:/usr/bin", "/Users/me"));
        Assert.Equal("/opt/homebrew/bin:/usr/local/bin:/Users/me/.local/bin", MacSearchPath.Extend(null, "/Users/me"));
    }
}
