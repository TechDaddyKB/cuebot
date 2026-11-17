using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Decks;
using Cuebot.Core.Events;
using Cuebot.Server;
using Cuebot.Server.Api;

namespace Cuebot.UiTests;

/// <summary>
/// A Cuebot with example data for the companion apps' documentation screenshots, skipped unless CUEBOT_COMPANION_DEMO_LINK
/// names a file. The app's real services serve the API on 127.0.0.1:17475 and the deck server on 127.0.0.1:17474 (so
/// nothing is reachable from the network; a USB phone reaches them with <c>adb reverse tcp:17475 tcp:17475</c> and
/// <c>adb reverse tcp:17474 tcp:17474</c>). It writes the pairing link to that file, fills in the example actions,
/// profiles, a deck, chat on four platforms and stream events, then keeps chatting and serves until the file is deleted
/// (at most 30 minutes). Nothing from a real Cuebot is used, so the pictures show no real names.
/// </summary>
public sealed class CompanionLiveDemo
{
    public const int ApiPort = 17475;
    public const int HttpPort = 17474;

    [Fact]
    public async Task Serve_example_data_to_the_companion_app()
    {
        var linkFile = Environment.GetEnvironmentVariable("CUEBOT_COMPANION_DEMO_LINK");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(linkFile), "Set CUEBOT_COMPANION_DEMO_LINK to a file to serve the companion demo");
        var data = Directory.CreateTempSubdirectory("cuebot-companion-demo-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        // Two personas, so the Dashboard shows the profile switcher.
        var profiles = Cuebot.Core.Profiles.ProfileCatalog.Open(data);
        profiles.Rename(Cuebot.Core.Profiles.ProfileCatalog.FirstId, "Kitsune");
        profiles.Create("Night Owl");
        await using var host = AppHost.Create(new AppPaths(profiles.ActiveFolder, data));
        try
        {
            DocsDemoData.Seed(host);
            Deck(host);

            var servers = host.Get<ServerManager>();
            var token = "demo-" + Guid.NewGuid().ToString("N");
            var deckToken = "deck-" + Guid.NewGuid().ToString("N");
            servers.Settings.Api.Enabled = true;
            servers.Settings.Api.Address = "127.0.0.1";
            servers.Settings.Api.Port = ApiPort;
            servers.Settings.Api.Token = token;
            servers.Settings.Http.Enabled = true;
            servers.Settings.Http.Address = "127.0.0.1";
            servers.Settings.Http.Port = HttpPort;
            servers.Settings.Http.DeckToken = deckToken;
            await servers.RestartAsync(ServerKind.Api, timeout.Token);
            await servers.RestartAsync(ServerKind.Http, timeout.Token);
            Assert.True(servers.Api?.IsRunning, $"The API server did not start (port {ApiPort} in use?)");

            var bus = host.Get<EventBus>();
            Events(bus);
            var link = PairingLink.Build("Demo Cuebot", "127.0.0.1", ApiPort, HttpPort, token, deckToken, "demo-" + Guid.NewGuid().ToString("N")[..8]);
            await File.WriteAllTextAsync(linkFile!, link, timeout.Token);

            // Chat keeps coming, so the chat page and the events card look like a stream in progress.
            var i = 0;
            while (File.Exists(linkFile) && !timeout.IsCancellationRequested)
            {
                var (platform, user, text) = Lines[i % Lines.Length];
                Chat(bus, platform, user, text);
                if (i % 7 == 6) Events(bus, oneOnly: i / 7);
                i++;
                await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token).ContinueWith(_ => { });
            }
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly (string Platform, string User, string Text)[] Lines =
    [
        ("twitch", "PixelPanda", "hi everyone! first time catching the stream live"),
        ("youtube", "Nora Nightowl", "hello from YouTube 👋"),
        ("kick", "speedy_gonzo", "that jump was insane"),
        ("twitch", "ModMaya", "remember to stay hydrated chat"),
        ("rumble", "CozyCass", "is there a giveaway today?"),
        ("twitch", "LurkingLuna", "!lurk"),
        ("youtube", "Tomás Tinker", "what game is next?"),
        ("kick", "pixel_panda", "same panda, different platform 😄"),
        ("twitch", "SpeedyGonzo", "GG"),
        ("rumble", "BlueJay", "first time here, love the overlay"),
    ];

    /// <summary>Publishes the demo's next chat line (for other demos too).</summary>
    internal static void SayNext(EventBus bus, int index)
    {
        var (platform, user, text) = Lines[index % Lines.Length];
        Chat(bus, platform, user, text);
    }

    /// <summary>A chat line as the platform's module publishes it ("twitch.chatMessage"...), so the chat hub takes it in.</summary>
    private static void Chat(EventBus bus, string platform, string user, string text) =>
        bus.Publish(new BotEvent($"{platform}.chatMessage", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["messageId"] = Guid.NewGuid().ToString("N"), ["user"] = user, ["userName"] = user.ToLowerInvariant().Replace(' ', '_'),
            ["userId"] = "id-" + user.ToLowerInvariant().Replace(' ', '-'), ["message"] = text, ["isModerator"] = user == "ModMaya",
            ["isSubscriber"] = platform == "twitch",
        }));

