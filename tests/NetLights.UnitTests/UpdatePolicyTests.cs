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
    }

    [Fact]
    public void SanitizesInstallerFileName()
    {
        Assert.Equal("NetLights-Setup-win-x64-1.0.1.exe", UpdatePolicy.SafeInstallerFileName("NetLights-Setup-win-x64-1.0.1.exe"));
        Assert.Null(UpdatePolicy.SafeInstallerFileName(@"NetLights-Setup-win-x64-1.0.1.exe\..\..\escape.exe"));
        Assert.Null(UpdatePolicy.SafeInstallerFileName(@"C:\Temp\NetLights-Setup-win-x64-1.0.1.exe"));
    }

    [Fact]
    public void RejectsSiblingPathOutsideUpdatesRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-lights-updates-root");
        Assert.True(UpdatePolicy.IsInsideRoot(root, Path.Combine(root, "1.0.1", "setup.exe")));
        Assert.False(UpdatePolicy.IsInsideRoot(root, Path.Combine(root + "-evil", "setup.exe")));
    }
}
