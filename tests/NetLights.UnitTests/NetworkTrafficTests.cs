using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class NetworkTrafficTests
{
    [Fact]
    public async Task StreamCountsSyncAndAsyncPayloadWithoutDoubleCounting()
    {
        string host = Guid.NewGuid().ToString("N") + ".test";
        var inner = new MemoryStream(new byte[64]);
        using (Stream tracked = NetworkTraffic.Track(inner, "test", host))
        {
            Assert.Equal(7, tracked.Read(new byte[7], 0, 7));
            Assert.Equal(5, await tracked.ReadAsync(new byte[5].AsMemory()));
            Assert.Equal(3, await tracked.ReadAsync(new byte[3], 0, 3));
            tracked.Write(new byte[4], 0, 4);
            await tracked.WriteAsync(new byte[8].AsMemory());
            await tracked.WriteAsync(new byte[2], 0, 2);
            NetworkTrafficRow row = Assert.Single(NetworkTraffic.Snapshot(), row => row.Host == host);
            Assert.Equal(15, row.ReceivedBytes);
            Assert.Equal(14, row.SentBytes);
        }
        Assert.False(inner.CanRead);
    }
}
