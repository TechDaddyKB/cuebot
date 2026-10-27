using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Cuebot.Rumble.Tests.Support;

/// <summary>
/// A text/event-stream response body the test writes to while the code under test reads it, like a server
/// pushing chat events over a long-lived connection.
/// </summary>
public sealed class FakeEventStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private ReadOnlyMemory<byte> _current;

    public HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(this) { Headers = { { "Content-Type", "text/event-stream" } } },
    };

    /// <summary>Sends one SSE event whose data is <paramref name="json"/>.</summary>
    public void SendData(string json) => Push("data: " + json + "\n\n");

    public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>Ends the response (the server closed the connection).</summary>
    public void End() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
            _chunks.Reader.TryRead(out var chunk);
            _current = chunk;
        }
        var n = Math.Min(buffer.Length, _current.Length);
        _current[..n].CopyTo(buffer);
        _current = _current[n..];
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        End();
        base.Dispose(disposing);
    }
}
