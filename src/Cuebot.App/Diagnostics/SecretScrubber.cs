using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.App.Diagnostics;

/// <summary>What the scrubber replaces besides secrets, which are always removed.</summary>
public sealed class ScrubOptions
{
    /// <summary>Keep account names and ids (logins, channel names, user ids). Off by default.</summary>
    public bool IncludeAccountNames { get; init; }

    /// <summary>Account names found in settings files; replaced in free text unless <see cref="IncludeAccountNames"/>.</summary>
    public IReadOnlyCollection<string> KnownAccountNames { get; init; } = [];

    /// <summary>The user's home folder; replaced with <c>~</c> in paths so the OS user name does not leak.</summary>
    public string? HomeFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static ScrubOptions Default { get; } = new();
}

/// <summary>
/// Removes secrets from text that may leave the machine: settings JSON (by property name, then every string value as
/// free text) and free text such as log lines (key=value pairs, auth headers, URL parameters, JWTs, webhook URLs,
/// well-known token formats and long opaque strings). It errs on the side of removing too much.
/// </summary>
public static partial class SecretScrubber
{
    public const string Redacted = "[redacted]";
    public const string AccountPlaceholder = "[account]";
    public const string EmailPlaceholder = "[email]";

    // ---- Free text ----

    /// <summary>Parts of key names that mark the value as a secret (matched inside longer names: SocketToken, apiKey).</summary>
    private const string SecretKeyName =
        @"[\w.-]*?(?:token|secret|passw(?:or)?d|passphrase|pwd|api[_-]?key|access[_-]?key(?:[_-]?id)?|private[_-]?key|"
        + @"stream[_-]?key|subscription[_-]?key|license[_-]?key|client[_-]?secret|jwt|authorization|auth[_-]?(?:code|key)|"
        + @"cookie|session[_-]?id|signature|credentials?|webhook(?:[_-]?url)?|verifier)[\w.-]*|key|auth|pin|otp";

