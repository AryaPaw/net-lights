using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class AutoStartStoreTests
{
    [Fact]
    public void RunEntry_IsOwnedOnlyByItsExactExecutable()
    {
        string installed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "installed", "NetLights.exe"));
        string portable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "portable", "NetLights.exe"));
        Assert.True(AutoStartStore.OwnsCommand(AutoStartStore.Quote(installed), installed));
        Assert.False(AutoStartStore.OwnsCommand(AutoStartStore.Quote(installed), portable));
        Assert.False(AutoStartStore.OwnsCommand(AutoStartStore.Quote(installed) + " --other", installed));
    }
}
