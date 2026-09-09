using System.Runtime.InteropServices;

namespace NetLights.Updates;

public static class SilentUpdatePolicy
{
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FailedRetry = TimeSpan.FromHours(6);
    public const string ProbeUrl = "https://github.com/AryaPaw/net-lights";

    public static bool TryParseTag(string? tag, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return Version.TryParse(UpdatePolicy.Normalize(tag), out version);
    }

    public static bool IsNewer(Version current, Version candidate) => candidate > current;

    public static bool AllowsBackgroundProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        return !processName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
            && !processName.StartsWith("vstest", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeToRestart() => true;

    public static string? RidFor(Architecture architecture)
        => architecture == Architecture.X64 ? "win-x64" : null;

    public static bool HasInnoUninstaller(string applicationDirectory)
        => File.Exists(Path.Combine(applicationDirectory, "unins000.exe"));

    public static bool IsSafeSetupPath(string path, string downloadDirectory)
    {
        if (!UpdatePolicy.IsInsideRoot(downloadDirectory, path))
        {
            return false;
        }

        return UpdatePolicy.SafeInstallerFileName(Path.GetFileName(path)) is not null;
    }

    public static bool ContainsShellMetacharacters(string path)
        => path.IndexOfAny(['&', '|', '^', '%', '"', '<', '>']) >= 0;
}
