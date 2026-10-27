using System.Net;
using System.Text;

namespace Cuebot.Kick.Auth;

/// <summary>The outcome of the browser redirect to the loopback URL.</summary>
internal sealed record OAuthCallbackResult(string? Code, string? Error)
{
    public bool Succeeded => Code is not null;
}

/// <summary>Pure parsing of the OAuth redirect query (code + state, or error).</summary>
internal static class OAuthCallback
{
    public static OAuthCallbackResult Parse(string query, string expectedState)
    {
        var values = ParseQuery(query);
        if (values.TryGetValue("error", out var error))
        {
            var description = values.GetValueOrDefault("error_description");
            return new OAuthCallbackResult(null, error == "access_denied"
                ? "Sign-in was cancelled on Kick."
                : $"Kick reported an error: {(string.IsNullOrEmpty(description) ? error : description)}");
        }
        if (!values.TryGetValue("state", out var state) || state != expectedState)
            return new OAuthCallbackResult(null, "The sign-in response did not match this request (state mismatch). Please try again.");
        if (!values.TryGetValue("code", out var code) || code.Length == 0)
            return new OAuthCallbackResult(null, "Kick did not return an authorization code.");
        return new OAuthCallbackResult(code, null);
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }
}

/// <summary>Waits for the OAuth redirect; the real one listens on localhost, tests substitute a fake.</summary>
internal interface IOAuthReceiver : IDisposable
{
    Task<OAuthCallbackResult> WaitAsync(string expectedState, CancellationToken ct);
}

/// <summary>
/// Waits for one OAuth redirect on http://localhost:{port}/callback with <see cref="HttpListener"/> (works without
/// administrator rights on Windows and on Linux) and answers the browser with a short page.
/// </summary>
internal sealed class LoopbackReceiver : IOAuthReceiver
{
    private readonly HttpListener _listener = new();

    public LoopbackReceiver(int port)
    {
        _listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new KickApiException($"Could not listen on http://localhost:{port}/ for the Kick sign-in ({ex.Message}). " +
                "Pick another redirect port in Kick settings and register the matching redirect URL in your Kick app.");
        }
    }

    public async Task<OAuthCallbackResult> WaitAsync(string expectedState, CancellationToken ct)
    {
        await using var registration = ct.Register(() => _listener.Stop());
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            if (context.Request.Url?.AbsolutePath.TrimEnd('/') != "/callback")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            var result = OAuthCallback.Parse(context.Request.Url.Query, expectedState);
            await RespondAsync(context.Response, result).ConfigureAwait(false);
            return result;
        }
    }

    private static async Task RespondAsync(HttpListenerResponse response, OAuthCallbackResult result)
    {
        var message = result.Succeeded
            ? "Cuebot is signed in to Kick. You can close this tab."
            : WebUtility.HtmlEncode(result.Error ?? "Sign-in failed.");
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Cuebot</title></head>" +
                   $"<body style=\"font-family:sans-serif;padding:3em\"><h2>{message}</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}
