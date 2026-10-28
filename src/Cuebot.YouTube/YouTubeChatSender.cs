using Cuebot.Core.Chat;

namespace Cuebot.YouTube;

/// <summary>Lets the combined chat send to YouTube while a broadcast is live (as the broadcaster).</summary>
internal sealed class YouTubeChatSender(YouTubeService youtube) : IChatSender
{
    public string Platform => YouTubeChatAdapter.PlatformName;

    public bool CanSend => youtube.IsLive;

    public Task SendAsync(string text, CancellationToken ct = default) => youtube.SendChatMessageAsync(text, ct: ct);

    /// <summary>The bot account and a broadcast id are honoured; YouTube echoes are never flagged internal.</summary>
    public Task SendAsync(string text, ChatSendOptions options, CancellationToken ct = default) =>
        youtube.SendChatMessageAsync(text, options.Bot == true, fallback: true,
            string.IsNullOrWhiteSpace(options.BroadcastId) ? null : options.BroadcastId.Trim(), ct);
}
