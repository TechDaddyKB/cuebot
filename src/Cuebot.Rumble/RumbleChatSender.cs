using Cuebot.Core.Chat;

namespace Cuebot.Rumble;

/// <summary>Lets the combined chat send to Rumble while signed in and live.</summary>
internal sealed class RumbleChatSender(RumbleService rumble) : IChatSender
{
    public string Platform => RumbleChatAdapter.PlatformName;

    public bool CanSend => rumble.CanSendChat && rumble.IsLive;

    public Task SendAsync(string text, CancellationToken ct = default) => rumble.SendChatMessageAsync(text, ct);
}
