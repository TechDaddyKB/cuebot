using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Webhooks.Services;

/// <summary>Sub-actions for the Patreon campaign totals, patron lookups and the member sync <see cref="PatreonTracker"/> keeps.</summary>
internal static class PatreonSubActions
{
    public const string Category = "Patreon";

    public static readonly string[] TypeIds = ["patreon.getPatron", "patreon.getCampaign", "patreon.syncMembers"];

    private sealed class Step(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => PatreonSubActions.Category;
        public string DisplayName => name;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            await run(context, p, ct).ConfigureAwait(false);
            return SubActionResult.Next;
        }
    }

    public static IReadOnlyList<ISubActionHandler> Create(PatreonTracker tracker) =>
    [
        new Step("patreon.getPatron", "Get Patreon patron",
            [
                new("platform", "Platform") { DefaultValue = "%platform%", Help = "twitch, youtube or discord; %platform% is the chatter's" },
                new("userId", "User id") { DefaultValue = "%userId%" },
                new("userName", "User name") { DefaultValue = "%userName%", Help = "Used for Twitch when the patron linked Twitch by name only" },
            ],
            (c, p, _) =>
            {
                StorePatron(c.Args, tracker.FindPatron(p["platform"], p["userId"].Trim(), p["userName"].Trim()));
                return Task.CompletedTask;
            }),
        new Step("patreon.getCampaign", "Get Patreon campaign totals", [], (c, _, _) =>
        {
            StoreTotals(c.Args, tracker);
            return Task.CompletedTask;
        }),
        new Step("patreon.syncMembers", "Sync Patreon members now", [], async (c, _, ct) =>
        {
            string result;
            try { result = await tracker.SyncMembersAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
            {
                result = ex is PatreonAuthException ? ex.Message : $"Patreon sync failed: {ex.Message}";
            }
            c.Args["patreonSyncResult"] = result;
            StoreTotals(c.Args, tracker);
        }),
    ];

    internal static void StorePatron(Dictionary<string, object?> a, PatreonPatronInfo? p)
    {
        a["isPatron"] = p is { Status: "active_patron" };
        a["isPayingPatron"] = p?.IsPaying ?? false;
        a["patreonName"] = p?.Name ?? "";
        a["patreonTier"] = p?.Tier ?? "";
        a["patreonStatus"] = p?.Status ?? "";
        a["patreonAmount"] = (decimal)Math.Round(p?.Monthly ?? 0, 2);
        a["patreonSince"] = p?.Since is { } since ? since.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    }

    internal static void StoreTotals(Dictionary<string, object?> a, PatreonTracker tracker)
    {
        var t = tracker.Totals();
        PatreonTracker.AddTotals(a, t);
        a["patreonCurrency"] = t.Currency;
        a["patreonCampaign"] = tracker.CampaignName;
    }
}

/// <summary>The Patreon sub-actions, made once so each DI registration hands out the same instance.</summary>
internal sealed class PatreonSubActionSet(IReadOnlyList<ISubActionHandler> steps)
{
    public ISubActionHandler Get(string typeId) => steps.Single(s => s.TypeId == typeId);
}

/// <summary>Script functions: <c>patreon.campaign</c>, <c>patreon.tiers</c>, <c>patreon.patron</c>, <c>patreon.members</c>, <c>patreon.sync</c>.</summary>
internal sealed class PatreonScriptFunctions(PatreonTracker tracker) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("patreon.campaign", "The Patreon campaign: name, currency, patronCount, paidCount, monthly, connected, lastSync", [], (_, _, _) =>
        {
            var t = tracker.Totals();
            return Task.FromResult<object?>(new
            {
                name = tracker.CampaignName, currency = t.Currency, patronCount = t.PatronCount, paidCount = t.PaidCount, monthly = t.Monthly,
                connected = tracker.Connected, lastSync = tracker.LastSync,
            });
        }),
        new("patreon.tiers", "The Patreon tiers Cuebot knows: id, title, amount", [], (_, _, _) =>
            Task.FromResult<object?>(tracker.Tiers().Select(t => new { id = t.Id, title = t.Title, amount = t.AmountCents / 100.0, published = t.Published }).ToArray())),
        new("patreon.patron", "The linked Patreon patron of a viewer (platform twitch/youtube/discord, user id, user name), or null", ["platform", "userId", "userName"],
            (_, a, _) => Task.FromResult<object?>(tracker.FindPatron(a.GetString("platform"), a.GetString("userId"), a.GetString("userName")) is { } p
                ? new { name = p.Name, status = p.Status, tier = p.Tier, monthly = p.Monthly, currency = p.Currency, since = p.Since, paying = p.IsPaying }
                : null)),
        new("patreon.members", "How many Patreon members Cuebot knows, and whether the list came from a full sync", [], (_, _, _) =>
            Task.FromResult<object?>(new { count = tracker.MemberCount, synced = tracker.LastSync is not null, lastSync = tracker.LastSync })),
        new("patreon.sync", "Read the Patreon member list now (needs the API connection); returns a status line", [], async (_, _, ct) =>
        {
            try { return await tracker.SyncMembersAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
            {
                return ex is PatreonAuthException ? ex.Message : $"Patreon sync failed: {ex.Message}";
            }
        }),
    ];
}