    private static readonly (string Platform, string Kind, string User)[] Feed =
    [
        ("twitch", "follow", "BlueJay"),
        ("youtube", "sub", "Nora Nightowl"),
        ("twitch", "cheer", "PixelPanda"),
        ("kick", "gift", "speedy_gonzo"),
        ("rumble", "tip", "CozyCass"),
        ("youtube", "superchat", "Tomás Tinker"),
        ("twitch", "raid", "NightOwlNora"),
        ("kick", "kicks", "pixel_panda"),
        ("twitch", "resub", "LurkingLuna"),
    ];

    /// <summary>Stream events as the platform modules publish them (the overlay emulator's samples, with the demo's names).</summary>
    private static void Events(EventBus bus, int? oneOnly = null)
    {
        var items = oneOnly is { } n ? [Feed[n % Feed.Length]] : Feed;
        foreach (var (platform, kind, user) in items)
        {
            if (Cuebot.Core.Overlays.Events.OverlayEmulator.Create(platform, kind) is not { } sample) continue;
            var args = new Dictionary<string, object?>(sample.Args, StringComparer.OrdinalIgnoreCase)
            {
                ["user"] = user, ["userName"] = user.ToLowerInvariant().Replace(' ', '_'), ["userId"] = "id-" + user.ToLowerInvariant().Replace(' ', '-'),
            };
            bus.Publish(new BotEvent(sample.Type, args));
        }
    }

    /// <summary>A small stream deck with the demo's actions.</summary>
    private static void Deck(AppHost host)
    {
        DeckItem Button(string name, int row, int column, string action, string icon, string color) => new()
        {
            Name = name, Row = row, Column = column, Action = action,
            States = [new DeckItemState { Title = name, Icon = icon, Background = color }],
        };
        host.Get<DeckService>().Save(new DeckDefinition
        {
            Name = "Stream deck",
            Rows = 2,
            Columns = 4,
            Pages =
            [
                new DeckPage
                {
                    Items =
                    [
                        Button("Starting", 0, 0, "Stream starting", "play", "#2e7d32"),
                        Button("BRB", 0, 1, "BRB scene", "coffee", "#6a1b9a"),
                        Button("Hydrate", 0, 2, "Hydration break", "droplet", "#0277bd"),
                        Button("Hype!", 0, 3, "Random hype sound", "zap", "#ef6c00"),
                        Button("Death +1", 1, 0, "Death counter (Python)", "skull", "#c62828"),
                        Button("Raid thanks", 1, 1, "Raid thank-you (C#)", "heart", "#ad1457"),
                    ],
                },
            ],
        });
    }
}
