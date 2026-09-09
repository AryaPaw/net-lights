using System.Runtime.InteropServices;
using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class SilentUpdateCoordinatorTests : IDisposable
{
    private readonly string _appDir;
    private readonly string _tempDir;

    public SilentUpdateCoordinatorTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-silent-" + Guid.NewGuid().ToString("N"));
        _appDir = Path.Combine(root, "app");
        _tempDir = Path.Combine(root, "tmp");
        Directory.CreateDirectory(_appDir);
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_appDir, "unins000.exe"), "x");
    }

    [Fact]
    public async Task RunOnce_SkipsTestHost()
    {
        FakeFeed feed = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("testhost", feed, new FakeInstaller()));
        Assert.Equal(SilentUpdateOutcome.Skipped, outcome);
        Assert.Equal(0, feed.LatestCalls);
    }

    [Fact]
    public async Task RunOnce_SkipsPortableLayout()
    {
        File.Delete(Path.Combine(_appDir, "unins000.exe"));
        FakeFeed feed = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, new FakeInstaller()));
        Assert.Equal(SilentUpdateOutcome.Skipped, outcome);
        Assert.Equal(0, feed.LatestCalls);
    }

    [Fact]
    public async Task RunOnce_SkipsWhenDisabled()
    {
        FakeFeed feed = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, new FakeInstaller(), enabled: false));
        Assert.Equal(SilentUpdateOutcome.Skipped, outcome);
        Assert.Equal(0, feed.LatestCalls);
    }

    [Fact]
    public async Task RunOnce_Offline_DoesNotCallFeed()
    {
        FakeFeed feed = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(
            Context("NetLights", feed, new FakeInstaller(), probe: new FakeProbe(false)));
        Assert.Equal(SilentUpdateOutcome.Offline, outcome);
        Assert.Equal(0, feed.LatestCalls);
    }

    [Fact]
    public async Task RunOnce_NoUpdateWhenAlreadyCurrent()
    {
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease { TagName = "v1.0.1", Prerelease = false, Assets = ReleaseAssets("1.0.1") }
        };
        FakeInstaller installer = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, installer, currentVersion: "1.0.1"));
        Assert.Equal(SilentUpdateOutcome.NoUpdate, outcome);
        Assert.Null(installer.Started);
    }

    [Fact]
    public async Task RunOnce_IgnoresPrerelease()
    {
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease { TagName = "v1.1.0", Prerelease = true, Assets = ReleaseAssets("1.1.0") }
        };
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, new FakeInstaller()));
        Assert.Equal(SilentUpdateOutcome.NoUpdate, outcome);
    }

    [Fact]
    public async Task RunOnce_DownloadsAndStartsSilentSetupThenExits()
    {
        string url = "https://github.com/AryaPaw/net-lights/releases/download/v1.0.2/NetLights-Setup-win-x64-1.0.2.exe";
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease { TagName = "v1.0.2", Prerelease = false, Assets = ReleaseAssets("1.0.2") },
            DownloadOk = true
        };
        FakeInstaller installer = new();
        int exits = 0;
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(
            Context("NetLights", feed, installer, currentVersion: "1.0.1", exit: () => exits++));
        Assert.Equal(SilentUpdateOutcome.Applied, outcome);
        Assert.Contains(url, feed.DownloadedUrls);
        Assert.EndsWith("NetLights-Setup-win-x64-1.0.2.exe", installer.Started);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task RunOnce_FailsWhenDownloadFails()
    {
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease { TagName = "v1.0.2", Prerelease = false, Assets = ReleaseAssets("1.0.2") },
            DownloadOk = false
        };
        FakeInstaller installer = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, installer));
        Assert.Equal(SilentUpdateOutcome.Failed, outcome);
        Assert.Null(installer.Started);
    }

    [Fact]
    public async Task RunOnce_FailsWhenDigestMissing()
    {
        string setup = "https://github.com/AryaPaw/net-lights/releases/download/v1.0.2/NetLights-Setup-win-x64-1.0.2.exe";
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease
            {
                TagName = "v1.0.2",
                Assets =
                [
                    new GitHubAsset { Name = "NetLights-Setup-win-x64-1.0.2.exe", BrowserDownloadUrl = new Uri(setup), Digest = null }
                ]
            },
            DownloadOk = true
        };
        FakeInstaller installer = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, installer));
        Assert.Equal(SilentUpdateOutcome.Failed, outcome);
        Assert.Null(installer.Started);
    }

    [Fact]
    public async Task RunOnce_FailsWhenDownloadUrlIsNotGithub()
    {
        FakeFeed feed = new()
        {
            Latest = new GitHubRelease
            {
                TagName = "v1.0.2",
                Assets =
                [
                    new GitHubAsset
                    {
                        Name = "NetLights-Setup-win-x64-1.0.2.exe",
                        BrowserDownloadUrl = new Uri("https://evil.example/setup.exe"),
                        Digest = MatchingDigest()
                    }
                ]
            },
            DownloadOk = true
        };
        FakeInstaller installer = new();
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, installer));
        Assert.Equal(SilentUpdateOutcome.Failed, outcome);
        Assert.Null(installer.Started);
        Assert.Empty(feed.DownloadedUrls);
    }

    [Fact]
    public async Task RunOnce_NoUpdateWhenLatestReleaseMissing()
    {
        FakeFeed feed = new() { NotFound = true };
        SilentUpdateOutcome outcome = await SilentUpdateCoordinator.RunOnce(Context("NetLights", feed, new FakeInstaller()));
        Assert.Equal(SilentUpdateOutcome.NoUpdate, outcome);
    }

    public void Dispose()
    {
        string? root = Directory.GetParent(_appDir)?.FullName;
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private SilentUpdateContext Context(
        string processName,
        IReleaseFeed feed,
        ISetupInstaller installer,
        string currentVersion = "1.0.1",
        bool enabled = true,
        Action? exit = null,
        IInternetProbe? probe = null)
        => new(
            enabled,
            currentVersion,
            processName,
            _appDir,
            _tempDir,
            Architecture.X64,
            probe ?? new FakeProbe(true),
            feed,
            installer,
            exit ?? (() => { }),
            CancellationToken.None);

    private static string MatchingDigest()
        => "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])).ToLowerInvariant();

    private static GitHubAsset[] ReleaseAssets(string version)
    {
        string setup = $"https://github.com/AryaPaw/net-lights/releases/download/v{version}/NetLights-Setup-win-x64-{version}.exe";
        return
        [
            new GitHubAsset
            {
                Name = $"NetLights-Setup-win-x64-{version}.exe",
                BrowserDownloadUrl = new Uri(setup),
                Digest = MatchingDigest()
            }
        ];
    }

    private sealed class FakeProbe : IInternetProbe
    {
        private readonly bool _online;

        public FakeProbe(bool online) => _online = online;

        public Task<bool> IsReachable(CancellationToken cancellationToken) => Task.FromResult(_online);
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public GitHubRelease? Latest { get; set; }
        public bool DownloadOk { get; set; }
        public int LatestCalls { get; private set; }
        public List<string> DownloadedUrls { get; } = [];
        public bool NotFound { get; set; }

        public Task<ReleaseQuery> QueryLatest(CancellationToken cancellationToken)
        {
            LatestCalls++;
            if (NotFound)
            {
                return Task.FromResult(new ReleaseQuery(true, null));
            }

            return Task.FromResult(new ReleaseQuery(false, Latest));
        }

        public Task<bool> Download(Uri url, string destinationPath, CancellationToken cancellationToken)
        {
            DownloadedUrls.Add(url.AbsoluteUri);
            if (!DownloadOk)
            {
                return Task.FromResult(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, [1]);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeInstaller : ISetupInstaller
    {
        public string? Started { get; private set; }

        public bool TryStartSilent(string setupPath)
        {
            Started = setupPath;
            return true;
        }
    }
}
