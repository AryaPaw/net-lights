using System.Runtime.InteropServices;

namespace NetLights.Updates;

public enum SilentUpdateOutcome
{
    Skipped,
    Busy,
    Offline,
    NoUpdate,
    Applied,
    Failed
}

public sealed record ReleaseQuery(bool NotFound, GitHubRelease? Snapshot);

public interface IReleaseFeed
{
    Task<ReleaseQuery> QueryLatest(CancellationToken cancellationToken);

    Task<bool> Download(Uri url, string destinationPath, CancellationToken cancellationToken);
}

public sealed record SilentUpdateContext(
    bool AutoUpdateEnabled,
    string CurrentVersion,
    string ProcessName,
    string ApplicationDirectory,
    string DownloadDirectory,
    Architecture ProcessArchitecture,
    IInternetProbe Probe,
    IReleaseFeed Feed,
    ISetupInstaller Installer,
    Action RequestExit,
    CancellationToken CancellationToken);

public static class SilentUpdateCoordinator
{
    public static async Task<SilentUpdateOutcome> RunOnce(SilentUpdateContext context)
    {
        if (!SilentUpdatePolicy.AllowsBackgroundProcess(context.ProcessName)
            || !context.AutoUpdateEnabled
            || !SilentUpdatePolicy.HasInnoUninstaller(context.ApplicationDirectory)
            || SilentUpdatePolicy.RidFor(context.ProcessArchitecture) is null)
        {
            return SilentUpdateOutcome.Skipped;
        }

        if (!SilentUpdatePolicy.IsSafeToRestart())
        {
            return SilentUpdateOutcome.Busy;
        }

        if (!await context.Probe.IsReachable(context.CancellationToken).ConfigureAwait(false))
        {
            return SilentUpdateOutcome.Offline;
        }

        if (!Version.TryParse(UpdatePolicy.Normalize(context.CurrentVersion), out Version? current))
        {
            return SilentUpdateOutcome.Failed;
        }

        ReleaseQuery query = await context.Feed.QueryLatest(context.CancellationToken).ConfigureAwait(false);
        if (query.NotFound)
        {
            return SilentUpdateOutcome.NoUpdate;
        }

        GitHubRelease? latest = query.Snapshot;
        if (latest is null)
        {
            return SilentUpdateOutcome.Failed;
        }

        if (latest.Prerelease
            || !SilentUpdatePolicy.TryParseTag(latest.TagName, out Version? candidate)
            || candidate is null
            || !SilentUpdatePolicy.IsNewer(current, candidate))
        {
            return SilentUpdateOutcome.NoUpdate;
        }

        GitHubAsset? asset = latest.Assets.FirstOrDefault(item =>
            UpdatePolicy.SafeInstallerFileName(item.Name) is not null);
        if (asset is null
            || asset.BrowserDownloadUrl is null
            || !UpdatePolicy.IsAllowedAssetUrl(asset.BrowserDownloadUrl)
            || IntegrityVerifier.DigestSha256(asset.Digest) is not string expected)
        {
            return SilentUpdateOutcome.Failed;
        }

        string? fileName = UpdatePolicy.SafeInstallerFileName(asset.Name);
        if (fileName is null)
        {
            return SilentUpdateOutcome.Failed;
        }

        string destination = Path.Combine(context.DownloadDirectory, fileName);
        if (!SilentUpdatePolicy.IsSafeSetupPath(destination, context.DownloadDirectory))
        {
            return SilentUpdateOutcome.Failed;
        }

        bool downloaded = await context.Feed.Download(asset.BrowserDownloadUrl, destination, context.CancellationToken)
            .ConfigureAwait(false);
        if (!downloaded)
        {
            return SilentUpdateOutcome.Failed;
        }

        string actual;
        await using (FileStream stream = File.OpenRead(destination))
        {
            actual = IntegrityVerifier.Sha256Hex(stream);
        }

        if (!IntegrityVerifier.Matches(expected, actual))
        {
            return SilentUpdateOutcome.Failed;
        }

        if (!SilentUpdatePolicy.IsSafeToRestart())
        {
            return SilentUpdateOutcome.Busy;
        }

        if (!context.Installer.TryStartSilent(destination))
        {
            return SilentUpdateOutcome.Failed;
        }

        context.RequestExit();
        return SilentUpdateOutcome.Applied;
    }
}
