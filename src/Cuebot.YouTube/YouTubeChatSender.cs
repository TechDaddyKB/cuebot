using Cuebot.Core.Chat;

namespace Cuebot.YouTube;

/// <summary>Lets the combined chat send to YouTube while a broadcast is live (as the broadcaster).</summary>
internal sealed class YouTubeChatSender(YouTubeService youtube) : IChatSender
{
    public string Platform => YouTubeChatAdapter.PlatformName;

    public bool CanSend => youtube.IsLive;

    public Task SendAsync(string text, CancellationToken ct = default) => youtube.SendChatMessageAsync(text, ct: ct);
}
