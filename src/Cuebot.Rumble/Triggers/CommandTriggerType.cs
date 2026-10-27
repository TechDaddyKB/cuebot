using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Rumble.Triggers;

/// <summary>
/// Chat command trigger ("!hello") with the same semantics as <c>twitch.command</c>: matching, permission level
/// and cooldowns. On a match it adds <c>command</c>, <c>input</c>, <c>rawInput</c>, <c>input0..inputN</c> and
/// <c>inputCount</c>.
/// </summary>
internal sealed class CommandTriggerType(CooldownTracker cooldowns) : ITriggerType
{
    public const string Id = "rumble.command";

    public string TypeId => Id;
    public string Category => RumbleTriggers.Category;
    public string DisplayName => "Chat command";
    public IReadOnlyCollection<string> EventTypes { get; } = [RumbleEventTypes.ChatMessage];

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("command", "Command") { DefaultValue = "!hello" },
        new("match", "Match", ParameterKind.Choice)
        {
            DefaultValue = CommandMatcher.StartsWithChoice,
            Choices = [CommandMatcher.StartsWithChoice, CommandMatcher.ExactChoice],
        },
        new("permission", "Who can use it", ParameterKind.Choice) { DefaultValue = "Everyone", Choices = ChatPermissions.Choices },
        new("globalCooldown", "Global cooldown (seconds)", ParameterKind.Number) { DefaultValue = "0" },
        new("userCooldown", "Per-user cooldown (seconds)", ParameterKind.Number) { DefaultValue = "0" },
        new("caseSensitive", "Case sensitive", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args)
    {
        var message = e.Args.GetString("message");
        var match = CommandMatcher.Match(message, settings["command"], CommandMatcher.ParseMode(settings["match"]),
            settings.GetBool("caseSensitive"));
        if (match is null) return false;
        if (ChatPermissions.Of(e.Args) < ChatPermissions.Parse(settings["permission"])) return false;
        if (!cooldowns.TryStart(trigger.Id, CooldownUser(e.Args),
                Seconds(settings.GetDouble("globalCooldown")), Seconds(settings.GetDouble("userCooldown"))))
            return false;

        args["command"] = match.Command;
        args["input"] = match.Input;
        args["rawInput"] = message;
        for (var i = 0; i < match.Words.Count; i++) args["input" + i.ToString(CultureInfo.InvariantCulture)] = match.Words[i];
        args["inputCount"] = match.Words.Count;
        return true;
    }

    /// <summary>Messages seen only through the Live Stream API have no user id; the username identifies them.</summary>
    private static string CooldownUser(IReadOnlyDictionary<string, object?> args) =>
        args.GetString("userId") is { Length: > 0 } id ? id : "name:" + args.GetString("user").ToLowerInvariant();

    private static TimeSpan Seconds(double value) => value > 0 ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;
}
