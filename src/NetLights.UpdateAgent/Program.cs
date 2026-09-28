using System.Diagnostics;
using NetLights.Updates;

namespace NetLights.UpdateAgent;

internal static class Program
{
    private static int Main(string[] args)
    {
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

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetLights",
                "updates");
            var store = new PendingUpdateStore(root);
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

            if (!WaitForParent(parentId))
            {
                store.WriteResult("unknown", false, "Родительский процесс не завершился.");
                return 6;
            }

            string actual;
            using (FileStream stream = File.OpenRead(fullInstaller))
            {
                actual = IntegrityVerifier.Sha256Hex(stream);
            }
            if (!IntegrityVerifier.Matches(sha256, actual))
            {
                store.WriteResult("unknown", false, "Контрольная сумма установщика не совпала.");
                CleanupInstaller(fullInstaller, allowedRoot);
                Restart(parsed);
                return 5;
            }

            using var install = new Process();
            install.StartInfo.FileName = fullInstaller;
            install.StartInfo.ArgumentList.Add("/VERYSILENT");
            install.StartInfo.ArgumentList.Add("/NORESTART");
            install.StartInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            install.StartInfo.UseShellExecute = false;
            install.StartInfo.CreateNoWindow = true;
            install.Start();
            if (!install.WaitForExit(10 * 60_000))
            {
                try
                {
                    install.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                store.WriteResult("unknown", false, "Установщик не завершился вовремя.");
                CleanupInstaller(fullInstaller, allowedRoot);
                Restart(parsed);
                return 7;
            }
            bool ok = install.ExitCode == 0 && InstalledExpectedVersion(parsed, fullInstaller);
            string resultMessage = install.ExitCode != 0
                ? "Установщик завершился с ошибкой " + install.ExitCode
                : ok ? "Установлено." : "Установщик завершился, но ожидаемая версия не найдена.";
            store.WriteResult(Path.GetFileName(fullInstaller), ok, resultMessage);
            if (ok)
            {
                store.ClearPending();
            }

            // Restart the old binary on install failure as well. Monitoring must
            // recover even if the installer returns an error.
            Restart(parsed);

            CleanupInstaller(fullInstaller, allowedRoot);

            return install.ExitCode;
        }
        catch (Exception ex)
        {
            try
            {
                new PendingUpdateStore().WriteResult("unknown", false, ex.GetType().Name);
            }
            catch (Exception)
            {
            }

            try
            {
                Dictionary<string, string> failedArgs = Parse(args);
                if (failedArgs.TryGetValue("pending", out string? failedInstaller))
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetLights", "updates");
                    CleanupInstaller(Path.GetFullPath(failedInstaller), Path.GetFullPath(root));
                }
            }
            catch (Exception) { }

            try { Restart(Parse(args)); }
            catch (Exception) { }

            return 1;
        }
    }

    private static void Restart(Dictionary<string, string> parsed)
    {
        if (!parsed.TryGetValue("restart", out string? restart) || !UpdatePolicy.IsSafeRestartPath(restart))
        {
            return;
        }

        string fullRestart = Path.GetFullPath(restart);
        using var next = new Process();
        next.StartInfo.FileName = fullRestart;
        next.StartInfo.WorkingDirectory = Path.GetDirectoryName(fullRestart);
        next.StartInfo.UseShellExecute = false;
        next.Start();
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

    private static bool WaitForParent(int parentId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            return parent.WaitForExit(60_000) && parent.HasExited;
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
}
