namespace NetLights.Updates;

public sealed class UpdateCoordinator : IDisposable
{
    private readonly GitHubReleaseFeed _feed;
    private readonly PendingUpdateStore _store;
    private readonly string _currentVersion;
    private readonly HttpClient _http;
    private int _checking;

    public UpdateCoordinator(GitHubReleaseFeed feed, PendingUpdateStore store, string currentVersion, HttpMessageHandler? downloadHandler = null)
    {
        _feed = feed;
        _store = store;
        _currentVersion = currentVersion;
        _http = new HttpClient(downloadHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights-Updater");
    }

    public bool TryReadLastFailure(out string? message) => _store.TryReadLastFailure(out message);

    public PendingUpdate? Pending => _store.ReadPending();

    public Task CheckInBackgroundAsync(CancellationToken cancellationToken, Action? onStaged = null)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            bool staged = await CheckAsync(cancellationToken).ConfigureAwait(false);
            if (staged)
            {
                onStaged?.Invoke();
            }
        });
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        bool staged = false;
        try
        {
            PendingUpdate? existing = _store.ReadPending();
            if (UpdatePolicy.ShouldApplyPending(_currentVersion, existing)
                && existing is not null
                && File.Exists(existing.InstallerPath))
            {
                staged = true;
                return staged;
            }

            GitHubRelease? release = await _feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            if (release is null || !UpdatePolicy.IsNewerStable(_currentVersion, release.TagName, release.Prerelease))
            {
                return false;
            }

            GitHubAsset? asset = release.Assets.FirstOrDefault(a =>
                UpdatePolicy.SafeInstallerFileName(a.Name) is not null);
            if (asset?.BrowserDownloadUrl is null || !UpdatePolicy.IsAllowedAssetUrl(asset.BrowserDownloadUrl))
            {
                return false;
            }

            if (asset.Size <= 0 || asset.Size > UpdatePolicy.MaxInstallerBytes)
            {
                return false;
            }

            string? fileName = UpdatePolicy.SafeInstallerFileName(asset.Name);
            string version = UpdatePolicy.Normalize(release.TagName);
            if (fileName is null
                || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || version.Contains("..", StringComparison.Ordinal)
                || !Version.TryParse(version, out _))
            {
                return false;
            }

            string dir = Path.Combine(_store.Root, version);
            string dest = Path.Combine(dir, fileName);
            string partial = dest + ".partial";
            if (!UpdatePolicy.IsInsideRoot(_store.Root, dest) || !UpdatePolicy.IsInsideRoot(_store.Root, partial))
            {
                return false;
            }

            Directory.CreateDirectory(dir);
            await DownloadAsync(asset.BrowserDownloadUrl, partial, asset.Size, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            await using (FileStream verify = File.OpenRead(partial))
            {
                hash = IntegrityVerifier.Sha256Hex(verify);
            }

            string? digest = IntegrityVerifier.DigestSha256(asset.Digest);
            if (digest is null || !IntegrityVerifier.Matches(digest, hash))
            {
                File.Delete(partial);
                return false;
            }

            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            File.Move(partial, dest, true);
            cancellationToken.ThrowIfCancellationRequested();
            _store.WritePending(new PendingUpdate(version, dest, hash, DateTimeOffset.UtcNow));
            staged = true;
        }
        catch (Exception)
        {
            // Update failures must not affect monitoring
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }

        return staged;
    }

    private async Task DownloadAsync(Uri url, string partialPath, long size, CancellationToken cancellationToken)
    {
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        using FileStream output = new(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken cancellationToken)
    {
        Uri current = url;
        for (int hop = 0; hop <= 5; hop++)
        {
            bool allowed = hop == 0
                ? UpdatePolicy.IsAllowedAssetUrl(current)
                : UpdatePolicy.IsAllowedRedirectUrl(current);
            if (!allowed)
            {
                throw new InvalidOperationException("Download URL is not allowed.");
            }

            HttpResponseMessage response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            Uri? next = response.Headers.Location;
            response.Dispose();
            if (next is null)
            {
                throw new InvalidOperationException("Redirect without Location.");
            }

            if (!next.IsAbsoluteUri)
            {
                next = new Uri(current, next);
            }

            current = next;
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    public void WaitIdle(TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (Volatile.Read(ref _checking) != 0 && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
    }

    public void Dispose() => _http.Dispose();
}

public static class UpdateAgentLauncher
{
    public static bool TryStart(UpdateCoordinator coordinator, string? restartPath = null)
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
        if (restartPath is not null && UpdatePolicy.IsSafeRestartPath(restartPath))
        {
            process.StartInfo.ArgumentList.Add("--restart");
            process.StartInfo.ArgumentList.Add(Path.GetFullPath(restartPath));
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process.Start();
    }
}
