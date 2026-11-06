using Cuebot.Core.Logging;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Tests;

/// <summary>
/// Real mods joining by invite through the shared relay, with the built-in sign-in apps (build with the
/// CUEBOT_PORTAL_* variables set, docs/BUILT_IN_APPS.md). Runs only with CUEBOT_PORTAL_SIGNIN_LINKS set to a file:
/// a fresh Cuebot identity writes one invite link per line there, then waits (CUEBOT_PORTAL_SIGNIN_MINUTES, default
/// 15) until as many members have joined as CUEBOT_PORTAL_SIGNIN_COUNT (default 3) and checks each one's account.
/// </summary>
public sealed class PortalSignInLiveTests
{
    private static readonly string? LinksFile = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_SIGNIN_LINKS");

    [Fact]
    public async Task Real_accounts_join_by_invite()
    {
        if (string.IsNullOrWhiteSpace(LinksFile)) return;
        var count = int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_PORTAL_SIGNIN_COUNT"), out var c) ? c : 3;
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_PORTAL_SIGNIN_MINUTES"), out var m) ? m : 15;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        using var log = new AppLog();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var directory = new PortalDirectory(new PortalSettings { Enabled = true, RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL") ?? "" }, () => { });
        var exchange = new PortalIdentityExchange(http);
        Assert.NotEmpty(exchange.Describe());
        await using var client = new PortalRelayClient(directory, log) { LinkHandler = new PortalSignInHandler(directory, exchange, log: log) };
        await client.ConnectAsync(timeout.Token);

        var links = Enumerable.Range(0, count).Select(i => directory.CreateInvite(DefaultRoles.StandardModId, $"Live test {i + 1}").Link).ToList();
        await File.WriteAllLinesAsync(LinksFile, links, timeout.Token);

        while (directory.Members.Count < count) await Task.Delay(500, timeout.Token);
        foreach (var member in directory.Members)
        {
            Assert.NotEmpty(member.ProviderUserId);
            Assert.NotEmpty(member.Name);
            Assert.Single(member.Devices);
            await File.AppendAllTextAsync(LinksFile + ".joined", $"{member.Provider} {member.Name} ({member.Devices[0].Name})\n", timeout.Token);
        }
        Assert.Empty(directory.Invites);
    }
}