    [GeneratedRegex(@"https?://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/webhooks/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordWebhook();

    [GeneratedRegex(@"https://hooks\.slack\.com/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlackWebhook();

    /// <summary>IFTTT Webhooks URLs carry the key in the path: <c>…/with/key/&lt;key&gt;</c>.</summary>
    [GeneratedRegex(@"(/with/key/)[^\s/?#""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex IftttKeyPath();

    [GeneratedRegex(@"\beyJ[\w-]{4,}\.[\w-]{4,}\.[\w-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b((?:proxy-)?authorization)(\s*[:=]\s*)(?:(?:bearer|basic|oauth|token|digest)\s+)?[^\s,;""']+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"\b(bearer|basic|oauth)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"\boauth:[A-Za-z0-9]{10,}", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchIrcToken();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_\w{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|sk-[A-Za-z0-9_-]{20,}|"
                    + @"xox[abprs]-[\w-]{10,}|(?:sk|pk|rk)_(?:live|test)_\w{10,})")]
    private static partial Regex KnownTokenFormat();

    /// <summary>URL query/fragment parameters that carry secrets (OAuth codes, tokens, keys, signatures).</summary>
    [GeneratedRegex(@"([?&#;])((?:access_token|refresh_token|id_token|token|code|state|key|api_key|apikey|client_secret|secret|password|"
                    + @"pass|sig|signature|auth|jwt|session|sessionid|x-amz-signature|x-amz-credential)=)[^&#\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretUrlParameter();

    /// <summary><c>name: "value"</c>, <c>name=value</c>, <c>"name": "value"</c> where the name looks secret.</summary>
    [GeneratedRegex(@"(?<key>[""']?\b(?:" + SecretKeyName + @")\b[""']?)(?<sep>\s*[:=]\s*)(?:(?<q>[""'])(?<qv>(?:\\.|(?!\k<q>).)*)\k<q>|(?<v>[^\s&,;}\]""']+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();

    [GeneratedRegex(@"[\w.+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    /// <summary>Long runs of letters and digits (at least one of each): API keys, access tokens, client secrets.</summary>
    [GeneratedRegex(@"\b(?=[A-Za-z0-9_]*[0-9])(?=[A-Za-z0-9_]*[A-Za-z])[A-Za-z0-9_]{24,}\b")]
    private static partial Regex OpaqueToken();

    /// <summary>Removes secrets from free text (log lines, exception messages, typed text).</summary>
    public static string ScrubText(string? text, ScrubOptions? options = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        options ??= ScrubOptions.Default;
        var s = text;
        s = DiscordWebhook().Replace(s, "https://discord.com/api/webhooks/" + Redacted);
        s = SlackWebhook().Replace(s, "https://hooks.slack.com/" + Redacted);
        s = IftttKeyPath().Replace(s, m => m.Groups[1].Value + Redacted);
        s = Jwt().Replace(s, Redacted);
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        s = AuthScheme().Replace(s, m => m.Groups[1].Value + " " + Redacted);
        s = TwitchIrcToken().Replace(s, "oauth:" + Redacted);
        s = KnownTokenFormat().Replace(s, Redacted);
        s = SecretUrlParameter().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        s = SecretPair().Replace(s, ReplacePair);
        s = Email().Replace(s, EmailPlaceholder);
        s = OpaqueToken().Replace(s, Redacted);
        if (!options.IncludeAccountNames) s = ReplaceAccountNames(s, options.KnownAccountNames);
        s = ReplaceHome(s, options.HomeFolder);
        return s;
    }

    private static string ReplacePair(Match m)
    {
        var quoted = m.Groups["q"].Success;
        var value = quoted ? m.Groups["qv"].Value : m.Groups["v"].Value;
        // Unset values and flags carry no secret and tell a reader whether something is configured.
        if (value.Length == 0 || value == Redacted || value is "true" or "false" or "null" or "True" or "False" || value.StartsWith('[')) return m.Value;
        var q = m.Groups["q"].Value;
        return m.Groups["key"].Value + m.Groups["sep"].Value + q + Redacted + q;
    }

    private static string ReplaceAccountNames(string s, IReadOnlyCollection<string> names)
    {
        foreach (var name in names.Where(n => n.Length >= 3).OrderByDescending(n => n.Length))
            s = Regex.Replace(s, @"(?<![\w])" + Regex.Escape(name) + @"(?![\w])", AccountPlaceholder, RegexOptions.IgnoreCase);
        return s;
    }

    private static string ReplaceHome(string s, string? home)
    {
        if (string.IsNullOrEmpty(home) || home.Length < 4) return s;
        s = s.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        var alt = home.Replace('\\', '/');
        return alt == home ? s : s.Replace(alt, "~", StringComparison.OrdinalIgnoreCase);
    }

    // ---- JSON ----

    private static readonly string[] SecretNameParts =
    [
        "token", "secret", "password", "passwd", "passphrase", "apikey", "accesskey", "privatekey", "streamkey", "jwt",
        "cookie", "credential", "signature", "authorization", "authcode", "oauth", "sessionid", "verifier", "webhook",
    ];

    private static readonly HashSet<string> SecretNames = ["key", "code", "auth", "pin", "otp", "pwd", "pass", "sig"];

    private static readonly HashSet<string> NotSecretKeyNames = ["hotkey", "sortkey", "groupkey", "exclusivekey"];

    private static readonly HashSet<string> AccountNames =
    [
        "login", "username", "user", "displayname", "channel", "channelname", "channelslug", "channeltitle", "channelid",
        "signedinuser", "broadcaster", "broadcasterlogin", "broadcastername", "broadcasterid", "botlogin", "botname",
        "botusername", "botchannelid", "botid", "botuserid", "userid", "accountname", "account", "accountid", "handle",
        "nickname", "nick", "streamername", "ownername", "slug", "customurl", "chatroomid",
    ];

    /// <summary>True for property names whose values are secrets (case and separators ignored).</summary>
    public static bool IsSecretName(string name)
    {
        var n = Normalize(name);
        if (SecretNames.Contains(n)) return true;
        if (n.EndsWith("key", StringComparison.Ordinal) && !NotSecretKeyNames.Contains(n)) return true;
        foreach (var part in SecretNameParts)
            if (n.Contains(part, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsAccountName(string name) => AccountNames.Contains(Normalize(name));

    private static bool IsEmailName(string name) => Normalize(name).Contains("email", StringComparison.Ordinal);

    private static string Normalize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>Account names and ids found under account-like properties (logins, channel names, user ids).</summary>
    public static IReadOnlyCollection<string> CollectAccountNames(string? json)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryParse(json) is { } node) Collect(node, found);
        return found;

        static void Collect(JsonNode? node, HashSet<string> found)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        if (IsAccountName(name) && value is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0) found.Add(s.Trim());
                        else Collect(value, found);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) Collect(item, found);
                    break;
            }
        }
    }

    /// <summary>
    /// Removes secrets from a settings file: values under secret-looking names become <see cref="Redacted"/> (empty
    /// strings and flags stay, so "not set" remains visible), account names and e-mail addresses are replaced, and every
    /// other string is scrubbed as free text. Text that is not JSON is scrubbed as free text.
    /// </summary>
    public static string ScrubJson(string? json, ScrubOptions? options = null)
    {
        options ??= ScrubOptions.Default;
        if (TryParse(json) is not { } node) return ScrubText(json, options);
        var scrubbed = ScrubNode(node, options);
        return scrubbed?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
    }

    private static JsonNode? ScrubNode(JsonNode? node, ScrubOptions options)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (name, value) in obj) result[name] = ScrubProperty(name, value, options);
                return result;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array) list.Add(ScrubNode(item, options));
                return list;
            case JsonValue value when value.TryGetValue<string>(out var s):
                return JsonValue.Create(ScrubText(s, options));
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? ScrubProperty(string name, JsonNode? value, ScrubOptions options)
    {
        if (IsSecretName(name))
        {
            if (value is null) return null;
            if (value is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s) && s.Length == 0) return JsonValue.Create("");
                if (v.TryGetValue<bool>(out var b)) return JsonValue.Create(b);
            }
            return JsonValue.Create(Redacted);
        }
        if (IsEmailName(name) && value is JsonValue ev && ev.TryGetValue<string>(out var email))
            return JsonValue.Create(email.Length == 0 ? "" : EmailPlaceholder);
        if (IsAccountName(name) && value is JsonValue av && !options.IncludeAccountNames)
        {
            if (av.TryGetValue<string>(out var account)) return JsonValue.Create(account.Length == 0 ? "" : AccountPlaceholder);
            if (av.GetValueKind() == JsonValueKind.Number) return JsonValue.Create(AccountPlaceholder);
        }
        return ScrubNode(value, options);
    }
}
