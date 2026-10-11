using NetLights.Updates;
using System.Diagnostics;
using Xunit;

namespace NetLights.UnitTests;

public sealed class InstallationSnapshotTests
{
    [Fact]
    public void Create_RejectsBackupInsideInstallation()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-install-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "NetLights.exe");
            File.WriteAllText(exe, "old-app");
            File.WriteAllText(Path.Combine(root, "unins000.exe"), "old-uninstaller");
            Assert.Throws<InvalidDataException>(() => InstallationSnapshot.Create(exe, Path.Combine(root, "updates")));
            Assert.False(Directory.Exists(Path.Combine(root, "updates")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Restore_ReplacesPartiallyUpdatedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-install-snapshot-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "app");
        string updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(app);
        try
        {
            string exe = Path.Combine(app, "NetLights.exe");
            string agent = Path.Combine(app, "NetLights.UpdateAgent.exe");
            string originalEmptyDirectory = Path.Combine(app, "original-empty-directory");
            Directory.CreateDirectory(originalEmptyDirectory);
            File.WriteAllText(exe, "old-app");
            File.WriteAllText(agent, "old-agent");
            File.WriteAllText(Path.Combine(app, "unins000.exe"), "old-uninstaller");

            using (InstallationSnapshot backup = InstallationSnapshot.Create(exe, updates))
            {
                File.WriteAllText(exe, "new-app");
                File.WriteAllText(agent, "new-agent");
                File.WriteAllText(Path.Combine(app, "new-installer-payload.dll"), "new-file");
                Assert.True(backup.Restore());
            }

            Assert.Equal("old-app", File.ReadAllText(exe));
            Assert.Equal("old-agent", File.ReadAllText(agent));
            Assert.False(File.Exists(Path.Combine(app, "new-installer-payload.dll")));
            Assert.True(Directory.Exists(originalEmptyDirectory));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Restore_RefusesWhenInstallationRootWasReplacedWithJunction()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-install-snapshot-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "app");
        string movedApp = Path.Combine(root, "app-original");
        string external = Path.Combine(root, "external");
        string updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(external);
        string linkCreated = app;
        try
        {
            string exe = Path.Combine(app, "NetLights.exe");
            File.WriteAllText(exe, "old-app");
            File.WriteAllText(Path.Combine(app, "unins000.exe"), "old-uninstaller");
            File.WriteAllText(Path.Combine(external, "keep.txt"), "outside-installation");

            using InstallationSnapshot backup = InstallationSnapshot.Create(exe, updates);
            Directory.Move(app, movedApp);
            using Process junction = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/c mklink /J \"" + app + "\" \"" + external + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not create the test junction.");
            junction.WaitForExit(5000);
            Assert.Equal(0, junction.ExitCode);
            Assert.True((File.GetAttributes(app) & FileAttributes.ReparsePoint) != 0);

            Assert.False(backup.Restore());

            Assert.Equal("outside-installation", File.ReadAllText(Path.Combine(external, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(movedApp, "NetLights.exe")));
        }
        finally
        {
            if (Directory.Exists(linkCreated))
            {
                try { Directory.Delete(linkCreated, recursive: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
