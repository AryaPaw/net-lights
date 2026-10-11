using System.Runtime.Versioning;

namespace NetLights.TestApp;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        string? pidFile = Environment.GetEnvironmentVariable("NL_TEST_APP_PID_FILE");
        if (!string.IsNullOrWhiteSpace(pidFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
            File.AppendAllText(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
        }

        if (args.Length == 2 && args[0] == "--update-health-event")
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("NL_TEST_APP_SKIP_HEALTH_SIGNAL"), "1", StringComparison.Ordinal))
            {
                using EventWaitHandle ready = EventWaitHandle.OpenExisting(args[1]);
                ready.Set();
            }
        }

        Thread.Sleep(TimeSpan.FromMinutes(5));
        return 0;
    }
}
