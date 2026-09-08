namespace NetLights.Updates;

public sealed class UpdateCoordinator
{
    private readonly GitHubReleaseFeed _feed;
    private readonly PendingUpdateStore _store;
    private readonly string _currentVersion;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private int _checking;

    public UpdateCoordinator(GitHubReleaseFeed feed, PendingUpdateStore store, string currentVersion, HttpMessageHandler? downloadHandler = null)
    {
        _feed = feed;
        _store = store;
        _currentVersion = currentVersion;
        _http = downloadHandler is null ? new HttpClient() : new HttpClient(downloadHandler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights-Updater");
    }

    public bool TryReadLastFailure(out string? message) => _store.TryReadLastFailure(out message);

    public PendingUpdate? Pending => _store.ReadPending();

    public Task CheckInBackgroundAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _lastCheck < UpdatePolicy.CheckInterval)
            {
                return Task.CompletedTask;
            }
        }

        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => CheckAsync(cancellationToken), cancellationToken);
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                if (DateTimeOffset.UtcNow - _lastCheck < UpdatePolicy.CheckInterval)
                {
                    return;
                }
            }

            GitHubRelease? release = await _feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _lastCheck = DateTimeOffset.UtcNow;
            }

            if (release is null || !UpdatePolicy.IsNewerStable(_currentVersion, release.TagName, release.Prerelease))
            {
                return;
            }

            GitHubAsset? asset = release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("NetLights-Setup-win-x64-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null || !UpdatePolicy.IsAllowedAssetUrl(asset.BrowserDownloadUrl))
            {
                return;
            }

            if (asset.Size <= 0 || asset.Size > UpdatePolicy.MaxInstallerBytes)
            {
                return;
            }

            string version = UpdatePolicy.Normalize(release.TagName);
            string dir = Path.Combine(_store.Root, version);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, asset.Name);
            string partial = dest + ".partial";
            await DownloadAsync(asset.BrowserDownloadUrl, partial, asset.Size, cancellationToken).ConfigureAwait(false);
            await using FileStream verify = File.OpenRead(partial);
            string hash = IntegrityVerifier.Sha256Hex(verify);
            string? digest = IntegrityVerifier.DigestSha256(asset.Digest);
            if (digest is not null && !IntegrityVerifier.Matches(digest, hash))
            {
                File.Delete(partial);
                return;
            }

            File.Move(partial, dest, true);
            _store.WritePending(new PendingUpdate(version, dest, hash, DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // Update failures must not affect monitoring
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task DownloadAsync(Uri url, string partialPath, long size, CancellationToken cancellationToken)
    {
        using FileStream output = new(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > size || total > UpdatePolicy.MaxInstallerBytes)
            {
                throw new IOException("Download exceeded size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

public static class UpdateAgentLauncher
{
    public static bool TryStart(UpdateCoordinator coordinator)
    {
        PendingUpdate? pending = coordinator.Pending;
        if (pending is null || !File.Exists(pending.InstallerPath))
        {
            return false;
        }

        string agent = Path.Combine(AppContext.BaseDirectory, "NetLights.UpdateAgent.exe");
        if (!File.Exists(agent))
        {
            return false;
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = agent;
        process.StartInfo.ArgumentList.Add("--parent");
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        process.StartInfo.ArgumentList.Add("--pending");
        process.StartInfo.ArgumentList.Add(pending.InstallerPath);
        process.StartInfo.ArgumentList.Add("--sha256");
        process.StartInfo.ArgumentList.Add(pending.Sha256);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process.Start();
    }
}
