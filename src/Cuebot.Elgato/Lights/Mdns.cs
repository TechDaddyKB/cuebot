using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Cuebot.Elgato.Lights;

/// <summary>One resource record of a DNS message (only the fields Cuebot reads are decoded).</summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl)
{
    public string? Target { get; init; }
    public int Port { get; init; }
    public IPAddress? Address { get; init; }
    public IReadOnlyDictionary<string, string>? Txt { get; init; }
}

/// <summary>A service instance found by mDNS (PTR + SRV + A/AAAA + TXT).</summary>
public sealed record MdnsService(string InstanceName, string Host, int Port, IReadOnlyList<IPAddress> Addresses, IReadOnlyDictionary<string, string> Txt);

/// <summary>A packet received in answer to a query.</summary>
public sealed record MdnsPacket(byte[] Data, IPAddress Source);

/// <summary>Sends an mDNS query and collects the packets that arrive during the listen window.</summary>
public interface IMdnsTransport
{
    Task<IReadOnlyList<MdnsPacket>> QueryAsync(byte[] query, TimeSpan listen, CancellationToken ct);
}

/// <summary>
/// Minimal multicast DNS (RFC 6762) / DNS-SD (RFC 6763) client: builds a PTR query and parses answers, with name
/// compression. Enough to find Elgato lights (<c>_elg._tcp.local</c>) without native libraries or a system daemon.
/// </summary>
public static class Mdns
{
    public const string ElgatoService = "_elg._tcp.local";
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;

    /// <summary>A one-question PTR query. <paramref name="unicastResponse"/> sets the QU bit (RFC 6762 §5.4).</summary>
    public static byte[] BuildQuery(string name, ushort id = 0, bool unicastResponse = true)
    {
        var bytes = new List<byte>(64);
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        U16(id);
        U16(0);          // flags: standard query
        U16(1);          // QDCOUNT
        U16(0); U16(0); U16(0);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ArgumentException($"Bad DNS label in '{name}'.");
            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }
        bytes.Add(0);
        U16(TypePtr);
        U16(unicastResponse ? 0x8001 : 0x0001);
        return [.. bytes];
    }

    /// <summary>Every answer, authority and additional record of a response. Throws <see cref="FormatException"/> on malformed packets.</summary>
    public static IReadOnlyList<DnsRecord> Parse(byte[] packet)
    {
        if (packet.Length < 12) throw new FormatException("DNS message shorter than its header.");
        var span = packet.AsSpan();
        int qd = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var rrCount = BinaryPrimitives.ReadUInt16BigEndian(span[6..]) + BinaryPrimitives.ReadUInt16BigEndian(span[8..])
                      + BinaryPrimitives.ReadUInt16BigEndian(span[10..]);
        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            ReadName(packet, ref offset);
            offset += 4;
        }
        var records = new List<DnsRecord>(rrCount);
        for (var i = 0; i < rrCount; i++)
        {
            var name = ReadName(packet, ref offset);
            Need(packet, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
            offset += 10;
            Need(packet, offset, length);
            var dataStart = offset;
            var record = new DnsRecord(name, type, ttl);
            switch (type)
            {
                case TypePtr:
                    var p = dataStart;
                    record = record with { Target = ReadName(packet, ref p) };
                    break;
                case TypeSrv when length >= 7:
                    var s = dataStart + 6;
                    record = record with { Port = BinaryPrimitives.ReadUInt16BigEndian(span[(dataStart + 4)..]), Target = ReadName(packet, ref s) };
                    break;
                case TypeA when length == 4:
                    record = record with { Address = new IPAddress(span.Slice(dataStart, 4)) };
                    break;
                case TypeAaaa when length == 16:
                    record = record with { Address = new IPAddress(span.Slice(dataStart, 16)) };
                    break;
                case TypeTxt:
                    record = record with { Txt = ParseTxt(span.Slice(dataStart, length)) };
                    break;
            }
            records.Add(record);
            offset = dataStart + length;
        }
        return records;
    }

    /// <summary>
    /// Groups records into service instances of <paramref name="service"/>. When a responder only sends the PTR (no SRV
    /// or address), the packet's source address and <paramref name="defaultPort"/> are used.
    /// </summary>
    public static IReadOnlyList<MdnsService> Resolve(IReadOnlyList<DnsRecord> records, string service, IPAddress? source = null, int defaultPort = 9123)
    {
        var result = new List<MdnsService>();
        var instances = records.Where(r => r.Type == TypePtr && SameName(r.Name, service) && r.Target is not null)
            .Select(r => r.Target!).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            var srv = records.FirstOrDefault(r => r.Type == TypeSrv && SameName(r.Name, instance));
            var host = srv?.Target ?? "";
            var addresses = records.Where(r => r.Address is not null && host.Length > 0 && SameName(r.Name, host))
                .Select(r => r.Address!).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
            if (addresses.Count == 0 && source is not null) addresses.Add(source);
            var txt = records.FirstOrDefault(r => r.Type == TypeTxt && SameName(r.Name, instance))?.Txt
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var label = instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? instance[..^(service.Length + 1)] : instance;
            result.Add(new MdnsService(label, host, srv?.Port is > 0 ? srv.Port : defaultPort, addresses, txt));
        }
        return result;
    }

    private static bool SameName(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (i + len > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(i, len));
            i += len;
            if (entry.Length == 0) continue;
            var eq = entry.IndexOf('=');
            if (eq < 0) txt[entry] = "";
            else txt[entry[..eq]] = entry[(eq + 1)..];
        }
        return txt;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            Need(packet, position, 1);
            int len = packet[position];
            if (len == 0)
            {
                position++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                Need(packet, position, 2);
                var pointer = ((len & 0x3F) << 8) | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                if (++jumps > 32 || pointer >= packet.Length) throw new FormatException("DNS name compression loop.");
                position = pointer;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("Unsupported DNS label type.");
            Need(packet, position + 1, len);
            labels.Add(Encoding.UTF8.GetString(packet, position + 1, len));
            position += 1 + len;
        }
        if (!jumped) offset = position;
        return string.Join('.', labels);
    }

    private static void Need(byte[] packet, int offset, int count)
    {
        if (offset < 0 || offset + count > packet.Length) throw new FormatException("DNS message truncated.");
    }
}

