using System.Net;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Tests.Support;

namespace Cuebot.Elgato.Tests;

public sealed class MdnsTests
{
    [Fact]
    public void Query_is_a_one_question_ptr_query_with_the_unicast_response_bit()
    {
        var query = Mdns.BuildQuery("_elg._tcp.local");
        byte[] expected =
        [
            0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            4, (byte)'_', (byte)'e', (byte)'l', (byte)'g',
            4, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
            5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            0, 12, 0x80, 1,
        ];
        Assert.Equal(expected, query);
        Assert.Equal(0x01, Mdns.BuildQuery("_elg._tcp.local", unicastResponse: false)[^1]);
        Assert.Equal(0x00, Mdns.BuildQuery("_elg._tcp.local", unicastResponse: false)[^2]);
    }

    [Fact]
    public void Parses_a_compressed_elgato_answer_into_a_service()
    {
        var packet = LightsServiceTests.ElgatoAnswer("Elgato Key Light 7F3A", "elgato-key-light-7f3a.local", 9123, "192.168.1.42");
        var records = Mdns.Parse(packet);
        Assert.Equal(4, records.Count);
        Assert.Equal("Elgato Key Light 7F3A._elg._tcp.local", records[0].Target);

        var service = Assert.Single(Mdns.Resolve(records, Mdns.ElgatoService));
        Assert.Equal("Elgato Key Light 7F3A", service.InstanceName);
        Assert.Equal("elgato-key-light-7f3a.local", service.Host);
        Assert.Equal(9123, service.Port);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), Assert.Single(service.Addresses));
        Assert.Equal("Elgato Key Light 20GAK9901", service.Txt["md"]);
        Assert.Equal("3C:6A:9D:14:7F:3A", service.Txt["id"]);
    }

    [Fact]
    public void Hand_written_packet_with_question_section_and_pointers_parses()
    {
        // A response that echoes the question (as legacy unicast answers do) and points every name back into it.
        byte[] packet =
        [
            0x12, 0x34, 0x84, 0x00, 0, 1, 0, 2, 0, 0, 0, 0,
            // question @12: _elg._tcp.local PTR IN
            4, (byte)'_', (byte)'e', (byte)'l', (byte)'g', 4, (byte)'_', (byte)'t', (byte)'c', (byte)'p', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            0, 12, 0, 1,
            // answer: name -> @12, PTR, IN, ttl 120, rdlength 5: label "kl" + pointer @12
            0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 120, 0, 5, 2, (byte)'k', (byte)'l', 0xC0, 12,
            // additional: A record for kl._elg._tcp.local (pointer to the PTR target @45)
            0xC0, 45, 0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4, 10, 0, 0, 7,
        ];
        var records = Mdns.Parse(packet);
        Assert.Equal("_elg._tcp.local", records[0].Name);
        Assert.Equal("kl._elg._tcp.local", records[0].Target);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), records[1].Address);

        // No SRV: the packet source and the default port are used.
        var service = Assert.Single(Mdns.Resolve(records, Mdns.ElgatoService, IPAddress.Parse("10.0.0.9")));
        Assert.Equal("kl", service.InstanceName);
        Assert.Equal(9123, service.Port);
        Assert.Equal(IPAddress.Parse("10.0.0.9"), Assert.Single(service.Addresses));
    }

    [Fact]
    public void Malformed_packets_throw_format_exceptions()
    {
        Assert.Throws<FormatException>(() => Mdns.Parse([0, 0, 0x84]));
        byte[] loop = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12];
        Assert.Throws<FormatException>(() => Mdns.Parse(loop));
        var truncated = LightsServiceTests.ElgatoAnswer("x", "x.local", 9123, "10.0.0.1")[..40];
        Assert.Throws<FormatException>(() => Mdns.Parse(truncated));
    }

    [Fact]
    public async Task Real_udp_transport_sends_and_returns_within_the_window()
    {
        // No lights are expected here; this checks the sockets open, send and close cleanly on this OS.
        var started = DateTime.UtcNow;
        _ = await new UdpMdnsTransport().QueryAsync(Mdns.BuildQuery(Mdns.ElgatoService), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Ignores_other_services_in_the_same_packet()
    {
        var packet = new DnsPacketBuilder()
            .Ptr("_airplay._tcp.local", "TV._airplay._tcp.local")
            .Ptr("_elg._tcp.local", "Ring._elg._tcp.local")
            .Srv("Ring._elg._tcp.local", 9123, "ring.local")
            .A("ring.local", "192.168.0.5")
            .Build();
        var service = Assert.Single(Mdns.Resolve(Mdns.Parse(packet), Mdns.ElgatoService));
        Assert.Equal("Ring", service.InstanceName);
    }
}
