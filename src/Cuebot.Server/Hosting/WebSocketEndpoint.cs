using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Cuebot.Server.Hosting;

/// <summary>Callbacks of a WebSocket server. They run on the connection's own task.</summary>
public interface IWebSocketHandler
{
    Task OnOpenAsync(WebSocketConnection connection);
    Task OnMessageAsync(WebSocketConnection connection, string message);
    void OnClose(WebSocketConnection connection);
}

/// <summary>A WebSocket server on one address, port and path, tracking its connections.</summary>
public sealed class WebSocketEndpoint : IAsyncDisposable
{
    private readonly KestrelListener _listener;
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new();
    private readonly CancellationTokenSource _stopping = new();

    private WebSocketEndpoint(KestrelListener listener) => _listener = listener;

    public int Port => _listener.Port;

    /// <summary>Raised (on a connection's task) when a client connects or disconnects.</summary>
    public event Action? ConnectionsChanged;

    public IReadOnlyList<WebSocketConnection> Connections =>
        _connections.Values.OrderBy(c => c.ConnectedAt).ToArray();

    public WebSocketConnection? Find(string id) => _connections.GetValueOrDefault(id);

    /// <param name="allowedOrigins">Read on every request so settings changes apply without a restart.</param>
    public static async Task<WebSocketEndpoint> StartAsync(IPAddress address, int port, string path, IWebSocketHandler handler,
        Func<IReadOnlyCollection<string>> allowedOrigins, CancellationToken ct = default)
    {
        WebSocketEndpoint? endpoint = null;
        var normalized = NetworkPolicy.NormalizePath(path);
        var listener = await KestrelListener.StartAsync(address, port,
            context => endpoint is null ? Reject(context, 503) : endpoint.HandleAsync(context, normalized, handler, allowedOrigins()),
            ct).ConfigureAwait(false);
        endpoint = new WebSocketEndpoint(listener);
        return endpoint;
    }

    /// <summary>Sends to every open connection (or those <paramref name="filter"/> accepts). Returns how many got it.</summary>
    public async Task<int> BroadcastAsync(string text, Func<WebSocketConnection, bool>? filter = null)
    {
        var targets = _connections.Values.Where(c => filter?.Invoke(c) ?? true).ToArray();
        var sent = await Task.WhenAll(targets.Select(c => c.SendAsync(text))).ConfigureAwait(false);
        return sent.Count(s => s);
    }

    private async Task HandleAsync(HttpContext context, string path, IWebSocketHandler handler, IReadOnlyCollection<string> origins)
    {
        if (!string.Equals(NetworkPolicy.NormalizePath(context.Request.Path.Value), path, StringComparison.OrdinalIgnoreCase))
        {
            await Reject(context, 404).ConfigureAwait(false);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Reject(context, 426, "WebSocket connections only").ConfigureAwait(false);
            return;
        }
        if (!NetworkPolicy.IsOriginAllowed(context.Request.Headers.Origin, origins, context.Request.Host.Value))
        {
            await Reject(context, 403, "Origin not allowed").ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = new WebSocketConnection(socket, $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}");
        _connections[connection.Id] = connection;
        ConnectionsChanged?.Invoke();
        try
        {
            await handler.OnOpenAsync(connection).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, context.RequestAborted);
            await connection.ReceiveAsync(message => handler.OnMessageAsync(connection, message), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            handler.OnClose(connection);
            ConnectionsChanged?.Invoke();
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static Task Reject(HttpContext context, int status, string? message = null)
    {
        context.Response.StatusCode = status;
        return message is null ? Task.CompletedTask : context.Response.WriteAsync(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_connections.Values.Select(c => c.CloseAsync())).ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
