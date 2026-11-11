using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Donations.Triggers;

namespace Cuebot.Donations.Streamlabs;

/// <summary>A Streamlabs REST step described by a table row.</summary>
internal sealed class StreamlabsStep(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => StreamlabsTriggers.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await run(context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>The steps made once per service (DI hands out each as an <see cref="ISubActionHandler"/>).</summary>
internal sealed record StreamlabsSubActionSet(IReadOnlyList<ISubActionHandler> Steps);

/// <summary>
/// The Streamlabs REST steps (they need the user's own Streamlabs app connected on the Donations page). Type ids are
/// stored in actions.json and must never change.
/// </summary>
internal static class StreamlabsSubActions
{
    /// <summary>Every type id <see cref="Create"/> makes (DI registers them without building the service).</summary>
    public static readonly string[] TypeIds =
    [
        "streamlabs.alertQueue", "streamlabs.getAlertQueueStatus", "streamlabs.createAlert", "streamlabs.sendTestAlert",
        "streamlabs.setAlertProfile", "streamlabs.spinWheel", "streamlabs.emptyJar", "streamlabs.rollCredits", "streamlabs.mediaShare",
        "streamlabs.getDonations", "streamlabs.getTopDonors", "streamlabs.createDonation", "streamlabs.getPoints", "streamlabs.addPoints",
        "streamlabs.setPoints", "streamlabs.addPointsToAll", "streamlabs.resetPoints", "streamlabs.getPointsLeaderboard",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(StreamlabsService sl)
    {
        static ParameterDescriptor Text(string key, string label, string defaultValue = "", string? help = null) =>
            new(key, label) { DefaultValue = defaultValue, Help = help };
        static ParameterDescriptor Number(string key, string label, string defaultValue, string? help = null) =>
            new(key, label, ParameterKind.Number) { DefaultValue = defaultValue, Help = help };
        static ParameterDescriptor Choice(string key, string label, string defaultValue, IReadOnlyDictionary<string, string> labels) =>
            new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue, Choices = [.. labels.Keys], ChoiceLabels = labels };
        static ParameterDescriptor User() =>
            new("user", "User") { DefaultValue = "%userName%", Help = "The viewer's login name on the streaming site" };
        static string N(int i) => i.ToString(CultureInfo.InvariantCulture);
        static long Whole(ParameterValues p, string key) => (long)Math.Round(p.GetDouble(key));

        var alertTypes = new Dictionary<string, string>
        {
            ["donation"] = "Donation", ["follow"] = "Follow", ["subscription"] = "Subscription", ["host"] = "Host",
        };

        return
        [
            // ---- alert box ----
            new StreamlabsStep("streamlabs.alertQueue", "Control Streamlabs alerts",
                [Choice("command", "Action", "skip", new Dictionary<string, string>
                {
                    ["skip"] = "Skip the current alert", ["pause_queue"] = "Pause the queue", ["unpause_queue"] = "Resume the queue",
                    ["mute_volume"] = "Mute alert sounds", ["unmute_volume"] = "Unmute alert sounds",
                    ["show_video"] = "Show media share video", ["hide_video"] = "Hide media share video",
                })],
                (_, p, ct) => sl.AlertCommandAsync(p["command"], ct)),
            new StreamlabsStep("streamlabs.getAlertQueueStatus", "Get Streamlabs alert queue status", [], async (ctx, _, ct) =>
            {
                ctx.Args["slAlertsPaused"] = await sl.IsAlertQueuePausedAsync(ct).ConfigureAwait(false);
            }),
            new StreamlabsStep("streamlabs.createAlert", "Show a custom Streamlabs alert",
                [
                    Choice("type", "Alert type (its style is used)", "donation", alertTypes),
                    Text("message", "Message", "*%user%* says hi!", "Wrap words in *stars* to highlight them"),
                    Text("userMessage", "Second line"),
                    Text("image", "Image or GIF URL", help: "Leave empty for the alert type's own"),
                    Text("sound", "Sound URL", help: "Leave empty for the alert type's own"),
                    Number("duration", "Duration (ms, 0 = default)", "0"),
                    Text("color", "Highlight colour", help: "A CSS colour such as #32C3A6; empty for the default"),
                ],
                (_, p, ct) => sl.CreateAlertAsync(p["type"], p["message"], p["userMessage"], p["image"], p["sound"],
                    (int)Math.Max(0, p.GetDouble("duration")), p["color"], ct)),
            new StreamlabsStep("streamlabs.sendTestAlert", "Send Streamlabs test alert",
                [
                    Choice("type", "Alert", "follow", new Dictionary<string, string>
                    {
                        ["follow"] = "Follow", ["subscription"] = "Subscription", ["donation"] = "Donation", ["host"] = "Host",
                        ["bits"] = "Bits", ["raid"] = "Raid",
                    }),
                    Choice("platform", "Platform", "twitch", new Dictionary<string, string> { ["twitch"] = "Twitch", ["youtube"] = "YouTube" }),
                ],
                (_, p, ct) => sl.SendTestAlertAsync(p["type"], p["platform"], ct)),
            new StreamlabsStep("streamlabs.setAlertProfile", "Switch Streamlabs alert profile", [Text("profile", "Profile name")], async (ctx, p, ct) =>
            {
                var profile = await sl.ActivateAlertProfileAsync(p["profile"], ct).ConfigureAwait(false);
                ctx.Args["slAlertProfileId"] = profile.Id;
                ctx.Args["slAlertProfileName"] = profile.Name;
            }),

            // ---- widgets ----
            new StreamlabsStep("streamlabs.spinWheel", "Spin the Streamlabs wheel", [], (_, _, ct) => sl.SpinWheelAsync(ct)),
            new StreamlabsStep("streamlabs.emptyJar", "Empty the Streamlabs tip jar", [], (_, _, ct) => sl.EmptyJarAsync(ct)),
            new StreamlabsStep("streamlabs.rollCredits", "Roll the Streamlabs credits", [], (_, _, ct) => sl.RollCreditsAsync(ct)),
            new StreamlabsStep("streamlabs.mediaShare", "Control Streamlabs media share",
                [
                    Choice("command", "Action", "skip-video", new Dictionary<string, string>
                    {
                        ["play-media"] = "Play", ["pause-media"] = "Pause", ["skip-video"] = "Skip", ["playback-video"] = "Play back",
                        ["volume-up"] = "Volume up", ["volume-down"] = "Volume down",
                        ["enable-requests"] = "Allow requests", ["disable-requests"] = "Stop requests",
                        ["enable-autoplay"] = "Autoplay on", ["disable-autoplay"] = "Autoplay off",
                        ["enable-autoshow"] = "Auto-show on", ["disable-autoshow"] = "Auto-show off",
                        ["enable-moderation"] = "Moderation on", ["disable-moderation"] = "Moderation off",
                    }),
                    Text("volume", "Volume (0-100, volume actions only)", help: "Leave empty to change by Streamlabs' own step"),
                ],
                (_, p, ct) =>
                {
                    int? volume = p["volume"].Trim().Length > 0 ? (int)Math.Round(p.GetDouble("volume")) : null;
                    return sl.MediaShareAsync(p["command"], volume, ct);
                }),

            // ---- donations ----
            new StreamlabsStep("streamlabs.getDonations", "Get latest Streamlabs donations",
                [Number("count", "Donations", "5"), Text("currency", "Currency", help: "e.g. USD; empty for every currency")], async (ctx, p, ct) =>
                {
                    var rows = await sl.GetDonationsAsync(Math.Clamp(p.GetInt("count", 5), 1, 100), p["currency"], ct).ConfigureAwait(false);
                    ctx.Args["slDonationCount"] = rows.Count;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        ctx.Args[$"slDonation{N(i)}User"] = rows[i].Name;
                        ctx.Args[$"slDonation{N(i)}Amount"] = rows[i].Amount;
                        ctx.Args[$"slDonation{N(i)}Currency"] = rows[i].Currency;
                        ctx.Args[$"slDonation{N(i)}FormattedAmount"] = rows[i].FormattedAmount;
                        ctx.Args[$"slDonation{N(i)}Message"] = rows[i].Message;
                        ctx.Args[$"slDonation{N(i)}Id"] = rows[i].Id;
                        ctx.Args[$"slDonation{N(i)}CreatedAt"] = rows[i].CreatedAt;
                    }
                }),
            new StreamlabsStep("streamlabs.getTopDonors", "Get top Streamlabs donors",
                [Number("count", "Donors", "5"), Text("currency", "Currency", "USD", "Only donations in this currency are added up")],
                async (ctx, p, ct) =>
                {
                    var rows = TopDonors(await sl.GetDonationsAsync(100, p["currency"], ct).ConfigureAwait(false), p.GetInt("count", 5));
                    ctx.Args["slTopDonorCount"] = rows.Count;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        ctx.Args[$"slTopDonor{N(i)}User"] = rows[i].Name;
                        ctx.Args[$"slTopDonor{N(i)}Total"] = rows[i].Total;
                        ctx.Args[$"slTopDonor{N(i)}FormattedTotal"] = Money.Format(rows[i].Total, rows[i].Currency);
                    }
                }),
            new StreamlabsStep("streamlabs.createDonation", "Add a donation to Streamlabs",
                [
                    Text("name", "Donor name", "%user%", "2 to 25 characters"), Number("amount", "Amount", "5"), Text("currency", "Currency", "USD"),
                    Text("message", "Message", help: "Under 255 characters"),
                    new("skipAlert", "Skip the alert", ParameterKind.Boolean) { DefaultValue = "true" },
                ],
                async (ctx, p, ct) =>
                {
                    ctx.Args["slDonationId"] = await sl.CreateDonationAsync(p["name"], p["name"], p.GetDouble("amount"), p["currency"], p["message"],
                        p.GetBool("skipAlert", true), ct).ConfigureAwait(false);
                }),

            // ---- Cloudbot loyalty points ----
            new StreamlabsStep("streamlabs.getPoints", "Get Streamlabs points", [User()], async (ctx, p, ct) =>
            {
                ctx.Args["slPointsUser"] = p["user"].Trim();
                ctx.Args["slPoints"] = await sl.GetPointsAsync(p["user"], null, ct).ConfigureAwait(false);
            }),
            new StreamlabsStep("streamlabs.addPoints", "Add Streamlabs points",
                [User(), Number("amount", "Points", "100", "Negative values remove points")], async (ctx, p, ct) =>
                {
                    ctx.Args["slPoints"] = await sl.AddPointsAsync(p["user"], Whole(p, "amount"), null, ct).ConfigureAwait(false);
                }),
            new StreamlabsStep("streamlabs.setPoints", "Set Streamlabs points", [User(), Number("points", "Points", "0")], async (ctx, p, ct) =>
            {
                ctx.Args["slPoints"] = await sl.SetPointsAsync(p["user"], Whole(p, "points"), null, ct).ConfigureAwait(false);
            }),
            new StreamlabsStep("streamlabs.addPointsToAll", "Give everyone Streamlabs points", [Number("amount", "Points", "50")],
                (_, p, ct) => sl.AddPointsToAllAsync(Whole(p, "amount"), null, ct)),
            new StreamlabsStep("streamlabs.resetPoints", "Reset everyone's Streamlabs points", [], (_, _, ct) => sl.ResetPointsAsync(ct)),
            new StreamlabsStep("streamlabs.getPointsLeaderboard", "Get Streamlabs points leaderboard", [Number("count", "Users", "5")],
                async (ctx, p, ct) =>
                {
                    var rows = await sl.GetPointsLeaderboardAsync(p.GetInt("count", 5), ct).ConfigureAwait(false);
                    ctx.Args["slPointsLeaderCount"] = rows.Count;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        ctx.Args[$"slPointsLeader{N(i)}User"] = rows[i].UserName;
                        ctx.Args[$"slPointsLeader{N(i)}Total"] = rows[i].Points;
                    }
                }),
        ];
    }

    /// <summary>Adds up donations per donor name (case-insensitive) and returns the biggest totals.</summary>
    internal static IReadOnlyList<(string Name, double Total, string Currency)> TopDonors(IEnumerable<StreamlabsDonation> donations, int count) =>
        donations.GroupBy(d => (Name: d.Name.ToLowerInvariant(), d.Currency))
            .Select(g => (g.First().Name, Math.Round(g.Sum(d => d.Amount), 2), g.Key.Currency))
            .OrderByDescending(r => r.Item2)
            .Take(Math.Clamp(count, 1, 100))
            .ToArray();
}
