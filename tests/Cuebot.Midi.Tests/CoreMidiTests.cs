using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Cuebot.Midi.Backends;

namespace Cuebot.Midi.Tests;

public sealed class MidiByteStreamTests
{
    private static List<string> Split(params byte[][] chunks)
    {
        var stream = new MidiByteStream();
        var messages = new List<string>();
        foreach (var chunk in chunks) stream.Feed(chunk, m => messages.Add(Convert.ToHexString(m)));
        return messages;
    }

    [Fact]
    public void Splits_several_messages_in_one_packet()
    {
        Assert.Equal(["903C64", "B00740", "C005", "F8"], Split([0x90, 0x3C, 0x64, 0xB0, 0x07, 0x40, 0xC0, 0x05, 0xF8]));
    }

    [Fact]
    public void Applies_running_status_and_ignores_orphan_data()
    {
        Assert.Equal(["903C64", "903E00"], Split([0x3C, 0x90, 0x3C, 0x64, 0x3E, 0x00]));
    }

    [Fact]
    public void Joins_sysex_across_packets_and_lets_real_time_bytes_through()
    {
        Assert.Equal(["F8", "F0010203F7"], Split([0xF0, 0x01], [0x02, 0xF8], [0x03, 0xF7]));
    }

    [Fact]
    public void A_status_byte_abandons_an_unterminated_sysex()
    {
        Assert.Equal(["803C00"], Split([0xF0, 0x01, 0x02, 0x80, 0x3C, 0x00]));
    }
}

/// <summary>
/// macOS only: the CoreMIDI backend against virtual endpoints this test creates (no hardware needed). In the
/// "Platform MIDI" collection: two CoreMIDI clients set up at the same time in one process can deadlock (Cuebot only
/// ever makes one), and the backend's client is made before the test's own.
/// </summary>
[Collection("Platform MIDI")]
public sealed class CoreMidiBackendTests
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task Receives_from_a_virtual_source_and_sends_to_a_virtual_destination()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var received = new ConcurrentQueue<byte[]>();
        ReadProc read = (list, _, _) => received.Enqueue([.. CoreMidiBackend.PacketBytes(list).SelectMany(b => b)]);
        using var backend = new CoreMidiBackend();
        Assert.Null(backend.UnavailableReason);
        Assert.Equal(0, MIDIClientCreate(Cf("Cuebot test"), 0, 0, out var client));
        Assert.Equal(0, MIDISourceCreate(client, Cf("Cuebot test source " + suffix), out var source));
        Assert.Equal(0, MIDIDestinationCreate(client, Cf("Cuebot test destination " + suffix), read, 0, out var destination));
        try
        {
            Assert.Contains(backend.ListPorts(MidiDirection.Input), p => p.Name == "Cuebot test source " + suffix);
            Assert.Contains(backend.ListPorts(MidiDirection.Output), p => p.Name == "Cuebot test destination " + suffix);

            var packets = new ConcurrentQueue<MidiPacket>();
            using (backend.OpenInput("Cuebot test source " + suffix, packets.Enqueue, _ => { }))
            {
                Inject(source, [0x90, 0x3C, 0x64, 0xB0, 0x07, 0x40]);
                await Until(() => packets.Count >= 2);
                Assert.Equal(["903C64", "B00740"], packets.Select(p => Convert.ToHexString(p.Data)));
            }

            using var output = backend.OpenOutput("Cuebot test destination " + suffix, _ => { });
            output.Send([0x80, 0x3C, 0x00]);
            output.Send([0xF0, 0x7D, 0x01, 0x02, 0xF7]);
            // CoreMIDI may deliver both messages in one packet list.
            string Got() => string.Concat(received.Select(Convert.ToHexString));
            await Until(() => Got().Length >= 16);
            Assert.Equal("803C00F07D0102F7", Got());
        }
        finally
        {
            MIDIEndpointDispose(source);
            MIDIEndpointDispose(destination);
            MIDIClientDispose(client);
            GC.KeepAlive(read);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static void Inject(uint source, byte[] bytes)
    {
        var list = Marshal.AllocHGlobal(14 + bytes.Length);
        try
        {
            for (var i = 0; i < 14; i++) Marshal.WriteByte(list, i, 0);
            Marshal.WriteInt32(list, 1);
            Marshal.WriteInt16(list, 12, (short)bytes.Length);
            Marshal.Copy(bytes, 0, list + 14, bytes.Length);
            Assert.Equal(0, MIDIReceived(source, list));
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    private static nint Cf(string text) => CFStringCreateWithCString(0, Encoding.UTF8.GetBytes(text + "\0"), 0x08000100);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReadProc(nint packetList, nint refCon, nint connRefCon);

    [DllImport(CoreMidi)] private static extern int MIDIClientCreate(nint name, nint notify, nint refCon, out uint client);
    [DllImport(CoreMidi)] private static extern int MIDIClientDispose(uint client);
    [DllImport(CoreMidi)] private static extern int MIDISourceCreate(uint client, nint name, out uint source);
    [DllImport(CoreMidi)] private static extern int MIDIDestinationCreate(uint client, nint name, ReadProc read, nint refCon, out uint destination);
    [DllImport(CoreMidi)] private static extern int MIDIEndpointDispose(uint endpoint);
    [DllImport(CoreMidi)] private static extern int MIDIReceived(uint source, nint packetList);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, byte[] text, uint encoding);
}
