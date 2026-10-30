using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cuebot.Server.Hosting;

/// <summary>
/// A minimal Kestrel listener on one address and port running one request delegate, with WebSockets enabled. Uses an
/// empty host: no configuration files, environment variables or logging providers are read, and no process lifetime:
/// the default one handles SIGTERM / Ctrl+C by stopping only this listener and cancelling the process exit, which kept a
/// desktop Cuebot running after SIGTERM. The app handles signals itself.
/// </summary>
internal sealed class KestrelListener : IAsyncDisposable
{
    public const long MaxRequestBodyBytes = 1024 * 1024;

    private readonly WebApplication _app;

    private KestrelListener(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The bound port (the real one when 0 was requested).</summary>
    public int Port { get; }

    /// <summary>Starts listening. Throws (e.g. <see cref="IOException"/> when the port is in use).</summary>
    public static async Task<KestrelListener> StartAsync(IPAddress address, int port, RequestDelegate handler, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            options.Listen(address, port);
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Run(handler);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var bound = port;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses?.FirstOrDefault() is { } first && Uri.TryCreate(first, UriKind.Absolute, out var uri)) bound = uri.Port;
        return new KestrelListener(app, bound);
    }

    /// <summary>A lifetime that does nothing: the listener starts and stops when Cuebot says so, never on a process signal.</summary>
    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Connections that did not close in time are aborted by DisposeAsync.
        }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
