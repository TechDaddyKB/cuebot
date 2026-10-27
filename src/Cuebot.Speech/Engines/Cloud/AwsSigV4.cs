using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Speech;

/// <summary>AWS Signature Version 4 request signing (hand-rolled; no AWS SDK).</summary>
internal static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Intermediate values, exposed for tests against AWS's published examples.</summary>
    internal sealed record Signature(string CanonicalRequest, string StringToSign, string SignedHeaders, string Value,
        string Authorization, string AmzDate);

    /// <summary>
    /// Adds X-Amz-Date and Authorization headers to <paramref name="request"/>. Signs host, x-amz-date and, when
    /// the request has a body, content-type.
    /// </summary>
    public static void Sign(HttpRequestMessage request, byte[] body, string accessKeyId, string secretAccessKey,
        string region, string service, DateTimeOffset now)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (request.Content?.Headers.ContentType is { } contentType) headers["content-type"] = contentType.ToString();
        var signature = Compute(request.Method.Method, request.RequestUri!, headers, body, accessKeyId, secretAccessKey,
            region, service, now);
        request.Headers.TryAddWithoutValidation("X-Amz-Date", signature.AmzDate);
        request.Headers.TryAddWithoutValidation("Authorization", signature.Authorization);
    }

    /// <param name="extraHeaders">Headers to sign besides host and x-amz-date (lower-case names).</param>
    internal static Signature Compute(string method, Uri uri, IReadOnlyDictionary<string, string> extraHeaders,
        byte[] body, string accessKeyId, string secretAccessKey, string region, string service, DateTimeOffset now)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = amzDate[..8];

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
            ["x-amz-date"] = amzDate,
        };
        foreach (var (name, value) in extraHeaders) headers[name.ToLowerInvariant()] = value;

        var canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{CollapseSpaces(h.Value)}\n"));
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join('\n',
            method.ToUpperInvariant(),
            CanonicalPath(uri),
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaders,
            Hex(SHA256.HashData(body)));

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        var authorization = $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new Signature(canonicalRequest, stringToSign, signedHeaders, signature, authorization, amzDate);
    }

    private static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.Length == 0) return "/";
        // Re-encode each segment with RFC 3986 rules (the Uri keeps its own escaping otherwise).
        return string.Join('/', path.Split('/').Select(s => Encode(Uri.UnescapeDataString(s))));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return "";
        return string.Join('&', query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var index = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(index < 0 ? pair : pair[..index]);
                var value = index < 0 ? "" : Uri.UnescapeDataString(pair[(index + 1)..]);
                return (Name: Encode(name), Value: Encode(value));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));
    }

    /// <summary>RFC 3986 encoding: everything except A-Z a-z 0-9 - _ . ~ is percent-encoded.</summary>
    internal static string Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string CollapseSpaces(string value) => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
