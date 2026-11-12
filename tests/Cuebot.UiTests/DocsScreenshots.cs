using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.ViewModels.Servers;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Quotes;
using Cuebot.Core.Subathon;

namespace Cuebot.UiTests;

/// <summary>
/// The screenshots on the documentation site (website/docs/assets/screens): every page of the app, with example
/// actions, chat and activity so the pictures show something, plus the action editor's views. Run with
/// CUEBOT_WRITE_SCREENSHOTS=1 to rewrite them (skipped otherwise, so the normal test run never touches the docs).
/// </summary>
public sealed class DocsScreenshots : IAsyncDisposable
{
    private static readonly string OutputDir = Path.Combine(RepoRoot(), "website", "docs", "assets", "screens");

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-docs-shots-").FullName;
    private readonly AppHost _host;
    private readonly Cuebot.Core.Profiles.ProfileCatalog _profiles;

    public DocsScreenshots()
    {
        // Two personas, so the top bar's profile chip and Settings > Profiles show what a streamer with profiles sees.
        _profiles = Cuebot.Core.Profiles.ProfileCatalog.Open(_dataDir);
        _profiles.Rename(Cuebot.Core.Profiles.ProfileCatalog.FirstId, "Kitsune");
        _profiles.Create("Night Owl");
        _host = AppHost.Create(new AppPaths(_profiles.ActiveFolder, _dataDir));
    }

