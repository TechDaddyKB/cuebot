using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// User-defined webhooks at <c>…/custom/&lt;name&gt;</c>. The name selects a hook from settings; the request must
/// carry that hook's token as <c>?token=</c> or an <c>X-Cuebot-Token</c> header (constant-time compare). Publishes
/// <c>webhook.custom</c> with the hook name, method, headers, query and body, plus the body's top-level JSON keys
/// (or form fields) as arguments, like Streamer.bot's IFTTT JSON trigger. Dedupe on the relay's delivery id only:
/// two identical calls are two events.
/// </summary>
public sealed class CustomWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "custom";
    public const string TokenHeader = "X-Cuebot-Token";

    /// <summary>Arguments Cuebot sets itself; body keys with these names do not overwrite them.</summary>
    internal static readonly string[] Reserved =
        ["hookName", "method", "path", "query", "headers", "contentType", "body", "service", "isTest"];

    public override string Service => Route;
    public override string DisplayName => "Custom webhook";
    public List<CustomWebhookSettings> Hooks => SettingsFile.Value.Custom;

    /// <summary>Custom hooks are switched on and off one by one.</summary>
    public override bool Enabled => true;

    /// <summary>True when at least one hook has a token (each delivery is then checked against its own hook).</summary>
    public override bool IsConfigured => Hooks.Any(h => h.Token.Trim().Length > 0);

    protected override string SecretName => "custom hook token";

    public override IReadOnlyList<string> TestEventKinds =>
        Hooks.Where(h => h.Name.Trim().Length > 0).Select(h => h.Name.Trim()).DefaultIfEmpty("example").ToArray();

    /// <summary>The hook a delivery is for: the first path segment, case-insensitive.</summary>
    internal CustomWebhookSettings? Find(WebhookDelivery delivery)
    {
        var name = delivery.Path.Trim('/').Split('/')[0];
        name = Uri.UnescapeDataString(name);
        return name.Length == 0 ? null : Hooks.FirstOrDefault(h => string.Equals(h.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A new hook with a generated token, saved.</summary>
    public CustomWebhookSettings AddHook(string name)
    {
        name = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (name.Length == 0) throw new ArgumentException("Enter a name (letters, digits, dashes)");
        if (Hooks.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A hook named '{name}' already exists");
        var hook = new CustomWebhookSettings { Name = name, Token = WebhookVerification.NewToken() };
        Hooks.Add(hook);
        SaveSettings();
        return hook;
    }

    public void RemoveHook(CustomWebhookSettings hook)
    {
        if (Hooks.Remove(hook)) SaveSettings();
    }

    private protected override bool IsConfiguredFor(WebhookDelivery delivery) => Find(delivery) is { } hook && hook.Token.Trim().Length > 0;

    private protected override bool Verify(WebhookDelivery delivery)
    {
        // An unknown hook or one without a token never verifies.
        var hook = Find(delivery);
        if (hook is null) return false;
        var given = FormData.Parse(delivery.Query).TryGetValue("token", out var q) && q.Length > 0 ? q : delivery.Header(TokenHeader).Trim();
        return WebhookVerification.TokensEqual(hook.Token.Trim(), given);
    }

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var hook = Find(delivery)!;
        if (!hook.Enabled) return [];
        return [Map(hook.Name, delivery, isTest: false)];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind)
    {
        var delivery = new WebhookDelivery("test", Route, kind, "POST", "",
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            System.Text.Encoding.UTF8.GetBytes(SampleBody), DateTimeOffset.Now);
        return [Map(kind, delivery, isTest: true)];
    }

    private protected override string Summarize(BotEvent e) =>
        $"{(e.Args.TryGetValue("isTest", out var t) && t is true ? "[Test] " : "")}{e.Args.GetValueOrDefault("hookName")} called ({e.Args.GetValueOrDefault("method")})";

    internal const string SampleBody = """{"value1":"Hello from a custom webhook","value2":42,"value3":true,"user":{"name":"Alice"}}""";

    internal static WebhookEvent Map(string hookName, WebhookDelivery delivery, bool isTest)
    {
        var a = NewArgs();
        var body = delivery.BodyText;
        var contentType = delivery.Header("Content-Type");
        a["hookName"] = hookName;
        a["method"] = delivery.Method;
        a["path"] = delivery.Path;
        var query = FormData.Parse(delivery.Query);
        query.Remove("token");
        a["query"] = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        foreach (var (key, value) in query) a[$"query.{key}"] = value;
        var headers = delivery.Headers
            .Where(h => !string.Equals(h.Key, TokenHeader, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        a["headers"] = JsonSerializer.Serialize(headers);
        a["contentType"] = contentType;
        a["body"] = body;
        a["isTest"] = isTest;

        // Top-level fields of a JSON object or form body become arguments of their own.
        if (JsonReaders.TryParse(body) is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var p in json.EnumerateObject())
                if (!Reserved.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) a.TryAdd(p.Name, JsonReaders.Scalar(p.Value));
        }
        else if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (key, value) in FormData.Parse(body))
                if (!Reserved.Contains(key, StringComparer.OrdinalIgnoreCase)) a.TryAdd(key, value);
        }
        return new WebhookEvent(CustomWebhookEventTypes.Received, [], a);
    }
}
