using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetLights.Updates;

public sealed class GitHubRelease
{
    public string TagName { get; init; } = "";
    public bool Prerelease { get; init; }
    public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
}

public sealed class GitHubAsset
{
    public string Name { get; init; } = "";
    public Uri? BrowserDownloadUrl { get; init; }
    public long Size { get; init; }
    public string? Digest { get; init; }
}

public sealed class GitHubReleaseFeed : IReleaseFeed, IInternetProbe, IDisposable
{
    private readonly HttpClient _http;

    public static TimeSpan QueryTimeout { get; } = TimeSpan.FromSeconds(20);

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(5);

    public GitHubReleaseFeed(HttpMessageHandler? handler = null, TimeSpan? timeout = null, bool githubApi = true)
    {
        HttpMessageHandler inner = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        _http = new HttpClient(inner, disposeHandler: true);
        _http.Timeout = timeout ?? DefaultTimeout;
        _http.DefaultRequestVersion = HttpVersion.Version11;
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights-Updater");
        if (githubApi)
        {
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }

    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        ReleaseQuery query = await QueryLatest(cancellationToken).ConfigureAwait(false);
        return query.Snapshot;
    }

    public async Task<ReleaseQuery> QueryLatest(CancellationToken cancellationToken)
    {
        try
        {
            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryTimeout.CancelAfter(QueryTimeout);
            using HttpResponseMessage response = await _http.GetAsync(
                UpdatePolicy.LatestApi,
                HttpCompletionOption.ResponseHeadersRead,
                queryTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ReleaseQuery(true, null);
            }

            if ((int)response.StatusCode is 429 or >= 500 || !response.IsSuccessStatusCode)
            {
                return new ReleaseQuery(false, null);
            }

            if (response.Content.Headers.ContentLength is > UpdatePolicy.MaxManifestBytes)
            {
                return new ReleaseQuery(false, null);
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(queryTimeout.Token).ConfigureAwait(false);
            byte[] bytes = await ReadCappedAsync(input, (int)UpdatePolicy.MaxManifestBytes, queryTimeout.Token).ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(bytes);
            JsonElement root = doc.RootElement;
            var assets = new List<GitHubAsset>();
            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsEl.EnumerateArray())
                {
                    assets.Add(new GitHubAsset
                    {
                        Name = asset.GetProperty("name").GetString() ?? "",
                        BrowserDownloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement url)
                            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? parsed)
                            ? parsed
                            : null,
                        Size = asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0,
                        Digest = asset.TryGetProperty("digest", out JsonElement digest) ? digest.GetString() : null
                    });
                }
            }

            return new ReleaseQuery(false, new GitHubRelease
            {
                TagName = root.GetProperty("tag_name").GetString() ?? "",
                Prerelease = root.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean(),
                Assets = assets
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReleaseQuery(false, null);
        }
        catch (InvalidOperationException)
        {
            return new ReleaseQuery(false, null);
        }
        catch (HttpRequestException)
        {
            return new ReleaseQuery(false, null);
        }
        catch (JsonException)
        {
            return new ReleaseQuery(false, null);
        }
    }

    public async Task<bool> Download(Uri url, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!UpdatePolicy.IsAllowedAssetUrl(url))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(directory) || !UpdatePolicy.IsInsideRoot(directory, destinationPath))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (response.Content.Headers.ContentLength is > UpdatePolicy.MaxInstallerBytes)
            {
                return false;
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > UpdatePolicy.MaxInstallerBytes)
                {
                    return false;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch (Exception)
                {
                }
            }

            return false;
        }
    }

    public async Task<bool> IsReachable(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(SilentUpdatePolicy.ProbeUrl));
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            using HttpResponseMessage response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _ = response.StatusCode;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (HttpProtocolException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
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

    public void Dispose() => _http.Dispose();

    internal static async Task<byte[]> ReadCappedAsync(Stream input, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 4096));
        byte[] buffer = new byte[4096];
        int total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("Manifest too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}

public static class IntegrityVerifier
{
    public static string Sha256Hex(Stream stream)
    {
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Matches(string expected, string actual)
    {
        string left = Normalize(expected);
        string right = Normalize(actual);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static string? DigestSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    private static string Normalize(string value)
    {
        string trimmed = value.Trim();
        return DigestSha256(trimmed) ?? trimmed;
    }
}
