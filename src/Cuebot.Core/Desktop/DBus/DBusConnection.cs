using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Cuebot.Core.Desktop.DBus;

/// <summary>
/// Minimal D-Bus client for the session bus (Linux): unix socket (path or abstract), SASL EXTERNAL auth, method calls
/// with replies, and signals. No file-descriptor passing, little-endian only. Enough for the desktop portals.
/// </summary>
internal sealed class DBusConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private int _serial;

    private DBusConnection(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    public string UniqueName { get; private set; } = "";

    /// <summary>Raised on the reader thread for every signal.</summary>
    public event Action<DBusMessage>? Signal;

    /// <summary>Raised once when the connection drops.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>The session bus address from DBUS_SESSION_BUS_ADDRESS, else $XDG_RUNTIME_DIR/bus. Null when none.</summary>
    public static string? SessionAddress()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(address)) return address;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, "bus")) ? "unix:path=" + Path.Combine(runtime, "bus") : null;
    }

    /// <summary>Socket endpoint for the first usable "unix:" address ("unix:path=..." or "unix:abstract=...").</summary>
    public static UnixDomainSocketEndPoint? ParseAddress(string address)
    {
        foreach (var entry in address.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entry.StartsWith("unix:", StringComparison.Ordinal)) continue;
            var values = entry[5..].Split(',').Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => Unescape(kv[1]));
            if (values.TryGetValue("path", out var path)) return new UnixDomainSocketEndPoint(path);
            if (values.TryGetValue("abstract", out var name)) return new UnixDomainSocketEndPoint("\0" + name);
        }
        return null;
    }

    public static async Task<DBusConnection> ConnectSessionAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("D-Bus is only used on Linux");
        var address = SessionAddress() ?? throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set)");
        var endpoint = ParseAddress(address) ?? throw new InvalidOperationException($"Unsupported D-Bus address '{address}'");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        DBusConnection? connection = null;
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            connection = new DBusConnection(socket);
            await connection.AuthenticateAsync(ct);
            connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
            var hello = await connection.CallAsync(DBusMessage.Call("org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus", "Hello"), ct);
            connection.UniqueName = hello.Body.FirstOrDefault() as string ?? "";
            return connection;
        }
        catch
        {
            if (connection is not null) connection.Dispose();
            else socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends a method call and waits for its reply.</summary>
    /// <exception cref="DBusException">The call returned an error.</exception>
    public async Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct = default)
    {
        var serial = (uint)Interlocked.Increment(ref _serial);
        call.Serial = serial;
        var reply = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[serial] = reply;
        try
        {
            await SendAsync(call.Serialize(), ct);
            var message = await reply.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (message.Type == DBusMessageType.Error)
                throw new DBusException(message.ErrorName ?? "org.freedesktop.DBus.Error.Failed", message.Body.FirstOrDefault() as string ?? "");
            return message;
        }
        finally
        {
            _pending.TryRemove(serial, out _);
        }
    }

    /// <summary>Subscribes to signals matching a rule, e.g. "type='signal',interface='...'".</summary>
    public Task AddMatchAsync(string rule, CancellationToken ct = default) =>
        CallAsync(DBusMessage.Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "AddMatch", "s", rule), ct);

    private async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stream.WriteAsync(data, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        var uid = OperatingSystem.IsLinux() ? GetUid().ToString(CultureInfo.InvariantCulture) : "0";
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(uid)).ToLowerInvariant();
        await _stream.WriteAsync(new byte[] { 0 }, ct);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes($"AUTH EXTERNAL {hex}\r\n"), ct);
        var line = await ReadLineAsync(ct);
        if (line.StartsWith("DATA", StringComparison.Ordinal))
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes("DATA\r\n"), ct);
            line = await ReadLineAsync(ct);
        }
        if (!line.StartsWith("OK", StringComparison.Ordinal)) throw new InvalidOperationException("D-Bus authentication failed: " + line);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes("BEGIN\r\n"), ct);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await _stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("D-Bus closed during authentication");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
            if (sb.Length > 512) throw new InvalidOperationException("D-Bus authentication reply too long");
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var head = new byte[16];
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(head, _cts.Token);
                var total = DBusMessage.TotalLength(head);
                if (total > 128 * 1024 * 1024) throw new InvalidOperationException("D-Bus message too large");
                var data = new byte[total];
                head.CopyTo(data, 0);
                await _stream.ReadExactlyAsync(data.AsMemory(16), _cts.Token);
                var message = DBusMessage.Parse(data);
                switch (message.Type)
                {
                    case DBusMessageType.MethodReturn or DBusMessageType.Error when message.ReplySerial is { } serial:
                        if (_pending.TryGetValue(serial, out var tcs)) tcs.TrySetResult(message);
                        break;
                    case DBusMessageType.Signal:
                        Signal?.Invoke(message);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex; }
        foreach (var pending in _pending.Values) pending.TrySetException(failure ?? new ObjectDisposedException(nameof(DBusConnection)));
        Closed?.Invoke(failure);
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('%')) return value;
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _stream.Dispose();
        try { _reader?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
