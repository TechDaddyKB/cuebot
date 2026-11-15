using Cuebot.Core.Events;
using Cuebot.Core.Games;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Steam;

namespace Cuebot.Integrations.Tests;

public sealed class SteamTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-steam-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Vdf_reads_nested_blocks_values_and_skips_comments_and_conditionals()
    {
        var vdf = SteamVdf.Parse("""
            // a comment
            "Registry"
            {
                "HKCU"
                {
                    "Software" { "Valve" { "Steam" { "apps" {
                        "1325860" { "Running" "1" "name" "VTube \"Studio\"" }
                        "553850" { "Running" "1" }
                        "730" { "Running" "0" }
                    } } } }
                }
            }
            "x" "y" [$WIN32]
            """);
        var apps = vdf.Path("Registry", "HKCU", "Software", "Valve", "Steam", "apps")!;
        Assert.Equal([1325860L, 553850L], SteamFiles.Running(apps).Order().Reverse());
        Assert.Equal("VTube \"Studio\"", apps.Block("1325860")!["NAME"]);
        Assert.Equal("y", vdf["x"]);
        Assert.Null(vdf.Path("Registry", "nope"));
        Assert.Empty(SteamVdf.Parse("\"broken\" { \"a\"").Blocks["broken"].Values);
    }

    [Fact]
    public void Names_come_from_manifests_in_every_library_and_processes_give_their_app_id()
    {
        var root = Path.Combine(_dir, "Steam");
        var second = Path.Combine(_dir, "Games");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(second, "steamapps"));
        // Steam escapes backslashes in its files ("C:\\Program Files (x86)\\Steam"), so Windows paths are written that way.
        static string Vdf(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Vdf(root)}\" }} \"1\" {{ \"path\" \"{Vdf(second)}\" }} }}");
        File.WriteAllText(Path.Combine(second, "steamapps", "appmanifest_553850.acf"), "\"AppState\" { \"appid\" \"553850\" \"name\" \"HELLDIVERS™ 2\" }");
        var proc = Path.Combine(_dir, "proc");
        foreach (var (pid, env) in new[] { ("100", "HOME=/x\0SteamAppId=553850\0"), ("101", "SteamAppId=1325860\0"), ("102", "PATH=/bin\0"), ("abc", "SteamAppId=9\0") })
        {
            Directory.CreateDirectory(Path.Combine(proc, pid));
            File.WriteAllText(Path.Combine(proc, pid, "environ"), env);
        }

        var steam = new SteamFiles([root], proc);
        Assert.Equal("HELLDIVERS™ 2", steam.NameOf(553850));
        Assert.Null(steam.NameOf(42));
        Assert.Equal([553850L, 1325860L], SteamFiles.ProcessAppIds(proc).Order());
    }

    [Fact]
    public void Streaming_tools_on_steam_are_never_the_game()
    {
        var excluded = SteamSettings.DefaultExcluded;
        Assert.True(SteamGameWatcher.IsExcluded(excluded, new SteamApp(1325860, "VTube Studio")));
        Assert.True(SteamGameWatcher.IsExcluded(excluded, new SteamApp(1, "vbridger")));
        Assert.True(SteamGameWatcher.IsExcluded(excluded, new SteamApp(2, "Proton 9.0")));
        Assert.True(SteamGameWatcher.IsExcluded(["730"], new SteamApp(730, "Counter-Strike 2")));
        Assert.False(SteamGameWatcher.IsExcluded(excluded, new SteamApp(553850, "HELLDIVERS™ 2")));
    }

    private sealed class FakeSteam : ISteamSource
    {
        public readonly Dictionary<long, string> Names = new() { [1325860] = "VTube Studio", [553850] = "Helldivers 2", [730] = "Counter-Strike 2" };
        public List<long> Running = [];
        public IReadOnlyCollection<long> RunningAppIds() => Running;
        public string? NameOf(long appId) => Names.GetValueOrDefault(appId);
    }

    [Fact]
    public void The_watcher_reports_the_game_not_the_tools_and_a_stop_only_after_two_missed_checks()
    {
        var log = new AppLog();
        using var settings = new JsonFile<IntegrationsSettings>(Path.Combine(_dir, "integrations.json"), log);
        var bus = new EventBus(log);
        var events = new List<(string Type, string Game)>();
        bus.Subscribe(e => events.Add((e.Type, (string)e.Args["game"]!)));
        var steam = new FakeSteam();
        var watcher = new SteamGameWatcher(steam, settings, bus, log);

        steam.Running = [1325860];   // VTube Studio first: not a game
        watcher.Poll();
        Assert.Empty(events);
        Assert.Null(watcher.Current);

        steam.Running = [1325860, 553850];
        watcher.Poll();
        Assert.Equal([(GameEvents.Started, "Helldivers 2")], events);
        watcher.Poll();
        Assert.Single(events);

        steam.Running = [1325860];
        watcher.Poll();   // missing once: could be a restart
        Assert.Single(events);
        steam.Running = [1325860, 553850];
        watcher.Poll();
        Assert.Single(events);

        steam.Running = [1325860, 553850, 730];   // a second game launched: it is the one being played now
        watcher.Poll();
        Assert.Equal((GameEvents.Started, "Counter-Strike 2"), events[^1]);
        Assert.Equal((GameEvents.Stopped, "Helldivers 2"), events[^2]);

        steam.Running = [1325860];
        watcher.Poll();
        watcher.Poll();
        Assert.Equal((GameEvents.Stopped, "Counter-Strike 2"), events[^1]);
        Assert.Null(watcher.Current);

        settings.Value.Steam.Enabled = false;
        steam.Running = [553850];
        watcher.Poll();
        Assert.Null(watcher.Current);
    }
}
