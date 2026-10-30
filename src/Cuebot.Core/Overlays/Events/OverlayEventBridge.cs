using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Overlays.Events;

/// <summary>
/// Turns bot events into StreamElements-shaped overlay messages on the <see cref="OverlayFeed"/>:
/// <list type="bullet">
/// <item>combined chat ("chat.message") becomes "message", with badge images from the platform's adapter and emotes
/// from the <see cref="EmoteService"/>;</item>
/// <item>"chat.messageDeleted" becomes "delete-message" and "chat.userBanned" / "chat.userTimedOut" "delete-messages";</item>
/// <item>platform events the adapters recognize become alerts ("follower-latest", "subscriber-latest", ...), each
/// followed by the raw "event" listener <c>{type, provider, data}</c>.</item>
/// </list>
/// </summary>
public sealed class OverlayEventBridge : IDisposable
{
    public const string MessageDeleted = "chat.messageDeleted";
    public const string UserBanned = "chat.userBanned";
    public const string UserTimedOut = "chat.userTimedOut";

    private readonly OverlayFeed _feed;
    private readonly EmoteService? _emotes;
    private readonly AppLog _log;
    private readonly IReadOnlyList<IOverlayEventAdapter> _adapters;
    private readonly IDisposable _subscription;

    public OverlayEventBridge(EventBus bus, OverlayFeed feed, IEnumerable<IOverlayEventAdapter> adapters, AppLog log, EmoteService? emotes = null)
    {
        _feed = feed;
        _emotes = emotes;
        _log = log;
        _adapters = adapters.ToArray();
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised (on the publisher's thread) after an alert went to the feed, with the adapter's service and the event time.</summary>
    public event Action<OverlayAlert, string, DateTimeOffset>? AlertSent;

    private void OnEvent(BotEvent e)
    {
        try
        {
            switch (e.Type)
            {
                case ChatHub.MessageEvent:
                    OnChat(e);
                    return;
                case MessageDeleted when OverlayAlerts.Str(e.Args, "messageId") is { Length: > 0 } id:
                    _feed.Publish(OverlayMessage.Event("delete-message", new JsonObject { ["msgId"] = id }));
                    return;
                case UserBanned or UserTimedOut when OverlayAlerts.Str(e.Args, "userId") is { Length: > 0 } userId:
                    _feed.Publish(OverlayMessage.Event("delete-messages", new JsonObject { ["userId"] = userId }));
                    return;
            }
            foreach (var adapter in _adapters)
            {
                if (adapter.ToAlert(e) is not { } alert) continue;
                _feed.Publish(OverlayMessage.Event(alert.Listener, alert.Event));
                _feed.Publish(OverlayMessage.Event("event", new JsonObject
                {
                    ["type"] = alert.Event["type"]?.DeepClone(),
                    ["provider"] = adapter.Service,
                    ["data"] = alert.Event.DeepClone(),
                }));
                AlertSent?.Invoke(alert, adapter.Service, e.Time);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Overlays", $"Could not convert {e.Type} for overlays", ex);
        }
    }

    private void OnChat(BotEvent e)
    {
        var platform = OverlayAlerts.Str(e.Args, "platform");
        var platformArgs = e.Args.GetValueOrDefault(ChatHub.PlatformArgsKey) as IReadOnlyDictionary<string, object?> ?? e.Args;
        var adapter = _adapters.FirstOrDefault(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));
        // Kick keeps its "[emote:id:name]" tokens in rawMessage; emote matching needs them (and turns them into names).
        var text = _emotes is not null && OverlayAlerts.First(platformArgs, "rawMessage", "message") is { Length: > 0 } raw
            ? raw
            : OverlayAlerts.Str(e.Args, "message");
        var emotes = _emotes?.FindEmotes(platform, text, platformArgs) ?? [];
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase) { ["message"] = text };
        var message = OverlayChatMessage.Build(args, platformArgs, emotes, adapter?.Badges(platformArgs) ?? [],
            adapter?.Service ?? platform.ToLowerInvariant(), e.Time);
        _feed.Publish(OverlayMessage.Event("message", message));
    }

    public void Dispose() => _subscription.Dispose();
}
