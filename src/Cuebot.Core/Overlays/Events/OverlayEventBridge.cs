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

    private readonly Chat.Pronouns.PronounService? _pronouns;

    public OverlayEventBridge(EventBus bus, OverlayFeed feed, IEnumerable<IOverlayEventAdapter> adapters, AppLog log, EmoteService? emotes = null,
        Chat.Pronouns.PronounService? pronouns = null)
    {
        _feed = feed;
        _emotes = emotes;
        _pronouns = pronouns;
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
            var (messages, alert, service) = ConvertCore(e);
            foreach (var (listener, data) in messages) _feed.Publish(OverlayMessage.Event(listener, data));
            if (alert is not null) AlertSent?.Invoke(alert, service!, e.Time);
        }
        catch (Exception ex)
        {
            _log.Error("Overlays", $"Could not convert {e.Type} for overlays", ex);
        }
    }

    /// <summary>
    /// The overlay events a bot event becomes (<c>(listener, event)</c> pairs), without sending them anywhere: the widget
    /// editor uses this to show emulated events in its preview only.
    /// </summary>
    public IReadOnlyList<(string Listener, JsonNode? Event)> Convert(BotEvent e) => ConvertCore(e).Messages;

    private (List<(string, JsonNode?)> Messages, OverlayAlert? Alert, string? Service) ConvertCore(BotEvent e)
    {
        switch (e.Type)
        {
            case ChatHub.MessageEvent:
                return ([("message", ChatMessage(e))], null, null);
            case MessageDeleted when OverlayAlerts.Str(e.Args, "messageId") is { Length: > 0 } id:
                return ([("delete-message", new JsonObject { ["msgId"] = id })], null, null);
            case UserBanned or UserTimedOut when OverlayAlerts.Str(e.Args, "userId") is { Length: > 0 } userId:
                return ([("delete-messages", new JsonObject { ["userId"] = userId })], null, null);
        }
        foreach (var adapter in _adapters)
        {
            if (adapter.ToAlert(e) is not { } alert) continue;
            var raw = new JsonObject
            {
                ["type"] = alert.Event["type"]?.DeepClone(),
                ["provider"] = adapter.Service,
                ["data"] = alert.Event.DeepClone(),
            };
            return ([(alert.Listener, alert.Event), ("event", raw)], alert, adapter.Service);
        }
        return ([], null, null);
    }

    private JsonObject ChatMessage(BotEvent e)
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
        if (Pronouns(platform, platformArgs) is { } pronouns) args["pronouns"] = pronouns;
        return OverlayChatMessage.Build(args, platformArgs, emotes, adapter?.Badges(platformArgs) ?? [],
            adapter?.Service ?? platform.ToLowerInvariant(), e.Time);
    }

    /// <summary>
    /// A Twitch chatter's pronouns when Cuebot already knows them; otherwise starts the lookup (alejo.io) so their next
    /// message has them. Chat is never held up waiting for it. Other platforms have no pronoun service.
    /// </summary>
    private string? Pronouns(string platform, IReadOnlyDictionary<string, object?> platformArgs)
    {
        if (_pronouns is null || !platform.Equals("Twitch", StringComparison.OrdinalIgnoreCase)) return null;
        var login = OverlayAlerts.Str(platformArgs, "userName");
        if (login.Length == 0) return null;
        var (known, value) = _pronouns.TryGetCached(login);
        if (!known) _ = _pronouns.GetAsync(login);
        return value?.Display;
    }

    public void Dispose() => _subscription.Dispose();
}
