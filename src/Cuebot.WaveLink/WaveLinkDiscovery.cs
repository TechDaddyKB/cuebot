using System.Text.Json;
using Cuebot.WaveLink.Common;

namespace Cuebot.WaveLink;

/// <summary>Where to look for Wave Link and how to tell the two API generations apart (see PROTOCOL.md).</summary>
internal static class WaveLinkDiscovery
{
    /// <summary>Wave Link 3 listens on one port in this range (also written to ws-info.json).</summary>
    public const int V3FirstPort = 1884, V3LastPort = 1893;

    /// <summary>Wave Link 1.x/2.x takes the first free port from 1824 (its clients scan ten ports; one more costs nothing).</summary>
    public const int LegacyFirstPort = 1824, LegacyLastPort = 1834;

    public sealed record Candidate(int Port, WaveLinkApi Hint);

    /// <summary>Ports to try, best first.</summary>
    public static IReadOnlyList<Candidate> Candidates(WaveLinkSettings settings, int? wsInfoPort)
    {
        if (settings.Port is > 0 and < 65536)
            return [new Candidate(settings.Port, settings.Api switch
            {
                WaveLinkApiPreference.V3 => WaveLinkApi.V3,
                WaveLinkApiPreference.Legacy => WaveLinkApi.Legacy,
                _ => HintFor(settings.Port),
            })];
        var list = new List<Candidate>();
        if (settings.Api != WaveLinkApiPreference.Legacy)
        {
            if (wsInfoPort is > 0 and < 65536) list.Add(new Candidate(wsInfoPort.Value, WaveLinkApi.V3));
            for (var p = V3FirstPort; p <= V3LastPort; p++)
                if (p != wsInfoPort) list.Add(new Candidate(p, WaveLinkApi.V3));
        }
        if (settings.Api != WaveLinkApiPreference.V3)
            for (var p = LegacyFirstPort; p <= LegacyLastPort; p++) list.Add(new Candidate(p, WaveLinkApi.Legacy));
        return list;
    }

    public static WaveLinkApi HintFor(int port) => port is >= V3FirstPort and <= V3LastPort ? WaveLinkApi.V3 : WaveLinkApi.Legacy;

    /// <summary>Where Cuebot looks, for status lines.</summary>
    public static string Describe(WaveLinkSettings settings) => settings.Port is > 0 and < 65536
        ? $"port {settings.Port}"
        : settings.Api switch
        {
            WaveLinkApiPreference.V3 => $"ports {V3FirstPort}-{V3LastPort}",
            WaveLinkApiPreference.Legacy => $"ports {LegacyFirstPort}-{LegacyLastPort}",
            _ => $"ports {V3FirstPort}-{V3LastPort} (Wave Link 3) and {LegacyFirstPort}-{LegacyLastPort} (1.x/2.x)",
        };

    /// <summary>
    /// Classifies a getApplicationInfo result. Wave Link 1.x/2.x answers <c>{appID, appName, interfaceRevision}</c> (revision
    /// 6-7 for 1.6+/2.x); Wave Link 3 answers <c>{appID:"EWL", name, interfaceRevision}</c> (revision 1). A forced API wins.
    /// Returns null when the answer does not look like Wave Link.
    /// </summary>
    public static WaveLinkApi? Classify(JsonElement info, int port, WaveLinkApiPreference preference)
    {
        if (info.ValueKind != JsonValueKind.Object) return null;
        var appId = info.Str("appID");
        var appName = info.Str("appName");
        var name = info.Str("name");
        if (appId.Length == 0 && appName.Length == 0 && name.Length == 0) return null;
        if (preference == WaveLinkApiPreference.V3) return WaveLinkApi.V3;
        if (preference == WaveLinkApiPreference.Legacy) return WaveLinkApi.Legacy;
        if (info.Has("appName")) return WaveLinkApi.Legacy;
        if (info.Has("name")) return WaveLinkApi.V3;
        return HintFor(port);
    }

    /// <summary>Wave Link 3's ws-info.json (written by the app with the port it listens on).</summary>
    public static IEnumerable<string> WsInfoPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsWindows() && local.Length > 0)
            yield return Path.Combine(local, "Packages", "Elgato.WaveLink_g54w8ztgkx496", "LocalState", "ws-info.json");
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Unverified guesses for the macOS app's container; the port scan covers it either way.
            yield return Path.Combine(home, "Library", "Application Support", "Elgato", "WaveLink", "ws-info.json");
            yield return Path.Combine(home, "Library", "Group Containers", "Y93VXCB8Q5.com.elgato.WaveLink", "ws-info.json");
        }
    }

    /// <summary>The port from the first readable ws-info.json (<c>{"port": 1884}</c>), if any.</summary>
    public static int? ReadWsInfoPort(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.Num("port") is { } port && port is > 0 and < 65536) return (int)port;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall back to the scan.
            }
        }
        return null;
    }
}
