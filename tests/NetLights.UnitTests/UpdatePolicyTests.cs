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
    }
}
