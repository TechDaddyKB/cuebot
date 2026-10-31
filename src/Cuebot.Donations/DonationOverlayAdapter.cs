using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Donations;

/// <summary>
/// Donations from every donation service as StreamElements "tip-latest" alerts (charity donations as
/// "charityCampaignDonation-latest", StreamElements and Streamlabs merch as "merch-latest"). Platform events the services relay (follows, subs...) are left to the platform
/// modules, so an alert never plays twice. Test events from the Donations page are passed on, for trying overlays.
/// </summary>
internal sealed class DonationOverlayAdapter : IOverlayEventAdapter
{
    public string Platform => "Donations";
    public string Service => "donations";

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs) => [];

    public OverlayAlert? ToAlert(BotEvent e) => e.Type switch
    {
        StreamlabsEventTypes.Donation or StreamElementsEventTypes.Tip or TipeeeStreamEventTypes.Donation or PallyEventTypes.CampaignTip =>
            new(Tip, Donation(e, "tip")),
        TreatStreamEventTypes.Treat => new(Tip, Donation(e, "tip", message: Str(e.Args, "type"))),
        StreamlabsEventTypes.CharityDonation or StreamElementsEventTypes.CharityDonation or DonorDriveEventTypes.Donation =>
            new(CharityDonation, Donation(e, "charityCampaignDonation")),
        StreamElementsEventTypes.Merch or StreamlabsEventTypes.Merch => new(Merch, MerchEvent(e)),
        _ => null,
    };

    /// <summary>Merch as StreamElements' <c>merch-latest</c>: what was bought in <c>items</c> (and the image Streamlabs sends).</summary>
    internal static System.Text.Json.Nodes.JsonObject MerchEvent(BotEvent e) =>
        Event(e, "merch", Number(e.Args, "amount"), extra:
        [
            new("currency", Str(e.Args, "currency")),
            new("formattedAmount", Str(e.Args, "formattedAmount")),
            new("items", Items(e.Args)),
            new("quantity", OverlayAlerts.ToNode(Math.Max(1, Number(e.Args, "merchQuantity")))),
            new("image", First(e.Args, "imageUrl", "merchImageUrl")),
            new("provider", Str(e.Args, "service")),
        ]);

    private static System.Text.Json.Nodes.JsonObject Donation(BotEvent e, string type, string? message = null) =>
        Event(e, type, Number(e.Args, "amount"), message: message, extra:
        [
            new("currency", Str(e.Args, "currency")),
            new("formattedAmount", Str(e.Args, "formattedAmount")),
            new("provider", Str(e.Args, "service")),
            new("isTest", Bool(e.Args, "isTest")),
        ]);
}
