using NetLights.UpdateAgent;
using NetLights.Updates;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class UpdateAgentProgramTests
{
    [Fact]
    public void MissingArguments_ReturnsUsageErrorWithoutCreatingUpdateFiles()
    {
        string root = CreateRoot();
        try
        {
            Assert.Equal(2, Program.RunForTests([], root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MissingInstaller_RecordsFailureInProvidedIsolatedRoot()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            int result = Program.RunForTests(Arguments(installer, new string('0', 64)), root);

            Assert.Equal(3, result);
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains("установщика", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InstallerOutsideUpdatesRoot_IsRejectedBeforeHashing()
    {
        string root = CreateRoot();
        string outside = Path.Combine(Path.GetTempPath(), "nl-agent-outside-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(outside, "not an installer");
            int result = Program.RunForTests(Arguments(outside, new string('0', 64)), root);

            Assert.Equal(4, result);
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains("вне каталога", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(outside);
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InstallerHashMismatch_IsRecordedAndStagedFileIsRemoved()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            File.WriteAllText(installer, "incorrect installer bytes");
            int result = Program.RunForTests(Arguments(installer, new string('0', 64)), root);

            Assert.Equal(5, result);
            Assert.False(File.Exists(installer));
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains("Контрольная сумма", message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InstallerHashMismatchAfterStagingChangeIsRejected()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            File.WriteAllText(installer, "replacement bytes after coordinator verification");
            string publishedDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("published bytes"))).ToLowerInvariant();

            int result = Program.RunForTests(Arguments(installer, publishedDigest), root);

            Assert.Equal(5, result);
            Assert.False(File.Exists(installer));
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains("Контрольная сумма", message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void EmptyInstaller_IsRejectedBeforeHashing()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            File.WriteAllBytes(installer, []);

            int result = Program.RunForTests(Arguments(installer, new string('0', 64)), root);

            Assert.Equal(9, result);
            Assert.False(File.Exists(installer));
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains("Размер установщика", message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InvalidReadySignalName_IsRejectedWithoutOpeningAnEvent()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            File.WriteAllText(installer, "installer bytes");
            string[] args = [.. Arguments(installer, new string('0', 64)), "--ready", "Local\\invalid"];

            Assert.Equal(2, Program.RunForTests(args, root));
            Assert.Empty(Directory.GetFiles(root, "result.json"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void RuntimeFailure_RecordsResultInProvidedIsolatedRoot()
    {
        string root = CreateRoot();
        try
        {
            string installer = Path.Combine(root, "NetLights-Setup-win-x64-1.5.0.exe");
            File.WriteAllText(installer, "valid staged test bytes");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installer))).ToLowerInvariant();
            string ready = "Local\\NetLights.UpdateAgentReady." + Guid.NewGuid().ToString("N");

            int result = Program.RunForTests(
                [.. Arguments(installer, hash), "--ready", ready],
                root);

            Assert.Equal(1, result);
            Assert.True(new PendingUpdateStore(root).TryReadLastFailure(out string? message));
            Assert.Contains(nameof(WaitHandleCannotBeOpenedException), message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SuccessfulInstallerProcess_RestartsVersionAndPreservesInstallationData()
    {
        using var fixture = new InstallerFixture("success", "1.5.0");

        int result = fixture.Run();

        Assert.Equal(0, result);
        Assert.Equal("saved-history", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "history.json")));
        Assert.Equal("new-app-payload", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "rollback-probe.marker")));
        Assert.True(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.TryGetRunningApp(out _));
        Assert.True(fixture.Store.TryReadLastFailure(out _ ) is false);
    }

    [Fact]
    public void InstallerFailure_RestoresPriorFilesAndRemovesInstallerAddedFiles()
    {
        using var fixture = new InstallerFixture("fail", "1.5.0");

        int result = fixture.Run();

        Assert.Equal(17, result);
        Assert.Equal("saved-history", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "history.json")));
        Assert.Equal("old-app-payload", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "rollback-probe.marker")));
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.TryGetRunningApp(out _));
        Assert.True(fixture.Store.TryReadLastFailure(out string? message));
        Assert.Contains("ошибкой 17", message);
    }

    [Fact]
    public void InstallerTimeout_KillsInstallerRestoresPriorFilesAndRestartsApp()
    {
        using var fixture = new InstallerFixture("hang", "1.5.0", installerWait: TimeSpan.FromMilliseconds(300));

        int result = fixture.Run();

        Assert.Equal(7, result);
        Assert.Equal("saved-history", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "history.json")));
        Assert.Equal("old-app-payload", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "rollback-probe.marker")));
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.TryGetRunningApp(out _));
        Assert.True(fixture.Store.TryReadLastFailure(out string? message));
        Assert.Contains("не завершился вовремя", message);
    }

    [Fact]
    public void InstalledVersionMismatch_RollsBackAndRestartsPriorApp()
    {
        using var fixture = new InstallerFixture("success", "1.6.0");

        int result = fixture.Run();

        Assert.Equal(8, result);
        Assert.Equal("saved-history", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "history.json")));
        Assert.Equal("old-app-payload", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "rollback-probe.marker")));
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.TryGetRunningApp(out _));
        Assert.True(fixture.Store.TryReadLastFailure(out string? message));
        Assert.Contains("ожидаемая версия", message);
    }

    [Fact]
    public void NewAppWithoutHealthSignal_RollsBackAndRestartsPriorApp()
    {
        using var fixture = new InstallerFixture("success", "1.5.0", skipHealthSignal: true);
        UpdateAgentTestOptions options = fixture.CreateOptions(TimeSpan.FromSeconds(5)) with
        {
            HealthSignalWait = TimeSpan.FromMilliseconds(250)
        };

        int result = fixture.Run(options: options);

        Assert.Equal(8, result);
        Assert.Equal("saved-history", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "history.json")));
        Assert.Equal("old-app-payload", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "rollback-probe.marker")));
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.TryGetRunningApp(out _));
        Assert.True(fixture.Store.TryReadLastFailure(out string? message));
        Assert.Contains("запуск мониторинга не подтвержден", message);
    }

    [Fact]
    public void ParentStillRunning_UsesBoundedWaitAndLeavesInstalledFilesUntouched()
    {
        using var fixture = new InstallerFixture("success", "1.5.0");
        using var parent = fixture.StartTestAppProcess();
        UpdateAgentTestOptions options = fixture.CreateOptions(TimeSpan.FromSeconds(5)) with
        {
            ParentWait = TimeSpan.FromMilliseconds(150)
        };

        int result = fixture.Run(parent.Id, options);

        Assert.Equal(6, result);
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "installer-added.marker")));
        Assert.True(fixture.Store.TryReadLastFailure(out string? message));
        Assert.Contains("Родительский процесс", message);
    }

    private static string[] Arguments(string installer, string sha256)
        => ["--parent", "-123", "--pending", installer, "--sha256", sha256];

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-update-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class InstallerFixture : IDisposable
    {
        private const string TestAppPrefix = "NetLights.TestApp";
        private const string TestInstallerPrefix = "NetLights.TestInstaller";
        private readonly string _installMode;
        private readonly string _installerPath;
        private readonly string _pidFile;
        private readonly TimeSpan _installerWait;
        private readonly bool _skipHealthSignal;

        public InstallerFixture(string installMode, string expectedVersion, TimeSpan? installerWait = null, bool skipHealthSignal = false)
        {
            Root = CreateRoot();
            InstallDirectory = Path.Combine(Path.GetTempPath(), "nl-agent-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(InstallDirectory);
            _installMode = installMode;
            _installerWait = installerWait ?? TimeSpan.FromSeconds(5);
            _skipHealthSignal = skipHealthSignal;
            _pidFile = Path.Combine(Root, "spawned-app-pids.txt");
            Store = new PendingUpdateStore(Root);

            CopyAppPayload(AppContext.BaseDirectory, InstallDirectory);
            File.WriteAllText(Path.Combine(InstallDirectory, "unins000.exe"), "test installer marker");
            File.WriteAllText(Path.Combine(InstallDirectory, "history.json"), "saved-history");
            File.WriteAllText(Path.Combine(InstallDirectory, "rollback-probe.marker"), "old-app-payload");

            _installerPath = Path.Combine(Root, $"NetLights-Setup-win-x64-{expectedVersion}.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, TestInstallerPrefix + ".exe"), _installerPath);
            foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, TestInstallerPrefix + extension), Path.Combine(Root, TestInstallerPrefix + extension));
        }

        public string Root { get; }
        public string InstallDirectory { get; }
        public PendingUpdateStore Store { get; }

        public int Run(int? parentId = null, UpdateAgentTestOptions? options = null)
        {
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_installerPath))).ToLowerInvariant();
            options ??= CreateOptions(_installerWait);
            string[] args = ["--parent", (parentId ?? -123).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--pending", _installerPath, "--sha256", hash,
                "--restart", Path.Combine(InstallDirectory, "NetLights.exe")];
            return Program.RunForTests(args, Root, options);
        }

        public UpdateAgentTestOptions CreateOptions(TimeSpan installerWait)
            => new(
                TimeSpan.FromSeconds(5),
                installerWait,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(150),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NL_TEST_INSTALL_ROOT"] = InstallDirectory,
                    ["NL_TEST_APP_PAYLOAD"] = AppContext.BaseDirectory,
                    ["NL_TEST_INSTALL_MODE"] = _installMode,
                    ["NL_TEST_APP_PID_FILE"] = _pidFile,
                    ["NL_TEST_APP_SKIP_HEALTH_SIGNAL"] = _skipHealthSignal ? "1" : "0"
                });

        public bool TryGetRunningApp(out int pid)
        {
            pid = 0;
            if (!File.Exists(_pidFile)) return false;
            foreach (string line in File.ReadAllLines(_pidFile))
            {
                if (!int.TryParse(line, out int candidate)) continue;
                try
                {
                    using Process process = Process.GetProcessById(candidate);
                    string? imagePath = process.MainModule?.FileName;
                    if (!process.HasExited && string.Equals(Path.GetFullPath(imagePath ?? ""),
                            Path.GetFullPath(Path.Combine(InstallDirectory, "NetLights.exe")), StringComparison.OrdinalIgnoreCase))
                    {
                        pid = candidate;
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            return false;
        }

        public Process StartTestAppProcess()
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, TestAppPrefix + ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.Environment["NL_TEST_APP_PID_FILE"] = _pidFile;
            return Process.Start(start) ?? throw new InvalidOperationException("Test app did not start.");
        }

        private static void CopyAppPayload(string sourceDirectory, string targetDirectory)
        {
            File.Copy(Path.Combine(sourceDirectory, TestAppPrefix + ".exe"), Path.Combine(targetDirectory, "NetLights.exe"));
            foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(sourceDirectory, TestAppPrefix + extension), Path.Combine(targetDirectory, TestAppPrefix + extension));
        }

        public void Dispose()
        {
            if (File.Exists(_pidFile))
            {
                foreach (string line in File.ReadAllLines(_pidFile))
                {
                    if (!int.TryParse(line, out int pid)) continue;
                    try
                    {
                        using Process process = Process.GetProcessById(pid);
                        string? imagePath = process.MainModule?.FileName;
                        if (!process.HasExited && IsFixtureOwnedAppPath(imagePath))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2000);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
            DeleteRoot(Root);
        }

        private bool IsFixtureOwnedAppPath(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return false;
            string fullPath = Path.GetFullPath(imagePath);
            return string.Equals(fullPath, Path.GetFullPath(Path.Combine(InstallDirectory, "NetLights.exe")), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, TestAppPrefix + ".exe")), StringComparison.OrdinalIgnoreCase);
        }
    }
}
