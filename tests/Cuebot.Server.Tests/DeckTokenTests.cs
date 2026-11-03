using System.Net;
using Cuebot.Server.Http;

namespace Cuebot.Server.Tests;

/// <summary>The deck token: other devices need it once set (made when "Allow LAN" is on); this computer never does.</summary>
public sealed class DeckTokenTests
{
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.50");

    [Fact]
    public void Without_a_token_every_device_may_use_decks()
    {
        Assert.True(DeckRoutes.IsAuthorized("", Phone, "", ""));
        Assert.True(DeckRoutes.IsAuthorized(null, Phone, null, null));
    }

    [Fact]
    public void Other_devices_need_the_token_in_the_header_or_the_address()
    {
        Assert.False(DeckRoutes.IsAuthorized("cb_secret", Phone, "", ""));
        Assert.False(DeckRoutes.IsAuthorized("cb_secret", Phone, "cb_wrong", ""));
        Assert.False(DeckRoutes.IsAuthorized("cb_secret", Phone, "", "cb_wrong"));
        Assert.False(DeckRoutes.IsAuthorized("cb_secret", null, "", ""));
        Assert.True(DeckRoutes.IsAuthorized("cb_secret", Phone, "cb_secret", ""));
        Assert.True(DeckRoutes.IsAuthorized("cb_secret", Phone, "", "cb_secret"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void This_computer_needs_no_token(string address)
    {
        Assert.True(DeckRoutes.IsAuthorized("cb_secret", IPAddress.Parse(address), "", ""));
    }

    [Fact]
    public async Task Allowing_the_lan_makes_a_deck_token_once()
    {
        await using var bot = new TestBot(new ServerSettings { AllowLan = true });
        Assert.Equal("", bot.Manager.Settings.Http.DeckToken);

        await bot.Manager.StartAsync();
        var token = bot.Manager.Settings.Http.DeckToken;

        Assert.StartsWith("cb_", token);
        Assert.Equal(token, bot.Manager.EnsureDeckToken());
        Assert.NotEqual(token, bot.Manager.RegenerateDeckToken());
    }

    [Fact]
    public async Task Local_only_setups_get_no_deck_token()
    {
        await using var bot = new TestBot(new ServerSettings());
        await bot.Manager.StartAsync();
        Assert.Equal("", bot.Manager.Settings.Http.DeckToken);
    }
}

/// <summary>The phone pairing link (cuebot://pair) and what the Servers page says still stops a phone from connecting.</summary>
public sealed class PairingTests
{
    [Fact]
    public void Link_has_every_field_escaped()
    {
        var link = Cuebot.Server.Api.PairingLink.Build("Kris's PC", "192.168.1.20", 7475, 7474, "cb_a+b", "cb_deck", "inst-1");

        Assert.Equal("cuebot://pair?v=1&name=Kris%27s%20PC&host=192.168.1.20&api=7475&http=7474&token=cb_a%2Bb&deckToken=cb_deck&id=inst-1", link);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        Assert.Equal("Kris's PC", query["name"]);
        Assert.Equal("cb_a+b", query["token"]);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("8.8.8.8", false)]
    public void Private_ranges_come_first(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, Cuebot.Server.Api.PairingLink.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void Lan_addresses_never_include_loopback()
    {
        Assert.DoesNotContain(Cuebot.Server.Api.PairingLink.LanAddresses(), a => a.StartsWith("127.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Problems_name_every_setting_a_phone_needs()
    {
        await using var bot = new TestBot(new ServerSettings());
        var problems = string.Join("\n", bot.Manager.PairingProblems());
        Assert.Contains("REST API", problems);
        Assert.Contains("HTTP server", problems);
        Assert.Contains("Allow LAN", problems);
        Assert.Contains("0.0.0.0", problems);

        var s = bot.Manager.Settings;
        s.AllowLan = true;
        s.Api.Enabled = true;
        s.Api.Address = "0.0.0.0";
        s.Http.Enabled = true;
        s.Http.Address = "0.0.0.0";
        Assert.Empty(bot.Manager.PairingProblems());

        s.Http.Address = "not an address";
        Assert.Contains(bot.Manager.PairingProblems(), p => p.Contains("not an IP address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pairing_link_makes_the_tokens()
    {
        await using var bot = new TestBot(new ServerSettings());
        var link = bot.Manager.PairingLink("192.168.1.20");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        Assert.Equal(bot.Manager.Settings.Api.Token, query["token"]);
        Assert.Equal(bot.Manager.Settings.Http.DeckToken, query["deckToken"]);
        Assert.StartsWith("cb_", query["deckToken"]);
        Assert.Equal(Cuebot.Server.StreamerBot.StreamerBotRequests.InstanceId, query["id"]);
    }
}
