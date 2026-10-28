using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks;

/// <summary>
/// Shared plumbing of the webhook services: verify → map → dedupe → publish, the "last events" list, the last
/// delivery's outcome and local test events. Subclasses only verify and map.
/// </summary>
public abstract class WebhookServiceBase : IWebhookHandler
{
    private const int RecentSize = 50;
    private readonly RecentIds _seen = new();
    private readonly LinkedList<WebhookRecord> _recent = new();
    private readonly object _gate = new();

    private protected WebhookServiceBase(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    {
        SettingsFile = settings;
        Bus = bus;
        Log = log;
    }

    private protected JsonFile<WebhooksSettings> SettingsFile { get; }
    private protected EventBus Bus { get; }
    private protected AppLog Log { get; }

    /// <summary>Route name, e.g. <c>kofi</c>.</summary>
    public abstract string Service { get; }

    /// <summary>Service name for the UI, the feed and the <c>service</c> argument, e.g. "Ko-fi".</summary>
    public abstract string DisplayName { get; }

    /// <summary>False while switched off in settings: deliveries are still verified, then ignored.</summary>
    public abstract bool Enabled { get; }

    /// <summary>True once the secret needed to verify deliveries is entered.</summary>
    public abstract bool IsConfigured { get; }

    /// <summary>Kinds accepted by <see cref="SendTestEvent"/>.</summary>
    public abstract IReadOnlyList<string> TestEventKinds { get; }

    /// <summary>The last delivery received (null before the first).</summary>
    public WebhookDeliveryInfo? LastDelivery { get; private set; }

    /// <summary>Raised (from any thread) when an event is added to <see cref="RecentEvents"/> or a delivery arrives.</summary>
    public event Action? Changed;

    public void SaveSettings() => SettingsFile.ScheduleSave();

    public IReadOnlyList<WebhookRecord> RecentEvents()
    {
        lock (_gate) return _recent.ToArray();
    }

    /// <summary>True when the delivery's signature / token matches the configured secret.</summary>
    private protected abstract bool Verify(WebhookDelivery delivery);

    /// <summary>Turns a verified delivery into events (none for types Cuebot ignores). May throw on malformed bodies.</summary>
    private protected abstract IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery);

    /// <summary>The sample events for <paramref name="kind"/> (marked isTest), or null for an unknown kind.</summary>
    private protected abstract IReadOnlyList<WebhookEvent>? Sample(string kind);

    public Task<WebhookOutcome> HandleAsync(WebhookDelivery delivery, CancellationToken ct) => Task.FromResult(Handle(delivery));

    /// <summary>Synchronous core of <see cref="HandleAsync"/> (also used by tests).</summary>
    public WebhookOutcome Handle(WebhookDelivery delivery)
    {
        var (outcome, detail) = Process(delivery);
        LastDelivery = new WebhookDeliveryInfo(delivery.ReceivedAt, outcome, detail);
        if (outcome is WebhookOutcome.Rejected or WebhookOutcome.Failed or WebhookOutcome.NotConfigured)
            Log.Warn(DisplayName, $"Webhook {outcome}: {detail}");
        Changed?.Invoke();
        return outcome;
    }

    private (WebhookOutcome, string) Process(WebhookDelivery delivery)
    {
        if (!IsConfiguredFor(delivery)) return (WebhookOutcome.NotConfigured, $"Enter the {SecretName} in Cuebot to accept {DisplayName} webhooks.");
        bool verified;
        try { verified = Verify(delivery); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { verified = false; }
        if (!verified) return (WebhookOutcome.Rejected, $"The {SecretName} did not match; the delivery was dropped.");
        if (!Enabled) return (WebhookOutcome.Ignored, $"{DisplayName} is switched off.");
        if (Unsupported(delivery) is { } problem) return (WebhookOutcome.Failed, problem);

        IReadOnlyList<WebhookEvent> events;
        try
        {
            events = Map(delivery);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (WebhookOutcome.Failed, $"Could not read the delivery: {ex.Message}");
        }
        if (events.Count == 0) return (WebhookOutcome.Ignored, "Nothing to do for this event type.");

        var published = 0;
        foreach (var e in events)
        {
            // Blank ids (a service that sent none) are dropped, so they never make unrelated deliveries look like duplicates.
            var keys = e.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => $"{Service}:{k.Trim()}")
                .Concat(string.IsNullOrWhiteSpace(delivery.Id) ? [] : [$"delivery:{delivery.Id}"]).ToArray();
            if (Publish(e with { Keys = keys }, dedupe: true)) published++;
        }
        return published > 0
            ? (WebhookOutcome.Handled, string.Join(", ", events.Select(e => e.Type).Distinct()))
            : (WebhookOutcome.Ignored, "Duplicate delivery (already handled).");
    }

    /// <summary>
    /// Publishes a sample event of <paramref name="kind"/> locally (nothing is sent to the service), marked
    /// <c>isTest</c>, so triggers, speech and overlays can be tried out. Returns false for an unknown kind.
    /// </summary>
    public bool SendTestEvent(string kind)
    {
        var events = Sample(kind);
        if (events is null) return false;
        foreach (var e in events) Publish(e, dedupe: false);
        return true;
    }

    /// <summary>Whether this delivery can be verified (custom hooks: its hook exists and has a token).</summary>
    private protected virtual bool IsConfiguredFor(WebhookDelivery delivery) => IsConfigured;

    /// <summary>
    /// A reason a verified delivery cannot be read at all (e.g. Shopify set to XML), reported as Failed; null when fine.
    /// </summary>
    private protected virtual string? Unsupported(WebhookDelivery delivery) => null;

    /// <summary>The "last events" line for events the activity feed does not show.</summary>
    private protected virtual string Summarize(BotEvent e) => e.Type;

    /// <summary>Name of the secret in messages ("verification token", "webhook secret"...).</summary>
    protected virtual string SecretName => "webhook secret";

    private bool Publish(WebhookEvent e, bool dedupe)
    {
        e.Args["service"] = DisplayName;
        e.Args.TryAdd("isTest", false);
        if (dedupe && !_seen.AddAll(e.Keys)) return false;
        var bot = new BotEvent(e.Type, e.Args);
        var summary = WebhookChatAdapter.Describe(bot)?.Title ?? Summarize(bot);
        var isTest = e.Args["isTest"] is true;
        lock (_gate)
        {
            _recent.AddFirst(new WebhookRecord(bot.Time, DisplayName, e.Type, summary, isTest));
            while (_recent.Count > RecentSize) _recent.RemoveLast();
        }
        Bus.Publish(bot);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Common arguments: who, how much, the message (used by the feed, speech and amount filters).</summary>
    private protected static void Common(Dictionary<string, object?> args, string user, double amount, string currency, string message, bool isTest)
    {
        args["user"] = user;
        args["amount"] = double.IsFinite(amount) ? (decimal)amount : 0m;   // Streamer.bot: decimal
        args["currency"] = currency;
        args["formattedAmount"] = Money.Format(amount, currency);
        args["message"] = message;
        args["isTest"] = isTest;
    }

    private protected static Dictionary<string, object?> NewArgs() => new(StringComparer.OrdinalIgnoreCase);
}
