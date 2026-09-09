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

        _ = Task.Run(() => Loop(autoUpdateEnabled, currentVersion, processPath, requestExit, updateGate, cancellationToken));
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
                await NetworkWaitPolicy.WaitUntilOnline(
                    probe,
                    Timeout.InfiniteTimeSpan,
                    NetworkWaitPolicy.OfflineRetry,
                    cancellationToken).ConfigureAwait(false);

                string downloadDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "NetLights",
                    "updates",
                    Guid.NewGuid().ToString("N"));

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
                        new CmdSilentSetupInstaller(),
                        () => exitRequested = true,
                        cancellationToken)).ConfigureAwait(false);
                }
                finally
                {
                    updateGate.Release();
                }

                if (exitRequested)
                {
                    requestExit();
                }

                if (outcome is SilentUpdateOutcome.Applied or SilentUpdateOutcome.NoUpdate)
                {
                    return;
                }

                if (outcome == SilentUpdateOutcome.Skipped
                    && (!autoUpdateEnabled()
                        || !SilentUpdatePolicy.HasInnoUninstaller(applicationDirectory)
                        || SilentUpdatePolicy.RidFor(architecture) is null))
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
