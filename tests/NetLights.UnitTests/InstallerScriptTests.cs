using Xunit;

namespace NetLights.UnitTests;

public sealed class InstallerScriptTests
{
    [Fact]
    public void SetupWaitsForAppWithoutForceKillingItOrTheUpdateAgent()
    {
        string script = File.ReadAllText(FindInstallerScript());
        Assert.Contains("CloseApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", script, StringComparison.Ordinal);
        Assert.Contains(@"AppMutex=Local\NetLights.SingleInstance", script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TaskKillImage", script, StringComparison.Ordinal);
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
