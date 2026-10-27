using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Core.Chat.Emotes;

internal static class EmoteCategory
{
    public const string Name = "Chat (all platforms)";
}

/// <summary>"Refresh emotes": loads 7TV/BTTV/FFZ sets again (from cache when fresh unless forced).</summary>
public sealed class RefreshEmotesSubAction(EmoteService emotes) : ISubActionHandler
{
    public string TypeId => "chat.refreshEmotes";
    public string Category => EmoteCategory.Name;
    public string DisplayName => "Refresh emotes";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("force", "Download again even when the cache is fresh", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["emoteCount"] = await emotes.RefreshAsync(p.GetBool("force", true), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Triggers for emote set changes (Streamer.bot's 7TV/BTTV emote added/removed, for any provider).</summary>
public sealed class EmoteTriggers : ITriggerProvider
{
    private static readonly IReadOnlyList<string> ProviderChoices = ["Any", .. EmoteProviders.ThirdParty];

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger("chat.emoteAdded", "Emote added", EmoteService.EmoteAddedEvent),
        Trigger("chat.emoteRemoved", "Emote removed", EmoteService.EmoteRemovedEvent),
        Trigger("chat.emotesChanged", "Emote set changed", EmoteService.SetsChangedEvent),
    ];

    private static EventTriggerType Trigger(string id, string name, string eventType) =>
        new(id, EmoteCategory.Name, name, eventType,
            [new ParameterDescriptor("provider", "Provider", ParameterKind.Choice) { DefaultValue = "Any", Choices = ProviderChoices }],
            (e, s, _) => s["provider"] == "Any"
                         || string.Equals(e.Args.GetValueOrDefault("provider") as string, s["provider"], StringComparison.OrdinalIgnoreCase));
}

/// <summary>Script functions: emotes.refresh, emotes.find, emotes.list.</summary>
public sealed class EmoteScriptFunctions(EmoteService emotes) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("emotes.refresh", "Reload third-party emote sets; returns the number of emotes known", ["force"],
            async (_, a, ct) => await emotes.RefreshAsync(a.GetBool("force", true), ct).ConfigureAwait(false)),
        new("emotes.find", "Emotes in a message: list of {start, length, code, provider, imageUrl} (UTF-16 positions)",
            ["platform", "message"], (ctx, a, _) =>
            {
                var platform = a.GetString("platform", ctx.Args.GetValueOrDefault("platform") as string ?? "Twitch");
                var message = a.GetString("message", ctx.Args.GetValueOrDefault("message") as string ?? "");
                var found = emotes.FindEmotes(platform, message, ctx.Args);
                return Task.FromResult<object?>(found.Select(m => new Dictionary<string, object?>
                {
                    ["start"] = m.Start, ["length"] = m.Length, ["code"] = m.Code, ["provider"] = m.Provider,
                    ["imageUrl"] = m.ImageUrl, ["zeroWidth"] = m.Emote.IsZeroWidth,
                }).ToArray());
            }),
        new("emotes.list", "Emote codes known for a platform", ["platform"],
            (_, a, _) => Task.FromResult<object?>(emotes.GetEmotes(a.GetString("platform", "Twitch")).Select(e => e.Code).ToArray())),
    ];
}
