using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cuebot.Server.Api;

/// <summary>
/// The link a phone scans to pair with this Cuebot (the companion app, docs/IOS_COMPANION_PLAN.md):
/// <c>cuebot://pair?v=1&amp;name=&amp;host=&amp;api=&amp;http=&amp;token=&amp;deckToken=&amp;id=</c>.
/// It holds the API token, so it is shown only on request.
/// </summary>
public static class PairingLink
{
    public const int Version = 1;

    public static string Build(string name, string host, int apiPort, int httpPort, string token, string deckToken, string instanceId)
    {
        static string E(string value) => Uri.EscapeDataString(value);
        return $"cuebot://pair?v={Version}&name={E(name)}&host={E(host)}&api={apiPort}&http={httpPort}" +
               $"&token={E(token)}&deckToken={E(deckToken)}&id={E(instanceId)}";
    }

    /// <summary>This computer's IPv4 addresses a phone on the same network can reach: private ranges first, no loopback.</summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        var found = new List<IPAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !IsLinkLocal(address))
                    found.Add(address);
            }
        }
        return [.. found.Distinct().OrderBy(a => IsPrivate(a) ? 0 : 1).Select(a => a.ToString())];
    }

    /// <summary>10/8, 172.16/12, 192.168/16 and 100.64/10 (carrier-grade NAT, also Tailscale).</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }
}
