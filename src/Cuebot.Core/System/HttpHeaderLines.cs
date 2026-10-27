using System.Net.Http.Headers;
using System.Text;

namespace Cuebot.Core.SystemTools;

/// <summary>Parses and formats HTTP headers written as "Name: value" lines.</summary>
public static class HttpHeaderLines
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token",
    };

    /// <summary>
    /// Parses header lines; blank lines and lines starting with '#' are skipped.
    /// Error messages never include the line's text, which may hold a secret.
    /// </summary>
    /// <exception cref="FormatException">A line is not in "Name: value" form.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? text)
    {
        var headers = new List<KeyValuePair<string, string>>();
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException($"Header line {i + 1} is not in \"Name: value\" form.");
            var name = line[..colon].Trim();
            if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                throw new FormatException($"Header line {i + 1} has an invalid header name.");
            headers.Add(new(name, line[(colon + 1)..].Trim()));
        }
        return headers;
    }

    public static bool IsSensitive(string name) => SensitiveHeaders.Contains(name);

    /// <summary>Formats response (and content) headers as "Name: value" lines.</summary>
    public static string Format(HttpResponseMessage response)
    {
        var builder = new StringBuilder();
        Append(builder, response.Headers);
        Append(builder, response.Content.Headers);
        return builder.ToString().TrimEnd();
    }

    private static void Append(StringBuilder builder, HttpHeaders headers)
    {
        foreach (var (name, values) in headers) builder.Append(name).Append(": ").AppendJoin(", ", values).Append('\n');
    }
}
