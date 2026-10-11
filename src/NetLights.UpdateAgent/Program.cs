using System.Diagnostics;
using NetLights.Updates;

namespace NetLights.UpdateAgent;

internal sealed record UpdateAgentTestOptions(
    TimeSpan ParentWait,
    TimeSpan InstallerWait,
    TimeSpan InstallerStopWait,
    TimeSpan HealthSignalWait,
    TimeSpan RestartLivenessWait,
    IReadOnlyDictionary<string, string> ChildEnvironment);

internal static class Program
{
    private static int Main(string[] args)
        => Run(args, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetLights",
            "updates"), null);

    internal static int RunForTests(string[] args, string updatesRoot, UpdateAgentTestOptions? testOptions = null)
        => Run(args, updatesRoot, testOptions);

    private static int Run(string[] args, string updatesRoot, UpdateAgentTestOptions? testOptions)
    {
        InstallationSnapshot? activeBackup = null;
        Process? activeInstaller = null;
        FileStream? installerReadLock = null;
        string root = Path.GetFullPath(updatesRoot);
        var store = new PendingUpdateStore(root);
        try
        {
            Dictionary<string, string> parsed = Parse(args);
            if (!parsed.TryGetValue("parent", out string? parentText)
                || !int.TryParse(parentText, out int parentId)
                || !parsed.TryGetValue("pending", out string? installer)
                || !parsed.TryGetValue("sha256", out string? sha256))
            {
                return 2;
            }

            store.WritePhase("agent-start");
            if (!File.Exists(installer))
            {
                store.WriteResult("unknown", false, "Файл установщика не найден.");
                return 3;
            }

            string allowedRoot = Path.GetFullPath(root);
            string fullInstaller = Path.GetFullPath(installer);
            if (!UpdatePolicy.IsInsideRoot(allowedRoot, fullInstaller))
            {
                store.WriteResult("unknown", false, "Путь установщика вне каталога обновлений.");
                return 4;
            }

            long installerSize = new FileInfo(fullInstaller).Length;
            if (installerSize <= 0 || installerSize > UpdatePolicy.MaxInstallerBytes)
            {
                store.WritePhase("installer-size-rejected");
                installerReadLock?.Dispose();
                installerReadLock = null;
                CleanupInstaller(fullInstaller, allowedRoot);
                bool recovered = Restart(parsed, testOptions: testOptions);
                store.WriteResult("unknown", false, "Размер установщика вне допустимых пределов."
                    + (recovered ? " Приложение запущено." : " Не удалось подтвердить запуск приложения."));
                return 9;
            }

            if (parsed.TryGetValue("ready", out string? readyName))
            {
                if (!OperatingSystem.IsWindows()
                    || !readyName.StartsWith("Local\\NetLights.UpdateAgentReady.", StringComparison.Ordinal)
                    || !Guid.TryParseExact(readyName["Local\\NetLights.UpdateAgentReady.".Length..], "N", out _))
                    return 2;
                using EventWaitHandle ready = EventWaitHandle.OpenExisting(readyName);
                ready.Set();
            }

            if (!WaitForParent(parentId, testOptions?.ParentWait ?? TimeSpan.FromSeconds(60)))
            {
                store.WritePhase("parent-timeout");
                store.WriteResult("unknown", false, "Родительский процесс не завершился.");
                return 6;
            }

            installerReadLock = new FileStream(fullInstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actual = IntegrityVerifier.Sha256Hex(installerReadLock);
            if (!IntegrityVerifier.Matches(sha256, actual))
            {
                installerReadLock?.Dispose();
                installerReadLock = null;
                store.WritePhase("installer-hash-failed");
                CleanupInstaller(fullInstaller, allowedRoot);
                bool recovered = Restart(parsed, testOptions: testOptions);
                store.WriteResult("unknown", false, "Контрольная сумма установщика не совпала."
                    + (recovered ? " Приложение запущено, работа монитора не подтверждена." : " Не удалось подтвердить запуск приложения."));
                return 5;
            }

            if (!parsed.TryGetValue("restart", out string? installedExe))
                return 2;
            InstallationSnapshot backup = InstallationSnapshot.Create(installedExe, allowedRoot);
            activeBackup = backup;
            store.WritePhase("installed-files-backed-up");
            var install = new Process();
            activeInstaller = install;
            install.StartInfo.FileName = fullInstaller;
            install.StartInfo.ArgumentList.Add("/VERYSILENT");
            install.StartInfo.ArgumentList.Add("/NORESTART");
            install.StartInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            install.StartInfo.ArgumentList.Add("/NETLIGHTSUPDATE");
            install.StartInfo.UseShellExecute = false;
            install.StartInfo.CreateNoWindow = true;
            ApplyTestEnvironment(install.StartInfo, testOptions);
            install.Start();
            store.WritePhase("installer-started");
            if (!install.WaitForExit(ToTimeoutMilliseconds(testOptions?.InstallerWait ?? TimeSpan.FromMinutes(10))))
            {
                store.WritePhase("installer-timeout");
                try
                {
                    install.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                bool stopped = install.WaitForExit(ToTimeoutMilliseconds(testOptions?.InstallerStopWait ?? TimeSpan.FromSeconds(5)));
                if (!stopped) backup.KeepForRecovery();
                bool restored = stopped && backup.Restore();
                store.WritePhase(restored ? "rollback-complete" : "rollback-failed");

                installerReadLock.Dispose();
                installerReadLock = null;
                CleanupInstaller(fullInstaller, allowedRoot);
                bool recovered = restored && Restart(parsed, testOptions: testOptions);
                store.WriteResult("unknown", false, "Установщик не завершился вовремя."
                    + (restored ? " Предыдущие файлы восстановлены." : " Восстановление предыдущих файлов не подтверждено.")
                    + (recovered ? " Приложение запущено, работа монитора не подтверждена." : " Не удалось подтвердить запуск приложения."));
                return 7;
            }
            bool versionOk = install.ExitCode == 0 && InstalledExpectedVersion(parsed, fullInstaller);
            store.WritePhase(versionOk ? "version-confirmed" : "version-not-confirmed");
            bool restarted = versionOk && Restart(parsed, requireMonitorSignal: true, testOptions: testOptions);
            store.WritePhase(restarted ? "process-restarted" : "process-restart-failed");
            bool ok = versionOk && restarted;
            bool restoredPrevious = false;
            bool recoveredPrevious = false;
            if (!ok)
            {
                restoredPrevious = backup.Restore();
                store.WritePhase(restoredPrevious ? "rollback-complete" : "rollback-failed");
                recoveredPrevious = restoredPrevious && Restart(parsed, testOptions: testOptions);
                store.WritePhase(recoveredPrevious ? "previous-process-restarted" : "previous-process-restart-failed");
            }
            string resultMessage = install.ExitCode != 0
                ? "Установщик завершился с ошибкой " + install.ExitCode
                : !versionOk ? "Установщик завершился, но ожидаемая версия не найдена."
                : !restarted ? "Новая версия установлена, но запуск мониторинга не подтвержден."
                : "Установлено, мониторинг запущен.";
            if (!ok)
                resultMessage += (restoredPrevious ? " Предыдущие файлы восстановлены." : " Восстановление предыдущих файлов не подтверждено.")
                    + (recoveredPrevious ? " Предыдущая версия запущена, работа монитора не подтверждена." : " Запуск предыдущей версии не подтвержден.");
            store.WriteResult(Path.GetFileName(fullInstaller), ok, resultMessage);
            if (ok)
            {
                store.ClearPending();
            }

            installerReadLock?.Dispose();
            installerReadLock = null;
            CleanupInstaller(fullInstaller, allowedRoot);

            return ok ? 0 : install.ExitCode == 0 ? 8 : install.ExitCode;
        }
        catch (Exception ex)
        {
            installerReadLock?.Dispose();
            installerReadLock = null;
            bool restored = activeBackup is null;
            if (activeBackup is not null)
            {
                bool installerStopped = true;
                try
                {
                    if (activeInstaller is { HasExited: false })
                    {
                        activeInstaller.Kill(entireProcessTree: true);
                        installerStopped = activeInstaller.WaitForExit(ToTimeoutMilliseconds(testOptions?.InstallerStopWait ?? TimeSpan.FromSeconds(5)));
                    }
                }
                catch (Exception)
                {
                    installerStopped = false;
                }

                if (installerStopped)
                    restored = activeBackup.Restore();
                else
                    activeBackup.KeepForRecovery();
            }
            try
            {
                store.WriteResult("unknown", false, ex.GetType().Name);
            }
            catch (Exception)
            {
            }

            try
            {
                Dictionary<string, string> failedArgs = Parse(args);
                if (failedArgs.TryGetValue("pending", out string? failedInstaller))
                {
                    CleanupInstaller(Path.GetFullPath(failedInstaller), root);
                }
            }
            catch (Exception) { }

            bool recovered = false;
            try { recovered = restored && Restart(Parse(args), testOptions: testOptions); }
            catch (Exception) { }
            try
            {
                string recovery = (restored ? " Предыдущие файлы восстановлены." : " Восстановление предыдущих файлов не подтверждено.")
                    + (recovered
                    ? " Приложение перезапущено, работа монитора не подтверждена."
                    : " Запуск приложения не подтвержден.");
                store.WriteResult("unknown", false, ex.GetType().Name + "." + recovery);
            }
            catch (Exception) { }

            return 1;
        }
        finally
        {
            installerReadLock?.Dispose();
            activeInstaller?.Dispose();
            activeBackup?.Dispose();
        }
    }

    private static bool Restart(
        Dictionary<string, string> parsed,
        bool requireMonitorSignal = false,
        UpdateAgentTestOptions? testOptions = null)
    {
        if (!parsed.TryGetValue("restart", out string? restart) || !UpdatePolicy.IsSafeRestartPath(restart))
        {
            return false;
        }

        try
        {
            string fullRestart = Path.GetFullPath(restart);
            string? healthName = requireMonitorSignal ? UpdateHealthSignal.CreateEventName() : null;
            using EventWaitHandle? health = healthName is null
                ? null
                : new EventWaitHandle(false, EventResetMode.ManualReset, healthName);
            using var next = new Process();
            next.StartInfo.FileName = fullRestart;
            next.StartInfo.WorkingDirectory = Path.GetDirectoryName(fullRestart);
            next.StartInfo.UseShellExecute = false;
            ApplyTestEnvironment(next.StartInfo, testOptions);
            if (healthName is not null)
            {
                next.StartInfo.ArgumentList.Add(UpdateHealthSignal.ArgumentName);
                next.StartInfo.ArgumentList.Add(healthName);
            }

            if (!next.Start()) return false;
            if (health is not null && !health.WaitOne(testOptions?.HealthSignalWait ?? TimeSpan.FromSeconds(20)))
            {
                try { if (!next.HasExited) next.Kill(entireProcessTree: true); }
                catch (Exception) { }
                next.WaitForExit(ToTimeoutMilliseconds(testOptions?.InstallerStopWait ?? TimeSpan.FromSeconds(5)));
                return false;
            }
            return !next.WaitForExit(ToTimeoutMilliseconds(testOptions?.RestartLivenessWait ?? TimeSpan.FromSeconds(3)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool InstalledExpectedVersion(Dictionary<string, string> parsed, string installer)
    {
        const string prefix = "NetLights-Setup-win-x64-";
        string name = Path.GetFileNameWithoutExtension(installer);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(name[prefix.Length..], out Version? expected)
            || !parsed.TryGetValue("restart", out string? restart)
            || !UpdatePolicy.IsSafeRestartPath(restart)) return false;

        string? productVersion = FileVersionInfo.GetVersionInfo(restart).ProductVersion;
        return productVersion is not null
            && Version.TryParse(UpdatePolicy.Normalize(productVersion), out Version? actual)
            && actual == expected;
    }

    private static void CleanupInstaller(string installer, string root)
    {
        try
        {
            if (!UpdatePolicy.IsInsideRoot(root, installer)
                || UpdatePolicy.SafeInstallerFileName(Path.GetFileName(installer)) is null) return;
            File.Delete(installer);
            string? directory = Path.GetDirectoryName(installer);
            if (directory is not null && !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool WaitForParent(int parentId, TimeSpan timeout)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            return parent.WaitForExit(ToTimeoutMilliseconds(timeout)) && parent.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                map[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return map;
    }

    private static void ApplyTestEnvironment(ProcessStartInfo startInfo, UpdateAgentTestOptions? testOptions)
    {
        if (testOptions is null)
            return;

        foreach ((string name, string value) in testOptions.ChildEnvironment)
            startInfo.Environment[name] = value;
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
        => timeout == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
}
