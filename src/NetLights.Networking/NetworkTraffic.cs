using System.Net.Http;
using System.Net.Sockets;

namespace NetLights.Networking;

public sealed record NetworkTrafficRow(string Category, string Host, long SentBytes, long ReceivedBytes);

/// <summary>Counts TCP stream payload, including TLS, but not DNS, TCP/IP headers or retransmissions.</summary>
public static class NetworkTraffic
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Category, string Host), TrafficCounter> Counters = [];
    public static DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public static IReadOnlyList<NetworkTrafficRow> Snapshot()
    {
        lock (Gate)
            return Counters.Select(pair => new NetworkTrafficRow(pair.Key.Category, pair.Key.Host,
                Interlocked.Read(ref pair.Value.Sent), Interlocked.Read(ref pair.Value.Received)))
                .OrderByDescending(row => row.SentBytes + row.ReceivedBytes).ToArray();
    }

    internal static Stream Track(Stream stream, string category, string host)
    {
        TrafficCounter counter;
        lock (Gate)
        {
            // Bound state even if the network returns many redirect destinations.
            var key = (category, host);
            if (!Counters.TryGetValue(key, out counter!))
            {
                if (Counters.Count >= 64) key = ("Прочее", "");
                if (!Counters.TryGetValue(key, out counter!)) Counters[key] = counter = new TrafficCounter();
            }
        }
        return new TrafficStream(stream, counter);
    }

    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken, string category)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return Track(new NetworkStream(socket, ownsSocket: true), category, context.DnsEndPoint.Host);
        }
        catch { socket.Dispose(); throw; }
    }

    private sealed class TrafficCounter { public long Sent; public long Received; }

    private sealed class TrafficStream(Stream inner, TrafficCounter counter) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref counter.Received, read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref counter.Received, read);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Interlocked.Add(ref counter.Sent, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref counter.Sent, buffer.Length);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
