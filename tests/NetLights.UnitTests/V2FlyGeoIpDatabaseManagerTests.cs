using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class V2FlyGeoIpDatabaseManagerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedReleaseIsNotDownloadedAgainAfterFiveMinutes(bool invalidChecksum)
    {
        var clock = new FakeTimeProvider();
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, clock, fixture.Handler);
        await manager.RefreshIfDueAsync(force: true);
        fixture.SetRelease("release-2", invalidChecksum ? ValidDatabase("TR") : [0xff], validChecksum: !invalidChecksum);
        clock.Advance(TimeSpan.FromDays(1));
        await manager.RefreshIfDueAsync(force: false);
        int downloads = fixture.Handler.AssetRequestsWithBinaryAccept;
        Assert.Equal(4, downloads);
        clock.Advance(TimeSpan.FromMinutes(5));
        await manager.RefreshIfDueAsync(force: false);
        Assert.Equal(downloads, fixture.Handler.AssetRequestsWithBinaryAccept);
        Assert.Equal("release-1", manager.Version);
        clock.Advance(TimeSpan.FromDays(1));
        await manager.RefreshIfDueAsync(force: false);
        Assert.Equal(downloads + 2, fixture.Handler.AssetRequestsWithBinaryAccept);
    }

    [Fact]
    public async Task LocalVersionMetadata_IsBoundedBeforeAllocation()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        string path = Path.Combine(fixture.Root, "version.txt");
        await File.WriteAllTextAsync(path, new string('x', 1025));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            V2FlyGeoIpDatabaseManager.ReadLocalDatabaseAsync(path, CancellationToken.None, 1024));
    }

    [Fact]
    public async Task DownloadsAndAtomicallyPublishesValidatedRelease()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, new FakeTimeProvider(), fixture.Handler);

        manager.Start();
        await WaitUntil(() => manager.Current is not null);

        Assert.Equal("release-1", manager.Version);
        Assert.Equal("DE", manager.Current!.Lookup(IPAddress.Parse("203.0.113.20")));
        Assert.Equal(fixture.Data.LongLength, new FileInfo(Path.Combine(fixture.Root, "GeoIP", "geoip.dat")).Length);
        Assert.Equal(2, fixture.Handler.AssetRequestsWithBinaryAccept);
    }

    [Fact]
    public async Task InvalidUpdateLeavesLastGoodDatabaseAndVersionInService()
    {
        var clock = new FakeTimeProvider();
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, clock, fixture.Handler);
        manager.Start();
        await WaitUntil(() => manager.Current is not null);
        V2FlyGeoIpDatabase first = manager.Current!;

        fixture.SetRelease("release-2", ValidDatabase("TR"), validChecksum: false);
        clock.Advance(TimeSpan.FromDays(1));
        await manager.RefreshIfDueAsync(force: false);

        Assert.Equal("DE", manager.Current!.Lookup(IPAddress.Parse("203.0.113.20")));
        Assert.Equal("release-1", File.ReadAllText(Path.Combine(fixture.Root, "GeoIP", "version.txt")));
    }

    [Fact]
    public async Task StalledAssetBodyStopsAtDownloadDeadline()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        fixture.Handler.StallData = true;
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: fixture.Handler,
            requestDeadline: TimeSpan.FromMilliseconds(100));

        await manager.RefreshIfDueAsync(force: true).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(manager.Current);
        Assert.Contains("TaskCanceledException", manager.LastFailure);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightDownload()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        fixture.Handler.StallData = true;
        var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: fixture.Handler,
            requestDeadline: TimeSpan.FromMinutes(5));

        Task refresh = manager.RefreshIfDueAsync(force: true);
        await WaitUntil(() => fixture.Handler.AssetRequestsWithBinaryAccept >= 2);
        manager.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(manager.Current);
        Assert.Null(manager.LastFailure);
    }

    [Fact]
    public async Task RestartLoadsVerifiedDatabaseWhenReleaseApiIsUnavailable()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        using (var first = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: fixture.Handler))
        {
            await first.RefreshIfDueAsync(force: true);
            Assert.Equal("DE", first.Current!.Lookup(IPAddress.Parse("203.0.113.20")));
        }

        using var offline = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: new UnavailableHandler());
        offline.Start();
        await WaitUntil(() => offline.Current is not null);

        Assert.Equal("release-1", offline.Version);
        Assert.Equal("DE", offline.Current!.Lookup(IPAddress.Parse("203.0.113.20")));
    }

    [Fact]
    public async Task PollDuringStartupDoesNotRedownloadVerifiedDatabase()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        string directory = Path.Combine(fixture.Root, "GeoIP");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "geoip.dat"), fixture.Data);
        File.WriteAllText(Path.Combine(directory, "version.txt"), "release-1");
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: fixture.Handler);

        manager.Start();
        for (int i = 0; i < 30; i++) manager.CheckForUpdate();
        await WaitUntil(() => manager.Current is not null);
        await WaitUntil(() => fixture.Handler.LatestRequests > 0);

        Assert.Equal("release-1", manager.Version);
        Assert.Equal(0, fixture.Handler.AssetRequestsWithBinaryAccept);
    }

    [Fact]
    public async Task LocalReaderRejectsOversizedFileBeforeAllocatingPayload()
    {
        using var fixture = new ReleaseFixture("release-1", ValidDatabase("DE"), validChecksum: true);
        string directory = Path.Combine(fixture.Root, "GeoIP");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "geoip.dat");
        using (var file = File.Create(path)) file.SetLength(V2FlyGeoIpDatabase.MaximumFileBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => V2FlyGeoIpDatabaseManager.ReadLocalDatabaseAsync(path, CancellationToken.None));
        using var manager = new V2FlyGeoIpDatabaseManager(fixture.Root, handler: fixture.Handler);
        manager.Start();
        await WaitUntil(() => manager.Current is not null);
        Assert.Equal("DE", manager.Current!.Lookup(IPAddress.Parse("203.0.113.20")));
        Assert.Equal(fixture.Data.Length, new FileInfo(path).Length);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static byte[] ValidDatabase(string country)
    {
        byte[] cidr = Message(0x0a, [203, 0, 113, 0]).Concat(Varint(0x10)).Concat(Varint(24)).ToArray();
        byte[] entry = Message(0x0a, Encoding.ASCII.GetBytes(country)).Concat(Message(0x12, cidr)).ToArray();
        return Message(0x0a, entry);
    }

    private static byte[] Message(byte tag, byte[] value) => [tag, .. Varint((ulong)value.Length), .. value];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private sealed class ReleaseFixture : IDisposable
    {
        public ReleaseFixture(string tag, byte[] data, bool validChecksum)
        {
            Root = Path.Combine(Path.GetTempPath(), "NetLightsGeoReleaseTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Handler = new FakeReleaseHandler();
            SetRelease(tag, data, validChecksum);
        }

        public string Root { get; }
        public FakeReleaseHandler Handler { get; }
        public byte[] Data => Handler.Data;

        public void SetRelease(string tag, byte[] data, bool validChecksum)
            => Handler.SetRelease(tag, data, validChecksum);

        public void Dispose()
        {
            Handler.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeReleaseHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private byte[] _data = [];
        private byte[] _checksum = [];
        private string _tag = "";
        public int LatestRequests { get; private set; }
        public int AssetRequestsWithBinaryAccept { get; private set; }
        public bool StallData { get; set; }
        public byte[] Data { get { lock (_gate) return _data; } }

        public void SetRelease(string tag, byte[] data, bool validChecksum)
        {
            lock (_gate)
            {
                _tag = tag;
                _data = data;
                string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                if (!validChecksum) hash = new string('0', 64);
                _checksum = Encoding.ASCII.GetBytes(hash + "  geoip.dat\n");
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    LatestRequests++;
                    string json = JsonSerializer.Serialize(new
                    {
                        tag_name = _tag,
                        assets = new[]
                        {
                            new { name = "geoip.dat", size = _data.Length, url = "https://api.github.com/repos/v2fly/geoip/releases/assets/1" },
                            new { name = "geoip.dat.sha256sum", size = _checksum.Length, url = "https://api.github.com/repos/v2fly/geoip/releases/assets/2" }
                        }
                    });
                    return Task.FromResult(Response(Encoding.UTF8.GetBytes(json)));
                }
                if (path.EndsWith("/assets/2", StringComparison.Ordinal) || path.EndsWith("/assets/1", StringComparison.Ordinal))
                {
                    if (request.Headers.Accept.Any(value => value.MediaType == "application/octet-stream"))
                        AssetRequestsWithBinaryAccept++;
                    if (path.EndsWith("/assets/1", StringComparison.Ordinal) && StallData)
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StreamContent(new StallingStream())
                        };
                        response.Content.Headers.ContentLength = _data.Length;
                        return Task.FromResult(response);
                    }
                    return Task.FromResult(Response(path.EndsWith("/assets/2", StringComparison.Ordinal) ? _checksum : _data));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static HttpResponseMessage Response(byte[] bytes)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
