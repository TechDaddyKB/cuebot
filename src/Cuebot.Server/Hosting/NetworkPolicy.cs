using System.Net;

namespace Cuebot.Server.Hosting;

/// <summary>Where servers may bind and which browser origins may talk to them.</summary>
internal static class NetworkPolicy
{
    /// <summary>
    /// The address to bind for a configured one: loopback stays as configured; anything else only when LAN access is
    /// allowed, otherwise 127.0.0.1 (and <paramref name="downgraded"/> is true). "localhost" means 127.0.0.1.
    /// </summary>
    public static IPAddress BindAddress(string? configured, bool allowLan, out bool downgraded)
    {
        downgraded = false;
        var text = (configured ?? "").Trim();
        IPAddress address;
        if (text.Length == 0 || string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
        else if (text is "*" or "+") address = IPAddress.Any;
        else if (!IPAddress.TryParse(text, out address!)) throw new FormatException($"'{text}' is not an IP address");

        if (IPAddress.IsLoopback(address) || allowLan) return address;
        downgraded = true;
        return IPAddress.Loopback;
    }

    /// <summary>
    /// Whether a request with this Origin header may use the API. No Origin (native apps, curl, OBS plugins), "null"
    /// (file:// pages, OBS local-file browser sources) and loopback pages are always allowed; other web origins only
    /// when listed (or "*" is listed). This stops arbitrary websites from driving Cuebot through the browser.
    /// </summary>
    public static bool IsOriginAllowed(string? origin, IReadOnlyCollection<string> allowed, string? requestHost = null)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Trim() == "null") return true;
        if (allowed.Any(a => a.Trim() == "*")) return true;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // Pages served by Cuebot itself (e.g. the HTTP server's docks on a LAN address) may use the other servers.
        if (requestHost is not null && Uri.TryCreate("http://" + requestHost, UriKind.Absolute, out var hostUri)
            && hostUri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6   // names could be DNS-rebound
            && string.Equals(uri.Host, hostUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return allowed.Any(a => Uri.TryCreate(a.Trim().TrimEnd('/'), UriKind.Absolute, out var allowedUri)
                                && string.Equals(allowedUri.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(allowedUri.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"/" or "/path" without a trailing slash.</summary>
    public static string NormalizePath(string? path)
    {
        var p = "/" + (path ?? "").Trim().Trim('/');
        return p;
    }
}
