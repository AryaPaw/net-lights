using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace NetLights.Updates;

public interface ISetupInstaller
{
    bool TryStartSilent(string setupPath, string verifiedSha256);
}

public static class SilentSetupLauncher
{
    public static string BuildCommand(string setupPath)
    {
        if (string.IsNullOrWhiteSpace(setupPath)
            || SilentUpdatePolicy.ContainsShellMetacharacters(setupPath)
            || !setupPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Setup path is not safe for a silent install command", nameof(setupPath));
        }

        string name = Path.GetFileName(setupPath);
        if (UpdatePolicy.SafeInstallerFileName(name) is null)
        {
            throw new ArgumentException("Setup path is not safe for a silent install command", nameof(setupPath));
        }

        return "/C ping 127.0.0.1 -n 5 >NUL & \"" + Path.GetFullPath(setupPath)
            + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /FORCECLOSEAPPLICATIONS";
    }
}

[ExcludeFromCodeCoverage]
public sealed class CmdSilentSetupInstaller : ISetupInstaller
{
    public bool TryStartSilent(string setupPath, string verifiedSha256)
    {
        if (!MatchesFileHash(setupPath, verifiedSha256))
            return false;
        ProcessStartInfo start = CreateStartInfo(setupPath);
        using Process? process = Process.Start(start);
        return process is not null;
    }

    private static bool MatchesFileHash(string setupPath, string expectedSha256)
    {
        try
        {
            using FileStream stream = new(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IntegrityVerifier.Matches(expectedSha256, IntegrityVerifier.Sha256Hex(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string setupPath)
        => new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = SilentSetupLauncher.BuildCommand(setupPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
}

// The agent waits for the application to exit, verifies the downloaded file,
// observes the installer's exit code and starts the application again.
public sealed class AgentSilentSetupInstaller : ISetupInstaller
{
    private readonly string _restartPath;

    public AgentSilentSetupInstaller(string restartPath) => _restartPath = restartPath;

    public bool TryStartSilent(string setupPath, string verifiedSha256)
    {
        if (!UpdatePolicy.IsSafeRestartPath(_restartPath))
        {
            return false;
        }

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetLights", "updates");
        if (!UpdatePolicy.IsInsideRoot(root, setupPath) || !File.Exists(setupPath))
        {
            return false;
        }

        string agent = Path.Combine(AppContext.BaseDirectory, "NetLights.UpdateAgent.exe");
        if (!File.Exists(agent))
        {
            return false;
        }

        // Setup replaces files in the application directory. Run the agent from
        // the staging directory so its own executable does not lock the install.
        string stagedAgent = Path.Combine(Path.GetDirectoryName(setupPath)!, "NetLights.UpdateAgent.exe");
        File.Copy(agent, stagedAgent, overwrite: true);

        string? hash = IntegrityVerifier.DigestSha256("sha256:" + verifiedSha256);
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            return false;
        using var process = new Process();
        string readyEventName = "Local\\NetLights.UpdateAgentReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        process.StartInfo.FileName = stagedAgent;
        process.StartInfo.ArgumentList.Add("--parent");
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        process.StartInfo.ArgumentList.Add("--pending");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(setupPath));
        process.StartInfo.ArgumentList.Add("--sha256");
        process.StartInfo.ArgumentList.Add(hash);
        process.StartInfo.ArgumentList.Add("--restart");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(_restartPath));
        process.StartInfo.ArgumentList.Add("--ready");
        process.StartInfo.ArgumentList.Add(readyEventName);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        if (!process.Start()) return false;
        if (ready.WaitOne(TimeSpan.FromSeconds(5)) && !process.HasExited) return true;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        try { process.WaitForExit(5000); }
        catch (Exception) { }
        return false;
    }
}
