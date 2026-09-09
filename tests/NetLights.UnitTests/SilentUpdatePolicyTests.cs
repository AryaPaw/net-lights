using System.Runtime.InteropServices;
using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class SilentUpdatePolicyTests
{
    [Theory]
    [InlineData("testhost", false)]
    [InlineData("testhost.net48", false)]
    [InlineData("vstest.console", false)]
    [InlineData("NetLights", true)]
    public void AllowsBackgroundProcess_SkipsTestHosts(string name, bool allowed)
        => Assert.Equal(allowed, SilentUpdatePolicy.AllowsBackgroundProcess(name));

    [Fact]
    public void RidFor_SupportsPackagedX64Only()
    {
        Assert.Equal("win-x64", SilentUpdatePolicy.RidFor(Architecture.X64));
        Assert.Null(SilentUpdatePolicy.RidFor(Architecture.Arm64));
        Assert.Null(SilentUpdatePolicy.RidFor(Architecture.X86));
    }

    [Fact]
    public void IsSafeSetupPath_RequiresKnownNameUnderTemp()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-silent-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ok = Path.Combine(root, "NetLights-Setup-win-x64-1.0.2.exe");
            Assert.True(SilentUpdatePolicy.IsSafeSetupPath(ok, root));
            Assert.False(SilentUpdatePolicy.IsSafeSetupPath(Path.Combine(root, "evil.exe"), root));
            Assert.False(SilentUpdatePolicy.IsSafeSetupPath(Path.Combine(root, "..", "NetLights-Setup-win-x64-1.0.2.exe"), root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HasInnoUninstaller_DetectsUnins000()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-silent-unins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(SilentUpdatePolicy.HasInnoUninstaller(root));
            File.WriteAllText(Path.Combine(root, "unins000.exe"), "x");
            Assert.True(SilentUpdatePolicy.HasInnoUninstaller(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
