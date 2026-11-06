using System.Text.Json.Nodes;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Portal;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;
using Cuebot.Server;

namespace Cuebot.UiTests;

/// <summary>
/// A manual check of overlays from the mod portal, skipped unless CUEBOT_PORTAL_OVERLAYS_LINK names a file: the app's
/// real services (the portal as the app registers it, the overlay server on ports 7499 and 7500) serve an overlay with a text
/// layer, an image layer, a goal and an alert box behind the shared relay (CUEBOT_PORTAL_RELAY_URL for another one).
/// It writes an invite link to that file and the overlay's URL to the same name plus ".overlay", for a browser or OBS,
/// and serves until the link file is deleted (at most 30 minutes). Sign-in is stood in for as in PortalUiLiveTests (the
/// code "test"); `node portal/shots.mjs "$(cat link)" out overlays` drives it. CUEBOT_PORTAL_UI_ROLE: lead (default) or
/// tech. Uploads need an ffmpeg (CUEBOT_FFMPEG_DIR).
/// </summary>
public sealed class PortalOverlaysLiveDemo
{
    private sealed class TestExchange : IPortalIdentityExchange
    {
        public JsonArray Describe() =>
        [
            new JsonObject
            {
                ["id"] = "google", ["name"] = "Google", ["pkce"] = true,
                ["authorize"] = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id=test",
            },
        ];

        public Task<ProviderIdentity> ExchangeAsync(IdentityProvider provider, string code, string verifier, CancellationToken ct) =>
            code == "test" ? Task.FromResult(new ProviderIdentity(provider, "demo-mod", "sam@example.com", "SamTheMod"))
                : throw new PortalAccessException("Only the test code works here.");
    }

    [Fact]
    public async Task Serve_overlays_to_the_portal()
    {
        var linkFile = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_OVERLAYS_LINK");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(linkFile), "Set CUEBOT_PORTAL_OVERLAYS_LINK to a file to serve overlays to the portal");
        var data = Directory.CreateTempSubdirectory("cuebot-portal-overlays-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await using var host = AppHost.Create(new AppPaths(data));
        try
        {
            var servers = host.Get<ServerManager>();
            servers.Settings.Overlays = new OverlayServerSettings { Enabled = true, Port = 7499, WidgetPort = 7500 };
            await servers.RestartAsync(ServerKind.Overlays, timeout.Token);
            Assert.True(servers.Overlays?.IsRunning, "The overlay server did not start (ports 7499 and 7500 in use?)");

            var overlays = host.Get<OverlayStore>();
            var library = host.Get<WidgetLibrary>();
            var overlay = overlays.Create("Main", 1920, 1080);
            var text = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Text, overlay);
            (text.Name, text.X, text.Y) = ("Headline", 40, 900);
            text.Settings!["text"] = "Starting soon";
            var image = OverlayLayerTypes.NewStatic(OverlayLayerTypes.Image, overlay);
            (image.Name, image.X, image.Y, image.Width, image.Height) = ("Logo", 1600, 40, 280, 280);
            overlays.Update(overlay.Id, o => o.Layers.AddRange([text, image]));
            library.AddBuiltIn(overlay.Id, "goal", JsonNode.Parse("""{"title": "Follower goal", "goal": 50}""")!.AsObject());
            library.AddAlertBox(overlay.Id);
            overlay = overlays.Get(overlay.Id)!;
            await File.WriteAllTextAsync(linkFile + ".overlay", $"http://127.0.0.1:{servers.Overlays!.Port}{Cuebot.Server.Overlays.OverlayServer.PathFor(overlay)}", timeout.Token);

            var directory = host.Get<PortalDirectory>();
            directory.Enabled = true;
            directory.RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL") ?? "";
            var log = host.Get<AppLog>();
            await using var client = new PortalRelayClient(directory, log)
            {
                LinkHandler = new PortalSignInHandler(directory, new TestExchange(), host.Get<PortalApi>().HandleAsync, log),
            };
            await client.ConnectAsync(timeout.Token);
            var role = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_UI_ROLE") == "tech" ? DefaultRoles.TechLeadId : DefaultRoles.LeadModId;
            await File.WriteAllTextAsync(linkFile, directory.CreateInvite(role, "Overlay test").Link, timeout.Token);
            try
            {
                while (File.Exists(linkFile)) await Task.Delay(500, timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            File.Delete(linkFile + ".overlay");
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
