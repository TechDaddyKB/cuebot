using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Core.Auth;

/// <summary>PKCE (RFC 7636) verifier/challenge pair and an unguessable state value.</summary>
public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Receives the OAuth redirect of a browser sign-in on http://127.0.0.1:&lt;random port&gt;/ (the loopback flow of
/// installed apps, e.g. Google "Desktop app" clients used by YouTube and the Google Drive backups). A minimal
/// one-request HTTP listener on the loopback interface only, so it works the same on Windows and Linux without URL
/// reservations.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _service;

    /// <param name="service">Named on the page the browser shows after the redirect, e.g. "YouTube".</param>
    public LoopbackReceiver(string service)
    {
        _service = service;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
    }

    public string RedirectUri { get; }

    /// <summary>
    /// Waits for the redirect and returns its query parameters (code, state or error). Requests for other paths
    /// (e.g. /favicon.ico) are answered with 404 and ignored.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForRedirectAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, ct).ConfigureAwait(false);
            if (target is null || !target.StartsWith("/?", StringComparison.Ordinal))
            {
                await RespondAsync(stream, "404 Not Found", "", ct).ConfigureAwait(false);
                continue;
            }

            var query = ParseQuery(target[2..]);
            var ok = query.ContainsKey("code");
            await RespondAsync(stream, "200 OK",
                ok ? $"Cuebot is signed in to {_service}. You can close this tab."
                   : $"{_service} sign-in did not complete. Return to Cuebot and try again.", ct).ConfigureAwait(false);
            return query;
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var firstLine = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string text, CancellationToken ct)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Cuebot</title></head><body style=\"font-family:sans-serif\"><p>{WebUtility.HtmlEncode(text)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
