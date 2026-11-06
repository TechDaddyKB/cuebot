using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Portal;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;
using Cuebot.Server;
using Cuebot.Server.Api;

namespace Cuebot.UiTests;

/// <summary>
/// A manual check of the phone app's mod mode, skipped unless CUEBOT_PORTAL_PHONE_LINK names a file: the app's real
/// services behind the shared relay (CUEBOT_PORTAL_RELAY_URL for another one), with a command "!hello" and the REST API
/// on 127.0.0.1:7481 (an Android emulator reaches it as 10.0.2.2). It writes a Standard Mod invite link (their changes
/// need the streamer's OK) to that file and a cuebot://pair link for the emulator to the same name plus ".pair", and
/// serves until the link file is deleted (at most 30 minutes). Sign-in is stood in for as in PortalUiLiveTests: the
/// provider page fails, and the code "test" is handed to the app with
/// `adb shell am start -d "cuebot-portal://signin?code=test&amp;state=…"`.
/// </summary>
public sealed class PortalPhoneLiveDemo
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
            code == "test" ? Task.FromResult(new ProviderIdentity(provider, "phone-mod", "sam@example.com", "SamTheMod"))
                : throw new PortalAccessException("Only the test code works here.");
    }

    [Fact]
    public async Task Serve_the_portal_to_a_phone()
    {
        var linkFile = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_PHONE_LINK");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(linkFile), "Set CUEBOT_PORTAL_PHONE_LINK to a file to serve the portal to a phone");
        var data = Directory.CreateTempSubdirectory("cuebot-portal-phone-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await using var host = AppHost.Create(new AppPaths(data));
        try
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var servers = host.Get<ServerManager>();
            servers.Settings.Api = new ApiServerSettings { Enabled = true, Port = 7481, Token = token };
            await servers.RestartAsync(ServerKind.Api, timeout.Token);
            Assert.True(servers.Api.IsRunning, "The REST API did not start (port 7481 in use?)");
            host.Get<CommandStore>().Save(new CommandDefinition { Name = "!hello", Commands = ["!hello"] });

            var directory = host.Get<PortalDirectory>();
            directory.Enabled = true;
            directory.RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL") ?? "";
            var log = host.Get<AppLog>();
            await using var client = new PortalRelayClient(directory, log)
            {
                LinkHandler = new PortalSignInHandler(directory, new TestExchange(), host.Get<PortalApi>().HandleAsync, log),
            };
            await client.ConnectAsync(timeout.Token);
            await File.WriteAllTextAsync(linkFile + ".pair", $"cuebot://pair?v=1&host=10.0.2.2&api=7481&http=7481&token={token}&name=Phone%20demo", timeout.Token);
            await File.WriteAllTextAsync(linkFile, directory.CreateInvite(DefaultRoles.StandardModId, "Phone test").Link, timeout.Token);
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
            File.Delete(linkFile + ".pair");
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
