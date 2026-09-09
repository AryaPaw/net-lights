using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class UpdatePolicyTests
{
    [Fact]
    public void IgnoresPrereleaseAndDowngrade()
    {
        Assert.False(UpdatePolicy.IsNewerStable("1.0.0", "1.1.0", true));
        Assert.False(UpdatePolicy.IsNewerStable("1.2.0", "1.1.0", false));
        Assert.True(UpdatePolicy.IsNewerStable("1.0.0", "v1.0.1", false));
    }

    [Fact]
    public void RejectsOffRepoUrls()
    {
        Assert.False(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://evil.example/AryaPaw/net-lights/x.exe")));
        Assert.True(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://github.com/AryaPaw/net-lights/releases/download/v1.0.1/NetLights-Setup-win-x64-1.0.1.exe")));
        Assert.False(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://github.com/AryaPaw/net-lights/archive/refs/heads/main.zip")));
        Assert.False(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://github.com/evil/x/AryaPaw/net-lights/releases/download/v1/NetLights-Setup-win-x64-1.0.1.exe")));
        Assert.False(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://raw.githubusercontent.com/AryaPaw/net-lights/main/setup.exe")));
        Assert.False(UpdatePolicy.IsAllowedAssetUrl(new Uri("https://objects.githubusercontent.com/github-production-release-asset-2e65be/foo")));
        Assert.True(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://objects.githubusercontent.com/github-production-release-asset-2e65be/foo")));
        Assert.True(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/foo")));
        Assert.True(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://github-releases.githubusercontent.com/github-production-release-asset/foo")));
        Assert.False(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://raw.githubusercontent.com/AryaPaw/net-lights/main/setup.exe")));
        Assert.False(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://evil.example/x")));
        Assert.False(UpdatePolicy.IsAllowedRedirectUrl(new Uri("https://githubusercontent.com.evil.example/x")));
        Assert.False(UpdatePolicy.IsAllowedRedirectUrl(new Uri("http://release-assets.githubusercontent.com/foo")));
    }

    [Fact]
    public void SanitizesInstallerFileName()
    {
        Assert.Equal("NetLights-Setup-win-x64-1.0.1.exe", UpdatePolicy.SafeInstallerFileName("NetLights-Setup-win-x64-1.0.1.exe"));
        Assert.Null(UpdatePolicy.SafeInstallerFileName(@"NetLights-Setup-win-x64-1.0.1.exe\..\..\escape.exe"));
        Assert.Null(UpdatePolicy.SafeInstallerFileName(@"C:\Temp\NetLights-Setup-win-x64-1.0.1.exe"));
    }

    [Fact]
    public void AppliesPendingOnlyWhenNewerThanRunningVersion()
    {
        var pending = new PendingUpdate("1.0.2", @"C:\x\setup.exe", "abc", DateTimeOffset.UtcNow);
        Assert.True(UpdatePolicy.ShouldApplyPending("1.0.1", pending));
        Assert.True(UpdatePolicy.ShouldApplyPending("v1.0.1", pending));
        Assert.False(UpdatePolicy.ShouldApplyPending("1.0.2", pending));
        Assert.False(UpdatePolicy.ShouldApplyPending("1.0.3", pending));
        Assert.False(UpdatePolicy.ShouldApplyPending("1.0.1", null));
        Assert.False(UpdatePolicy.ShouldApplyPending(
            "1.0.2",
            new PendingUpdate("1.0.1", @"C:\x\setup.exe", "abc", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void RestartPathMustBeExistingNetLightsExe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nl-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "NetLights.exe");
            File.WriteAllText(exe, "x");
            Assert.True(UpdatePolicy.IsSafeRestartPath(exe));
            Assert.False(UpdatePolicy.IsSafeRestartPath(Path.Combine(dir, "other.exe")));
            Assert.False(UpdatePolicy.IsSafeRestartPath(null));
            Assert.False(UpdatePolicy.IsSafeRestartPath(""));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RejectsSiblingPathOutsideUpdatesRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-lights-updates-root");
        Assert.True(UpdatePolicy.IsInsideRoot(root, Path.Combine(root, "1.0.1", "setup.exe")));
        Assert.False(UpdatePolicy.IsInsideRoot(root, Path.Combine(root + "-evil", "setup.exe")));
    }
}
