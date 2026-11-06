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

namespace Cuebot.UiTests;

/// <summary>
/// A manual check of the phone apps' mod mode and approvals, skipped unless CUEBOT_PORTAL_PHONE_LINK names a file: the
/// app's real services, with a command "!hello", the REST API on 127.0.0.1 (CUEBOT_PORTAL_PHONE_API_PORT, default 7495)
/// and the portal behind the shared relay (CUEBOT_PORTAL_RELAY_URL for another one). It writes a Standard Mod invite
/// link to that file (that role's changes wait for the streamer's OK) and a cuebot://pair link for the REST API to the
/// same name plus ".pair", for the phone that approves. The pairing link names 127.0.0.1, which an iOS simulator
/// reaches; set CUEBOT_PORTAL_PHONE_PAIR_HOST=10.0.2.2 for an Android emulator. Sign-in is stood in for as in
/// PortalUiLiveTests (the code "test"; a phone gets it as cuebot-portal://signin?code=test&amp;state=…, with
/// `xcrun simctl openurl` or `adb shell am start -d`). Serves until the link file is deleted (at most 30 minutes).
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
    public async Task Serve_the_portal_and_approvals_to_phones()
    {
        var linkFile = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_PHONE_LINK");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(linkFile), "Set CUEBOT_PORTAL_PHONE_LINK to a file to serve the portal to a phone");
        var data = Directory.CreateTempSubdirectory("cuebot-portal-phone-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await using var host = AppHost.Create(new AppPaths(data));
        try
        {
            var servers = host.Get<ServerManager>();
            var token = "cb_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var port = int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_PORTAL_PHONE_API_PORT"), out var p) ? p : 7495;
            var pairHost = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_PHONE_PAIR_HOST") is { Length: > 0 } h ? h : "127.0.0.1";
            (servers.Settings.Api.Enabled, servers.Settings.Api.Port, servers.Settings.Api.Token) = (true, port, token);
            await servers.RestartAsync(ServerKind.Api, timeout.Token);
            Assert.True(servers.Api?.IsRunning, $"The REST API did not start (port {port} in use?)");
            host.Get<CommandStore>().Save(new CommandDefinition { Name = "!hello", Commands = ["!hello"] });
            await File.WriteAllTextAsync(linkFile + ".pair", $"cuebot://pair?v=1&name=Portal%20demo&host={pairHost}&api={port}&http={port}&token={token}", timeout.Token);

            var directory = host.Get<PortalDirectory>();
            directory.Enabled = true;
            directory.RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL") ?? "";
            var log = host.Get<AppLog>();
            await using var client = new PortalRelayClient(directory, log)
            {
                LinkHandler = new PortalSignInHandler(directory, new TestExchange(), host.Get<PortalApi>().HandleAsync, log),
            };
            await client.ConnectAsync(timeout.Token);
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
