namespace NetLights.TestInstaller;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!args.Contains("/NETLIGHTSUPDATE", StringComparer.OrdinalIgnoreCase))
            return 91;

        string target = Environment.GetEnvironmentVariable("NL_TEST_INSTALL_ROOT")
            ?? throw new InvalidOperationException("NL_TEST_INSTALL_ROOT is required.");
        string payload = Environment.GetEnvironmentVariable("NL_TEST_APP_PAYLOAD")
            ?? throw new InvalidOperationException("NL_TEST_APP_PAYLOAD is required.");
        string mode = Environment.GetEnvironmentVariable("NL_TEST_INSTALL_MODE") ?? "success";

        Directory.CreateDirectory(target);
        CopyPayload(payload, target, "NetLights.TestApp.exe", "NetLights.exe");
        CopyPayload(payload, target, "NetLights.TestApp.dll", "NetLights.TestApp.dll");
        CopyPayload(payload, target, "NetLights.TestApp.deps.json", "NetLights.TestApp.deps.json");
        CopyPayload(payload, target, "NetLights.TestApp.runtimeconfig.json", "NetLights.TestApp.runtimeconfig.json");
        File.WriteAllText(Path.Combine(target, "installer-added.marker"), "test install");
        File.WriteAllText(Path.Combine(target, "rollback-probe.marker"), "new-app-payload");

        if (mode.Equals("legacy-autostart", StringComparison.OrdinalIgnoreCase))
        {
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(target, "NetLights.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = target
            }) ?? throw new InvalidOperationException("Legacy app did not start.");
            File.WriteAllText(Path.Combine(target, "installer-started-app.pid"), child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (mode.Equals("hang", StringComparison.OrdinalIgnoreCase))
            Thread.Sleep(Timeout.Infinite);

        return mode.Equals("fail", StringComparison.OrdinalIgnoreCase) ? 17 : 0;
    }

    private static void CopyPayload(string payloadDirectory, string targetDirectory, string sourceName, string targetName)
        => File.Copy(Path.Combine(payloadDirectory, sourceName), Path.Combine(targetDirectory, targetName), overwrite: true);
}
