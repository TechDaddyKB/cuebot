using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Core.Overlays;

internal static class OverlayCategories
{
    public const string Overlays = "Overlays";
}

/// <summary>Pauses, resumes, skips or mutes the alert queue on every overlay page.</summary>
public sealed class AlertQueueSubAction(OverlayAlertQueue queue) : ISubActionHandler
{
    internal const string Pause = "Pause";
    internal const string Resume = "Resume";
    internal const string TogglePause = "Toggle pause";
    internal const string Skip = "Skip current alert";
    internal const string Mute = "Mute";
    internal const string Unmute = "Unmute";
    internal const string ToggleMute = "Toggle mute";

    public string TypeId => "overlays.alertQueue";
    public string Category => OverlayCategories.Overlays;
    public string DisplayName => "Alert queue";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Mode", ParameterKind.Choice)
        {
            DefaultValue = Pause, Choices = [Pause, Resume, TogglePause, Skip, Mute, Unmute, ToggleMute],
            Help = "Paused: the alert playing finishes and new ones wait. Muted: alert sounds and videos are silent.",
        },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        Apply(queue, parameters["mode"]);
        context.Args["alertsPaused"] = queue.Paused;
        context.Args["alertsMuted"] = queue.Muted;
        return Task.FromResult(SubActionResult.Next);
    }

    /// <summary>Applies a mode by name (case-insensitive); false for an unknown one.</summary>
    public static bool Apply(OverlayAlertQueue queue, string mode)
    {
        switch (mode.Trim().ToLowerInvariant())
        {
            case "pause": queue.Paused = true; break;
            case "resume": queue.Paused = false; break;
            case "toggle pause" or "togglepause": queue.Paused = !queue.Paused; break;
            case "skip current alert" or "skip": queue.Skip(); break;
            case "mute": queue.Muted = true; break;
            case "unmute": queue.Muted = false; break;
            case "toggle mute" or "togglemute": queue.Muted = !queue.Muted; break;
            default: return false;
        }
        return true;
    }
}

/// <summary>
/// Shows an alert on Cuebot's alert boxes with the design of the chosen alert type ("custom" by default), like a real
/// alert: it waits its turn in the queue, and the alert box's variations apply.
/// </summary>
public sealed class ShowAlertSubAction(OverlayFeed feed) : ISubActionHandler
{
    /// <summary>The listener alert boxes show these on.</summary>
    public const string Listener = "cuebot-alert";

    public static readonly IReadOnlyList<string> AlertTypes =
        ["custom", "follower", "subscriber", "tip", "cheer", "raid", "superchat", "kicks", "redemption", "jewels", "charityCampaignDonation", "merch"];

    public string TypeId => "overlays.showAlert";
    public string Category => OverlayCategories.Overlays;
    public string DisplayName => "Show alert";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("alertType", "Design", ParameterKind.Choice) { DefaultValue = "custom", Choices = AlertTypes, Help = "Which alert box design to use" },
        new("name", "Name") { DefaultValue = "%user%", Help = "{name} in the design" },
        new("message", "Message", ParameterKind.Multiline) { Help = "{message} in the design" },
        new("amount", "Amount", ParameterKind.Number) { DefaultValue = "1", Help = "{amount}; variations by amount use it" },
        new("currency", "Currency") { Help = "For money amounts, e.g. USD" },
        new("platform", "Platform") { DefaultValue = "%platform%", Help = "twitch, kick, rumble or youtube; variations by platform use it" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var type = parameters["alertType"].Trim();
        var data = new JsonObject
        {
            ["alertType"] = type.Length > 0 ? type : "custom",
            ["name"] = Clean(parameters["name"]),
            ["displayName"] = Clean(parameters["name"]),
            ["message"] = Clean(parameters["message"]),
            ["amount"] = OverlayAlerts.ToNode(parameters.GetDouble("amount", 1)),
            ["currency"] = Clean(parameters["currency"]).ToUpperInvariant(),
            ["service"] = Clean(parameters["platform"]).ToLowerInvariant(),
            ["type"] = type.Length > 0 ? type : "custom",
            ["_id"] = Guid.NewGuid().ToString("N"),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        feed.Publish(OverlayMessage.Event(Listener, data));
        return Task.FromResult(SubActionResult.Next);
    }

    /// <summary>An unresolved %variable% (no such argument) is left out rather than shown.</summary>
    private static string Clean(string value) => value.Trim() is var v && v.StartsWith('%') && v.EndsWith('%') ? "" : v;
}
