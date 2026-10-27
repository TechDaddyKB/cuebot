using Cuebot.Core.Actions;
using Cuebot.Rumble.Triggers;

namespace Cuebot.Rumble;

/// <summary>Sub-action that posts a message to the live stream's Rumble chat (needs a chat sign-in).</summary>
internal sealed class SendChatMessageSubAction(RumbleService rumble) : ISubActionHandler
{
    public string TypeId => "rumble.sendMessage";
    public string Category => RumbleTriggers.Category;
    public string DisplayName => "Send chat message";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("message", "Message", ParameterKind.Multiline) { Help = "Needs a Rumble chat sign-in and a live stream" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await rumble.SendChatMessageAsync(parameters["message"], ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
