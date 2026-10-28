using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.Pally;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.TreatStream;
using Cuebot.Donations.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Donations.Tests;

/// <summary>Triggers, feed items and registration of TipeeeStream, DonorDrive, TreatStream and Pally.gg.</summary>
public sealed class CharityModuleTests
{
    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    [Fact]
    public void Every_trigger_is_in_its_service_category_with_the_documented_streamerbot_triggers()
    {
        foreach (var (provider, category, prefix, names) in new (ITriggerProvider, string, string, string[])[]
                 {
                     (new TipeeeStreamTriggers(), "TipeeeStream", "tipeeestream.", ["Donation", "Connected", "Disconnected"]),
                     (new DonorDriveTriggers(), "DonorDrive", "donordrive.", ["Donation", "Incentive", "Profile updated"]),
                     (new TreatStreamTriggers(), "TreatStream", "treatstream.", ["Treat", "Authenticated", "Connected", "Disconnected"]),
                     (new PallyTriggers(), "Pally.gg", "pally.", ["Campaign tip", "Connected", "Disconnected"]),
                 })
        {
            var triggers = provider.GetTriggers().ToArray();
            Assert.All(triggers, t =>
            {
                Assert.Equal(category, t.Category);
                Assert.StartsWith(prefix, t.TypeId);
                Assert.Equal(t.TypeId, Assert.Single(t.EventTypes));
            });
            foreach (var name in names) Assert.Contains(triggers, t => t.DisplayName == name);
        }
    }

    [Fact]
    public void Filters_on_amount_incentive_treat_and_campaign()
    {
        var tip = new PallyTriggers().GetTriggers().Single(t => t.TypeId == PallyEventTypes.CampaignTip);
        var e = new BotEvent(PallyEventTypes.CampaignTip, new Dictionary<string, object?> { ["amount"] = 5d, ["currency"] = "USD", ["campaignSlug"] = "pally" });
        Assert.True(Matches(tip, e, new() { ["minAmount"] = "5", ["campaign"] = "Pally" }));
        Assert.False(Matches(tip, e, new() { ["campaign"] = "other" }));
        Assert.False(Matches(tip, e, new() { ["minAmount"] = "6" }));
        var replay = new BotEvent(PallyEventTypes.CampaignTip, new Dictionary<string, object?>(e.Args) { ["isReplay"] = true });
        Assert.True(Matches(tip, replay));
        Assert.False(Matches(tip, replay, new() { ["includeReplays"] = "false" }));
        Assert.True(Matches(tip, e, new() { ["includeReplays"] = "false" }));

        var incentive = new DonorDriveTriggers().GetTriggers().Single(t => t.TypeId == DonorDriveEventTypes.Incentive);
        var claimed = new BotEvent(DonorDriveEventTypes.Incentive, new Dictionary<string, object?> { ["amount"] = 25d, ["incentive.description"] = "Spin the wheel" });
        Assert.True(Matches(incentive, claimed, new() { ["incentive"] = "spin the wheel" }));
        Assert.False(Matches(incentive, claimed, new() { ["incentive"] = "Sing" }));

        var treat = new TreatStreamTriggers().GetTriggers().Single(t => t.TypeId == TreatStreamEventTypes.Treat);
        var pizza = new BotEvent(TreatStreamEventTypes.Treat, new Dictionary<string, object?> { ["type"] = "Pizza" });
        Assert.True(Matches(treat, pizza));
        Assert.True(Matches(treat, pizza, new() { ["treat"] = "pizza" }));
        Assert.False(Matches(treat, pizza, new() { ["treat"] = "Cake" }));
    }

    [Theory]
    [InlineData(TipeeeStreamEventTypes.Donation, FeedKind.Tip, "Kevin donated €5.00", "€5.00")]
    [InlineData(TipeeeStreamEventTypes.Follow, FeedKind.Follow, "Kevin followed", null)]
    [InlineData(DonorDriveEventTypes.Donation, FeedKind.Tip, "Kevin donated €5.00 to charity", "€5.00")]
    [InlineData(PallyEventTypes.CampaignTip, FeedKind.Tip, "Kevin tipped €5.00", "€5.00")]
    public void Feed_titles(string type, FeedKind kind, string title, string? amount)
    {
        var item = DonationChatAdapter.Describe(new BotEvent(type, new Dictionary<string, object?>
        {
            ["service"] = "S", ["user"] = "Kevin", ["formattedAmount"] = "€5.00",
        }))!;
        Assert.Equal((kind, title, amount), (item.Kind, item.Title, item.Amount));
        Assert.Null(DonationChatAdapter.Describe(new BotEvent(DonorDriveEventTypes.Incentive)));
        Assert.Null(DonationChatAdapter.Describe(new BotEvent(DonorDriveEventTypes.ProfileUpdated)));
    }

    [Fact]
    public async Task AddDonations_registers_the_four_services_with_status_feed_triggers_and_script_functions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-charity-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddDonations().BuildServiceProvider())
            {
                var all = provider.GetRequiredService<DonationServices>();
                Assert.Equal(6, all.All.Count);
                Assert.Same(provider.GetRequiredService<TipeeeStreamService>(), all.TipeeeStream);
                var statuses = provider.GetServices<IConnectionStatus>().ToArray();
                foreach (var service in all.Added) Assert.Contains(statuses, s => ReferenceEquals(s, service));
                Assert.Equal(["TipeeeStream", "DonorDrive", "TreatStream", "Pally.gg"], all.Added.Select(s => s.Name));

                var registry = provider.GetRequiredService<Registry>();
                foreach (var id in new[] { TipeeeStreamEventTypes.Donation, DonorDriveEventTypes.Donation, DonorDriveEventTypes.Incentive,
                             DonorDriveEventTypes.ProfileUpdated, TreatStreamEventTypes.Treat, PallyEventTypes.CampaignTip })
                    Assert.NotNull(registry.GetTrigger(id));

                var api = provider.GetRequiredService<ScriptApi>();
                var none = JsonDocument.Parse("{}").RootElement;
                foreach (var prefix in new[] { "tipeeestream", "donordrive", "treatstream", "pally" })
                {
                    Assert.Equal(false, await api.CallAsync(null!, $"{prefix}.isConnected", none, default));
                    Assert.Equal(true, await api.CallAsync(null!, $"{prefix}.sendTestEvent", none, default));
                }
                Assert.Null(await api.CallAsync(null!, "donordrive.getProfile", none, default));

                // Each test tip reached the activity feed (and so credits and event speech).
                var feed = provider.GetRequiredService<ChatHub>().RecentFeed();
                foreach (var platform in new[] { "TipeeeStream", "DonorDrive", "TreatStream", "Pally.gg" })
                    Assert.Contains(feed, f => f.Platform == platform && f.Kind == FeedKind.Tip && f.Title.StartsWith("[Test]", StringComparison.Ordinal));

                // Nothing configured: auto-connect does nothing and never throws.
                await all.StartAsync();
                Assert.All(all.All, s => Assert.NotEqual(ConnectionState.Connected, s.State));
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
