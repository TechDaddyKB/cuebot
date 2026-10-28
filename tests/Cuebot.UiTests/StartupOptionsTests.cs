using Cuebot.App.Shell;

namespace Cuebot.UiTests;

public class StartupOptionsTests
{
    [Fact]
    public void No_arguments_means_defaults()
    {
        var o = StartupOptions.Parse([]);
        Assert.False(o.Headless);
        Assert.False(o.Minimized);
        Assert.False(o.ResetWindow);
        Assert.Null(o.DataFolder);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parses_every_switch_case_insensitively()
    {
        var o = StartupOptions.Parse(["--Headless", "--minimized", "--RESET-WINDOW", "--backend=x11"]);
        Assert.True(o.Headless);
        Assert.True(o.Minimized);
        Assert.True(o.ResetWindow);
        Assert.Equal("x11", o.Backend);
    }

    [Fact]
    public void Accepts_streamerbot_style_slash_switches()
    {
        var o = StartupOptions.Parse(["/headless", "/resetwindow"]);
        Assert.True(o.Headless);
        Assert.True(o.ResetWindow);
    }

    [Fact]
    public void Data_folder_takes_a_separate_or_inline_value_and_is_made_absolute()
    {
        var spaced = StartupOptions.Parse(["--data", "some/folder"]);
        Assert.Equal(Path.GetFullPath("some/folder"), spaced.DataFolder);

        var inline = StartupOptions.Parse(["--data=other"]);
        Assert.Equal(Path.GetFullPath("other"), inline.DataFolder);

        // An absolute Unix-style path after --data is a value, not a "/switch".
        var root = OperatingSystem.IsWindows() ? @"C:\data" : "/data";
        Assert.Equal(Path.GetFullPath(root), StartupOptions.Parse(["--data", root]).DataFolder);
    }

    [Fact]
    public void Data_without_a_value_is_an_error()
    {
        Assert.NotEmpty(StartupOptions.Parse(["--data"]).Errors);
        Assert.NotEmpty(StartupOptions.Parse(["--data", "--headless"]).Errors);
    }

    [Fact]
    public void Data_switch_overrides_the_environment_variable()
    {
        Func<string, string?> env = key => key == "CUEBOT_DATA" ? "from-env" : null;
        Assert.Equal(Path.GetFullPath("from-arg"), StartupOptions.Parse(["--data", "from-arg"]).ResolveDataFolder(env));
        Assert.Equal(Path.GetFullPath("from-env"), StartupOptions.Parse([]).ResolveDataFolder(env));
        Assert.Null(StartupOptions.Parse([]).ResolveDataFolder(_ => null));
    }

    [Fact]
    public void Unknown_arguments_are_kept_not_rejected()
    {
        var o = StartupOptions.Parse(["--something", "file.txt"]);
        Assert.Equal(["--something", "file.txt"], o.Unknown);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Help_is_recognised()
    {
        Assert.True(StartupOptions.Parse(["--help"]).ShowHelp);
        Assert.True(StartupOptions.Parse(["-h"]).ShowHelp);
        Assert.Contains("--headless", StartupOptions.HelpText);
        Assert.Contains("--shutdown", StartupOptions.HelpText);
        Assert.True(StartupOptions.Parse(["/shutdown"]).Shutdown);
        Assert.True(StartupOptions.Parse(["--Shutdown"]).Shutdown);
    }
}
