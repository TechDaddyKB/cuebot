using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>
/// UI copies of tables the speech module keeps internal (sub-command defaults, event templates and variables).
/// Keep in sync with Cuebot.Speech's TtsCommands and EventSpeech.
/// </summary>
public static class SpeechDefaults
{
    public sealed record SubCommand(string Name, ChatAudience DefaultPermission, bool ViewerCommand, string Usage);

    public static IReadOnlyList<SubCommand> SubCommands { get; } =
    [
        new("voices", ChatAudience.Everyone, true, "voices"),
        new("voice", ChatAudience.Everyone, true, "voice [alias|reset]"),
        new("speed", ChatAudience.Everyone, true, "speed <-10..10|reset>"),
        new("pitch", ChatAudience.Everyone, true, "pitch <-10..10|reset>"),
        new("nick", ChatAudience.Everyone, true, "nick <nickname|reset>"),
        new("me", ChatAudience.Everyone, true, "me on|off"),
        new("commands", ChatAudience.Everyone, true, "commands"),
        new("about", ChatAudience.Everyone, true, "about"),
        new("status", ChatAudience.Everyone, true, "status"),
        new("pause", ChatAudience.Moderators, false, "pause"),
        new("resume", ChatAudience.Moderators, false, "resume"),
        new("clear", ChatAudience.Moderators, false, "clear"),
        new("skip", ChatAudience.Moderators, false, "skip [user]"),
        new("on", ChatAudience.Moderators, false, "on"),
        new("off", ChatAudience.Moderators, false, "off"),
        new("mode", ChatAudience.Moderators, false, "mode all|command"),
        new("ignore", ChatAudience.Moderators, false, "ignore add|del <user>"),
        new("ignored", ChatAudience.Moderators, false, "ignored"),
        new("reg", ChatAudience.Moderators, false, "reg add|del <user>"),
        new("events", ChatAudience.Moderators, false, "events on|off"),
        new("random", ChatAudience.Moderators, false, "random reset <user>"),
        new("set", ChatAudience.Moderators, false, "set nickname|sticky|voice ..."),
        new("assign", ChatAudience.Moderators, false, "assign last <user> | assign <user> <alias>"),
        new("profile", ChatAudience.Moderators, false, "profile [list|<name>|none]"),
        new("voicegate", ChatAudience.Moderators, false, "voicegate [<profile>|none]"),
    ];

    /// <summary>Activity kinds that can be spoken, with their built-in message.</summary>
    public static IReadOnlyList<(string Kind, string Label, string Text, bool Enabled)> EventKinds { get; } =
    [
        ("Follow", "Follow", "%name% just followed!", true),
        ("Subscription", "Subscription", "%name% subscribed! %message%", true),
        ("GiftSubscription", "Gift subscription", "%name% gifted %gift% subs!", true),
        ("Cheer", "Cheer (bits)", "%name% cheered %bits% bits! %message%", true),
        ("Tip", "Tip / donation", "%name% tipped %amount%! %message%", true),
        ("Merch", "Merch purchase", "%name% bought %product%! %message%", true),
        ("Raid", "Raid", "%name% is raiding with %viewers% viewers!", true),
        ("Redemption", "Reward redemption", "%name% redeemed %title%. %input%", true),
        ("StreamOnline", "Stream online", "The stream is live!", false),
        ("StreamOffline", "Stream offline", "The stream has ended.", false),
    ];

    public static IReadOnlyList<string> EventVariables { get; } =
    [
        "name", "user", "platform", "kind", "description", "level", "gift", "subtier", "tier", "title", "cost", "bits",
        "amount", "percent", "currency", "recipient", "cumulative", "message", "input", "viewers", "product", "quantity",
    ];

    public static IReadOnlyList<string> ChatVariables { get; } = ["user", "message", "platform", "userId", "nickname"];

    public static IReadOnlyList<string> PollyRegions { get; } =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "sa-east-1", "eu-west-1", "eu-west-2",
        "eu-west-3", "eu-central-1", "eu-central-2", "eu-north-1", "eu-south-2", "af-south-1", "ap-east-1",
        "ap-south-1", "ap-south-2", "ap-southeast-1", "ap-southeast-2", "ap-southeast-5", "ap-northeast-1",
        "ap-northeast-2", "ap-northeast-3", "me-south-1", "me-central-1", "us-gov-west-1",
    ];

    public static IReadOnlyList<string> PollyEngines { get; } = ["standard", "neural", "long-form", "generative"];

    public static string Variables(IEnumerable<string> names) => string.Join("  ", names.Select(n => $"%{n}%"));
}
