using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cuebot.Core.Logging;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>Finds running VTube Studio instances from their API state broadcasts.</summary>
public interface IVtsDiscovery : IDisposable
{
    /// <summary>Starts listening (idempotent; never throws: without the port, discovery just finds nothing).</summary>
    void Start();

    /// <summary>Instances heard from recently, first window first.</summary>
    IReadOnlyList<VtsInstance> Instances { get; }
}

/// <summary>
/// Listens on UDP 47779, where every VTube Studio instance announces its API port about every 2 seconds (even with the
/// API switched off). Other plugins listen on the same port, so the socket is shared (SO_REUSEADDR).
/// </summary>
internal sealed class VtsUdpDiscovery(AppLog log, TimeProvider time) : IVtsDiscovery
{
    /// <summary>An instance not heard from for this long is considered closed.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, VtsInstance> _instances = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _client;
    private int _started;
    private IPAddress[] _ownAddresses = [];

    public IReadOnlyList<VtsInstance> Instances
    {
        get
        {
            var now = time.GetUtcNow();
            return _instances.Values.Where(i => now - i.SeenAt < Stale)
                .OrderBy(i => i.WindowTitle.Length).ThenBy(i => i.WindowTitle, StringComparer.Ordinal).ThenBy(i => i.Port).ToArray();
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            _ownAddresses = OwnAddresses();
            var client = new UdpClient { ExclusiveAddressUse = false };
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, VtsProtocol.DiscoveryPort));
            _client = client;
            _ = ListenAsync(client, _stop.Token);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            log.Debug("VTube Studio", $"Automatic discovery is unavailable (UDP {VtsProtocol.DiscoveryPort}): {ex.Message}");
        }
    }

    private async Task ListenAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            // Only VTube Studio on this computer: the API listens on all interfaces, but Cuebot connects to localhost.
            var from = result.RemoteEndPoint.Address;
            if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
            if (!IPAddress.IsLoopback(from) && !_ownAddresses.Contains(from)) continue;
            if (VtsProtocol.ParseBroadcast(Encoding.UTF8.GetString(result.Buffer), time.GetUtcNow()) is { } instance)
                _instances[instance.InstanceId.Length > 0 ? instance.InstanceId : instance.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)] = instance;
        }
    }

    private static IPAddress[] OwnAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Safe to call more than once (DI disposes the service once per registration).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _client?.Dispose();
        _stop.Dispose();
    }

    private int _disposed;
}
