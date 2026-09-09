using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class SilentSetupLauncherTests
{
    [Fact]
    public void BuildCommand_WaitsThenRunsInnoSilent()
    {
        string setup = @"C:\Temp\NetLights\NetLights-Setup-win-x64-1.0.2.exe";
        string command = SilentSetupLauncher.BuildCommand(setup);
        Assert.Contains("ping 127.0.0.1 -n 5", command, StringComparison.Ordinal);
        Assert.Contains("/VERYSILENT", command, StringComparison.Ordinal);
        Assert.Contains("/SUPPRESSMSGBOXES", command, StringComparison.Ordinal);
        Assert.Contains("/NORESTART", command, StringComparison.Ordinal);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", command, StringComparison.Ordinal);
        Assert.Contains("\"" + setup + "\"", command, StringComparison.Ordinal);
        Assert.StartsWith("/C ping 127.0.0.1 -n 5 >NUL & ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommand_RejectsUnsafePaths()
    {
        Assert.Throws<ArgumentException>(() => SilentSetupLauncher.BuildCommand(@"C:\Temp\setup.exe & notepad.exe"));
    }
}
