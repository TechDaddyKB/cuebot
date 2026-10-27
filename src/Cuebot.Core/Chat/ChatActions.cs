using Cuebot.Core.Actions;

namespace Cuebot.Core.Chat;

/// <summary>Any-platform triggers built on the chat hub's normalized events.</summary>
public sealed class ChatTriggers(ChatHub hub) : ITriggerProvider
{
    private const string Category = "Chat (all platforms)";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("chat.message", Category, "Chat message (any platform)", ChatHub.MessageEvent,
            [
                new ParameterDescriptor("platform", "Platform", ParameterKind.Choice)
                {
                    DefaultValue = ChatHub.AllPlatforms, Choices = [ChatHub.AllPlatforms, .. hub.Platforms],
                },
                new ParameterDescriptor("contains", "Message contains") { Help = "Leave empty for every message" },
            ],
            (e, s, _) => PlatformMatches(e.Args, s["platform"])
                         && (s["contains"].Length == 0
                             || (e.Args.GetValueOrDefault("message") as string ?? "").Contains(s["contains"], StringComparison.OrdinalIgnoreCase))),
        new EventTriggerType("chat.activity", Category, "Activity (any platform)", ChatHub.ActivityEvent,
            [
                new ParameterDescriptor("platform", "Platform", ParameterKind.Choice)
                {
                    DefaultValue = ChatHub.AllPlatforms, Choices = [ChatHub.AllPlatforms, .. hub.Platforms],
                },
                new ParameterDescriptor("kind", "Kind", ParameterKind.Choice)
                {
                    DefaultValue = "Any", Choices = ["Any", .. Enum.GetNames<FeedKind>()],
                },
            ],
            (e, s, _) => PlatformMatches(e.Args, s["platform"])
                         && (s["kind"] == "Any" || string.Equals(e.Args.GetValueOrDefault("kind") as string, s["kind"], StringComparison.OrdinalIgnoreCase))),
    ];

    private static bool PlatformMatches(IReadOnlyDictionary<string, object?> args, string platform) =>
        platform == ChatHub.AllPlatforms
        || string.Equals(args.GetValueOrDefault("platform") as string, platform, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sends a chat message to one platform or all connected ones.</summary>
public sealed class SendChatToPlatformsSubAction(ChatHub hub) : ISubActionHandler
{
    public string TypeId => "chat.send";
    public string Category => "Chat (all platforms)";
    public string DisplayName => "Send chat message (any platform)";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("platform", "Send to", ParameterKind.Choice) { DefaultValue = ChatHub.AllPlatforms, Choices = [ChatHub.AllPlatforms, .. hub.Platforms] },
        new("message", "Message", ParameterKind.Multiline),
        new("sameAsTrigger", "Reply on the platform the trigger came from", ParameterKind.Boolean)
        {
            DefaultValue = "false", Help = "Uses %platform% from an any-platform chat trigger",
        },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var platform = p.GetBool("sameAsTrigger") && context.Args.GetValueOrDefault("platform") is string origin && origin.Length > 0
            ? origin
            : p["platform"];
        var sent = await hub.SendAsync(platform, p["message"], ct).ConfigureAwait(false);
        context.Args["sentTo"] = string.Join(", ", sent);
        return SubActionResult.Next;
    }
}
