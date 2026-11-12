using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Variables;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

/// <summary>
/// Patreon currency, duplicate members:create, monthly amounts, anonymous patrons, tier filters, stale backlogs, post
/// events, and the optional API connection (campaign, member sync, pledge history, webhook setup, token refresh) against
/// a fake Patreon API. Payload fixtures only; no real Patreon account.
/// </summary>
public sealed class PatreonFeatureTests : IDisposable
{
    private const string Secret = "patreon-webhook-secret";
    private const string MemberId = "03ca69c3-ebea-4b9a-8fac-e4a837873254";
    private readonly Harness _h = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 27, 13, 0, 0, TimeSpan.Zero));
    private readonly FakePatreonApi _api = new();
    private readonly PatreonWebhookHandler _patreon;

    public PatreonFeatureTests()
    {
        _patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log)
        {
            Settings = { Secret = Secret }, Clock = _clock, Seen = new RecentIds(time: _clock),
        };
        _patreon.Tracker = new PatreonTracker(_h.Settings, _h.Log, new HttpClient(_api), time: _clock, apiBaseUrl: "https://patreon.test");
    }

    public void Dispose() => _h.Dispose();

    private WebhookOutcome Send(string topic, string body) =>
        _patreon.Handle(Harness.Delivery("patreon", body, new Dictionary<string, string>
        {
            ["X-Patreon-Event"] = topic,
            ["X-Patreon-Signature"] = WebhookVerification.HmacMd5Hex(Secret, Encoding.UTF8.GetBytes(body)),
        }));

    private BotEvent Last(string type) => _h.Events(type).Last();

    private static string Pledge => Harness.Data("patreon-pledge-create.json");

    /// <summary>The pledge fixture with member attributes, campaign attributes or the user's attributes changed.</summary>
    private static string Edit(string json, Action<JsonObject>? member = null, Action<JsonObject>? campaign = null, Action<JsonObject>? user = null)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        member?.Invoke(root["data"]!["attributes"]!.AsObject());
        foreach (var item in root["included"]!.AsArray())
        {
            var attributes = item!["attributes"]!.AsObject();
            if ((string?)item["type"] == "campaign") campaign?.Invoke(attributes);
            if ((string?)item["type"] == "user") user?.Invoke(attributes);
        }
        return root.ToJsonString();
    }

    private static ITriggerType Trigger(string id, PatreonTracker? tracker = null) => new PatreonTriggers(tracker).GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    // ---- corrections ----

    [Fact]
    public void Currency_comes_from_the_campaign_in_the_delivery_and_is_remembered()
    {
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", Edit(Pledge, campaign: c => c["currency"] = "eur")));
        var e = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal(("EUR", "€5.00"), (e.Args["currency"], e.Args["formattedAmount"]));
        Assert.True(Matches(Trigger(PatreonEventTypes.PledgeCreated), e, new() { ["currency"] = "EUR" }));
        Assert.False(Matches(Trigger(PatreonEventTypes.PledgeCreated), e, new() { ["currency"] = "USD" }));

        // A later delivery that does not name it uses the campaign currency Cuebot learnt.
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:update", Pledge));
        Assert.Equal("EUR", Last(PatreonEventTypes.PledgeUpdated).Args["currency"]);
        Assert.Equal("EUR", _patreon.Tracker.Totals().Currency);
    }

    [Fact]
    public void Currency_falls_back_to_the_setting_and_unknown_currency_passes_the_filter()
    {
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", Pledge));
        var unknown = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal("", unknown.Args["currency"]);
        Assert.True(Matches(Trigger(PatreonEventTypes.PledgeCreated), unknown, new() { ["currency"] = "USD" }));

        _patreon.Options.Currency = "gbp";
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:update", Pledge));
        Assert.Equal(("GBP", "£5.00"), (Last(PatreonEventTypes.PledgeUpdated).Args["currency"], Last(PatreonEventTypes.PledgeUpdated).Args["formattedAmount"]));
    }

    [Fact]
    public void A_repeated_members_create_for_the_same_member_runs_once_within_ten_minutes()
    {
        var free = Pledge.Replace("\"active_patron\"", "null", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, Send("members:create", free));
        // Patreon may send members:create again with a different body (another timestamp): still the same member.
        var again = Edit(free, member: m => m["last_charge_date"] = "2026-10-27T12:00:05.000+00:00");
        Assert.Equal(WebhookOutcome.Ignored, Send("members:create", again));
        Assert.Single(_h.Events(PatreonEventTypes.FollowCreated));

        // Long after (left and joined again), it is a new event.
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(WebhookOutcome.Handled, Send("members:create", Edit(free, member: m => m["note"] = "back")));
        Assert.Equal(2, _h.Events(PatreonEventTypes.FollowCreated).Length);

        // Other topics still dedupe by body only: two different members:update deliveries both run.
        Assert.Equal(WebhookOutcome.Handled, Send("members:update", Pledge));
        Assert.Equal(WebhookOutcome.Handled, Send("members:update", Edit(Pledge, member: m => m["note"] = "x")));
    }

    [Fact]
    public void A_new_pledge_resent_with_another_body_counts_once()
    {
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", Pledge));
        Assert.Equal(WebhookOutcome.Ignored, Send("members:pledge:create", Edit(Pledge, member: m => m["last_charge_status"] = "Pending")));
        // A later pledge (new start) is new.
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", Edit(Pledge, member: m => m["pledge_relationship_start"] = "2026-10-27T12:30:00.000+00:00")));
        Assert.Equal(2, _h.Events(PatreonEventTypes.PledgeCreated).Length);
    }

    [Fact]
    public void A_yearly_pledge_is_reported_and_filtered_by_its_monthly_value()
    {
        var yearly = Edit(Pledge, member: m =>
        {
            m["pledge_cadence"] = 12;
            m["currently_entitled_amount_cents"] = 6000;
            m["will_pay_amount_cents"] = 6000;
            m["campaign_lifetime_support_cents"] = 12000;
        }, campaign: c => c["currency"] = "USD");
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", yearly));
        var e = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal((5m, 5m, 60m, 12, "$60.00", 120m), (e.Args["amount"], e.Args["monthlyAmount"], e.Args["chargeAmount"], e.Args["pledgeCadence"],
            e.Args["formattedCharge"], e.Args["lifetimeSupport"]));
        Assert.False(Matches(Trigger(PatreonEventTypes.PledgeCreated), e, new() { ["minAmount"] = "10" }));
        Assert.True(Matches(Trigger(PatreonEventTypes.PledgeCreated), e, new() { ["minAmount"] = "5" }));
        Assert.Equal("$5.00", WebhookChatAdapter.Describe(e)!.Amount);
    }

    [Fact]
    public void A_patron_who_hides_their_identity_stays_anonymous()
    {
        var hidden = Edit(Pledge, user: u =>
        {
            u["full_name"] = null;
            u["first_name"] = null;
            u["image_url"] = null;
            u["url"] = null;
        });
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", hidden));
        var e = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal((PatreonWebhookHandler.AnonymousName, true, "Jane Patron", "", ""),
            (e.Args["user"], e.Args["isAnonymous"], e.Args["privateName"], e.Args["avatarUrl"], e.Args["profileUrl"]));
        Assert.StartsWith("An anonymous patron became a patron", WebhookChatAdapter.Describe(e)!.Title, StringComparison.Ordinal);

        Send("members:pledge:update", Pledge);
        var shown = Last(PatreonEventTypes.PledgeUpdated);
        Assert.Equal(("Jane Patron", false, "https://c8.patreon.com/2/200/98765432"), (shown.Args["user"], shown.Args["isAnonymous"], shown.Args["avatarUrl"]));
    }

    [Fact]
    public void Free_members_read_as_joining_and_the_deprecated_follow_wording_is_gone()
    {
        Assert.True(_patreon.SendTestEvent("followCreated"));
        Assert.Equal("Jane Patron joined your Patreon as a free member", WebhookChatAdapter.Describe(Last(PatreonEventTypes.FollowCreated))!.Title.Replace("[Test] ", ""));
        // The sample no longer suggests deprecated fields; the replacement is published.
        Assert.True(_patreon.SendTestEvent("pledgeCreated"));
        var e = Last(PatreonEventTypes.PledgeCreated);
        Assert.False(e.Args.ContainsKey("attributes.is_follower"));
        Assert.False(e.Args.ContainsKey("attributes.lifetime_support_cents"));
        Assert.Equal((5m, "USD"), (e.Args["lifetimeSupport"], e.Args["currency"]));
    }

    [Fact]
    public void Tier_filter_matches_titles_with_commas_lists_and_picks_by_id()
    {
        var gold = Pledge.Replace("\"title\": \"Supporter\"", "\"title\": \"Gold, Silver & Bronze\"", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", gold));
        var e = Last(PatreonEventTypes.PledgeCreated);
        var trigger = Trigger(PatreonEventTypes.PledgeCreated);
        Assert.Equal(["Gold, Silver & Bronze"], (List<string>)e.Args["tiers"]!);
        Assert.True(Matches(trigger, e, new() { ["tier"] = "gold, silver & bronze" }));
        // In a list, a title with commas is named by its pick ("Title #id") instead.
        Assert.False(Matches(trigger, e, new() { ["tier"] = "Fan, Gold" }));
        Assert.False(Matches(trigger, e, new() { ["tier"] = "Gold" }));
        // A pick from the list: "Old name #id" still matches after the tier was renamed.
        Assert.True(Matches(trigger, e, new() { ["tier"] = "Supporter #7654321" }));
        Assert.True(Matches(trigger, e, new() { ["tier"] = "7654321" }));
        Assert.False(Matches(trigger, e, new() { ["tier"] = "Supporter #1111111" }));
        Assert.True(Matches(trigger, e, new() { ["tier"] = "Fan, Other #7654321" }));
    }

    [Fact]
    public async Task Tier_picker_offers_the_known_tiers()
    {
        _patreon.Options.AccessToken = "creator-access";
        await _patreon.Tracker.ConnectAsync();
        var tier = Trigger(PatreonEventTypes.PledgeCreated, _patreon.Tracker).Parameters.Single(p => p.Key == "tier");
        Assert.Equal(["Free #1000", "Supporter #7654321", "Gold #2222222"], tier.Suggestions!().ToArray());
    }

    [Fact]
    public void A_backlog_after_a_paused_webhook_is_marked_stale_and_skipped_by_default()
    {
        _clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", Pledge));
        var e = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal((true, 73.0), (e.Args["isStale"], e.Args["ageHours"]));
        var trigger = Trigger(PatreonEventTypes.PledgeCreated);
        Assert.False(Matches(trigger, e));
        Assert.True(Matches(trigger, e, new() { ["skipStale"] = "false" }));
        // Still recorded: the member list and totals know the patron.
        Assert.Equal(1, _patreon.Tracker.Totals().PaidCount);

        _patreon.Options.StaleHours = 0;
        Send("members:pledge:create", Edit(Pledge, member: m => m["pledge_relationship_start"] = "2026-10-20T00:00:00.000+00:00"));
        Assert.Equal(false, Last(PatreonEventTypes.PledgeCreated).Args["isStale"]);
    }

    [Fact]
    public void Post_updates_and_deletions_are_triggers()
    {
        const string post = """{"data":{"attributes":{"title":"Patch notes","url":"/posts/patch-notes-9","is_paid":true,"is_public":false},"id":"9","type":"post"}}""";
        Assert.Equal(WebhookOutcome.Handled, Send("posts:update", post));
        Assert.Equal(WebhookOutcome.Handled, Send("posts:delete", post.Replace("Patch notes", "Patch notes!", StringComparison.Ordinal)));
        var updated = Last(PatreonEventTypes.PostUpdated);
        Assert.Equal(("Patch notes", "https://www.patreon.com/posts/patch-notes-9", true, false), (updated.Args["title"], updated.Args["url"], updated.Args["isPaid"], updated.Args["isPublic"]));
        Assert.Single(_h.Events(PatreonEventTypes.PostDeleted));
        Assert.Equal(WebhookOutcome.Ignored, Send("pledges:create", Pledge));   // API v1 trigger
        Assert.True(_patreon.SendTestEvent("postUpdated"));
        Assert.True(_patreon.SendTestEvent("postDeleted"));
    }

    [Fact]
    public void Pledge_updated_knows_upgrades_from_the_member_list_without_the_api()
    {
        Send("members:pledge:create", Edit(Pledge, campaign: c => c["currency"] = "USD"));
        Send("members:pledge:update", Edit(Pledge, member: m => m["currently_entitled_amount_cents"] = 1000));
        var up = Last(PatreonEventTypes.PledgeUpdated);
        Assert.Equal(("upgrade", 5m, "Supporter", 10m), (up.Args["direction"], up.Args["previousAmount"], up.Args["previousTier"], up.Args["amount"]));
        var trigger = Trigger(PatreonEventTypes.PledgeUpdated);
        Assert.True(Matches(trigger, up, new() { ["direction"] = PatreonTriggers.UpgradesOnly }));
        Assert.False(Matches(trigger, up, new() { ["direction"] = PatreonTriggers.DowngradesOnly }));

        Send("members:pledge:update", Edit(Pledge, member: m => m["currently_entitled_amount_cents"] = 300));
        Assert.Equal("downgrade", Last(PatreonEventTypes.PledgeUpdated).Args["direction"]);
        Assert.Equal(3m, Last(PatreonEventTypes.PledgeUpdated).Args["patreonMonthly"]);
    }

    // ---- overlays ----

    private static WebhookOverlayAdapter Overlay => WebhookOverlayAdapter.All().Single(a => a.Service == "patreon");

    /// <summary>The alerts the deliveries so far make, in order.</summary>
    private List<Cuebot.Core.Overlays.Events.OverlayAlert> Alerts() =>
        [.. _h.Published.Select(e => Overlay.ToAlert(e)).OfType<Cuebot.Core.Overlays.Events.OverlayAlert>()];

    [Fact]
    public void A_new_paying_patron_is_one_subscriber_alert_with_tier_and_monthly_price()
    {
        var pledge = Edit(Pledge, campaign: c => c["currency"] = "USD");
        // A paying patron's join sends members:create and members:pledge:create; Patreon may resend either.
        Assert.Equal(WebhookOutcome.Ignored, Send("members:create", pledge));
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:create", pledge));
        Assert.Equal(WebhookOutcome.Ignored, Send("members:create", Edit(pledge, member: m => m["note"] = "resent")));
        Assert.Equal(WebhookOutcome.Ignored, Send("members:pledge:create", Edit(pledge, member: m => m["note"] = "resent")));

        var alert = Assert.Single(Alerts());
        Assert.Equal(Cuebot.Core.Overlays.Events.OverlayAlerts.Subscriber, alert.Listener);
        var a = alert.Event;
        Assert.Equal(("Jane Patron", "Supporter", 5.0, "USD", 1), (a["name"]!.GetValue<string>(), a["tierName"]!.GetValue<string>(),
            Cuebot.Core.Overlays.Events.OverlayAlerts.NumberOf(a["price"]), a["currency"]!.GetValue<string>(), a["amount"]!.GetValue<int>()));
        Assert.Equal("https://c8.patreon.com/2/200/98765432", a["avatar"]!.GetValue<string>());
        Assert.False(Overlay.IsCatchUp(_h.Events(PatreonEventTypes.PledgeCreated).Single()));
    }

    [Fact]
    public void A_free_member_joining_is_a_follower_alert()
    {
        var free = Pledge.Replace("\"active_patron\"", "null", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, Send("members:create", free));
        Assert.Equal(WebhookOutcome.Ignored, Send("members:create", Edit(free, member: m => m["note"] = "again")));
        var alert = Assert.Single(Alerts());
        Assert.Equal((Cuebot.Core.Overlays.Events.OverlayAlerts.Follower, "Jane Patron"), (alert.Listener, alert.Event["name"]!.GetValue<string>()));
    }

    [Fact]
    public void An_anonymous_patron_alert_never_carries_their_name_or_picture()
    {
        var hidden = Edit(Pledge, user: u =>
        {
            u["full_name"] = null;
            u["image_url"] = null;
            u["url"] = null;
        });
        Send("members:pledge:create", hidden);
        var json = Assert.Single(Alerts()).Event.ToJsonString();
        Assert.Contains(PatreonWebhookHandler.AnonymousName, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", json, StringComparison.Ordinal);
        Assert.DoesNotContain("98765432", json, StringComparison.Ordinal);   // no picture, no user id
    }

    [Fact]
    public void Only_an_upgrade_is_an_alert_and_a_stale_backlog_is_a_catch_up()
    {
        Send("members:pledge:create", Edit(Pledge, campaign: c => c["currency"] = "USD"));
        Send("members:pledge:update", Edit(Pledge, member: m => m["currently_entitled_amount_cents"] = 1000));
        Send("members:pledge:update", Edit(Pledge, member: m => m["currently_entitled_amount_cents"] = 300));
        var alerts = Alerts();
        Assert.Equal(2, alerts.Count);   // the new pledge and the upgrade; the downgrade is not celebrated
        var upgrade = alerts[1].Event;
        Assert.Equal((true, "Supporter", 5.0, 10.0), (upgrade["upgrade"]!.GetValue<bool>(), upgrade["previousTier"]!.GetValue<string>(),
            Cuebot.Core.Overlays.Events.OverlayAlerts.NumberOf(upgrade["previousAmount"]), Cuebot.Core.Overlays.Events.OverlayAlerts.NumberOf(upgrade["price"])));

        _clock.Advance(TimeSpan.FromDays(3));
        Send("members:pledge:create", Edit(Pledge, member: m => m["pledge_relationship_start"] = "2026-10-01T00:00:00.000+00:00"));
        var stale = Last(PatreonEventTypes.PledgeCreated);
        Assert.Equal(true, stale.Args["isStale"]);
        Assert.NotNull(Overlay.ToAlert(stale));   // still an alert for lists and totals...
        Assert.True(Overlay.IsCatchUp(stale));    // ...that alert boxes skip by default
    }

    // ---- API connection ----

    [Fact]
    public async Task Connect_reads_the_campaign_tiers_and_members_and_sets_variables_and_links()
    {
        var globals = new GlobalVariables(null, _h.Log);
        var users = new UserVariables(null, _h.Log);
        _patreon.Tracker.Globals = globals;
        _patreon.Tracker.UserVariables = users;
        _patreon.Options.AccessToken = "creator-access";

        var result = await _patreon.Tracker.ConnectAsync();
        Assert.Contains("Connected to streams", result, StringComparison.Ordinal);
        Assert.True(_patreon.Tracker.Connected);
        Assert.All(_api.Requests, r => Assert.Equal("Bearer creator-access", r.Authorization));
        Assert.All(_api.Requests, r => Assert.StartsWith("Cuebot", r.UserAgent, StringComparison.Ordinal));
        // Both pages of the member list were read (cursor).
        Assert.Equal(2, _api.Requests.Count(r => r.Path.EndsWith("/members", StringComparison.Ordinal)));
        Assert.Contains(_api.Requests, r => r.Query.Contains("page%5Bcursor%5D=next-page", StringComparison.Ordinal));

        var totals = _patreon.Tracker.Totals();
        // 3 members: a $5 monthly patron, a $60 yearly patron ($5 a month), a free member; Patreon's own count is 42.
        Assert.Equal((42, 2, 10.0, "USD", true), (totals.PatronCount, totals.PaidCount, totals.Monthly, totals.Currency, totals.FromSync));
        Assert.Equal(("42", "2", "10", "USD"), (globals.Get("patreonPatronCount"), globals.Get("patreonPaidCount"), globals.Get("patreonMonthly"), globals.Get("patreonCurrency")));

        // Linked viewers: Twitch by numeric id, YouTube by id.
        Assert.Equal(true, users.Get("twitch", "11112222", "isPatron"));
        Assert.Equal("Gold", users.Get("twitch", "11112222", "patreonTier"));
        Assert.Equal(5m, users.Get("twitch", "11112222", "patreonAmount"));
        Assert.Equal(true, users.Get("youtube", "UC-yearly", "isPatron"));
        Assert.Equal("Gold", _patreon.Tracker.FindPatron("twitch", "", "goldfan")!.Tier);
        Assert.Null(_patreon.Tracker.FindPatron("twitch", "999", "nobody"));

        // Overlay session data and the Goal widget's source.
        var data = new JsonObject();
        _patreon.Tracker.AddTo(data);
        Assert.Equal((2, 10.0, "USD"), (data["patreon"]!["paidCount"]!.GetValue<int>(), data["patreon"]!["monthly"]!.GetValue<double>(), data["patreon"]!["currency"]!.GetValue<string>()));

        // A webhook now uses the campaign's currency even when the delivery does not name it.
        Send("members:pledge:create", Pledge);
        Assert.Equal("USD", Last(PatreonEventTypes.PledgeCreated).Args["currency"]);

        // The patron leaves: their link is cleared.
        _api.Members = _api.Members.Where(m => m.Id != "m-gold").ToArray();
        await _patreon.Tracker.SyncMembersAsync();
        Assert.Equal(false, users.Get("twitch", "11112222", "isPatron"));
        Assert.Equal("", users.Get("twitch", "11112222", "patreonTier"));
    }

    [Fact]
    public async Task Pledge_updated_reads_the_direction_and_previous_pledge_from_the_history()
    {
        _patreon.Options.AccessToken = "creator-access";
        await _patreon.Tracker.ConnectAsync();
        Assert.Equal(WebhookOutcome.Handled, Send("members:pledge:update", Edit(Pledge, member: m => m["currently_entitled_amount_cents"] = 1000)));
        var e = Last(PatreonEventTypes.PledgeUpdated);
        Assert.Equal(("upgrade", 3m, "Fan"), (e.Args["direction"], e.Args["previousAmount"], e.Args["previousTier"]));
        Assert.Contains(_api.Requests, r => r.Path == $"/api/oauth2/v2/members/{MemberId}" && r.Query.Contains("pledge_history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Posts_say_whether_they_are_for_paying_patrons()
    {
        _patreon.Options.AccessToken = "creator-access";
        await _patreon.Tracker.ConnectAsync();
        Assert.Equal(WebhookOutcome.Handled, Send("posts:publish", """{"data":{"attributes":{"title":"Behind the scenes","url":"/posts/bts-77"},"id":"77","type":"post"}}"""));
        var e = Last(PatreonEventTypes.PostPublished);
        Assert.Equal((true, false, "Gold"), (e.Args["isPaid"], e.Args["isPublic"], e.Args["tierNames"]));
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_once_and_a_refused_one_stops_the_calls()
    {
        _patreon.Options.AccessToken = "old-access";
        _patreon.Options.RefreshToken = "refresh-1";
        _patreon.Options.ClientId = "client-id";
        _patreon.Options.ClientSecret = "client-secret";
        _api.ValidToken = "new-access";
        await _patreon.Tracker.ConnectAsync();
        Assert.True(_patreon.Tracker.Connected);
        Assert.Equal(("new-access", "refresh-2"), (_patreon.Options.AccessToken, _patreon.Options.RefreshToken));
        var refresh = _api.Requests.Single(r => r.Path == "/api/oauth2/token");
        Assert.Contains("grant_type=refresh_token", refresh.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", refresh.Body, StringComparison.Ordinal);
        // Secrets never reach the log.
        Assert.DoesNotContain(_h.Log.Recent(), l => l.Message.Contains("new-access", StringComparison.Ordinal) || l.Message.Contains("refresh-1", StringComparison.Ordinal));

        // Refused even after a refresh: stop and ask to reconnect, no retry loop.
        _api.ValidToken = "never";
        _api.RefreshRefused = true;
        var before = _api.Requests.Count;
        var result = await _patreon.Tracker.ConnectAsync();
        Assert.True(_patreon.Tracker.NeedsReconnect);
        Assert.Contains("connect again", result, StringComparison.Ordinal);
        Assert.True(_api.Requests.Count - before <= 2);
        _patreon.Tracker.Tick(_clock.GetUtcNow().AddHours(7));
        Assert.True(_api.Requests.Count - before <= 2);
    }

    [Fact]
    public async Task Create_webhook_registers_the_relay_url_saves_its_secret_and_resume_unpauses_it()
    {
        _patreon.Settings.Secret = "";
        _patreon.Options.AccessToken = "creator-access";
        await _patreon.Tracker.ConnectAsync();
        var result = await _patreon.Tracker.CreateWebhookAsync("https://relay.test/h/route-secret/patreon", s => _patreon.Settings.Secret = s);
        Assert.StartsWith("Created", result, StringComparison.Ordinal);
        Assert.Equal("hook-secret-from-patreon", _patreon.Settings.Secret);
        Assert.Equal("hook-1", _patreon.Options.WebhookId);
        var post = JsonNode.Parse(_api.Requests.Single(r => r.Method == "POST" && r.Path == "/api/oauth2/v2/webhooks").Body)!;
        Assert.Equal("https://relay.test/h/route-secret/patreon", (string?)post["data"]!["attributes"]!["uri"]);
        Assert.Equal(PatreonApi.WebhookTriggers, post["data"]!["attributes"]!["triggers"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("123456", (string?)post["data"]!["relationships"]!["campaign"]!["data"]!["id"]);

        _api.Paused = true;
        Assert.Contains("paused", await _patreon.Tracker.CheckWebhookAsync(), StringComparison.Ordinal);
        Assert.Equal((true, 7), (_patreon.Tracker.WebhookHealth!.Paused, _patreon.Tracker.WebhookHealth.ConsecutiveFailures));
        Assert.StartsWith("Resumed", await _patreon.Tracker.ResumeWebhookAsync(), StringComparison.Ordinal);
        var patch = JsonNode.Parse(_api.Requests.Single(r => r.Method == "PATCH").Body)!;
        Assert.False((bool)patch["data"]!["attributes"]!["paused"]!);
        Assert.False(_patreon.Tracker.WebhookHealth!.Paused);
    }

    [Fact]
    public async Task Periodic_sync_runs_every_six_hours_only_with_a_token()
    {
        _patreon.Tracker.Tick(_clock.GetUtcNow().AddMinutes(5));
        Assert.Empty(_api.Requests);
        _patreon.Options.AccessToken = "creator-access";
        _patreon.Tracker.Tick(_clock.GetUtcNow().AddMinutes(5));
        await WaitFor(() => _patreon.Tracker.LastSync is not null);
        var after = _api.Requests.Count;
        _patreon.Tracker.Tick(_clock.GetUtcNow().AddHours(1));
        await Task.Delay(100);
        Assert.Equal(after, _api.Requests.Count);
    }

    [Fact]
    public async Task Script_functions_and_steps_reach_the_tracker()
    {
        _patreon.Options.AccessToken = "creator-access";
        await _patreon.Tracker.ConnectAsync();
        var functions = new PatreonScriptFunctions(_patreon.Tracker).GetFunctions().ToDictionary(f => f.Name);
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
        var campaign = JsonSerializer.SerializeToElement(await functions["patreon.campaign"].Invoke(null!, Args(new { }), default));
        Assert.Equal(("streams", 2, true), (campaign.GetProperty("name").GetString(), campaign.GetProperty("paidCount").GetInt32(), campaign.GetProperty("connected").GetBoolean()));
        var patron = JsonSerializer.SerializeToElement(await functions["patreon.patron"].Invoke(null!, Args(new { platform = "twitch", userId = "11112222" }), default));
        Assert.Equal("Gold", patron.GetProperty("tier").GetString());
        Assert.Null(await functions["patreon.patron"].Invoke(null!, Args(new { platform = "twitch", userId = "1" }), default));
        Assert.Equal(3, JsonSerializer.SerializeToElement(await functions["patreon.tiers"].Invoke(null!, Args(new { }), default)).GetArrayLength());

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        PatreonSubActions.StorePatron(args, _patreon.Tracker.FindPatron("youtube", "UC-yearly"));
        Assert.Equal((true, true, "Gold", 5m, "2025-01-15"), (args["isPatron"], args["isPayingPatron"], args["patreonTier"], args["patreonAmount"], args["patreonSince"]));
        PatreonSubActions.StoreTotals(args, _patreon.Tracker);
        Assert.Equal((42, "USD"), (args["patreonPatronCount"], args["patreonCurrency"]));
        Assert.Equal(PatreonSubActions.TypeIds.Order(), PatreonSubActions.Create(_patreon.Tracker).Select(s => s.TypeId).Order());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    /// <summary>A clock tests move by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A fake Patreon API v2 with one campaign, three tiers, three members (two pages) and one webhook.</summary>
    private sealed class FakePatreonApi : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Query, string Body, string Authorization, string UserAgent);

        public sealed record Member(string Id, string Status, int Cents, int Cadence, string TierId, string TwitchId, string YouTubeId);

        public List<Request> Requests { get; } = [];
        public string ValidToken { get; set; } = "creator-access";
        public bool RefreshRefused { get; set; }
        public bool Paused { get; set; }
        private bool _created;

        public Member[] Members { get; set; } =
        [
            new("m-gold", "active_patron", 500, 1, "2222222", "11112222", ""),
            new("m-yearly", "active_patron", 6000, 12, "2222222", "", "UC-yearly"),
            new("m-free", "", 0, 1, "1000", "", ""),
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            lock (Requests)
                Requests.Add(new(request.Method.Method, path, query, body, request.Headers.Authorization?.ToString() ?? "", request.Headers.UserAgent.ToString()));
            if (path == "/api/oauth2/token")
            {
                if (RefreshRefused) return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
                return Json(HttpStatusCode.OK, $$"""{"access_token":"{{ValidToken}}","refresh_token":"refresh-2","expires_in":2678400,"token_type":"Bearer"}""");
            }
            if (request.Headers.Authorization?.Parameter != ValidToken) return Json(HttpStatusCode.Unauthorized, """{"errors":[{"status":"401"}]}""");
            return (request.Method.Method, path) switch
            {
                ("GET", "/api/oauth2/v2/identity") => Json(HttpStatusCode.OK,
                    """{"data":{"id":"1","type":"user","relationships":{"campaign":{"data":{"id":"123456","type":"campaign"}}}}}"""),
                ("GET", "/api/oauth2/v2/campaigns/123456") => Json(HttpStatusCode.OK, """
                    {"data":{"id":"123456","type":"campaign","attributes":{"creation_name":"streams","currency":"USD","patron_count":42,"vanity":"streams","url":"https://www.patreon.com/streams"}},
                     "included":[{"id":"2222222","type":"tier","attributes":{"title":"Gold","amount_cents":500,"published":true}},
                                 {"id":"1000","type":"tier","attributes":{"title":"Free","amount_cents":0,"published":true}},
                                 {"id":"7654321","type":"tier","attributes":{"title":"Supporter","amount_cents":300,"published":true}}]}
                    """),
                ("GET", "/api/oauth2/v2/campaigns/123456/members") => Json(HttpStatusCode.OK, MembersPage(query.Contains("cursor", StringComparison.Ordinal))),
                ("GET", var p) when p.StartsWith("/api/oauth2/v2/members/", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """
                    {"data":{"id":"x","type":"member"},"included":[
                      {"id":"e1","type":"pledge-event","attributes":{"type":"pledge_start","amount_cents":300,"currency_code":"USD","tier_title":"Fan","date":"2026-01-01T00:00:00.000+00:00"}},
                      {"id":"e2","type":"pledge-event","attributes":{"type":"pledge_upgrade","amount_cents":1000,"currency_code":"USD","tier_title":"Gold","date":"2026-10-27T12:59:00.000+00:00"}}]}
                    """),
                ("GET", "/api/oauth2/v2/posts/77") => Json(HttpStatusCode.OK,
                    """{"data":{"id":"77","type":"post","attributes":{"title":"Behind the scenes","url":"/posts/bts-77","is_paid":true,"is_public":false,"tiers":["2222222"]}}}"""),
                ("GET", "/api/oauth2/v2/webhooks") => Json(HttpStatusCode.OK, _created ? "{\"data\":[" + Hook() + "]}" : """{"data":[]}"""),
                ("POST", "/api/oauth2/v2/webhooks") => Created(),
                ("PATCH", "/api/oauth2/v2/webhooks/hook-1") => Resume(),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        }

        private HttpResponseMessage Created()
        {
            _created = true;
            return Json(HttpStatusCode.Created, "{\"data\":" + Hook() + "}");
        }

        private HttpResponseMessage Resume()
        {
            Paused = false;
            return Json(HttpStatusCode.OK, "{\"data\":" + Hook() + "}");
        }

        private string Hook() => new JsonObject
        {
            ["id"] = "hook-1",
            ["type"] = "webhook",
            ["attributes"] = new JsonObject
            {
                ["uri"] = "https://relay.test/h/route-secret/patreon", ["paused"] = Paused, ["num_consecutive_times_failed"] = Paused ? 7 : 0,
                ["last_attempted_at"] = "2026-10-27T12:00:00.000+00:00", ["secret"] = "hook-secret-from-patreon", ["triggers"] = new JsonArray("members:create"),
            },
        }.ToJsonString();

        private string MembersPage(bool second)
        {
            var page = (second ? Members.Skip(2) : Members.Take(2)).ToArray();
            static JsonNode? Link(string id, string url) => id.Length > 0 ? new JsonObject { ["user_id"] = id, ["url"] = url } : null;
            var data = new JsonArray([.. page.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id,
                ["type"] = "member",
                ["attributes"] = new JsonObject
                {
                    ["full_name"] = m.Id + " name", ["patron_status"] = m.Status.Length > 0 ? m.Status : null,
                    ["currently_entitled_amount_cents"] = m.Cents, ["pledge_cadence"] = m.Cadence, ["is_gifted"] = false, ["is_free_trial"] = false,
                    ["pledge_relationship_start"] = "2025-01-15T00:00:00.000+00:00",
                },
                ["relationships"] = new JsonObject
                {
                    ["currently_entitled_tiers"] = new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = m.TierId, ["type"] = "tier" }) },
                    ["user"] = new JsonObject { ["data"] = new JsonObject { ["id"] = "u-" + m.Id, ["type"] = "user" } },
                },
            })]);
            var users = new JsonArray([.. page.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = "u-" + m.Id,
                ["type"] = "user",
                ["attributes"] = new JsonObject
                {
                    ["full_name"] = m.Id + " name",
                    ["social_connections"] = new JsonObject
                    {
                        ["twitch"] = Link(m.TwitchId, "https://twitch.tv/goldfan"),
                        ["youtube"] = Link(m.YouTubeId, "https://youtube.com/channel/" + m.YouTubeId),
                    },
                },
            })]);
            var next = second || Members.Length <= 2 ? null : "next-page";
            return new JsonObject
            {
                ["data"] = data,
                ["included"] = users,
                ["meta"] = new JsonObject { ["pagination"] = new JsonObject { ["cursors"] = new JsonObject { ["next"] = next }, ["total"] = Members.Length } },
            }.ToJsonString();
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
