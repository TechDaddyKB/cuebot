using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Webhooks;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;

namespace Cuebot.Portal.Tests;

/// <summary>
/// A real round trip through a running relay Worker: Cuebot connects and proves its key, a "mod device" does the
/// handshake and a ping over its own WebSocket, and a webhook posted to the relay arrives sealed. Runs only when
/// CUEBOT_PORTAL_RELAY_URL is set, e.g. http://localhost:8787 (<c>npx wrangler dev</c> in portal/) or the deployed
/// https://cuebot-portal.&lt;account&gt;.workers.dev.
/// </summary>
public sealed class PortalRelayLiveTests
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL");

    [Fact]
    public async Task Round_trip_through_a_running_relay()
    {
        if (string.IsNullOrWhiteSpace(RelayUrl)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        using var log = new AppLog();
        var directory = new PortalDirectory(new PortalSettings { Enabled = true, SharedWebhooks = true, RelayUrl = RelayUrl }, () => { });
        var webhooks = Channel.CreateUnbounded<WebhookDelivery>();
        await using var client = new PortalRelayClient(directory, log) { WebhookSink = (d, t) => webhooks.Writer.WriteAsync(d, t).AsTask() };

        await client.ConnectAsync(ct);
        while (client.State != ConnectionState.Connected)
        {
            Assert.NotEqual(ConnectionState.Error, client.State);
            await Task.Delay(50, ct);
        }

        // A mod device.
        var connect = PortalRelayClient.ConnectUri(RelayUrl, directory.RelayId)!;
        var deviceUri = new UriBuilder(connect) { Path = connect.AbsolutePath.Replace("/connect", "/c/" + directory.RelayId), Query = "" }.Uri;
        using var device = new ClientWebSocket();
        await device.ConnectAsync(deviceUri, ct);
        var (ephemeral, hello) = PortalHandshake.Start();
        await device.SendAsync(hello, WebSocketMessageType.Binary, true, ct);
        using var session = PortalHandshake.Finish(ephemeral, await ReceiveAsync(device, ct), directory.Identity.Fingerprint);
        ephemeral.Dispose();
        await device.SendAsync(session.Encrypt("""{"op":"ping"}"""u8), WebSocketMessageType.Binary, true, ct);
        Assert.Equal("pong", (string?)JsonNode.Parse(session.Decrypt(await ReceiveAsync(device, ct)))!["op"]);
        Assert.Equal(1, client.DeviceCount);

        // A webhook.
        using var http = new HttpClient();
        var hook = client.WebhookUrl("kofi")!;
        using var response = await http.PostAsync(hook, new StringContent("data=live"), ct);
        Assert.Equal(200, (int)response.StatusCode);
        var delivery = await webhooks.Reader.ReadAsync(ct);
        Assert.Equal("kofi", delivery.Service);
        Assert.Equal("data=live", delivery.BodyText);

        await device.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        while (client.DeviceCount > 0) await Task.Delay(50, ct);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException($"closed: {ws.CloseStatus} {ws.CloseStatusDescription}");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }
}
