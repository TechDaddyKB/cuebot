using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;

namespace Cuebot.Portal.Tests;

/// <summary>Push through <see cref="PortalRelayClient"/>: subscribe on the encrypted link, frames in the session's order.</summary>
public sealed class PortalPushRelayTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class FakeRelay : IPortalRelayTransport
    {
        public readonly Channel<RelayMessage?> ToCuebot = Channel.CreateUnbounded<RelayMessage?>();
        public readonly Channel<RelayMessage> FromCuebot = Channel.CreateUnbounded<RelayMessage>();
        public int? CloseCode => null;
        public string? CloseReason => null;
        public Task ConnectAsync(Uri url, CancellationToken ct) => Task.CompletedTask;
        public Task SendTextAsync(string text, CancellationToken ct) => FromCuebot.Writer.WriteAsync(new(false, Encoding.UTF8.GetBytes(text)), ct).AsTask();
        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => FromCuebot.Writer.WriteAsync(new(true, data.ToArray()), ct).AsTask();
        public async Task<RelayMessage?> ReceiveAsync(CancellationToken ct) => await ToCuebot.Reader.ReadAsync(ct);
        public void Text(object json) => ToCuebot.Writer.TryWrite(new(false, JsonSerializer.SerializeToUtf8Bytes(json)));
        public void Binary(uint ch, byte[] payload) => ToCuebot.Writer.TryWrite(new(true, PortalRelayClient.Frame(ch, payload)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<RelayMessage> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            while (true)
            {
                var m = await FromCuebot.Reader.ReadAsync(timeout.Token);
                if (m.Binary || !m.Text.Contains("\"ping\"", StringComparison.Ordinal)) return m;
            }
        }
    }

    /// <summary>Signs the link in as the given member on any "resume" (the real handler checks a device proof).</summary>
    private sealed class FakeSignIn(Guid member, Guid device) : IPortalLinkHandler
    {
        public Task<byte[]?> HandleAsync(PortalLinkContext link, byte[] message, CancellationToken ct)
        {
            var request = JsonNode.Parse(message)!;
            if ((string?)request["op"] == "resume")
            {
                link.MemberId = member;
                link.DeviceId = device;
            }
            return Task.FromResult<byte[]?>(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["ok"] = true, ["req"] = request["req"]?.DeepClone() }));
        }
    }

    private readonly PortalDirectory _directory = new(new PortalSettings { Enabled = true }, () => { });
    private readonly AppLog _log = new();
    private readonly Channel<FakeRelay> _relays = Channel.CreateUnbounded<FakeRelay>();
    private readonly PortalRelayClient _client;

    public PortalPushRelayTests()
    {
        _client = new PortalRelayClient(_directory, _log, () =>
        {
            var relay = new FakeRelay();
            _relays.Writer.TryWrite(relay);
            return relay;
        }, new PortalRelayTimings(Wait, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(95)));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _log.Dispose();
    }

    [Fact]
    public async Task A_signed_in_device_subscribes_and_receives_pushed_frames_over_the_encrypted_link()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var role = _directory.SaveRole(new PortalRole { Name = "Chat", Permissions = [PortalPermission.ViewActivity] });
        var (member, device) = _directory.RedeemInvite(_directory.CreateInvite(role.Id).Secret,
            new ProviderIdentity(IdentityProvider.Google, "1", "m@example.com", "Mod"), PortalKeys.RawPublicKey(key.ExportParameters(false)), "Phone");
        _client.LinkHandler = new FakeSignIn(member.Id, device.Id);
        IPortalPush push = _client.Push;
        push.Register(new PortalTopic("chat") { Permission = PortalPermission.ViewActivity });

        await _client.ConnectAsync();
        using var wait = new CancellationTokenSource(Wait);
        var relay = await _relays.Reader.ReadAsync(wait.Token);
        relay.Text(new { type = "challenge", version = 1, nonce = PortalKeys.Base64Url(RandomNumberGenerator.GetBytes(32)) });
        await relay.NextAsync();   // auth
        relay.Text(new { type = "ready", version = 1, maxDevices = 20 });
        while (_client.State != ConnectionState.Connected) await Task.Delay(10, wait.Token);

        relay.Text(new { type = "open", ch = 12u });
        var (ephemeral, hello) = PortalHandshake.Start();
        relay.Binary(12, hello);
        using var session = PortalHandshake.Finish(ephemeral, (await relay.NextAsync()).Data[4..], _directory.Identity.Fingerprint);
        ephemeral.Dispose();

        async Task<JsonNode> Next()
        {
            var m = await relay.NextAsync();
            Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(m.Data));
            return JsonNode.Parse(session.Decrypt(m.Data.AsSpan(4)))!;
        }

        relay.Binary(12, session.Encrypt("""{"op":"subscribe","req":1,"topic":"chat"}"""u8));
        var refused = await Next();
        Assert.True((bool)refused["signIn"]!);

        relay.Binary(12, session.Encrypt("""{"op":"resume","req":2}"""u8));
        Assert.Equal(2, (int)(await Next())["req"]!);
        relay.Binary(12, session.Encrypt("""{"op":"subscribe","req":3,"topic":"chat","params":{"platform":"all"}}"""u8));
        var answer = await Next();
        Assert.Equal(3, (int)answer["req"]!);
        var sub = (long)answer["sub"]!;

        for (var i = 0; i < 3; i++) push.Publish("chat", new JsonObject { ["n"] = i });
        var frame = await Next();   // within one flush interval
        Assert.Equal("chat", (string?)frame["push"]);
        Assert.Equal(sub, (long)frame["sub"]!);
        Assert.Equal(3, frame["events"]!.AsArray().Count);

        // Answers and pushes share the session's counter: a request still works after pushes.
        relay.Binary(12, session.Encrypt("""{"op":"resume","req":4}"""u8));
        Assert.Equal(4, (int)(await Next())["req"]!);
        Assert.Equal(1, push.Subscribers("chat"));

        relay.Text(new { type = "close", ch = 12u });
        while (_client.DeviceCount > 0 || push.Subscribers("chat") > 0) await Task.Delay(10, wait.Token);   // times out if never cleaned up
    }
}
