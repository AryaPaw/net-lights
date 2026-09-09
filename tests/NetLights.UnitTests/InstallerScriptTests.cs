using Xunit;

namespace NetLights.UnitTests;

public sealed class InstallerScriptTests
{
    [Fact]
    public void SetupDoesNotUseRestartManagerCloseDialog()
    {
        string script = File.ReadAllText(FindInstallerScript());
        Assert.Contains("CloseApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("PrepareToInstall", script, StringComparison.Ordinal);
        Assert.Contains("taskkill.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TaskKillImage('NetLights.exe')", script, StringComparison.Ordinal);
        Assert.Contains("TaskKillImage('NetLights.UpdateAgent.exe')", script, StringComparison.Ordinal);
        Assert.Contains("'/F /IM ' + ImageName + ' /T'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseApplications=yes", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SilentSetupRelaunchesTheAppAfterInstall()
    {
        string script = File.ReadAllText(FindInstallerScript());
        Assert.Contains("Flags: nowait postinstall skipifsilent", script, StringComparison.Ordinal);
        Assert.Contains("Flags: nowait skipifnotsilent", script, StringComparison.Ordinal);
    }

    private static string FindInstallerScript()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string iss = Path.Combine(dir, "installer", "net-lights.iss");
            if (File.Exists(iss))
            {
                return iss;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("installer/net-lights.iss was not found.");
    }
}
