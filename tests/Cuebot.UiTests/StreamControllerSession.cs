using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Server;
using Cuebot.StreamDeck;
using static Cuebot.UiTests.DocsScreenshots;

namespace Cuebot.UiTests;

/// <summary>
/// A demo Cuebot for trying the StreamController plugin (streamcontroller/ in the repo) by hand and taking its
/// documentation screenshots. Opt-in: CUEBOT_SC_TOKEN=&lt;REST API token to use&gt; CUEBOT_SC_DIR=&lt;empty folder&gt;
/// (optional CUEBOT_SC_PORT, default 7475). It seeds the documentation's demo actions, turns on the REST API with that
/// token and then follows a script: every line appended to &lt;dir&gt;/commands.txt is one of
/// <c>command {json}</c> (a Stream Deck command, e.g. {"command":"setTitle","buttonId":"go-live","title":"LIVE"}),
/// <c>shot &lt;name&gt;</c> (saves the Stream Deck page as &lt;dir&gt;/&lt;name&gt;.png), <c>cards</c> (saves its cards) or
/// <c>quit</c>. Action runs, Stream Deck events and connected apps go to &lt;dir&gt;/events.log. Nothing is sent anywhere
/// else: the demo folder has no sign-ins.
/// </summary>
public sealed class StreamControllerSession
{
    [AvaloniaFact]
    public void Run_a_demo_cuebot_for_the_streamcontroller_plugin()
    {
        var token = Environment.GetEnvironmentVariable("CUEBOT_SC_TOKEN");
        var dir = Environment.GetEnvironmentVariable("CUEBOT_SC_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(token) || string.IsNullOrEmpty(dir), "Set CUEBOT_SC_TOKEN and CUEBOT_SC_DIR");
        var port = int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_SC_PORT"), out var p) ? p : 7475;
        Directory.CreateDirectory(dir!);
        var log = Path.Combine(dir!, "events.log");
        var commands = Path.Combine(dir!, "commands.txt");
        File.WriteAllText(commands, "");

        var data = Directory.CreateTempSubdirectory("cuebot-sc-session-").FullName;
        var profiles = Cuebot.Core.Profiles.ProfileCatalog.Open(data);
        var host = AppHost.Create(new AppPaths(profiles.ActiveFolder, data));
        var docs = OutputDir;
        OutputDir = dir!;
        try
        {
            DocsDemoData.Seed(host);
            var servers = host.Get<ServerManager>();
            servers.Settings.Api.Enabled = true;
            servers.Settings.Api.Port = port;
            servers.Settings.Api.Token = token!;
            Wait(servers.RestartAsync(ServerKind.Api));

            void Write(string line)
            {
                lock (log) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            host.Get<ActionEngine>().RunCompleted += r => Write($"run {r.ActionName} source={r.Source} ok={r.Success} {r.Error}");
            using var events = host.Get<EventBus>().Subscribe(e =>
            {
                if (e.Type.StartsWith("streamDeck.", StringComparison.Ordinal))
                    Write($"event {e.Type} {JsonSerializer.Serialize(e.Args.ToDictionary(k => k.Key, k => k.Value?.ToString()))}");
            });
            var hub = host.Get<StreamDeckHub>();
            hub.StatusChanged += () => Write("apps " + string.Join("; ", hub.Instances.Select(i =>
                $"{i.AppVersion} {i.Platform} plugin {i.PluginVersion}, {i.Devices.Count} devices, {i.Buttons.Count} keys")));

            var vm = new MainWindowViewModel(host, new Cuebot.App.Shell.DetachedShellHost(host.Settings, profiles));
            var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
            window.Show();
            Pump();
            vm.SelectedPage = vm.Pages.First(page => page is Cuebot.App.ViewModels.StreamDeck.StreamDeckViewModel);
            Settle();
            Write($"ready on port {servers.Api.Port}");

            var read = 0;
            var until = DateTime.Now.AddMinutes(30);
            while (DateTime.Now < until)
            {
                Settle();
                var lines = File.ReadAllLines(commands);
                for (; read < lines.Length; read++)
                {
                    var line = lines[read].Trim();
                    if (line == "quit") return;
                    if (line == "cards") SaveCards(window, []);
                    else if (line.StartsWith("shot ", StringComparison.Ordinal))
                    {
                        Settle();
                        Save(window, line[5..].Trim());
                    }
                    else if (line.StartsWith("command ", StringComparison.Ordinal))
                    {
                        var c = JsonNode.Parse(line[8..])!.AsObject();
                        var sent = Run(hub.SendCommandAsync(new StreamDeckCommand((string)c["command"]!, (string)c["buttonId"]!,
                            (string?)c["title"], (int?)c["state"], (string?)c["image"], (string?)c["value"], (double?)c["indicator"])));
                        Write($"command {line[8..]} reached {sent}");
                    }
                    Write("done " + line);
                }
            }
        }
        finally
        {
            OutputDir = docs;
            Wait(host.DisposeAsync().AsTask());
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    private static T Run<T>(Task<T> task)
    {
        while (!task.IsCompleted) Settle();
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) Settle();
        task.GetAwaiter().GetResult();
    }
}
