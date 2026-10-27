using System.Net;
using System.Text;

namespace Cuebot.Donations.TreatStream;

/// <summary>The outcome of the browser redirect to the loopback URL.</summary>
internal sealed record OAuthRedirect(string? Code, string? Error);

/// <summary>Waits for one OAuth redirect; the real one listens on localhost, tests substitute a fake.</summary>
internal interface IOAuthRedirectReceiver : IDisposable
{
    Task<OAuthRedirect> WaitAsync(string expectedState, CancellationToken ct);
}

internal static class OAuthRedirects
{
    /// <summary>Parses <c>?code=…&amp;state=…</c> or <c>?error=…</c>, checking the state.</summary>
    public static OAuthRedirect Parse(string query, string expectedState, string service)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            values.TryAdd(key, eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')));
        }
        if (values.TryGetValue("error", out var error))
            return new OAuthRedirect(null, error == "access_denied" ? $"Sign-in was cancelled on {service}." :
                $"{service} reported an error: {(values.GetValueOrDefault("error_description") is { Length: > 0 } d ? d : error)}");
        if (values.GetValueOrDefault("state") != expectedState)
            return new OAuthRedirect(null, "The sign-in response did not match this request (state mismatch). Please try again.");
        return values.GetValueOrDefault("code") is { Length: > 0 } code
            ? new OAuthRedirect(code, null)
            : new OAuthRedirect(null, $"{service} did not return an authorization code.");
    }
}

/// <summary>Answers one redirect on http://localhost:{port}/callback with <see cref="HttpListener"/> (no admin rights needed).</summary>
internal sealed class LoopbackRedirectReceiver : IOAuthRedirectReceiver
{
    private readonly HttpListener _listener = new();
    private readonly string _service;

    public LoopbackRedirectReceiver(int port, string service)
    {
        _service = service;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Could not listen on http://localhost:{port}/ for the {service} sign-in ({ex.Message}). " +
                "Pick another redirect port and register the matching redirect URL in your app.");
        }
    }

    public async Task<OAuthRedirect> WaitAsync(string expectedState, CancellationToken ct)
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
            var result = OAuthRedirects.Parse(context.Request.Url.Query, expectedState, _service);
            var message = result.Code is not null
                ? $"Cuebot is signed in to {_service}. You can close this tab."
                : WebUtility.HtmlEncode(result.Error ?? "Sign-in failed.");
            var bytes = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Cuebot</title></head>" +
                $"<body style=\"font-family:sans-serif;padding:3em\"><h2>{message}</h2></body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Close();
            }
            return result;
        }
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}
