using System.Buffers.Binary;
using System.Text;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Tests;

public sealed class OscCodecTests
{
    [Fact]
    public void Encodes_an_int_message_byte_for_byte()
    {
        var bytes = Osc.Encode(new OscMessage("/OBSBOT/WebCam/General/SetZoom", 50));
        // "/OBSBOT/WebCam/General/SetZoom" is 30 chars → 31 with NUL → padded to 32; ",i" → 4; int → 4.
        Assert.Equal(40, bytes.Length);
        Assert.Equal("/OBSBOT/WebCam/General/SetZoom\0\0", Encoding.ASCII.GetString(bytes, 0, 32));
        Assert.Equal(",i\0\0", Encoding.ASCII.GetString(bytes, 32, 4));
        Assert.Equal(new byte[] { 0, 0, 0, 50 }, bytes[36..40]);
    }

    [Theory]
    [InlineData("/a", 4)]
    [InlineData("/ab", 4)]
    [InlineData("/abc", 8)]
    [InlineData("/abcdefg", 12)]
    public void Strings_are_nul_terminated_and_padded_to_four_bytes(string address, int addressBytes)
    {
        var bytes = Osc.Encode(new OscMessage(address));
        Assert.Equal(addressBytes + 4, bytes.Length);   // "," padded to 4
        Assert.Equal(0, bytes[addressBytes - 1]);
    }

    [Fact]
    public void Every_type_round_trips()
    {
        var message = new OscMessage("/test", 7, -1.5f, "Tiny 2", new byte[] { 1, 2, 3, 4, 5 }, true, false, null, 1L << 40, 0.25);
        var bytes = Osc.Encode(message);
        Assert.Equal(0, bytes.Length % 4);
        var back = Assert.Single(Osc.Decode(bytes));
        Assert.Equal("/test", back.Address);
        Assert.Equal(7, back.Args[0]);
        Assert.Equal(-1.5f, back.Args[1]);
        Assert.Equal("Tiny 2", back.Args[2]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, back.Args[3]);
        Assert.Equal(true, back.Args[4]);
        Assert.Equal(false, back.Args[5]);
        Assert.Null(back.Args[6]);
        Assert.Equal(1L << 40, back.Args[7]);
        Assert.Equal(0.25, back.Args[8]);
        Assert.Equal(7, back.Int(0));
        Assert.Equal(-2, back.Int(1));   // rounds floats (away from zero for .5 under banker's? -1.5 → -2)
        Assert.Equal("Tiny 2", back.Str(2));
        Assert.Null(back.Int(99));
    }

    [Fact]
    public void Utf8_strings_count_bytes_not_chars()
    {
        var back = Assert.Single(Osc.Decode(Osc.Encode(new OscMessage("/n", "Kämera ✓"))));
        Assert.Equal("Kämera ✓", back.Str(0));
    }

    [Fact]
    public void Bundles_are_flattened()
    {
        var a = Osc.Encode(new OscMessage("/a", 1));
        var b = Osc.Encode(new OscMessage("/b", "x"));
        using var stream = new MemoryStream();
        stream.Write("#bundle\0"u8);
        stream.Write(new byte[8]);   // time tag
        foreach (var element in new[] { a, b })
        {
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
            stream.Write(size);
            stream.Write(element);
        }
        var messages = Osc.Decode(stream.ToArray());
        Assert.Equal(["/a", "/b"], messages.Select(m => m.Address));
    }

    [Fact]
    public void Message_without_type_tags_decodes_with_no_arguments()
    {
        Assert.Empty(Assert.Single(Osc.Decode("/old\0\0\0\0"u8.ToArray())).Args);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { (byte)'/', (byte)'a', 0 })]                                   // not a multiple of 4
    [InlineData(new byte[] { (byte)'x', 0, 0, 0 })]                                         // no leading slash
    [InlineData(new byte[] { (byte)'/', (byte)'a', 0, 0, (byte)',', (byte)'i', 0, 0 })]     // int missing
    [InlineData(new byte[] { (byte)'/', (byte)'a', 0, 0, (byte)',', (byte)'q', 0, 0 })]     // unknown tag
    [InlineData(new byte[] { (byte)'/', (byte)'a', (byte)'b', (byte)'c' })]                 // unterminated
    public void Malformed_packets_throw_FormatException(byte[] packet)
    {
        Assert.Throws<FormatException>(() => Osc.Decode(packet));
    }

    [Fact]
    public void Cannot_encode_bad_addresses_or_types()
    {
        Assert.Throws<ArgumentException>(() => Osc.Encode(new OscMessage("noslash")));
        Assert.Throws<ArgumentException>(() => Osc.Encode(new OscMessage("/a", DateTime.Now)));
        Assert.Throws<ArgumentException>(() => Osc.Encode(new OscMessage("/a", "nul\0inside")));
    }

    [Fact]
    public void Tcp_frames_are_length_prefixed_and_reassembled_across_reads()
    {
        var one = Osc.Frame(Osc.Encode(new OscMessage("/one", 1)));
        var two = Osc.Frame(Osc.Encode(new OscMessage("/two", "zwei")));
        Assert.Equal(one.Length - 4, BinaryPrimitives.ReadInt32BigEndian(one));

        var all = one.Concat(two).ToArray();
        var decoder = new OscStreamDecoder();
        var received = new List<OscMessage>();
        foreach (var chunk in all.Chunk(3)) received.AddRange(decoder.Push(chunk));
        Assert.Equal(["/one", "/two"], received.Select(m => m.Address));
    }

    [Fact]
    public void Little_endian_lengths_from_Tail_hardware_are_accepted()
    {
        var packet = Osc.Encode(new OscMessage("/le", 5));
        var frame = new byte[4 + packet.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, packet.Length);
        packet.CopyTo(frame, 4);
        Assert.Equal("/le", Assert.Single(new OscStreamDecoder().Push(frame)).Address);
    }

    [Fact]
    public void Garbage_on_the_stream_is_rejected_but_one_bad_packet_is_skipped()
    {
        Assert.Throws<FormatException>(() => new OscStreamDecoder().Push(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n")));

        var bad = Osc.Frame("nope"u8.ToArray());
        var good = Osc.Frame(Osc.Encode(new OscMessage("/ok")));
        Assert.Equal("/ok", Assert.Single(new OscStreamDecoder().Push(bad.Concat(good).ToArray())).Address);
    }

    [Fact]
    public void ToString_is_readable()
    {
        Assert.Equal("/x 1 2.5 \"s\" true nil", new OscMessage("/x", 1, 2.5f, "s", true, null).ToString());
    }
}
