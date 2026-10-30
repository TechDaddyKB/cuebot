using Cuebot.Core.Events;

namespace Cuebot.Donations.Tests;

public sealed class DonationOverlayAdapterTests
{
    private readonly DonationOverlayAdapter _adapter = new();

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData(StreamlabsEventTypes.Donation)]
    [InlineData(StreamElementsEventTypes.Tip)]
    [InlineData(TipeeeStreamEventTypes.Donation)]
    [InlineData(PallyEventTypes.CampaignTip)]
    public void Donations_are_tips(string type)
    {
        var tip = _adapter.ToAlert(E(type, ("user", "Kris"), ("amount", 12.5), ("currency", "USD"), ("formattedAmount", "$12.50"),
            ("message", "thanks"), ("service", "Streamlabs"), ("isTest", true)))!;
        Assert.Equal("tip-latest", tip.Listener);
        Assert.Equal((12.5, "USD", "$12.50", "thanks", true), (tip.Event["amount"]!.GetValue<double>(), tip.Event["currency"]!.GetValue<string>(),
            tip.Event["formattedAmount"]!.GetValue<string>(), tip.Event["message"]!.GetValue<string>(), tip.Event["isTest"]!.GetValue<bool>()));
    }

    [Fact]
    public void Charity_donations_have_their_own_listener()
    {
        Assert.Equal("charityCampaignDonation-latest", _adapter.ToAlert(E(DonorDriveEventTypes.Donation, ("user", "Kris"), ("amount", 5)))!.Listener);
        Assert.Equal("charityCampaignDonation-latest", _adapter.ToAlert(E(StreamlabsEventTypes.CharityDonation, ("user", "Kris")))!.Listener);
    }

    [Fact]
    public void Relayed_platform_events_are_left_to_the_platforms()
    {
        Assert.Null(_adapter.ToAlert(E(StreamElementsEventTypes.Follow, ("user", "Kris"))));
        Assert.Null(_adapter.ToAlert(E(StreamlabsEventTypes.Subscription, ("user", "Kris"))));
        Assert.Null(_adapter.ToAlert(E(StreamElementsEventTypes.Cheer, ("user", "Kris"))));
    }
}
