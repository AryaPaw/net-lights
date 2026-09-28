using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace NetLights.Updates;

public interface ISetupInstaller
{
    bool TryStartSilent(string setupPath);
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
    public bool TryStartSilent(string setupPath)
    {
        ProcessStartInfo start = CreateStartInfo(setupPath);
        using Process? process = Process.Start(start);
        return process is not null;
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

    public bool TryStartSilent(string setupPath)
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

        using FileStream stream = File.OpenRead(setupPath);
        string hash = IntegrityVerifier.Sha256Hex(stream);
        using var process = new Process();
        process.StartInfo.FileName = stagedAgent;
        process.StartInfo.ArgumentList.Add("--parent");
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        process.StartInfo.ArgumentList.Add("--pending");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(setupPath));
        process.StartInfo.ArgumentList.Add("--sha256");
        process.StartInfo.ArgumentList.Add(hash);
        process.StartInfo.ArgumentList.Add("--restart");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(_restartPath));
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process.Start();
    }
}
