using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cuebot.App.Shell;

/// <summary>
/// One Cuebot per data folder. The first instance holds an exclusive lock on <c>instance.lock</c> in the data folder
/// and listens on a local (Unix domain) socket; Windows 10+ and Linux both support those. A later launch that cannot
/// take the lock connects and asks the first instance to show its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string LockFileName = "instance.lock";
    public const string SocketFileName = "instance.sock";
    private const string ActivateMessage = "activate";
    private const int MaxSocketPathBytes = 100;   // sun_path is 108 bytes on Linux and Windows

    private readonly FileStream _lock;
    private readonly Socket? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    private SingleInstance(FileStream lockFile, Socket? listener, string socketPath, string? listenError)
    {
        _lock = lockFile;
        _listener = listener;
        SocketPath = socketPath;
        ListenError = listenError;
        _acceptLoop = listener is null ? Task.CompletedTask : Task.Run(() => AcceptLoopAsync(listener));
    }

    /// <summary>Why this instance cannot be activated by later launches (it still owns the data folder), if so.</summary>
    public string? ListenError { get; }

    public string SocketPath { get; }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event Action? ActivationRequested;

    /// <summary>The socket path for a data folder: inside it when short enough, else a hashed name in the temp folder.</summary>
    public static string SocketPathFor(string dataRoot)
    {
        var inFolder = Path.Combine(dataRoot, SocketFileName);
        if (Encoding.UTF8.GetByteCount(inFolder) <= MaxSocketPathBytes) return inFolder;
        var key = OperatingSystem.IsWindows() ? Path.GetFullPath(dataRoot).ToLowerInvariant() : Path.GetFullPath(dataRoot);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"cuebot-{hash}.sock");
    }

    /// <summary>Claims the data folder. Returns null when another instance already holds it.</summary>
    public static SingleInstance? TryAcquire(string dataRoot) =>
        TryAcquire(Path.Combine(dataRoot, LockFileName), SocketPathFor(dataRoot));

    public static SingleInstance? TryAcquire(string lockPath, string socketPath)
    {
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;   // held by another instance (a sharing violation on Windows, flock on Unix)
        }

        Socket? listener = null;
        try
        {
            if (File.Exists(socketPath)) File.Delete(socketPath);   // stale: the lock proves nobody is listening
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
            return new SingleInstance(lockFile, listener, socketPath, null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Still the only instance (we hold the lock); later launches just cannot bring this window forward.
            listener?.Dispose();
            return new SingleInstance(lockFile, null, socketPath, ex.Message);
        }
    }

    /// <summary>Asks the instance owning <paramref name="dataRoot"/> to show its window. True when it answered.</summary>
    public static Task<bool> ActivateExistingAsync(string dataRoot, TimeSpan timeout) =>
        ActivateExistingAsync(SocketPathFor(dataRoot), timeout, retryUntilTimeout: true);

    public static async Task<bool> ActivateExistingAsync(string socketPath, TimeSpan timeout, bool retryUntilTimeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(ActivateMessage + "\n"), cts.Token);
                var buffer = new byte[16];
                var read = await stream.ReadAsync(buffer, cts.Token);
                return Encoding.UTF8.GetString(buffer, 0, read).StartsWith("ok", StringComparison.Ordinal);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // The first instance may hold the lock but not be listening yet (or be shutting down): retry briefly.
                if (!retryUntilTimeout) return false;
                try { await Task.Delay(200, cts.Token); }
                catch (OperationCanceledException) { return false; }
            }
        }
    }

    private async Task AcceptLoopAsync(Socket listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(Socket client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var stream = new NetworkStream(client, ownsSocket: false);
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer, timeout.Token);
                var message = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (message != ActivateMessage) return;
                try { ActivationRequested?.Invoke(); }
                catch (Exception) { /* a failing handler must not break the handshake */ }
                await stream.WriteAsync("ok\n"u8.ToArray(), timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _listener?.Dispose();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        try { File.Delete(SocketPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _lock.Dispose();
        _stop.Dispose();
    }
}
