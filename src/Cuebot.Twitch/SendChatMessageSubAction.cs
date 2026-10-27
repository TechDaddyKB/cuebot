using Cuebot.Core.Actions;
using Cuebot.Twitch.Triggers;

namespace Cuebot.Twitch;

/// <summary>Sub-action that posts a message to the broadcaster's chat.</summary>
internal sealed class SendChatMessageSubAction(TwitchService twitch) : ISubActionHandler
{
    public string TypeId => "twitch.sendMessage";
    public string Category => TwitchTriggers.Category;
    public string DisplayName => "Send chat message";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("message", "Message", ParameterKind.Multiline)];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await twitch.SendChatMessageAsync(parameters["message"], ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
