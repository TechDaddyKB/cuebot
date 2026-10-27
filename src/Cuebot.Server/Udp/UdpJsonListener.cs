using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Server.Udp;

/// <summary>Receives JSON datagrams on one address and port and hands each parsed object to a handler.</summary>
public sealed class UdpJsonListener : IAsyncDisposable
{
    private readonly UdpClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private UdpJsonListener(UdpClient client, string name, Func<JsonElement, Task> handler, AppLog log)
    {
        _client = client;
        Port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => LoopAsync(name, handler, log));
    }

    public int Port { get; }

    /// <summary>Raised after each datagram was handled (or rejected), for tests and the UI.</summary>
    public event Action? DatagramHandled;

    public static UdpJsonListener Start(IPAddress address, int port, string name, Func<JsonElement, Task> handler, AppLog log)
    {
        var client = new UdpClient(new IPEndPoint(address, port));
        return new UdpJsonListener(client, name, handler, log);
    }

    private async Task LoopAsync(string name, Func<JsonElement, Task> handler, AppLog log)
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _client.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // e.g. ICMP port unreachable on Windows after a send; keep listening.
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(datagram.Buffer));
                await handler(doc.RootElement.Clone()).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                log.Warn(name, $"Ignored a datagram from {datagram.RemoteEndPoint.Address} that is not JSON");
            }
            catch (Exception ex)
            {
                log.Warn(name, $"A datagram from {datagram.RemoteEndPoint.Address} failed: {ex.Message}");
            }
            DatagramHandled?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _client.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        _stop.Dispose();
    }
}
