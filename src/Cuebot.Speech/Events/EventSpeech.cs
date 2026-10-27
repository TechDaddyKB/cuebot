using System.Globalization;
using System.Text.RegularExpressions;
using Cuebot.Core.Actions;

namespace Cuebot.Speech;

/// <summary>A text to speak for an activity.</summary>
internal sealed record EventUtterance(string Kind, string Text, string Alias);

/// <summary>
/// Turns "chat.activity" events into speech using per-kind templates with weighted random messages.
/// Args used: kind (FeedKind name), platform, user, title, detail, amount; optional richer args from platform
/// modules: bits, viewers, tier, months/cumulative, total/gift, recipient, currency, rewardTitle, cost, input,
/// level, percent, message.
/// </summary>
internal sealed partial class EventSpeech(Random? random = null)
{
    /// <summary>Template variables (without % signs).</summary>
    public static readonly string[] VariableNames =
    [
        "name", "user", "platform", "kind", "description", "level", "gift", "subtier", "tier", "title", "cost", "bits",
        "amount", "percent", "currency", "recipient", "cumulative", "message", "input", "viewers",
    ];

    private readonly Random _random = random ?? Random.Shared;
    private readonly HashSet<string> _followers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();

    public EventUtterance? Build(IReadOnlyDictionary<string, object?> args, EventSpeechSettings settings, FilterProfile profile,
        SpeakChatSettings legacy)
    {
        var variables = Variables(args);
        var kind = (string)variables["kind"]!;
        var template = FindTemplate(settings, kind);
        if (template is null || !template.Enabled) return null;
        if (!MeetsMinimum(kind, variables, settings)) return null;
        if (kind == "Follow" && settings.SuppressDuplicateFollows)
        {
            var key = $"{variables["platform"]}|{variables["user"]}";
            lock (_gate)
            {
                if (!_followers.Add(key)) return null;
            }
        }

        var message = Pick(template.Messages);
        if (message is null) return null;

        // Viewer-written parts go through the filters; a skipped part is simply left out.
        foreach (var key in new[] { "message", "input" })
        {
            var raw = (string)variables[key]!;
            if (raw.Length == 0) continue;
            variables[key] = MessageFilter.Apply(raw, profile, legacy).Text ?? "";
        }

        var text = TextFilter.CollapseWhitespace(VariableResolver.Resolve(message.Text, variables));
        if (text.Length == 0) return null;
        var alias = template.Alias.Length > 0 ? template.Alias : settings.Alias;
        return new EventUtterance(kind, text, alias);
    }

    /// <summary>Forgets which users followed (a new session).</summary>
    public void Reset()
    {
        lock (_gate) _followers.Clear();
    }

    /// <summary>The configured template for a kind, else the built-in default.</summary>
    internal static EventTemplate? FindTemplate(EventSpeechSettings settings, string kind) =>
        settings.Templates.FirstOrDefault(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase))
        ?? DefaultTemplate(kind);

    internal static EventTemplate? DefaultTemplate(string kind)
    {
        var text = kind switch
        {
            "Follow" => "%name% just followed!",
            "Subscription" => "%name% subscribed! %message%",
            "GiftSubscription" => "%name% gifted %gift% subs!",
            "Tip" => "%name% tipped %amount%! %message%",
            "Cheer" => "%name% cheered %bits% bits! %message%",
            "Raid" => "%name% is raiding with %viewers% viewers!",
            "Redemption" => "%name% redeemed %title%. %input%",
            "StreamOnline" => "The stream is live!",
            "StreamOffline" => "The stream has ended.",
            _ => null,
        };
        return text is null
            ? null
            : new EventTemplate
            {
                Kind = kind,
                Enabled = kind is not ("StreamOnline" or "StreamOffline"),
                Messages = [new EventMessage { Text = text }],
            };
    }

    /// <summary>Every template variable, with sensible fallbacks from the generic feed fields.</summary>
    internal static Dictionary<string, object?> Variables(IReadOnlyDictionary<string, object?> args)
    {
        string Get(string key) => args.TryGetValue(key, out var value) ? VariableResolver.Format(value) : "";
        string First(params string[] keys) => keys.Select(Get).FirstOrDefault(v => v.Length > 0) ?? "";
        string NumberIn(string text) => NumberPattern().Match(text) is { Success: true } m ? m.Value : "";

        var user = First("user", "userName", "name");
        var description = Get("title");
        var detail = Get("detail");
        var amount = Get("amount");
        var bits = First("bits");
        if (bits.Length == 0 && amount.Contains("bit", StringComparison.OrdinalIgnoreCase)) bits = NumberIn(amount);
        var kind = First("kind");
        if (kind.Equals("Tip", StringComparison.OrdinalIgnoreCase) && bits.Length > 0) kind = "Cheer";

        var rewardTitle = First("rewardTitle", "reward");
        var redeemed = $"{user} redeemed ";
        if (rewardTitle.Length == 0 && description.StartsWith(redeemed, StringComparison.OrdinalIgnoreCase))
            rewardTitle = description[redeemed.Length..];

        var variables = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = user,
            ["user"] = user,
            ["platform"] = Get("platform"),
            ["kind"] = Normalize(kind),
            ["description"] = description,
            ["level"] = First("level"),
            ["gift"] = First("gift", "total", "count") is { Length: > 0 } gift ? gift : NumberIn(description),
            ["subtier"] = First("subTier", "tier"),
            ["tier"] = First("tier", "subTier"),
            ["title"] = rewardTitle.Length > 0 ? rewardTitle : description,
            ["cost"] = First("cost", "rewardCost"),
            ["bits"] = bits,
            ["amount"] = amount,
            ["percent"] = First("percent"),
            ["currency"] = First("currency"),
            ["recipient"] = First("recipient", "recipientUser"),
            ["cumulative"] = First("cumulative", "cumulativeMonths", "months"),
            ["message"] = First("message") is { Length: > 0 } m ? m : detail,
            ["input"] = First("input", "userInput") is { Length: > 0 } i ? i : detail,
            ["viewers"] = First("viewers") is { Length: > 0 } v ? v : NumberIn(description),
        };
        return variables;
    }

    private static string Normalize(string kind)
    {
        foreach (var known in new[] { "Follow", "Subscription", "GiftSubscription", "Tip", "Cheer", "Raid", "Redemption", "StreamOnline", "StreamOffline", "Other" })
            if (string.Equals(known, kind, StringComparison.OrdinalIgnoreCase)) return known;
        return kind.Length == 0 ? "Other" : kind;
    }

    private static bool MeetsMinimum(string kind, Dictionary<string, object?> variables, EventSpeechSettings settings)
    {
        static double Number(object? value) =>
            double.TryParse(NumberPattern().Match(VariableResolver.Format(value)).Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return kind switch
        {
            "Raid" => Number(variables["viewers"]) >= settings.MinRaidViewers,
            "Cheer" => Number(variables["bits"]) >= settings.MinCheerBits,
            "Tip" => Number(variables["amount"]) >= settings.MinTipAmount,
            _ => true,
        };
    }

    private EventMessage? Pick(IEnumerable<EventMessage> messages)
    {
        var enabled = messages.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        if (enabled.Length == 0) return null;
        var total = enabled.Sum(m => Math.Max(1, m.Weight));
        int roll;
        lock (_gate) roll = _random.Next(total);
        foreach (var message in enabled)
        {
            roll -= Math.Max(1, message.Weight);
            if (roll < 0) return message;
        }
        return enabled[^1];
    }
}
