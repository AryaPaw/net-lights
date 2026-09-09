using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class UpdateCoordinatorTests
{
    private const string InstallerBody = "installer";

    [Fact]
    public async Task RejectsReleaseWithoutAssetDigest()
    {
        string root = NewRoot();
        try
        {
            using var feed = new GitHubReleaseFeed(new StaticHandler(ManifestJson(digest: null), "application/json"));
            using var coordinator = new UpdateCoordinator(
                feed,
                new PendingUpdateStore(root),
                "1.0.0",
                new StaticHandler(InstallerBody, "application/octet-stream"));
            Assert.False(await coordinator.CheckAsync(CancellationToken.None));
            Assert.Null(coordinator.Pending);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StagesInstallerWhenDigestMatches()
    {
        string root = NewRoot();
        try
        {
            string hash = Sha256Hex(InstallerBody);
            using var feed = new GitHubReleaseFeed(new StaticHandler(ManifestJson("sha256:" + hash), "application/json"));
            using var coordinator = new UpdateCoordinator(
                feed,
                new PendingUpdateStore(root),
                "1.0.0",
                new BytesHandler(Encoding.UTF8.GetBytes(InstallerBody)));
            Assert.True(await coordinator.CheckAsync(CancellationToken.None));
            PendingUpdate? pending = coordinator.Pending;
            Assert.NotNull(pending);
            Assert.Equal("1.0.1", pending.Version);
            Assert.Equal(hash, pending.Sha256, StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(pending.InstallerPath));
            Assert.True(UpdatePolicy.IsInsideRoot(root, pending.InstallerPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DoesNotWriteOutsideStoreWhenAssetNameEscapes()
    {
        string root = NewRoot();
        try
        {
            string hash = Sha256Hex(InstallerBody);
            string json = ManifestJson("sha256:" + hash, @"NetLights-Setup-win-x64-1.0.1.exe\..\..\escape.exe");
            using var feed = new GitHubReleaseFeed(new StaticHandler(json, "application/json"));
            using var coordinator = new UpdateCoordinator(
                feed,
                new PendingUpdateStore(root),
                "1.0.0",
                new BytesHandler(Encoding.UTF8.GetBytes(InstallerBody)));
            Assert.False(await coordinator.CheckAsync(CancellationToken.None));
            Assert.Null(coordinator.Pending);
            string parent = Directory.GetParent(root)!.FullName;
            Assert.False(File.Exists(Path.Combine(parent, "escape.exe")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsRedirectOffAllowList()
    {
        string root = NewRoot();
        try
        {
            string hash = Sha256Hex(InstallerBody);
            using var feed = new GitHubReleaseFeed(new StaticHandler(ManifestJson("sha256:" + hash), "application/json"));
            using var coordinator = new UpdateCoordinator(
                feed,
                new PendingUpdateStore(root),
                "1.0.0",
                new RedirectHandler(new Uri("https://evil.example/setup.exe"), Encoding.UTF8.GetBytes(InstallerBody)));
            Assert.False(await coordinator.CheckAsync(CancellationToken.None));
            Assert.Null(coordinator.Pending);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReportsReadyWhenPendingNewerInstallerAlreadyOnDisk()
    {
        string root = NewRoot();
        try
        {
            var store = new PendingUpdateStore(root);
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.0.1.exe");
            File.WriteAllText(installer, InstallerBody);
            store.WritePending(new PendingUpdate("1.0.1", installer, "abc", DateTimeOffset.UtcNow));
            using var feed = new GitHubReleaseFeed(new StatusHandler(HttpStatusCode.NotFound));
            using var coordinator = new UpdateCoordinator(
                feed,
                store,
                "1.0.0",
                new StatusHandler(HttpStatusCode.NotFound));
            int ready = 0;
            await coordinator.CheckInBackgroundAsync(CancellationToken.None, () => ready++);
            Assert.Equal(1, ready);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetLatestRejectsOversizedManifestWithoutKeepingAllBytesAsSuccess()
    {
        using var feed = new GitHubReleaseFeed(new BytesHandler(new byte[UpdatePolicy.MaxManifestBytes + 8]));
        GitHubRelease? latest = await feed.GetLatestAsync(CancellationToken.None);
        Assert.Null(latest);
    }

    [Fact]
    public async Task ReturnsNullWhenLatestReleaseIsMissing()
    {
        using var feed = new GitHubReleaseFeed(new StatusHandler(HttpStatusCode.NotFound));
        GitHubRelease? release = await feed.GetLatestAsync(CancellationToken.None);
        Assert.Null(release);
    }

    [Fact]
    public void StoreRoundTripsPendingAndFailure()
    {
        string root = NewRoot();
        try
        {
            var store = new PendingUpdateStore(root);
            store.WritePending(new PendingUpdate("1.0.1", Path.Combine(root, "setup.exe"), "abc", DateTimeOffset.UtcNow));
            Assert.Equal("1.0.1", store.ReadPending()?.Version);
            store.WriteResult("1.0.1", false, "Контрольная сумма установщика не совпала.");
            Assert.True(store.TryReadLastFailure(out string? message));
            Assert.Contains("Контрольная", message, StringComparison.Ordinal);
            store.ClearPending();
            Assert.Null(store.ReadPending());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IntegrityVerifierAcceptsSha256Prefix()
    {
        string hash = Sha256Hex(InstallerBody);
        Assert.Equal(hash, IntegrityVerifier.DigestSha256("sha256:" + hash), StringComparer.OrdinalIgnoreCase);
        Assert.True(IntegrityVerifier.Matches("sha256:" + hash, hash));
        Assert.False(IntegrityVerifier.Matches("", hash));
        Assert.Null(IntegrityVerifier.DigestSha256(hash));
        Assert.Null(IntegrityVerifier.DigestSha256("md5:" + hash));
    }

    [Fact]
    public void LauncherReturnsFalseWhenAgentMissing()
    {
        string root = NewRoot();
        try
        {
            var store = new PendingUpdateStore(root);
            string installer = Path.Combine(root, "setup.exe");
            File.WriteAllText(installer, InstallerBody);
            store.WritePending(new PendingUpdate("1.0.1", installer, "abc", DateTimeOffset.UtcNow));
            using var feed = new GitHubReleaseFeed(new StatusHandler(HttpStatusCode.NotFound));
            using var coordinator = new UpdateCoordinator(feed, store, "1.0.0", new StatusHandler(HttpStatusCode.NotFound));
            Assert.False(UpdateAgentLauncher.TryStart(coordinator));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string ManifestJson(string? digest, string? name = null)
    {
        string file = name ?? "NetLights-Setup-win-x64-1.0.1.exe";
        string digestJson = digest is null ? "" : $",\"digest\":\"{digest}\"";
        string encodedName = file.Replace("\\", "\\\\");
        return "{\"tag_name\":\"v1.0.1\",\"prerelease\":false,\"assets\":[{\"name\":\"" + encodedName + "\",\"browser_download_url\":\"https://github.com/AryaPaw/net-lights/releases/download/v1.0.1/NetLights-Setup-win-x64-1.0.1.exe\",\"size\":9" + digestJson + "}]}";
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        private readonly Uri _location;
        private readonly byte[] _body;

        public RedirectHandler(Uri location, byte[] body)
        {
            _location = location;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null
                && request.RequestUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = _location;
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public BytesHandler(byte[] body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _media;

        public StaticHandler(string body, string media)
        {
            _body = body;
            _media = media;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _media)
            });
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status)
        {
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }
}