/// <summary>
/// Real UDP transport: sends the query from an ephemeral port on every multicast-capable IPv4 interface (responders
/// answer such "one-shot" queries by unicast, RFC 6762 §5.1/§6.7) and also listens on 5353 (shared) for multicast answers.
/// </summary>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    public async Task<IReadOnlyList<MdnsPacket>> QueryAsync(byte[] query, TimeSpan listen, CancellationToken ct)
    {
        var packets = new List<MdnsPacket>();
        var clients = new List<UdpClient>();
        try
        {
            foreach (var address in LocalIPv4Addresses())
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(address, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    clients.Add(client);
                }
                catch (SocketException)
                {
                    // Interface without multicast; skip it.
                }
            }
            if (clients.Count == 0)
            {
                // No usable interface found (e.g. a container): let the OS pick one.
                clients.Add(new UdpClient(new IPEndPoint(IPAddress.Any, 0)));
            }
            var passive = TryOpenPassiveListener();
            if (passive is not null) clients.Add(passive);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(listen);
            var receivers = clients.Select(c => ReceiveAllAsync(c, packets, window.Token)).ToList();
            var target = new IPEndPoint(Mdns.Group, Mdns.Port);
            foreach (var client in clients.Where(c => c != passive))
            {
                try
                {
                    await client.SendAsync(query, target, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Interface went away or has no route to the group.
                }
            }
            await Task.WhenAll(receivers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
        ct.ThrowIfCancellationRequested();
        lock (packets) return packets.ToArray();
    }

    private static async Task ReceiveAllAsync(UdpClient client, List<MdnsPacket> packets, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                // Answers only (QR bit); skips our own query looped back to the passive listener.
                if (result.Buffer.Length >= 12 && (result.Buffer[2] & 0x80) != 0)
                    lock (packets) packets.Add(new MdnsPacket(result.Buffer, result.RemoteEndPoint.Address));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static UdpClient? TryOpenPassiveListener()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Mdns.Port));
            var client = new UdpClient { Client = socket };
            foreach (var address in LocalIPv4Addresses())
            {
                try { client.JoinMulticastGroup(Mdns.Group, address); }
                catch (SocketException) { }
            }
            return client;
        }
        catch (SocketException)
        {
            return null;   // 5353 not shareable here; the one-shot unicast answers still arrive.
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast)
                continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var unicast in props.UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                    yield return unicast.Address;
        }
    }
}