    [AvaloniaFact]
    public void Write_the_documentation_screenshots()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CUEBOT_WRITE_SCREENSHOTS") != "1",
            "Set CUEBOT_WRITE_SCREENSHOTS=1 to rewrite website/docs/assets/screens");
        Directory.CreateDirectory(OutputDir);
        DocsDemoData.Seed(_host);

        var vm = new MainWindowViewModel(_host, new Cuebot.App.Shell.DetachedShellHost(_host.Settings, _profiles));
        vm.Settings.Profiles.Selected = vm.Settings.Profiles.Rows[^1];   // Settings > Profiles shows the switch and edit buttons
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        DocsDemoData.Chat(_host);
        Settle();

        DocsDemoData.RunSome(_host);
        Settle();
        foreach (var page in vm.Pages)
        {
            vm.SelectedPage = page;
            Settle();
            Save(window, "page-" + Slug(page.Title));
        }

        // Every tab of every page, so each integration's page in the guides can show its own tab:
        // tab-<page>-<tab>.png. Only the page's own (top-level) tabs; tabs inside a tab are left to the list below.
        // Every card with a heading (one per app or service on the integration pages) also goes in its own picture,
        // card-<heading>.png, taken in a tall window so whole cards fit.
        var cards = new HashSet<string>();
        foreach (var page in vm.Pages)
        {
            vm.SelectedPage = page;
            Settle();
            // The page's own tabs only (not tabs inside them), and some headers change once picked ("Queue — empty").
            var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault()?.Items.OfType<TabItem>()
                .Where(t => t.Header is string && t.IsVisible).ToList() ?? [];
            if (tabs.Count == 0) SaveCards(window, cards);
            foreach (var tab in tabs)
            {
                var header = (string)tab.Header!;
                tab.IsSelected = true;
                Settle();
                Save(window, "tab-" + FileSlug(page.Title) + "-" + FileSlug(header));
                SaveCards(window, cards);
            }
            // Donations: one picture per service in its list.
            if (window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "ServiceList") is { } services)
                for (var i = 0; i < services.ItemCount; i++)
                {
                    services.SelectedIndex = i;
                    Settle();
                    var name = services.SelectedItem is Cuebot.App.ViewModels.Donations.DonationCardViewModel card ? card.Name : i.ToString();
                    Save(window, "donations-" + FileSlug(name));
                }
        }

        // Pages whose guide talks about one tab.
        foreach (var (page, tab, name) in new[]
                 {
                     ("Settings", "Profiles", "settings-profiles"), ("Settings", "Backups", "settings-backups"), ("Settings", "About", "settings-about"),
                     ("Support", "Updates", "support-updates"), ("Stream apps", "VTuber apps", "stream-apps-vtuber"),
                     ("Apps & devices", "Discord bot", "apps-devices-discord"), ("Twitch", "Rewards", "twitch-rewards"),
                     ("Twitch", "Polls & predictions", "twitch-polls"),
                     ("Twitch", "Channel", "twitch-channel"), ("Twitch", "Moderation", "twitch-moderation"), ("Twitch", "Settings", "twitch-settings"), ("Input", "Game controllers", "input-controllers"),
                     ("Webhooks", "Relay", "webhooks-relay"), ("History", "Trigger viewer", "history-trigger-viewer"),
                     ("Import / Export", "Export", "import-export-export"),
                 })
        {
            vm.SelectedPage = vm.Pages.Single(p => p.Title == page);
            Settle();
            SelectTab(window, tab);
            Settle();
            Save(window, name);
        }

        // Pairing the phone app: the QR code on the Servers page (a throwaway token of this temporary profile).
        var servers = vm.Pages.OfType<ServersViewModel>().Single();
        vm.SelectedPage = servers;
        servers.AllowLan = true;
        servers.ShowPairing = true;
        window.Height = 1000;
        Settle();
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "New deck token")).BringIntoView();
        Settle();
        Save(window, "servers-pairing");
        window.Height = 860;

        // The action editor: the list of steps, then the same action as a flowchart.
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Welcome raiders");
        actions.IsGraphView = false;
        Settle();
        Save(window, "actions-sub-actions");
        // The pickers that open from "+ Trigger" and "+ Sub-action", with a search typed in.
        actions.AddTriggerCommand.Execute(null);
        actions.Picker.Search = "twitch";
        Settle();
        Save(window, "actions-trigger-picker");
        actions.Picker.IsOpen = false;
        actions.AddStepCommand.Execute(null);
        actions.Picker.Search = "obs scene";
        Settle();
        Save(window, "actions-sub-action-picker");
        actions.Picker.IsOpen = false;
        Settle();

        var raid = actions.SelectedAction!;
        raid.Selected = raid.Steps.Items.First();
        Settle();
        Save(window, "actions-step-details");
        // A trigger's own settings, and the variables it gives the action.
        raid.Selected = raid.Triggers.First();
        Settle();
        Save(window, "actions-trigger-details");
        // A branch of an If: new steps go into it.
        raid.Selected = raid.Steps.Items.First().Branches.First();
        Settle();
        Save(window, "actions-branch-selected");
        // A step inside a branch: its settings and the variables it adds.
        raid.Selected = raid.Steps.Items.First().Branches.First().Steps.Items.Last();
        Settle();
        Save(window, "actions-step-in-branch");
        raid.Selected = null;
        // "Test trigger…": run the action as if a trigger fired, with made-up values.
        actions.TestTriggerCommand.Execute(null);
        Settle();
        Save(window, "actions-test-trigger");
        actions.Test.CancelCommand.Execute(null);
        Settle();
        // Switch, While and a random group, in the list.
        var color = actions.Actions.Single(a => a.Name == "Light colour (!color)");
        actions.SelectedAction = color;
        color.Selected = color.Steps.Items[0];
        Settle();
        Save(window, "actions-switch");
        color.Selected = color.Steps.Items[1];
        Settle();
        Save(window, "actions-while");
        color.Selected = color.Steps.Items[2];
        Settle();
        Save(window, "actions-random-group");
        color.Selected = null;
        // The trigger with its variables opened.
        actions.SelectedAction = raid;
        raid.Selected = raid.Triggers.First();
        Settle();
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(e => e.IsEffectivelyVisible)) expander.IsExpanded = true;
        Settle();
        Save(window, "actions-trigger-variables");
        raid.Selected = null;
        // Searching the actions list.
        var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Search actions");
        search.Text = "alert";
        Settle();
        Save(window, "actions-search");
        search.Text = "";
        Settle();
        // The narrow layout: triggers and sub-actions become tabs.
        window.Width = 980;
        Settle();
        Save(window, "actions-narrow");
        window.Width = 1360;
        Settle();
        // Random action: one top-level step runs, picked by weight.
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Random hype sound");
        actions.SelectedAction.Selected = actions.SelectedAction.Steps.Items.First();
        Settle();
        Save(window, "actions-random-action");
        actions.SelectedAction.Selected = null;
        actions.SelectedAction = raid;
        Settle();

        // The flowchart needs room: a wider window shows the whole raid action.
        window.Width = 1760;
        window.Height = 960;
        actions.IsGraphView = true;
        Settle();
        Save(window, "actions-flowchart");
        // Click a node: its settings open beside the flowchart.
        raid.Selected = raid.Steps.Items.First().Branches.First().Steps.Items.Last();
        Settle();
        Save(window, "actions-flowchart-selected");
        raid.Selected = null;
        Settle();
        SaveControl(window, window.GetVisualDescendants().OfType<Cuebot.App.Graph.FlowGraphControl>().Single(), "actions-flowchart-close");
        // Run at the same time: three lanes, joined before the chat message.
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Big entrance");
        actions.AutoArrangeCommand.Execute(null);
        Settle();
        SaveControl(window, window.GetVisualDescendants().OfType<Cuebot.App.Graph.FlowGraphControl>().Single(), "actions-flowchart-parallel");
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Light colour (!color)");
        actions.AutoArrangeCommand.Execute(null);
        Settle();
        SaveControl(window, window.GetVisualDescendants().OfType<Cuebot.App.Graph.FlowGraphControl>().Single(), "actions-flowchart-switch");
        window.Width = 1360;
        window.Height = 860;
        actions.IsGraphView = false;
        Settle();
        Save(window, "actions-parallel-list");

        var python = actions.Actions.Single(a => a.Name == "Death counter (Python)");
        actions.SelectedAction = python;
        python.Selected = python.Steps.Items.First();
        window.Height = 1000;
        Settle();
        Save(window, "actions-python-step");
        var csharp = actions.Actions.Single(a => a.Name == "Raid thank-you (C#)");
        actions.SelectedAction = csharp;
        csharp.Selected = csharp.Steps.Items.First();
        Settle();
        Save(window, "actions-csharp-step");

        // Export to code: the copy holds one code step with the whole action as a script.
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Big entrance");
        actions.ExportToCodeCommand.Execute("csharp");
        var exported = actions.SelectedAction!;
        exported.Selected = exported.Steps.Items.First();
        Settle();
        Save(window, "actions-exported-code");
        window.Height = 860;

        window.Close();
    }

    /// <summary>Opens the tab with this header on the current page (only that page is in the window). Fails when there is none, so the list stays right.</summary>
    private static void SelectTab(Window window, string header)
    {
        var tab = window.GetVisualDescendants().OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, header))
                  ?? throw new InvalidOperationException($"No tab \"{header}\" among: " + string.Join(", ",
                      window.GetVisualDescendants().OfType<TabItem>().Select(t => $"{t.Header}({t.IsEffectivelyVisible})")));
        tab.IsSelected = true;
    }

    internal static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets background work (feeds, images, lookups) land before a picture is taken.</summary>
    internal static void Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(15);
            Pump();
        }
    }

    internal static void Save(TopLevel window, string name)
    {
        Pump();
        TestApp.SaveFrame(window, Path.Combine(OutputDir, name + ".png"));
    }

    /// <summary>Saves just one control of the window, for a close-up.</summary>
    private static void SaveControl(Window window, Control control, string name)
    {
        Pump();
        var origin = Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(0, 0), window) ?? throw new InvalidOperationException("Not in the window");
        var area = new Avalonia.Rect(origin, control.Bounds.Size);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame");
        using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)area.Width, (int)area.Height));
        using (var context = target.CreateDrawingContext())
            context.DrawImage(frame, area, new Avalonia.Rect(area.Size));
        target.Save(Path.Combine(OutputDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    internal static string Slug(string title) =>
        string.Join('-', title.ToLowerInvariant().Split([' ', '&', '/', '.'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Saves every visible card that has a heading (class h2) as card-&lt;heading&gt;.png, once per run.</summary>
    private static void SaveCards(Window window, HashSet<string> done)
    {
        var height = window.Height;
        window.Height = 3600;
        Settle();
        foreach (var card in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible).ToList())
        {
            var heading = card.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("h2"))?.Text;
            if (string.IsNullOrWhiteSpace(heading) || !done.Add(FileSlug(heading))) continue;
            card.BringIntoView();
            Pump();
            if (card.Bounds.Width < 10 || card.Bounds.Height < 10) continue;
            SaveControl(window, card, "card-" + FileSlug(heading));
        }
        window.Height = height;
        Settle();
    }

    /// <summary>"OBS & Streamlabs Desktop" → "obs-streamlabs-desktop": letters and digits only, for file names.</summary>
    internal static string FileSlug(string title) =>
        string.Join('-', System.Text.RegularExpressions.Regex.Split(title.ToLowerInvariant(), "[^a-z0-9]+").Where(s => s.Length > 0));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuebot.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cuebot.slnx not found");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Example content for the documentation screenshots: what a small streamer's Cuebot might hold.</summary>
internal static class DocsDemoData
{
    /// <summary>Two mods, an open invite and a command waiting for approval, for the Mod portal page.</summary>
    private static void ModPortal(AppHost host)
    {
        var directory = host.Get<Cuebot.Portal.Members.PortalDirectory>();
        Cuebot.Portal.Members.PortalMember Join(Guid role, string id, string name)
        {
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var identity = new Cuebot.Portal.Members.ProviderIdentity(Cuebot.Portal.Members.IdentityProvider.Twitch, id, name.ToLowerInvariant(), name);
            return directory.RedeemInvite(directory.CreateInvite(role).Secret, identity,
                Cuebot.Portal.Crypto.PortalKeys.RawPublicKey(key.ExportParameters(false)), "Phone").Member;
        }
        Join(Cuebot.Portal.Members.DefaultRoles.LeadModId, "1001", "SamTheMod");
        var riley = Join(Cuebot.Portal.Members.DefaultRoles.StandardModId, "1002", "Riley");
        directory.CreateInvite(Cuebot.Portal.Members.DefaultRoles.StandardModId, "For Alex");
        var command = new Cuebot.Core.Commands.CommandDefinition { Name = "!socials", Commands = ["!socials"] };
        host.Get<Cuebot.Portal.Changes.PortalChanges>().SubmitAsync(riley, Cuebot.Portal.Changes.CommandArea.AreaName, command.Id,
            Cuebot.Portal.Changes.PortalChanges.ToNode(command)).GetAwaiter().GetResult();
    }

    public static void Seed(AppHost host)
    {
        var profile = host.Get<ProfileService>();
        profile.Profile.Actions.Add(RaidWelcome());
        profile.Profile.Actions.Add(BigEntrance());
        profile.Profile.Actions.Add(DeathCounter());
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Raid thank-you (C#)",
            Group = "Alerts",
            SubActions =
            [
                new SubActionDefinition
                {
                    Type = "code.csharp",
                    Parameters =
                    {
                        ["code"] = """
                            public class CPHInline : CPHInlineBase
                            {
                                public bool Execute()
                                {
                                    var user = args["user"]?.ToString() ?? "friend";
                                    CPH.SendMessage($"Thanks for the raid, {user}!");
                                    return true;
                                }
                            }
                            """,
                    },
                },
            ],
        });
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "BRB scene",
            Group = "Scenes",
            SubActions =
            [
                new SubActionDefinition { Type = "obs.setScene", Parameters = { ["sceneName"] = "Be Right Back" } },
                new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Back in 5 minutes!" } },
            ],
        });
        // Steps that work with nothing connected, so the example runs succeed.
        foreach (var name in new[] { "Stream starting", "Hydration break" })
            profile.Profile.Actions.Add(new ActionDefinition
            {
                Name = name,
                Group = "Stream",
                SubActions = [new SubActionDefinition { Type = "core.log", Parameters = { ["message"] = name } }],
            });
        profile.Profile.Actions.Add(ColorCommand());
        // Random action: one of these three plays, the airhorn twice as often.
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Random hype sound",
            Group = "Alerts",
            RandomAction = true,
            Triggers = [new TriggerDefinition { Type = "twitch.cheer" }],
            SubActions =
            [
                new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = "C:\\Sounds\\airhorn.mp3" }, Weight = 2 },
                new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = "C:\\Sounds\\drumroll.mp3" } },
                new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = "C:\\Sounds\\tada.mp3" } },
            ],
        });
        // Two overlays with a few widgets, so Overlays > Overlays & Widgets shows what a streamer's looks like.
        var overlays = host.Get<Cuebot.Core.Overlays.OverlayStore>();
        var widgets = host.Get<Cuebot.Core.Overlays.Widgets.WidgetLibrary>();
        var main = overlays.Create("Main", 1920, 1080);
        widgets.AddChatBox(main.Id);
        widgets.AddAlertBox(main.Id);
        widgets.AddBuiltIn(main.Id, "goal");
        widgets.AddBuiltIn(main.Id, "label");
        var vertical = overlays.Create("Vertical", 1080, 1920);
        widgets.AddChatBox(vertical.Id);
        profile.Profile.Timers.Add(new TimerDefinition { Name = "Social links", IntervalSeconds = 900 });
        profile.Profile.Timers.Add(new TimerDefinition { Name = "Hydration reminder", IntervalSeconds = 1800 });
        profile.NotifyChanged();
        ModPortal(host);

        var commands = host.Get<CommandStore>();
        foreach (var (name, group, trigger, cooldown) in new[]
                 {
                     ("Lurk", "Chat", "!lurk", 0.0), ("Discord link", "Chat", "!discord", 30), ("Shout-out", "Mods", "!so", 10),
                     ("Death counter", "Games", "!death", 5),
                 })
            commands.Save(new CommandDefinition { Name = name, Group = group, Commands = [trigger], GlobalCooldownSeconds = cooldown });

        var quotes = host.Get<QuoteStore>();
        quotes.Add("I meant to do that.", "PixelPanda", "id-pixelpanda", "Twitch", "Mario Kart");
        quotes.Add("Chat, we are NOT going back for the coin.", "Streamer", "", "Twitch", "Celeste");
        quotes.Add("Hydrate or diedrate.", "ModMaya", "id-modmaya", "Twitch", "Just Chatting");

        var globals = host.Get<GlobalVariables>();
        globals.Set("deaths", "7", persist: true);
        globals.Set("lastRaider", "NightOwlNora", persist: true);

        var subathon = host.Get<SubathonService>();
        var timer = subathon.AddTimer("Subathon");
        subathon.AddTime(timer.Name, 25 * 60, user: "CozyCass", reason: "Sub");
        var goal = subathon.AddGoal("Sub goal");
        subathon.AddPoints(goal.Name, 14, user: "PixelPanda", reason: "Sub");
    }

    /// <summary>A Python step that counts deaths and says the new total in chat.</summary>
    public static ActionDefinition DeathCounter() => new()
    {
        Name = "Death counter (Python)",
        Group = "Games",
        Triggers = [new TriggerDefinition { Type = "twitch.command", Settings = { ["command"] = "!death" } }],
        SubActions =
        [
            new SubActionDefinition
            {
                Type = "code.python",
                Parameters =
                {
                    ["code"] = """
                        deaths = int(cb.get_global("deaths", 0)) + 1
                        cb.set_global("deaths", deaths)
                        cb.twitch_send_message(f"Death number {deaths}. Chat, it was lag.")
                        """,
                },
            },
        ],
    };

    /// <summary>Runs a few actions so the dashboard and history show runs.</summary>
    public static void RunSome(AppHost host)
    {
        var engine = host.Get<ActionEngine>();
        var profile = host.Get<ProfileService>();
        foreach (var name in new[] { "Stream starting", "Hydration break", "Stream starting" })
            if (profile.Snapshot.FindAction(name) is { } action) _ = engine.RunAsync(action);
    }

    /// <summary>A raid action with an If: big raids get a scene change and a sound, small ones a chat thank-you.</summary>
    public static ActionDefinition RaidWelcome()
    {
        var check = new SubActionDefinition
        {
            Type = "core.if",
            Parameters = { ["left"] = "%viewers%", ["operator"] = "greater than", ["right"] = "10" },
        };
        check.Branch("True").AddRange(
        [
            new SubActionDefinition { Type = "obs.setScene", Parameters = { ["sceneName"] = "Raid Party" } },
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "RAID! Welcome %viewers% friends from %user%!" } },
        ]);
        check.Branch("False").Add(
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Thanks for the raid, %user%!" } });
        return new ActionDefinition
        {
            Name = "Welcome raiders",
            Group = "Alerts",
            Triggers = [new TriggerDefinition { Type = "twitch.raid" }],
            SubActions =
            [
                check,
                Celebration(),
            ],
        };
    }

    /// <summary>!color: a Switch on the first word, a While that cheers three times, and a group that picks one random reply.</summary>
    private static ActionDefinition ColorCommand()
    {
        var pick = new SubActionDefinition { Type = "core.switch", Parameters = { ["input"] = "%input0%", ["operator"] = "equals", ["cases"] = "red\nblue|navy\ngreen" } };
        pick.Branch("red").Add(new SubActionDefinition { Type = "elgato.controlCenter.setColor", Parameters = { ["hue"] = "0", ["saturation"] = "100" } });
        pick.Branch("blue|navy").Add(new SubActionDefinition { Type = "elgato.controlCenter.setColor", Parameters = { ["hue"] = "240", ["saturation"] = "100" } });
        pick.Branch("green").Add(new SubActionDefinition { Type = "elgato.controlCenter.setColor", Parameters = { ["hue"] = "120", ["saturation"] = "100" } });
        pick.Branch("Default").Add(new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Try !color red, blue or green" } });
        var cheer = new SubActionDefinition { Type = "core.while", Parameters = { ["left"] = "%loopIteration%", ["operator"] = "less than", ["right"] = "3", ["maxIterations"] = "100" } };
        cheer.Branch("Body").Add(new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Hype %loopIteration%!" } });
        var replies = new SubActionDefinition { Type = "core.group", Parameters = { ["name"] = "One random reply", ["random"] = "true" } };
        replies.Branch("Steps").AddRange(
        [
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Nice pick, %user%!" }, Weight = 3 },
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Ooh, fancy." } },
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "The lights obey you, %user%." } },
        ]);
        return new ActionDefinition
        {
            Name = "Light colour (!color)",
            Group = "Examples",
            Triggers = [new TriggerDefinition { Type = "twitch.command", Settings = { ["command"] = "!color" } }],
            SubActions = [pick, cheer, replies],
        };
    }

    /// <summary>A follow alert that does three things at once with "Run at the same time", then thanks the follower.</summary>
    private static ActionDefinition BigEntrance()
    {
        var together = new SubActionDefinition { Type = "core.parallel", Parameters = { ["lanes"] = "3" } };
        together.Branch("Lane 1").Add(
            new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = "C:\\Sounds\\fanfare.mp3", ["wait"] = "true" } });
        together.Branch("Lane 2").AddRange(
        [
            new SubActionDefinition { Type = "vtubestudio.triggerHotkey", Parameters = { ["hotkey"] = "Wave" } },
            new SubActionDefinition { Type = "vtubestudio.moveModel", Parameters = { ["time"] = "1", ["positionX"] = "0.4", ["positionY"] = "-0.2" } },
        ]);
        together.Branch("Lane 3").AddRange(
        [
            new SubActionDefinition { Type = "obs.setSourceVisibility", Parameters = { ["scene"] = "Live", ["source"] = "Confetti", ["state"] = "Visible" } },
            new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "4000" } },
            new SubActionDefinition { Type = "obs.setSourceVisibility", Parameters = { ["scene"] = "Live", ["source"] = "Confetti", ["state"] = "Hidden" } },
        ]);
        return new ActionDefinition
        {
            Name = "Big entrance",
            Group = "Alerts",
            Triggers = [new TriggerDefinition { Type = "twitch.follow" }],
            SubActions =
            [
                together,
                new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Welcome to the crew, %user%!" } },
            ],
        };
    }

    /// <summary>A coloured group of steps: wait, then say the raid out loud.</summary>
    private static SubActionDefinition Celebration()
    {
        var group = new SubActionDefinition
        {
            Type = "core.group",
            Parameters = { ["name"] = "Celebrate", ["random"] = "false", ["color"] = "#7B4FD6" },
        };
        group.Branch("Steps").AddRange(
        [
            new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "3000" } },
            new SubActionDefinition { Type = "speech.speak", Parameters = { ["text"] = "%user% raided with %viewers% viewers" } },
        ]);
        return group;
    }

    /// <summary>Chat and activity from viewers, published like the Twitch module does.</summary>
    public static void Chat(AppHost host)
    {
        var bus = host.Get<EventBus>();
        (string User, string Text)[] lines =
        [
            ("PixelPanda", "hi everyone! first time catching the stream live"),
            ("NightOwlNora", "!hello"),
            ("ModMaya", "remember to stay hydrated chat"),
            ("SpeedyGonzo", "that jump was insane"),
            ("CozyCass", "is there a giveaway today?"),
        ];
        foreach (var (user, text) in lines)
            bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?>
            {
                ["messageId"] = Guid.NewGuid().ToString("N"), ["user"] = user, ["userName"] = user.ToLowerInvariant(),
                ["userId"] = "id-" + user.ToLowerInvariant(), ["message"] = text,
            }));
    }
}
