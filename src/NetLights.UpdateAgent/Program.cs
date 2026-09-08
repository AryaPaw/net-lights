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

            using FileStream stream = File.OpenRead(fullInstaller);
            string actual = IntegrityVerifier.Sha256Hex(stream);
            if (!IntegrityVerifier.Matches(sha256, actual))
            {
                store.WriteResult("unknown", false, "Контрольная сумма установщика не совпала.");
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
                return 7;
            }
            bool ok = install.ExitCode == 0;
            store.WriteResult(Path.GetFileName(fullInstaller), ok, ok ? "Установлено." : "Установщик завершился с ошибкой " + install.ExitCode);
            if (ok)
            {
                store.ClearPending();
            }

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

            return 1;
        }
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
