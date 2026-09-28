using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using NetLights.Updates;

namespace NetLights.App;

[ExcludeFromCodeCoverage]
internal static class SilentUpdateRuntime
{
    public static void Start(
        Func<bool> autoUpdateEnabled,
        string currentVersion,
        string processPath,
        Action requestExit,
        SemaphoreSlim updateGate,
        CancellationToken cancellationToken)
    {
        if (!SilentUpdatePolicy.AllowsBackgroundProcess(Process.GetCurrentProcess().ProcessName))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            CleanupOldStaging();
            await Loop(autoUpdateEnabled, currentVersion, processPath, requestExit, updateGate, cancellationToken).ConfigureAwait(false);
        });
    }

    private static async Task Loop(
        Func<bool> autoUpdateEnabled,
        string currentVersion,
        string processPath,
        Action requestExit,
        SemaphoreSlim updateGate,
        CancellationToken cancellationToken)
    {
        string? applicationDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            return;
        }

        using GitHubReleaseFeed probe = new(timeout: NetworkWaitPolicy.ProbeTimeout, githubApi: false);
        using GitHubReleaseFeed feed = new();
        Architecture architecture = RuntimeInformation.ProcessArchitecture;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!autoUpdateEnabled())
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await NetworkWaitPolicy.WaitUntilOnline(
                    probe,
                    Timeout.InfiniteTimeSpan,
                    NetworkWaitPolicy.OfflineRetry,
                    cancellationToken).ConfigureAwait(false);

                string downloadDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetLights", "updates", Guid.NewGuid().ToString("N"));

                await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                SilentUpdateOutcome outcome;
                bool exitRequested = false;
                try
                {
                    outcome = await SilentUpdateCoordinator.RunOnce(new SilentUpdateContext(
                        autoUpdateEnabled(),
                        currentVersion,
                        Process.GetCurrentProcess().ProcessName,
                        applicationDirectory,
                        downloadDirectory,
                        architecture,
                        probe,
                        feed,
                        new AgentSilentSetupInstaller(processPath),
                        () => exitRequested = true,
                        cancellationToken)).ConfigureAwait(false);
                }
                finally
                {
                    updateGate.Release();
                    if (!exitRequested)
                    {
                        TryDeleteStaging(downloadDirectory);
                    }
                }

                if (exitRequested)
                {
                    requestExit();
                    return;
                }

                if (outcome == SilentUpdateOutcome.NoUpdate)
                {
                    await Task.Delay(TimeSpan.FromHours(12), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (outcome == SilentUpdateOutcome.Skipped && !autoUpdateEnabled())
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (outcome == SilentUpdateOutcome.Applied)
                {
                    return;
                }

                await Task.Delay(DelayAfter(outcome), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await Task.Delay(SilentUpdatePolicy.FailedRetry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static void TryDeleteStaging(string directory)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetLights", "updates");
        string name = Path.GetFileName(directory);
        if (!UpdatePolicy.IsInsideRoot(root, directory) || name.Length != 32 || !name.All(Uri.IsHexDigit))
        {
            return;
        }
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (UpdatePolicy.SafeInstallerFileName(Path.GetFileName(file)) is not null
                    || file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(file), "NetLights.UpdateAgent.exe", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CleanupOldStaging()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetLights", "updates");
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                    TryDeleteStaging(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TimeSpan DelayAfter(SilentUpdateOutcome outcome)
    {
        switch (outcome)
        {
            case SilentUpdateOutcome.Busy:
                return SilentUpdatePolicy.BusyRetry;
            case SilentUpdateOutcome.Offline:
                return NetworkWaitPolicy.OfflineRetry;
            case SilentUpdateOutcome.Failed:
                return SilentUpdatePolicy.FailedRetry;
            case SilentUpdateOutcome.NoUpdate:
            case SilentUpdateOutcome.Skipped:
            case SilentUpdateOutcome.Applied:
                return TimeSpan.Zero;
            default:
                SilentUpdateOutcome unreachable = outcome;
                throw new InvalidOperationException($"Unhandled silent update outcome {unreachable}");
        }
    }
}
